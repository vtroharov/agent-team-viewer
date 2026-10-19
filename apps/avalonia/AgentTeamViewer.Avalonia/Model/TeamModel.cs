using System.Collections.ObjectModel;
using AgentTeamViewer.Feed;

namespace AgentTeamViewer.Model;

/// <summary>
/// Applies feed events to a set of <see cref="AgentState"/>s. Must be driven from the UI thread
/// (its collections are bound to directly). Raises coarse events for structural changes the
/// view layer needs (new agents, edges, message pulses).
/// </summary>
public sealed class TeamModel : ObservableObject
{
    private readonly Dictionary<string, AgentState> _agents = new(StringComparer.Ordinal);
    private string? _sessionId;
    private string? _cwd;
    private string? _version;
    private long _clock;
    private int _eventCount;
    private int _unknownCount;

    public TeamModel()
    {
        Main = new AgentState(AgentState.MainId, 0) { Name = "Claude", RosterStatus = "idle" };
        _agents[AgentState.MainId] = Main;
        Agents.Add(Main);
    }

    /// <summary>The lead ("main"), created implicitly.</summary>
    public AgentState Main { get; }

    /// <summary>All agents including <see cref="Main"/>, in first-seen order.</summary>
    public ObservableCollection<AgentState> Agents { get; } = new();

    public string? SessionId { get => _sessionId; private set => Set(ref _sessionId, value); }
    public string? Cwd { get => _cwd; private set => Set(ref _cwd, value); }
    public string? Version { get => _version; private set => Set(ref _version, value); }

    /// <summary>Latest feed time seen (Unix ms). Used as "now" when replaying.</summary>
    public long Clock { get => _clock; private set => Set(ref _clock, value); }
    public int EventCount { get => _eventCount; private set => Set(ref _eventCount, value); }
    public int UnknownCount { get => _unknownCount; private set => Set(ref _unknownCount, value); }

    /// <summary>A new agent appeared (never for main).</summary>
    public event Action<AgentState>? AgentAdded;
    /// <summary>An agent's parent changed (including first assignment): (agent, oldParentId).</summary>
    public event Action<AgentState, string?>? ParentChanged;
    /// <summary>An agent addressed another: (fromId, toId). Only raised when both resolve.</summary>
    public event Action<string, string>? MessagePulsed;
    /// <summary>A block was appended to an agent's log.</summary>
    public event Action<AgentState, LogBlock>? BlockAppended;

    /// <summary>An agent became dismissed (once per agent). The view layer removes its card.</summary>
    public event Action<AgentState>? AgentDismissed;
    /// <summary>The mod answered a control request (accepted | failed).</summary>
    public event Action<ControlEvent>? ControlReceived;

    public AgentState? Find(string id) => _agents.GetValueOrDefault(id);

    /// <summary>Drops every agent except the lead and resets session facts (used when switching sessions).</summary>
    public void ClearAgents()
    {
        foreach (var id in _agents.Keys.Where(k => k != AgentState.MainId).ToList()) _agents.Remove(id);
        for (int i = Agents.Count - 1; i >= 0; i--) if (!Agents[i].IsMain) Agents.RemoveAt(i);
        SessionId = null; Cwd = null; Version = null;
        Clock = 0; EventCount = 0; UnknownCount = 0;
        Main.RosterStatus = "idle";
        Main.CompletedAt = null;
        Main.Model = null;                                   // the lead's model, worked time and turn belong to the old session
        Main.WorkedMs = 0;
        Main.CurrentTurnStartT = null;
    }

    public void Apply(FeedEvent ev)
    {
        EventCount++;
        if (ev.T > Clock) Clock = ev.T;
        switch (ev)
        {
            case SessionEvent s: ApplySession(s); break;
            case AgentEvent a: ApplyAgent(a); break;
            case TurnEvent t: ApplyTurn(t); break;
            case ChunkEvent c: ApplyChunk(c); break;
            case ToolEvent tl: ApplyTool(tl); break;
            case RowEvent r: ApplyRow(r); break;
            case MessageEvent m: ApplyMessage(m); break;
            case ControlEvent c: ControlReceived?.Invoke(c); break;
            case DismissedEvent d: ApplyDismissed(d); break;
            default: UnknownCount++; break;
        }
    }

    private void ApplySession(SessionEvent s)
    {
        SessionId = s.SessionId ?? SessionId;
        Cwd = s.Cwd ?? Cwd;
        Version = s.Version ?? Version;
        if (!string.IsNullOrEmpty(s.Model)) Main.Model = s.Model;
    }

    private AgentState GetOrCreate(string id, long t)
    {
        if (_agents.TryGetValue(id, out var a)) return a;
        a = new AgentState(id, t) { RosterStatus = "pending" };
        _agents[id] = a;
        Agents.Add(a);
        AgentAdded?.Invoke(a);
        return a;
    }

    private void ApplyAgent(AgentEvent e)
    {
        if (e.Id == AgentState.MainId) return;              // the lead is implicit
        var a = GetOrCreate(e.Id, e.T);
        a.LastEventT = e.T;
        if (e.Name is not null) a.Name = e.Name;
        if (e.Type is not null) a.Type = e.Type;
        if (e.Description is not null) a.Description = e.Description;
        if (!string.IsNullOrEmpty(e.Model)) a.Model = e.Model;
        a.IsTeammate = e.IsTeammate || a.IsTeammate;

        var newParent = string.IsNullOrEmpty(e.ParentId) ? (a.ParentId ?? AgentState.MainId) : e.ParentId;
        if (_agents.TryGetValue(newParent, out var np) && np.IsDismissed) newParent = AgentState.MainId;   // children of a dismissed agent hang off the hub
        if (newParent != a.ParentId)
        {
            var old = a.ParentId;
            a.ParentId = newParent;
            ParentChanged?.Invoke(a, old);
        }

        if (e.Status == "dismissed") { MarkDismissed(a, e.T); return; }
        if (e.Status is not null && !a.IsDismissed)           // dismissed is sticky: later roster syncs do not revive it
        {
            a.RosterStatus = e.Status;
            a.ActivityStatus = null;                         // a roster sync resets the inferred status
            if (AgentState.IsTerminal(e.Status))
            {
                a.CompletedAt ??= e.T;
                a.ActivityLine = e.Status;
                CloseOpenTools(a, e.T, failed: e.Status is "failed" or "killed");
                CloseTurn(a, e.T, null);                     // an agent killed mid-turn still worked until now
            }
            else
            {
                a.CompletedAt = null;
            }
        }
    }

    private void ApplyDismissed(DismissedEvent e)
    {
        if (_agents.TryGetValue(e.Agent, out var a) && !a.IsMain) MarkDismissed(a, e.T);
    }

    private void MarkDismissed(AgentState a, long t)
    {
        if (a.IsDismissed) return;
        a.RosterStatus = "dismissed";
        a.ActivityStatus = null;
        a.CompletedAt ??= t;
        a.ActivityLine = "dismissed";
        CloseOpenTools(a, t, failed: false);
        CloseTurn(a, t, null);
        foreach (var child in Agents.Where(c => c.ParentId == a.Id).ToList())
        {
            child.ParentId = AgentState.MainId;
            ParentChanged?.Invoke(child, a.Id);
        }
        AgentDismissed?.Invoke(a);
    }

    private void ApplyTurn(TurnEvent e)
    {
        var a = GetOrCreate(e.Agent, e.T);
        a.LastEventT = e.T;
        if (!string.IsNullOrEmpty(e.Model)) a.Model = e.Model;
        if (e.Phase == "step") return;                       // first model request of the turn: carries model only
        if (e.Phase == "start")
        {
            CloseTurn(a, e.T, null);                         // a start without an end: count the previous turn up to here
            a.CurrentTurnStartT = e.T;
            var b = new LogBlock(BlockKind.TurnStart, e.T) { Tag = e.TurnId };
            // The prompt/delivery row that triggered the turn usually carries the same text;
            // don't show it twice, render the turn start as a bare separator in that case.
            var prev = a.LastBlock;
            bool duplicate = prev is { Kind: BlockKind.Prompt or BlockKind.Delivery or BlockKind.Notice } && prev.Text.Trim() == (e.Text ?? "").Trim();
            b.SetText(duplicate ? "" : e.Text ?? "");
            Append(a, b);
            a.ActivityStatus = "running";
            a.ActivityLine = a.IsMain ? "responding" : "working on task";
            if (!a.IsFinished) a.CompletedAt = null;
        }
        else
        {
            var b = new LogBlock(BlockKind.TurnEnd, e.T) { Tag = e.Reason ?? (e.IsAborted ? "aborted" : "answer"), DurationMs = e.DurationMs };
            b.SetText(e.Answer ?? "");
            Append(a, b);
            CloseOpenTools(a, e.T, failed: false);
            CloseTurn(a, e.T, e.DurationMs);
            if (a.IsMain)
            {
                a.ActivityStatus = "idle";
                a.ActivityLine = "waiting for you";
            }
            else
            {
                a.ActivityStatus = e.Reason is "error" or "refusal" ? "failed" : "idle";
                a.ActivityLine = e.Reason is "error" ? "turn failed" : "turn ended";
            }
        }
    }

    private void ApplyChunk(ChunkEvent e)
    {
        var a = GetOrCreate(e.Agent, e.T);
        a.LastEventT = e.T;
        if (!string.IsNullOrEmpty(e.Model)) a.Model = e.Model;
        var kind = e.Type == "thinking" ? BlockKind.Thinking : BlockKind.Text;
        var last = a.LastBlock;
        if (last is not null && last.Kind == kind)
        {
            last.Append(e.Text);                             // coalesce consecutive chunks of one type
        }
        else
        {
            var b = new LogBlock(kind, e.T);
            b.Append(e.Text);
            Append(a, b);
        }
        if (!a.IsFinished)
        {
            a.ActivityStatus = kind == BlockKind.Thinking ? "thinking" : "running";
            a.ActivityLine = kind == BlockKind.Thinking ? "thinking" : "writing";
        }
    }

    private void ApplyTool(ToolEvent e)
    {
        var a = GetOrCreate(e.Agent, e.T);
        a.LastEventT = e.T;
        if (e.Phase == "start")
        {
            var b = new LogBlock(BlockKind.Tool, e.T)
            {
                ToolId = e.Id, ToolName = e.Name, ToolInput = e.InputJson, ToolStatus = ToolStatus.Running,
            };
            b.SetText(e.Summary ?? e.Name ?? "tool");
            Append(a, b);
            if (!string.IsNullOrEmpty(e.Id)) a.OpenTools[e.Id] = b;
            if (!a.IsFinished)
            {
                a.ActivityStatus = "working";
                a.ActivityLine = b.Text;
            }
        }
        else
        {
            LogBlock? b = null;
            if (!string.IsNullOrEmpty(e.Id) && a.OpenTools.Remove(e.Id, out var open)) b = open;
            if (b is null)
            {
                // End without a start we saw (e.g. opened in a chunk we never read): synthesise.
                b = new LogBlock(BlockKind.Tool, e.T) { ToolId = e.Id, ToolName = e.Name };
                b.SetText(e.Name ?? "tool");
                Append(a, b);
            }
            b.Result = e.Text;
            b.IsTruncated = e.Truncated;
            b.DurationMs = e.DurationMs;
            b.ToolStatus = e.IsError ? ToolStatus.Failed : ToolStatus.Done;
            if (!a.IsFinished && a.OpenTools.Count == 0)
            {
                a.ActivityStatus = "running";
                a.ActivityLine = e.IsError ? $"{b.ToolName} failed" : $"{b.ToolName} done";
            }
        }
    }

    private void ApplyRow(RowEvent e)
    {
        var a = GetOrCreate(e.Agent, e.T);
        a.LastEventT = e.T;
        var kind = e.Door switch
        {
            "prompt" => BlockKind.Prompt,
            "delivery" => BlockKind.Delivery,
            _ => BlockKind.Notice,
        };
        var b = new LogBlock(kind, e.T) { From = e.From, Tag = e.Door };
        b.SetText(e.Text ?? "");
        Append(a, b);
    }

    private void ApplyMessage(MessageEvent e)
    {
        var from = e.From;
        var to = e.To;
        if (string.IsNullOrEmpty(to) && !string.IsNullOrEmpty(e.ToName)) to = ResolveName(e.ToName);
        if (string.IsNullOrEmpty(to)) return;
        if (!_agents.ContainsKey(from) || !_agents.ContainsKey(to)) return;
        MessagePulsed?.Invoke(from, to);
    }

    /// <summary>Resolves a teammate name to the most recent live agent with that name; "main" resolves to the lead.</summary>
    public string? ResolveName(string name)
    {
        if (name == AgentState.MainId || name.Equals("lead", StringComparison.OrdinalIgnoreCase) || name.Equals("team-lead", StringComparison.OrdinalIgnoreCase))
            return AgentState.MainId;
        AgentState? best = null;
        foreach (var a in Agents)
        {
            if (a.IsMain) continue;
            if (a.IsDismissed || !string.Equals(a.Name, name, StringComparison.Ordinal)) continue;
            if (best is null || (!a.IsFinished && best.IsFinished) || a.FirstSeen >= best.FirstSeen) best = a;
        }
        return best?.Id;
    }

    private void Append(AgentState a, LogBlock b)
    {
        a.Blocks.Add(b);
        BlockAppended?.Invoke(a, b);
    }

    /// <summary>Ends the in-progress turn, adding its duration (the feed's figure when given) to time worked.</summary>
    private static void CloseTurn(AgentState a, long t, long? durationMs)
    {
        if (a.CurrentTurnStartT is not long start) return;
        a.WorkedMs += durationMs ?? Math.Max(0, t - start);
        a.CurrentTurnStartT = null;
    }

    private static void CloseOpenTools(AgentState a, long t, bool failed)
    {
        if (a.OpenTools.Count == 0) return;
        foreach (var b in a.OpenTools.Values)
        {
            if (b.ToolStatus == ToolStatus.Running)
            {
                b.ToolStatus = failed ? ToolStatus.Failed : ToolStatus.Done;
                b.DurationMs ??= Math.Max(0, t - b.T);
            }
        }
        a.OpenTools.Clear();
    }
}
