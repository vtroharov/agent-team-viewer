using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using Avalonia.Threading;
using AgentTeamViewer.Feed;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.ViewModels;

/// <summary>
/// Owns the model, the feed source (live tail or demo replay), and the card list. The window
/// binds to this and subscribes to <see cref="EdgeAdded"/> / <see cref="MessagePulsed"/> for the
/// connector layer.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, AgentCardViewModel> _cardsById = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _tick;
    private readonly SessionRegistry _registry;
    private FeedReader? _reader;
    private FeedReplayer? _replayer;
    private AgentCardViewModel? _expanded;
    private string _sourceLabel = "";
    private string _sessionName = "";
    private string _sessionShortId = "";
    private string _modeLabel = "";
    private string _statusLine = "";
    private bool _isLive;
    private bool _catchingUp;
    private string? _currentDirectory;
    private string? _currentSessionId;
    private string? _sessionsRoot;
    private SessionPickerItem? _selectedSession;
    private Dictionary<string, int> _ranks = new(StringComparer.Ordinal);
    private string? _orderKey;

    public MainViewModel(Dispatcher dispatcher, string? registryDirectory = null)
    {
        _dispatcher = dispatcher;
        _registry = new SessionRegistry(dispatcher, registryDirectory);
        Model = new TeamModel();
        Model.AgentAdded += OnAgentAdded;
        Model.ParentChanged += OnParentChanged;
        Model.MessagePulsed += (f, t) => { if (IsShown(f) && IsShown(t)) MessagePulsed?.Invoke(f, t); };
        Model.AgentDismissed += OnAgentDismissed;
        Model.ControlReceived += OnControl;
        Model.PropertyChanged += OnModelChanged;
        Model.Main.PropertyChanged += OnMainChanged;
        Model.Main.Blocks.CollectionChanged += (_, _) => Raise(nameof(HubSpin));

        Lead = new AgentCardViewModel(Model.Main, Now);
        LeadLog = new LogViewModel(Model.Main);

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { Lead.Tick(); foreach (var c in Cards) c.Tick(); CheckDismissTimeouts(); Raise(nameof(HubSpin)); UpdateStatusLine(null); };
        _tick.Start();

        // Last: its first refresh raises Changed, which reads Model and the labels.
        _registry.Changed += OnRegistryChanged;
        _registry.Start();
    }

    public TeamModel Model { get; }
    public AgentCardViewModel Lead { get; }
    public LogViewModel LeadLog { get; }

    /// <summary>Agent cards in display order: depth-first under their parent, newest last.</summary>
    public ObservableCollection<AgentCardViewModel> Cards { get; } = new();

    /// <summary>
    /// Picker rows: live sessions first (those with a feed by newest feed, then live sessions with no
    /// feed yet, disabled), then older feeds by newest first.
    /// </summary>
    public ObservableCollection<SessionPickerItem> Sessions { get; } = new();

    public SessionPickerItem? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!Set(ref _selectedSession, value) || value is null) return;
            if (!value.HasFeed)
            {
                // Not openable: snap the selection back to the current session.
                _dispatcher.Post(() => { _selectedSession = Sessions.FirstOrDefault(s => s.SameDirectory(_currentDirectory)); Raise(nameof(SelectedSession)); });
                return;
            }
            if (_isLive && !value.SameDirectory(_currentDirectory)) OpenLive(value.Directory!);
        }
    }

    /// <summary>The session's real name from the registry (follows /rename), else the cwd leaf, else the id.</summary>
    public string SessionName { get => _sessionName; private set => Set(ref _sessionName, value); }
    /// <summary>First 8 characters of the session id, shown dimmed after the name.</summary>
    public string SessionShortId { get => _sessionShortId; private set => Set(ref _sessionShortId, value); }

    public AgentCardViewModel? ExpandedCard
    {
        get => _expanded;
        private set
        {
            if (ReferenceEquals(_expanded, value)) return;
            if (_expanded is not null) _expanded.IsExpanded = false;
            _expanded = value;
            if (_expanded is not null) _expanded.IsExpanded = true;
            Raise();
            Raise(nameof(IsCardExpanded));
            Raise(nameof(IsDragEnabled));
            Raise(nameof(ExpandedLog));
        }
    }
    public bool IsCardExpanded => _expanded is not null;
    public LogViewModel? ExpandedLog => _expanded?.Log;

    /// <summary>The cwd label: the working directory leaf of the session being shown.</summary>
    public string SourceLabel { get => _sourceLabel; private set => Set(ref _sourceLabel, value); }
    public string ModeLabel { get => _modeLabel; private set => Set(ref _modeLabel, value); }
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }

    /// <summary>True while the initial catch-up read is being applied (the window can mute animations).</summary>
    public bool IsCatchingUp { get => _catchingUp; private set => Set(ref _catchingUp, value); }

    /// <summary>Hub breathes while the lead is thinking.</summary>
    public bool HubBreathe => Model.Main.Status == "thinking";
    /// <summary>Hub spins while a lead tool call is running.</summary>
    public bool HubSpin => Model.Main.OpenTools.Count > 0;
    public bool LeadIsThinking => Model.Main.Status == "thinking";

    /// <summary>The card list or a parent link changed: the window re-derives every connector edge from <see cref="Cards"/>.</summary>
    public event Action? EdgesChanged;
    /// <summary>The source was switched or closed: the window clears all edges, anchors and pending pulses.</summary>
    public event Action? SessionReset;
    /// <summary>An agent messaged another: (fromId, toId).</summary>
    public event Action<string, string>? MessagePulsed;
    /// <summary>A new card was added (for anchor registration / animation).</summary>
    public event Action<AgentCardViewModel>? CardAdded;

    // ------------------------------------------------------------------ sources

    public void OpenDemo(string sessionDirectory, double eventsPerSecond = 20)
    {
        CloseSource();
        IsLive = false;
        ModeLabel = "DEMO";
        _currentDirectory = sessionDirectory;
        MarkCurrentRow();
        LoadOrder(sessionDirectory);
        _currentSessionId = SessionLocator.Describe(sessionDirectory)?.SessionId ?? Path.GetFileName(sessionDirectory);
        UpdateSessionLabels();
        _replayer = new FeedReplayer(sessionDirectory, eventsPerSecond);
        _replayer.EventReceived += Apply;
        _replayer.Finished += () => UpdateStatusLine("replay finished");
        UpdateStatusLine("replaying");
        RefreshDismissAllowed();
        _replayer.Start();
    }

    public void OpenLive(string sessionDirectory)
    {
        CloseSource();
        IsLive = true;
        ModeLabel = "LIVE";
        var info = SessionLocator.Describe(sessionDirectory);
        _currentDirectory = sessionDirectory;
        MarkCurrentRow();
        LoadOrder(sessionDirectory);
        _currentSessionId = info?.SessionId ?? Path.GetFileName(sessionDirectory);
        UpdateSessionLabels();
        IsCatchingUp = true;
        _reader = new FeedReader(sessionDirectory, _dispatcher);
        _reader.EventReceived += Apply;
        _reader.CaughtUp += () => { IsCatchingUp = false; UpdateStatusLine("tailing"); };
        _reader.Error += msg => UpdateStatusLine(msg);
        UpdateStatusLine("reading");
        RefreshDismissAllowed();
        _reader.Start();
    }

    public void RefreshSessions(string? root = null)
    {
        if (root is not null) _sessionsRoot = root;
        _registry.Refresh();

        var feeds = SessionLocator.ListSessions(_sessionsRoot);
        var items = new List<SessionPickerItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in feeds)
        {
            items.Add(MakeItem(f, _registry.Find(f.SessionId)));
            seen.Add(f.SessionId);
        }
        // Live Claude Code sessions whose agent-feed mod has not written anything yet.
        foreach (var r in _registry.Live)
            if (seen.Add(r.SessionId)) items.Add(MakeItem(null, r));

        var ordered = items
            .OrderByDescending(i => i.IsLive)
            .ThenByDescending(i => i.HasFeed)
            .ThenByDescending(i => i.SortTime)
            .ToList();

        Sessions.Clear();
        foreach (var s in ordered) Sessions.Add(s);
        _selectedSession = Sessions.FirstOrDefault(s => s.SameDirectory(_currentDirectory));
        Raise(nameof(SelectedSession));
    }

    /// <summary>Keeps the picker rows' IsCurrent flags in step with the open session (the delete X must never show on it).</summary>
    private void MarkCurrentRow()
    {
        foreach (var s in Sessions) s.IsCurrent = s.HasFeed && s.SameDirectory(_currentDirectory);
    }

    private SessionPickerItem MakeItem(SessionInfo? feed, SessionRecord? record) =>
        new(feed, record) { IsCurrent = feed is not null && string.Equals(feed.Directory, _currentDirectory, StringComparison.OrdinalIgnoreCase), DeleteRequested = DeleteSession };

    /// <summary>
    /// Removes a session's feed directory from disk after confirmation. Never the open session,
    /// never anything outside the feed root.
    /// </summary>
    public async void DeleteSession(SessionPickerItem item)
    {
        if (!item.CanDelete || item.Directory is null) return;
        if (item.SameDirectory(_currentDirectory)) return;

        string dir, root;
        try
        {
            dir = Path.GetFullPath(item.Directory).TrimEnd('\\', '/');
            root = Path.GetFullPath(_sessionsRoot ?? SessionLocator.DefaultRoot).TrimEnd('\\', '/');
        }
        catch (Exception ex)
        {
            UpdateStatusLine($"delete refused: {ex.Message}");
            return;
        }
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var insideRoot = dir.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                         && !dir.AsSpan(rootPrefix.Length).ContainsAny('\\', '/');
        if (!insideRoot)
        {
            UpdateStatusLine($"delete refused: {dir} is not a session directory under {root}");
            return;
        }

        if (!await ConfirmAsync("Delete session",
            $"Delete session '{item.Label}'?\n\nThis removes its feed directory from disk:\n{dir}")) return;

        try
        {
            Directory.Delete(dir, recursive: true);
            UpdateStatusLine($"deleted session {item.ShortId}");
        }
        catch (Exception ex)
        {
            UpdateStatusLine($"delete failed: {ex.Message}");
        }
        RefreshSessions();
    }

    /// <summary>Name, short id and cwd for the header, from the registry when it knows this session.</summary>
    private void UpdateSessionLabels()
    {
        var id = _currentSessionId ?? "";
        var rec = _registry.Find(id);
        var cwd = rec?.Cwd ?? Model.Cwd ?? (_currentDirectory is null ? null : SessionLocator.Describe(_currentDirectory)?.Cwd);
        var leaf = string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/'));
        SessionName = rec?.Name is { Length: > 0 } n ? n : (leaf.Length > 0 ? leaf : id);
        SessionShortId = id.Length > 8 ? id[..8] : id;
        SourceLabel = IsLive ? leaf : "sample-feed";
    }

    private void OnRegistryChanged()
    {
        UpdateSessionLabels();
        RefreshDismissAllowed();
        if (IsLive) RefreshSessions();
    }

    /// <summary>No session to show: an empty model with an explanatory status line.</summary>
    public void OpenNothing(string message)
    {
        CloseSource();
        IsLive = true;
        ModeLabel = "IDLE";
        _currentDirectory = null;
        _currentSessionId = null;
        SessionName = "no session";
        SessionShortId = "";
        SourceLabel = "";
        UpdateStatusLine(message);
        RefreshDismissAllowed();
    }

    private void CloseSource()
    {
        _reader?.Dispose(); _reader = null;
        _replayer?.Dispose(); _replayer = null;
        ExpandedCard = null;
        Cards.Clear();
        _cardsById.Clear();
        _awaitingAccept.Clear();
        ResetModel();
        SessionReset?.Invoke();
    }

    private void ResetModel()
    {
        // TeamModel has no reset; replace its agents by clearing the lead's log and dropping cards.
        // Since the model is only ever re-created here, we rebuild by re-wiring a fresh instance.
        Model.Main.Blocks.Clear();
        Model.Main.OpenTools.Clear();
        Model.Main.ActivityStatus = null;
        Model.Main.ActivityLine = null;
        // Agents other than main are dropped by the model via ClearAgents.
        Model.ClearAgents();
    }

    // ------------------------------------------------------------------ apply

    private long Now() => IsLive ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : Model.Clock;

    private void Apply(FeedEvent ev)
    {
        Model.Apply(ev);
        if (Model.EventCount % 25 == 0 || !IsLive) UpdateStatusLine(null);
    }

    private void UpdateStatusLine(string? state)
    {
        if (state is not null) _lastState = state;
        var agents = Cards.Count;
        var live = Cards.Count(c => !c.IsFinished);
        StatusLine = $"{_lastState} · {Model.EventCount} events · {agents} agents ({live} active)" +
                     (Model.UnknownCount > 0 ? $" · {Model.UnknownCount} unknown" : "");
    }
    private string _lastState = "";

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TeamModel.SessionId) or nameof(TeamModel.Cwd))
        {
            // The feed's own session event is the fallback source for id and cwd.
            if (!string.IsNullOrEmpty(Model.SessionId)) _currentSessionId = Model.SessionId;
            UpdateSessionLabels();
        }
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentState.Status)) { Raise(nameof(HubBreathe)); Raise(nameof(LeadIsThinking)); }
        if (e.PropertyName == nameof(AgentState.ActivityLine)) Raise(nameof(HubSpin));
    }

    private void OnAgentAdded(AgentState a)
    {
        var card = new AgentCardViewModel(a, Now) { SessionAllowsDismiss = DismissAllowed };
        _cardsById[a.Id] = card;
        ResyncCards();
        CardAdded?.Invoke(card);
        UpdateStatusLine(null);
    }

    private void OnParentChanged(AgentState a, string? oldParent)
    {
        if (!_cardsById.TryGetValue(a.Id, out var card)) return;
        ResyncCards();
    }

    /// <summary>Top-level agents hang off the hub; nested ones off their parent card.</summary>
    public string EdgeSource(AgentCardViewModel c) =>
        string.IsNullOrEmpty(c.ParentId) || c.ParentId == AgentState.MainId || !_cardsById.ContainsKey(c.ParentId) ? "hub" : c.ParentId;

    /// <summary>Depth-first order: each agent followed by its children, siblings in first-seen order.</summary>
    private void ResyncCards()
    {
        var byParent = new Dictionary<string, List<AgentCardViewModel>>(StringComparer.Ordinal);
        foreach (var a in Model.Agents)
        {
            if (a.IsMain || !_cardsById.TryGetValue(a.Id, out var c)) continue;
            var parent = a.ParentId ?? AgentState.MainId;
            if (!_cardsById.ContainsKey(parent)) parent = AgentState.MainId;   // unknown parent: treat as top-level
            if (!byParent.TryGetValue(parent, out var list)) byParent[parent] = list = new();
            list.Add(c);
        }

        // Siblings: manual rank first (user drag order, persisted per session), unranked ones after in first-seen order.
        foreach (var kids in byParent.Values)
        {
            var sorted = kids
                .Select((c, i) => (c, i))
                .OrderBy(t => _ranks.TryGetValue(t.c.Id, out var r) ? r : int.MaxValue)
                .ThenBy(t => t.i)
                .Select(t => t.c)
                .ToList();
            kids.Clear();
            kids.AddRange(sorted);
        }

        var ordered = new List<AgentCardViewModel>();
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string parentId, int depth)
        {
            if (!byParent.TryGetValue(parentId, out var kids)) return;
            foreach (var k in kids)
            {
                if (!visiting.Add(k.Id)) continue;              // cycle guard
                k.Depth = depth;
                ordered.Add(k);
                Walk(k.Id, depth + 1);
            }
        }
        Walk(AgentState.MainId, 0);
        // Anything unreachable (cyclic parents) goes at the end, flat.
        foreach (var c in _cardsById.Values) if (visiting.Add(c.Id)) { c.Depth = 0; ordered.Add(c); }

        // Minimal-diff sync so WPF animates inserts rather than rebuilding everything.
        for (int i = 0; i < ordered.Count; i++)
        {
            var want = ordered[i];
            int have = Cards.IndexOf(want);
            if (have == i) continue;
            if (have < 0) Cards.Insert(i, want);
            else Cards.Move(have, i);
        }
        while (Cards.Count > ordered.Count) Cards.RemoveAt(Cards.Count - 1);
        EdgesChanged?.Invoke();
    }

    // ------------------------------------------------------------------ dismiss (control channel)

    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(420);
    private readonly Dictionary<string, DateTime> _awaitingAccept = new(StringComparer.Ordinal);

    /// <summary>Dismissing needs a running Claude Code session: never in --demo, never for an old recording.</summary>
    private bool DismissAllowed =>
        IsLive && _currentDirectory is not null && _currentSessionId is not null && _registry.Find(_currentSessionId)?.IsLive == true;

    private void RefreshDismissAllowed()
    {
        bool allowed = DismissAllowed;
        foreach (var c in _cardsById.Values) c.SessionAllowsDismiss = allowed;
    }

    /// <summary>A card is drawn (not hidden or gone): the lead always is.</summary>
    private bool IsShown(string id) => id == AgentState.MainId || (_cardsById.TryGetValue(id, out var c) && !c.IsRemoving);

    private static readonly bool? ConfirmOverride =
        Environment.GetEnvironmentVariable("ATV_CONFIRM_DISMISS")?.Trim().ToLowerInvariant() switch
        {
            "yes" => true,
            "no" => false,
            _ => null,
        };

    /// <summary>Asks the user to confirm; the window supplies the dialog (a modal in-app prompt, cross-platform). Declines when none is wired.</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    private async Task<bool> ConfirmAsync(string title, string message, bool testOverride = false)
    {
        if (testOverride && ConfirmOverride is { } forced) return forced;
        return Confirm is not null && await Confirm(title, message);
    }

    /// <summary>The X was clicked: confirm, append a dismiss request to control.jsonl, show the card as dismissing.</summary>
    public async void RequestDismiss(AgentCardViewModel card)
    {
        if (!card.CanDismiss || _currentDirectory is null) return;
        // Test-only bypass: ATV_CONFIRM_DISMISS=yes|no answers the confirm without showing the dialog.
        if (!await ConfirmAsync("Dismiss agent",
            $"Dismiss {card.Name}? It will be asked for a short summary, stopped, and its summary passed to the lead.", testOverride: true)) return;

        try
        {
            FeedFiles.AppendDismiss(_currentDirectory, card.Id);
        }
        catch (Exception ex)
        {
            UpdateStatusLine($"dismiss failed: {ex.Message}");
            return;
        }
        _awaitingAccept[card.Id] = DateTime.UtcNow;
        card.IsDismissing = true;
        UpdateStatusLine($"dismiss requested for {card.Name}");
    }

    private void OnControl(ControlEvent c)
    {
        if (!_cardsById.TryGetValue(c.Agent, out var card) || card.IsRemoving) return;
        if (c.Status == "accepted")
        {
            _awaitingAccept.Remove(card.Id);
            card.IsDismissing = true;                           // also restores the state after a viewer restart
        }
        else if (c.Status == "failed")
        {
            _awaitingAccept.Remove(card.Id);
            card.IsDismissing = false;
            if (!IsCatchingUp)
                UpdateStatusLine($"dismiss of {card.Name} failed" + (string.IsNullOrWhiteSpace(c.Reason) ? "" : $": {c.Reason}"));
        }
    }

    /// <summary>A request that was written but never answered must not leave the card stuck in "dismissing".</summary>
    private void CheckDismissTimeouts()
    {
        if (_awaitingAccept.Count == 0) return;
        foreach (var (id, at) in _awaitingAccept.ToList())
        {
            if (DateTime.UtcNow - at < AcceptTimeout) continue;
            _awaitingAccept.Remove(id);
            if (_cardsById.TryGetValue(id, out var card) && card.IsDismissing)
            {
                card.IsDismissing = false;
                UpdateStatusLine($"dismiss of {card.Name} was not acknowledged");
            }
        }
    }

    private void OnAgentDismissed(AgentState a)
    {
        _awaitingAccept.Remove(a.Id);
        if (!_cardsById.TryGetValue(a.Id, out var card)) return;
        if (ReferenceEquals(_expanded, card)) ExpandedCard = null;
        card.IsDismissing = false;
        card.IsRemoving = true;
        if (!IsCatchingUp) UpdateStatusLine($"dismissed {card.Name}");
        if (IsCatchingUp || !IsLive) { RemoveCard(card); return; }   // replaying: no fade
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = FadeDuration };
        timer.Tick += (_, _) => { timer.Stop(); RemoveCard(card); };
        timer.Start();
    }

    /// <summary>Drops a dismissed card, its connector and its rank. Children were already reparented to the hub by the model.</summary>
    private void RemoveCard(AgentCardViewModel card)
    {
        if (!_cardsById.TryGetValue(card.Id, out var current) || !ReferenceEquals(current, card)) return;
        _cardsById.Remove(card.Id);
        if (_ranks.Remove(card.Id) && _orderKey is not null) CardOrderStore.Save(_orderKey, _ranks);
        ResyncCards();
        UpdateStatusLine(null);
    }

    // ------------------------------------------------------------------ manual order (drag to reorder)

    /// <summary>Dragging is allowed only while no card is expanded.</summary>
    public bool IsDragEnabled => !IsCardExpanded;

    /// <summary>Siblings of <paramref name="card"/> (same parent) in current display order, including itself.</summary>
    public List<AgentCardViewModel> SiblingsOf(AgentCardViewModel card)
    {
        var parent = card.ParentId ?? AgentState.MainId;
        if (!_cardsById.ContainsKey(parent)) parent = AgentState.MainId;
        return Cards.Where(c =>
        {
            var p = c.ParentId ?? AgentState.MainId;
            if (!_cardsById.ContainsKey(p)) p = AgentState.MainId;
            return p == parent;
        }).ToList();
    }

    /// <summary>Number of consecutive cards that form <paramref name="card"/>'s block: itself plus its nested descendants.</summary>
    public int BlockSize(AgentCardViewModel card)
    {
        int i = Cards.IndexOf(card);
        if (i < 0) return 0;
        int n = 1;
        for (int j = i + 1; j < Cards.Count && Cards[j].Depth > card.Depth; j++) n++;
        return n;
    }

    /// <summary>Applies a new order for one sibling group, persists it, and re-syncs the card list.</summary>
    public void ReorderSiblings(IReadOnlyList<AgentCardViewModel> orderedSiblings)
    {
        for (int i = 0; i < orderedSiblings.Count; i++) _ranks[orderedSiblings[i].Id] = i;
        if (_orderKey is not null) CardOrderStore.Save(_orderKey, _ranks);
        ResyncCards();
    }

    private void LoadOrder(string sessionDirectory)
    {
        _orderKey = Path.GetFileName(sessionDirectory.TrimEnd('\\', '/'));
        _ranks = CardOrderStore.Load(_orderKey);
    }

    // ------------------------------------------------------------------ expand / collapse

    public void ToggleExpand(AgentCardViewModel card) => ExpandedCard = ReferenceEquals(ExpandedCard, card) ? null : card;
    public void Collapse() => ExpandedCard = null;

    public AgentCardViewModel? CardFor(string id) => _cardsById.GetValueOrDefault(id);

    public void Dispose()
    {
        _tick.Stop();
        _registry.Dispose();
        _reader?.Dispose();
        _replayer?.Dispose();
    }
}
