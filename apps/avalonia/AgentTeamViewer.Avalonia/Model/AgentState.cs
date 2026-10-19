using System.Collections.ObjectModel;

namespace AgentTeamViewer.Model;

/// <summary>
/// Everything known about one agent (or the lead, id "main"): roster facts, derived activity,
/// and the ordered log blocks. Mutated only on the UI thread by <see cref="TeamModel"/>.
/// </summary>
public sealed class AgentState : ObservableObject
{
    public const string MainId = "main";

    private string? _name;
    private string? _type;
    private string? _description;
    private string? _parentId;
    private bool _isTeammate;
    private string? _rosterStatus;
    private string? _activityStatus;
    private string? _activityLine;
    private long? _completedAt;
    private long _lastEventT;
    private string? _model;
    private long _workedMs;
    private long? _currentTurnStartT;

    public AgentState(string id, long firstSeen)
    {
        Id = id;
        FirstSeen = firstSeen;
        _lastEventT = firstSeen;
    }

    public string Id { get; }
    public bool IsMain => Id == MainId;
    public long FirstSeen { get; }

    public string? Name { get => _name; set { if (Set(ref _name, value)) Raise(nameof(DisplayName)); } }
    public string? Type { get => _type; set { if (Set(ref _type, value)) Raise(nameof(Tag)); } }
    public string? Description { get => _description; set { if (Set(ref _description, value)) Raise(nameof(DisplayName)); } }
    public string? ParentId { get => _parentId; set => Set(ref _parentId, value); }
    public bool IsTeammate { get => _isTeammate; set { if (Set(ref _isTeammate, value)) Raise(nameof(Tag)); } }

    /// <summary>Latest status from an <c>agent</c> event (FEED.md statuses).</summary>
    public string? RosterStatus
    {
        get => _rosterStatus;
        set { if (Set(ref _rosterStatus, value)) { Raise(nameof(Status)); Raise(nameof(IsDismissed)); } }
    }

    /// <summary>Status inferred from events between roster syncs: working | thinking | running | idle.</summary>
    public string? ActivityStatus
    {
        get => _activityStatus;
        set { if (Set(ref _activityStatus, value)) Raise(nameof(Status)); }
    }

    /// <summary>What the agent is doing right now, one line (tool summary, "thinking", ...).</summary>
    public string? ActivityLine { get => _activityLine; set => Set(ref _activityLine, value); }

    /// <summary>Feed time of completion (turn end for main, terminal roster status for agents).</summary>
    public long? CompletedAt { get => _completedAt; set { if (Set(ref _completedAt, value)) Raise(nameof(IsFinished)); } }

    public long LastEventT { get => _lastEventT; set => Set(ref _lastEventT, value); }

    /// <summary>LLM model id when the feed told us (optional field), else null.</summary>
    public string? Model { get => _model; set { if (Set(ref _model, value)) Raise(nameof(ModelLabel)); } }

    /// <summary>Short model label for chips: "opus-5-5" from "claude-opus-5-5"; "unknown" when not known.</summary>
    public string ModelLabel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_model)) return "model unknown";
            var m = _model.Trim();
            if (m.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) m = m[7..];
            return m;
        }
    }

    /// <summary>Milliseconds spent inside completed model turns (turn start to turn end).</summary>
    public long WorkedMs { get => _workedMs; set { if (Set(ref _workedMs, value)) Raise(nameof(IsWorking)); } }

    /// <summary>Feed time the current turn began, or null when no turn is in progress.</summary>
    public long? CurrentTurnStartT { get => _currentTurnStartT; set { if (Set(ref _currentTurnStartT, value)) Raise(nameof(IsWorking)); } }

    public bool IsWorking => _currentTurnStartT is not null;

    /// <summary>Total time worked as of <paramref name="nowMs"/>: completed turns plus the in-progress one.</summary>
    public long WorkedAs(long nowMs) =>
        _workedMs + (_currentTurnStartT is long s ? Math.Max(0, nowMs - s) : 0);

    /// <summary>The user dismissed this agent through the control channel; it is hidden everywhere.</summary>
    public bool IsDismissed => _rosterStatus == "dismissed";

    public bool IsFinished => IsTerminal(RosterStatus);

    /// <summary>The status a glyph should show: terminal roster states win, else the latest activity, else the roster.</summary>
    public string Status
    {
        get
        {
            if (IsTerminal(RosterStatus)) return RosterStatus!;
            return ActivityStatus ?? RosterStatus ?? "pending";
        }
    }

    public string DisplayName => Name ?? (string.IsNullOrWhiteSpace(Description) ? Id : Description!);

    /// <summary>"teammate" for named teammates, else the agent type, else "agent".</summary>
    public string Tag => IsTeammate ? "teammate" : (Type ?? "agent");

    public ObservableCollection<LogBlock> Blocks { get; } = new();

    /// <summary>Open tool blocks by tool id, for start/end pairing.</summary>
    public Dictionary<string, LogBlock> OpenTools { get; } = new(StringComparer.Ordinal);

    public static bool IsTerminal(string? status) =>
        status is "completed" or "failed" or "killed" or "gone" or "dismissed";

    public LogBlock? LastBlock => Blocks.Count == 0 ? null : Blocks[^1];
}
