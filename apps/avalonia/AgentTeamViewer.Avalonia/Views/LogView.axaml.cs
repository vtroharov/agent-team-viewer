using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>
/// A terminal-style log of <see cref="LogBlock"/>s with auto-scroll that pauses when the user
/// scrolls up and resumes when they return to the bottom (or click the "new output" chip).
/// </summary>
public partial class LogView : UserControl
{
    private const double BottomSlack = 24;   // px from the bottom that still counts as "at the bottom"
    private bool _autoScroll = true;
    private bool _pendingNewContent;
    private bool _toggling;                     // a block was just expanded or collapsed: this layout growth is not new output
    private bool _pressed;                     // the pointer is down inside the log (a selection drag may be starting)
    private bool _heldWhileFollowing;          // auto-scroll was on when a press or selection paused it
    private Point _pressPos;
    private int _pressClicks;
    private readonly HashSet<LogText> _selected = new();

    /// <summary>Auto-scroll pauses while the user is pressing in the log or has text selected.</summary>
    private bool HoldsScroll => _pressed || _selected.Count > 0;

    public LogView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += Scroller_ScrollChanged;
        Scroller.AddHandler(PointerPressedEvent, Items_PointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(PointerReleasedEvent, Items_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Scroller.AddHandler(PointerCaptureLostEvent, (_, _) => { _pressed = false; ReleaseHold(); }, RoutingStrategies.Bubble, handledEventsToo: true);
        Items.AddHandler(LogText.SelectionChangedEvent, Items_SelectionChanged);
        DataContextChanged += (_, _) =>
        {
            // A different log: start at its end, following.
            _autoScroll = true;
            _pendingNewContent = false;
            ResumeChip.IsVisible = false;
            Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);
        };
    }


    private void Scroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        bool contentGrew = e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0;
        if (!contentGrew)
        {
            // The user moved the thumb / wheel: follow only if they are at (or near) the bottom.
            double scrollable = Scroller.Extent.Height - Scroller.Viewport.Height;
            _autoScroll = Scroller.Offset.Y >= scrollable - BottomSlack;
            if (_autoScroll && _pendingNewContent)
            {
                _pendingNewContent = false;
                ResumeChip.IsVisible = false;
            }
            return;
        }

        if (_toggling) return;

        if (HoldsScroll)
        {
            // Output keeps arriving while the user selects: hold the view still and offer the chip.
            if (_autoScroll) { _heldWhileFollowing = true; _autoScroll = false; }
            if (e.ExtentDelta.Y > 0) { _pendingNewContent = true; ResumeChip.IsVisible = true; }
            return;
        }

        if (_autoScroll)
        {
            Scroller.ScrollToEnd();
        }
        else if (e.ExtentDelta.Y > 0)
        {
            _pendingNewContent = true;
            ResumeChip.IsVisible = true;
        }
    }

    private void ResumeChip_Released(object? sender, PointerReleasedEventArgs e)
    {
        _heldWhileFollowing = false;
        _autoScroll = true;
        _pendingNewContent = false;
        ResumeChip.IsVisible = false;
        Scroller.ScrollToEnd();
        e.Handled = true;
    }

    private void Items_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = e.GetCurrentPoint(Items).Properties.IsLeftButtonPressed;
        _pressPos = e.GetPosition(Items);
        _pressClicks = e.ClickCount;
        // A press anywhere in the log other than on a selected text clears that selection (as in a browser).
        var target = (e.Source as Visual)?.FindAncestorOfType<LogText>(includeSelf: true);
        if (e.GetCurrentPoint(Items).Properties.IsLeftButtonPressed)
            foreach (var t in _selected.Where(t => !ReferenceEquals(t, target)).ToList()) t.ClearSelection();
    }

    private void Items_SelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not LogText t) return;
        if (t.HasSelection) _selected.Add(t); else _selected.Remove(t);
        ReleaseHold();
    }

    /// <summary>Once nothing is pressed or selected any more, follow the output again if the view was following before.</summary>
    /// <summary>Expanding or collapsing a block must not make the view jump to the end: the offset stays put for the layout pass.</summary>
    private void Toggle(Action change, Func<bool> expanded)
    {
        _toggling = true;
        _autoScroll = false;
        _heldWhileFollowing = false;
        var offset = Scroller.Offset;
        change();
        // Keep the view exactly where it was while the layout settles (focusing the clicked text or the new
        // extent could otherwise scroll it), then follow the output again only if it still sits at the end.
        void Restore() => Scroller.Offset = new Vector(offset.X, Math.Min(offset.Y, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height)));
        Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(() =>
        {
            Restore();
            _toggling = false;
            // An expand keeps the view paused: its body may still be laying out (Markdown renders on load) and must not drag the view along.
            _autoScroll = !expanded() && Scroller.Offset.Y >= Scroller.Extent.Height - Scroller.Viewport.Height - BottomSlack;
        }, DispatcherPriority.Background);
    }

    private void ReleaseHold()
    {
        if (HoldsScroll || !_heldWhileFollowing) return;
        _heldWhileFollowing = false;
        _autoScroll = true;
        _pendingNewContent = false;
        ResumeChip.IsVisible = false;
        Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    /// <summary>A click on a tool one-liner toggles its input/result. Templates cannot carry handlers, so this listens on the list. A drag, a double-click or a selection is not a click.</summary>
    private void Items_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = false;
        var end = e.GetPosition(Items);
        bool click = _pressClicks == 1 && _selected.Count == 0 && Math.Abs(end.X - _pressPos.X) <= 3 && Math.Abs(end.Y - _pressPos.Y) <= 3;
        if (e.InitialPressMouseButton == MouseButton.Left && click)
        {
            for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, Items); v = v.GetVisualParent())
            {
                if (v is Border { DataContext: LogBlock b } row)
                {
                    if (row.Classes.Contains("ToolRow")) b.IsExpanded = !b.IsExpanded;
                    else if (row.Classes.Contains("DisclosureRow")) Toggle(() => b.IsOpen = !b.IsOpen, () => b.IsOpen);
                    else if (row.Classes.Contains("NoteRow")) Toggle(() => b.NoteOpen = !b.NoteOpen, () => b.NoteOpen);
                    else continue;
                    e.Handled = true;
                    break;
                }
            }
        }
        ReleaseHold();
    }
}
