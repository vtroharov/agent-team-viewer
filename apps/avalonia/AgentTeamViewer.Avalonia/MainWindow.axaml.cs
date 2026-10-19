using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AgentTeamViewer.Controls;
using AgentTeamViewer.Model;
using AgentTeamViewer.ViewModels;
using AgentTeamViewer.Views;

namespace AgentTeamViewer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ConnectorLayer _connectors;
    private readonly DispatcherTimer _pulseTimer;
    private readonly Queue<(string from, string to, DateTime at)> _pulseQueue = new();
    private readonly CardDragController _drag;
    private bool _anchorsDirty;

    /// <summary>Parameterless constructor for the XAML runtime loader and previewer; the app uses the view-model overload.</summary>
    public MainWindow() : this(new MainViewModel(Dispatcher.UIThread)) { }

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Confirm = (title, message) => ConfirmDialog.ShowAsync(this, title, message);

        _connectors = Connectors;
        _connectors.AddEdge("main", "hub");
        _drag = new CardDragController(CardsList, vm, OnDragAnchorsChanged);

        // Cards raise their actions as bubbling routed events (item templates cannot carry handlers).
        CardsList.AddHandler(AgentCard.ClickedEvent, (_, e) => _vm.ToggleExpand(e.Card));
        CardsList.AddHandler(AgentCard.DragRequestedEvent, (_, e) => { if (e.Pointer is not null) _drag.Begin(e.View, e.Pointer); });
        CardsList.AddHandler(AgentCard.DismissRequestedEvent, (_, e) => _vm.RequestDismiss(e.Card));

        // Top-level cards hang off the hub (horizontal S-curve); nested cards branch down out of their parent card.
        _vm.EdgesChanged += SyncEdges;
        _vm.SessionReset += OnSessionReset;
        _vm.MessagePulsed += OnMessagePulsed;
        _vm.CardAdded += _ => MarkAnchorsDirty();
        _vm.PropertyChanged += OnVmChanged;
        _vm.Cards.CollectionChanged += (_, _) => MarkAnchorsDirty();

        _pulseTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _pulseTimer.Tick += DrainPulseQueue;

        // Every anchor is recomputed from the live layout on each pass; these hooks make sure a pass is
        // scheduled after any resize of the window, the panel, the hub, the cards list or the layer itself.
        ContentArea.LayoutUpdated += (_, _) => MarkAnchorsDirty();
        SizeChanged += (_, _) => MarkAnchorsDirty();
        LeadPanelView.SizeChanged += (_, _) => MarkAnchorsDirty();
        HubHost.SizeChanged += (_, _) => MarkAnchorsDirty();
        CardsList.SizeChanged += (_, _) => MarkAnchorsDirty();
        Connectors.SizeChanged += (_, _) => MarkAnchorsDirty();
        CardsScroller.ScrollChanged += (_, _) => MarkAnchorsDirty();
        SessionPicker.DropDownOpened += (_, _) => _vm.RefreshSessions();
        AddHandler(KeyDownEvent, Window_PreviewKeyDown, RoutingStrategies.Tunnel);
        Loaded += (_, _) => { MarkAnchorsDirty(); UpdateAnchors(); };
        Closed += (_, _) => _vm.Dispose();
    }

    // ------------------------------------------------------------------ edges

    private readonly HashSet<(string From, string To)> _edgeSet = new();
    private readonly HashSet<string> _agentAnchors = new(StringComparer.Ordinal);

    /// <summary>
    /// Derives the whole edge set from the current cards on every call: lead to hub, then for each card
    /// parent card to child (Branch) when the parent has a card, else hub to child. Edges whose endpoints
    /// no longer exist are removed, so none can outlive its card.
    /// </summary>
    private void SyncEdges()
    {
        var want = new Dictionary<(string, string), ConnectorShape> { [("main", "hub")] = ConnectorShape.Horizontal };
        foreach (var c in _vm.Cards)
        {
            var src = _vm.EdgeSource(c);
            want[(src, c.Id)] = src == "hub" ? ConnectorShape.Horizontal : ConnectorShape.Branch;
        }
        foreach (var e in _edgeSet.Where(e => !want.ContainsKey(e)).ToList())
        {
            _connectors.RemoveEdge(e.From, e.To);
            _edgeSet.Remove(e);
        }
        foreach (var (k, shape) in want)
        {
            _edgeSet.Add(k);
            _connectors.AddEdge(k.Item1, k.Item2, shape);
        }
        MarkAnchorsDirty();
    }

    /// <summary>Session switched: drop every edge, agent anchor and queued pulse, then keep only lead and hub.</summary>
    private void OnSessionReset()
    {
        _pulseQueue.Clear();
        _connectors.ClearEdges();
        _edgeSet.Clear();
        foreach (var id in _agentAnchors) _connectors.RemoveAnchor(id);
        _agentAnchors.Clear();
        SyncEdges();
    }

    // ------------------------------------------------------------------ anchors

    private void MarkAnchorsDirty()
    {
        if (_anchorsDirty) return;
        _anchorsDirty = true;
        Dispatcher.UIThread.Post(UpdateAnchors, DispatcherPriority.Loaded);
    }

    /// <summary>Registers "main" (lead panel right edge at the ring centre height), "hub" (ring centre) and every visible card (left-middle).</summary>
    private void UpdateAnchors()
    {
        _anchorsDirty = false;
        if (!IsLoaded) return;

        // The hub ring draws slightly inside its box; the layer offsets "hub" to the rim using HubRadius.
        _connectors.HubRadius = HubHost.RingRadius;
        var hub = _connectors.PointFrom(HubHost, 0.5, HubHost.RingCenterYFraction);
        if (!double.IsNaN(hub.X)) _connectors.SetAnchor("hub", hub);

        // Lead anchor: on the panel's right edge (Bounds excludes the 8 px margin, so x fraction 1 is the frame
        // line; +1 starts the stroke just outside the 1 px frame), at the ring centre's height so the line is straight.
        var main = _connectors.PointFrom(LeadPanelView, 1, 0.5);
        if (!double.IsNaN(main.X))
        {
            double y = double.IsNaN(hub.Y) ? main.Y : hub.Y;
            var frameTop = _connectors.PointFrom(LeadPanelView, 1, 0);
            var frameBottom = _connectors.PointFrom(LeadPanelView, 1, 1);
            if (!double.IsNaN(frameTop.Y) && !double.IsNaN(frameBottom.Y)) y = Math.Clamp(y, frameTop.Y, frameBottom.Y);
            _connectors.SetAnchor("main", new Point(main.X + 1, y));
        }

        // Cards: edges arrive at the left-middle and leave (for nested children) from a point on the
        // bottom edge near the left. Only cards inside the scroll viewport get an anchor; the rest are
        // removed so no edge is drawn to an off-screen point.
        double top = CardsScroller.TranslatePoint(new Point(0, 0), _connectors)?.Y ?? 0;
        double bottom = top + CardsScroller.Viewport.Height;
        var live = _vm.Cards.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _agentAnchors.Where(id => !live.Contains(id)).ToList())
        {
            _connectors.RemoveAnchor(gone);
            _agentAnchors.Remove(gone);
        }
        for (int i = 0; i < _vm.Cards.Count; i++)
        {
            var card = _vm.Cards[i];
            _agentAnchors.Add(card.Id);
            var view = (CardsList.ContainerFromIndex(i) as ContentPresenter)?.Child as AgentCard;
            if (view is null || !view.IsLoaded) continue;
            var inPoint = _connectors.PointFrom(view.AnchorElement, 0, 0.5);
            var outPoint = _connectors.PointFrom(view.AnchorElement, 0, 1);
            outPoint = new Point(outPoint.X + 11, outPoint.Y);       // inside the 22 px indent of a nested child
            if (double.IsNaN(inPoint.Y) || inPoint.Y < top - 4 || inPoint.Y > bottom + 4) _connectors.RemoveAnchor(card.Id);
            else _connectors.SetAnchor(card.Id, inPoint, outPoint);
        }
    }

    // ------------------------------------------------------------------ pulses

    /// <summary>
    /// Routes a message pulse along the drawn topology: main ↔ hub ↔ top-level card, and parent
    /// card → nested card. Siblings go via the hub; pairs with no edge get the layer's temporary
    /// dashed edge. Segments are fired one after another.
    /// </summary>
    private void OnMessagePulsed(string from, string to)
    {
        if (from == to) return;
        var route = Route(from, to);
        var now = DateTime.UtcNow;
        for (int i = 0; i < route.Count - 1; i++)
            _pulseQueue.Enqueue((route[i], route[i + 1], now + TimeSpan.FromMilliseconds(i * 520)));
        if (!_pulseTimer.IsEnabled) _pulseTimer.Start();
    }

    private void DrainPulseQueue(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        while (_pulseQueue.Count > 0 && _pulseQueue.Peek().at <= now)
        {
            var (f, t, _) = _pulseQueue.Dequeue();
            _connectors.Pulse(f, t);
        }
        if (_pulseQueue.Count == 0) _pulseTimer.Stop();
    }

    private List<string> Route(string from, string to)
    {
        var up = Chain(from);                     // from ... main
        var down = Chain(to);                     // to ... main
        var downSet = new HashSet<string>(down, StringComparer.Ordinal);
        int lcaIndex = up.FindIndex(downSet.Contains);
        if (lcaIndex < 0) return new List<string> { from, to };
        var lca = up[lcaIndex];

        var route = new List<string>();
        for (int i = 0; i <= lcaIndex; i++) route.Add(up[i]);
        int j = down.IndexOf(lca) - 1;
        for (; j >= 0; j--) route.Add(down[j]);

        // The lead is wired to the hub, and the hub to top-level cards: insert the hub on main↔card hops,
        // and replace a pass-through "main" (sibling messages) with the hub.
        var wired = new List<string>();
        for (int i = 0; i < route.Count; i++)
        {
            var k = route[i];
            bool endpoint = i == 0 || i == route.Count - 1;
            if (k == AgentState.MainId)
            {
                if (endpoint)
                {
                    if (i == 0) { wired.Add(k); wired.Add("hub"); }
                    else { wired.Add("hub"); wired.Add(k); }
                }
                else wired.Add("hub");
            }
            else wired.Add(k);
        }
        // De-duplicate adjacent repeats (e.g. hub,hub).
        var result = new List<string>();
        foreach (var k in wired) if (result.Count == 0 || result[^1] != k) result.Add(k);
        return result;
    }

    private List<string> Chain(string id)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cur = id;
        while (cur is not null && seen.Add(cur))
        {
            chain.Add(cur);
            if (cur == AgentState.MainId) break;
            var next = _vm.Model.Find(cur)?.ParentId ?? AgentState.MainId;
            while (next != AgentState.MainId && _vm.Model.Find(next) is { IsDismissed: true } gone && seen.Add(next)) next = gone.ParentId ?? AgentState.MainId;
            cur = next;
        }
        if (chain[^1] != AgentState.MainId) chain.Add(AgentState.MainId);
        return chain;
    }

    // ------------------------------------------------------------------ expand / collapse

    // The card itself grows in place (Views/AgentCard) and the layout change re-marks the anchors via LayoutUpdated.
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsCatchingUp)) AgentCard.AnimateNew = !_vm.IsCatchingUp;
    }

    private void Window_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_drag.IsDragging)
        {
            _drag.Cancel();
            e.Handled = true;
        }
        else if (_vm.IsCardExpanded)
        {
            _vm.Collapse();
            e.Handled = true;
        }
    }

    /// <summary>During a drag the cards move by RenderTransform, which LayoutUpdated does not see: re-anchor directly.</summary>
    private void OnDragAnchorsChanged()
    {
        _anchorsDirty = true;
        UpdateAnchors();
    }
}
