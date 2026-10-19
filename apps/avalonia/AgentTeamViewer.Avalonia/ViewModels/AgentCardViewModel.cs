using System.Collections.Specialized;
using System.ComponentModel;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.ViewModels;

/// <summary>Compact card for one agent: name, tag, status glyph, elapsed, last three lines.</summary>
public sealed class AgentCardViewModel : ObservableObject
{
    private readonly AgentState _agent;
    private readonly Func<long> _now;
    private string _elapsed = "";
    private IReadOnlyList<string> _lastLines = Array.Empty<string>();
    private int _depth;
    private bool _isExpanded;
    private LogViewModel? _log;
    private bool _isDismissing;
    private bool _isRemoving;
    private bool _sessionAllowsDismiss;

    public AgentCardViewModel(AgentState agent, Func<long> nowProvider)
    {
        _agent = agent;
        _now = nowProvider;
        _agent.PropertyChanged += OnAgentChanged;
        _agent.Blocks.CollectionChanged += OnBlocksChanged;
        foreach (var b in _agent.Blocks) b.PropertyChanged += OnBlockChanged;
        RefreshLastLines();
        Tick();
    }

    public AgentState Agent => _agent;
    public string Id => _agent.Id;
    public string? ParentId => _agent.ParentId;
    public string Name => _agent.DisplayName;
    /// <summary>"teammate" for named teammates, otherwise the agent type.</summary>
    public string Tag => _agent.Tag;
    /// <summary>Status word for the StatusGlyph control (feed statuses plus working/thinking).</summary>
    /// <summary>Status word for StatusGlyph; with Mode=Ready it groups to green dot / gear / red dot.</summary>
    public string StatusGlyph => _isDismissing ? "tool" : _agent.Status == "dismissed" ? "gone" : _agent.Status;
    public string ActivityLine => _agent.ActivityLine ?? "";
    public bool IsFinished => _agent.IsFinished;
    public bool IsGone => _agent.RosterStatus == "gone";

    /// <summary>Time actually worked (sum of model turns, including the one in progress). Updated by <see cref="Tick"/>.</summary>
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }

    /// <summary>Short LLM model label ("opus-5-5"), or "model unknown" until the feed says.</summary>
    public string Model => _agent.ModelLabel;
    public bool HasModel => !string.IsNullOrEmpty(_agent.Model);

    /// <summary>The three most recent one-line summaries, oldest first.</summary>
    public IReadOnlyList<string> LastLines { get => _lastLines; private set => Set(ref _lastLines, value); }

    /// <summary>Nesting depth (0 = spawned by the lead).</summary>
    public int Depth { get => _depth; set { if (Set(ref _depth, value)) { Raise(nameof(IsNested)); Raise(nameof(Indent)); } } }
    public bool IsNested => _depth > 0;
    public double Indent => _depth * 22;

    /// <summary>A dismiss request was written (or accepted) and the agent has not yet been dismissed.</summary>
    public bool IsDismissing
    {
        get => _isDismissing;
        set
        {
            if (!Set(ref _isDismissing, value)) return;
            Raise(nameof(StatusGlyph)); Raise(nameof(CanDismiss)); RefreshLastLines();
        }
    }

    /// <summary>Set once the agent is dismissed: the card fades out and is then removed from the list.</summary>
    public bool IsRemoving { get => _isRemoving; set => Set(ref _isRemoving, value); }

    /// <summary>Whether the session being shown is live (never in --demo or for old recordings); set by the main view model.</summary>
    public bool SessionAllowsDismiss
    {
        get => _sessionAllowsDismiss;
        set { if (Set(ref _sessionAllowsDismiss, value)) Raise(nameof(CanDismiss)); }
    }

    /// <summary>The X button is enabled.</summary>
    public bool CanDismiss => _sessionAllowsDismiss && !_isDismissing && !_isRemoving && _agent.RosterStatus is not ("gone" or "dismissed");

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public LogViewModel Log => _log ??= new LogViewModel(_agent);

    public void Tick()
    {
        // Time worked, not time since spawn: completed turns plus the in-progress one.
        long now = Math.Max(_now(), _agent.LastEventT);
        long ms = _agent.WorkedAs(now);
        Elapsed = ms <= 0 && !_agent.IsWorking ? "" : LogBlock.FormatDuration(ms);
    }

    private void OnAgentChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AgentState.DisplayName): Raise(nameof(Name)); break;
            case nameof(AgentState.Tag): Raise(nameof(Tag)); break;
            case nameof(AgentState.Status): Raise(nameof(StatusGlyph)); Raise(nameof(IsFinished)); Raise(nameof(IsGone)); Raise(nameof(CanDismiss)); break;
            case nameof(AgentState.ActivityLine): Raise(nameof(ActivityLine)); break;
            case nameof(AgentState.ParentId): Raise(nameof(ParentId)); break;
            case nameof(AgentState.CompletedAt): Tick(); break;
            case nameof(AgentState.WorkedMs): case nameof(AgentState.CurrentTurnStartT): Tick(); break;
            case nameof(AgentState.ModelLabel): Raise(nameof(Model)); Raise(nameof(HasModel)); break;
        }
    }

    private void OnBlocksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (LogBlock b in e.NewItems) b.PropertyChanged += OnBlockChanged;
        if (e.OldItems is not null)
            foreach (LogBlock b in e.OldItems) b.PropertyChanged -= OnBlockChanged;
        RefreshLastLines();
    }

    private void OnBlockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogBlock.Summary) && sender is LogBlock b && IsRecent(b)) RefreshLastLines();
    }

    private bool IsRecent(LogBlock b)
    {
        var blocks = _agent.Blocks;
        for (int i = blocks.Count - 1, n = 0; i >= 0 && n < 4; i--, n++)
            if (ReferenceEquals(blocks[i], b)) return true;
        return false;
    }

    private void RefreshLastLines()
    {
        if (_isDismissing) { LastLines = new[] { "dismissing… waiting for its summary" }; return; }
        var blocks = _agent.Blocks;
        var lines = new List<string>(3);
        for (int i = blocks.Count - 1; i >= 0 && lines.Count < 3; i--)
        {
            var b = blocks[i];
            if (b.Kind == BlockKind.Notice) continue;         // notices are not activity
            var s = b.Summary;
            if (string.IsNullOrWhiteSpace(s)) continue;
            lines.Add(s);
        }
        lines.Reverse();
        LastLines = lines;
    }
}
