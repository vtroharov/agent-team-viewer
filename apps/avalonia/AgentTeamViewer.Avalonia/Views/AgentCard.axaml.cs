using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AgentTeamViewer.ViewModels;

namespace AgentTeamViewer.Views;

public partial class AgentCard : UserControl
{
    private const double ExpandedFraction = 0.58;   // of the cards column viewport
    private const double ExpandedMinHeight = 320;
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>Set false by the window while catching up on an existing feed so hundreds of cards do not all animate.</summary>
    public static bool AnimateNew { get; set; } = true;

    // Cards are created by an item template, which cannot carry handlers, so the three actions bubble as
    // routed events and the window listens on the cards list.

    /// <summary>Bubbles when the card is clicked; the window toggles expansion.</summary>
    public static readonly RoutedEvent<CardEventArgs> ClickedEvent =
        RoutedEvent.Register<AgentCard, CardEventArgs>(nameof(Clicked), RoutingStrategies.Bubble);
    public event EventHandler<CardEventArgs> Clicked { add => AddHandler(ClickedEvent, value); remove => RemoveHandler(ClickedEvent, value); }

    /// <summary>Bubbles when the title row is pressed and moved past the drag threshold; the window runs the drag.</summary>
    public static readonly RoutedEvent<CardEventArgs> DragRequestedEvent =
        RoutedEvent.Register<AgentCard, CardEventArgs>(nameof(DragRequested), RoutingStrategies.Bubble);
    public event EventHandler<CardEventArgs> DragRequested { add => AddHandler(DragRequestedEvent, value); remove => RemoveHandler(DragRequestedEvent, value); }

    /// <summary>Bubbles when the X is clicked; the window asks the view model to dismiss the agent.</summary>
    public static readonly RoutedEvent<CardEventArgs> DismissRequestedEvent =
        RoutedEvent.Register<AgentCard, CardEventArgs>(nameof(DismissRequested), RoutingStrategies.Bubble);
    public event EventHandler<CardEventArgs> DismissRequested { add => AddHandler(DismissRequestedEvent, value); remove => RemoveHandler(DismissRequestedEvent, value); }

    private void Raise(RoutedEvent<CardEventArgs> ev, PointerEventArgs? pointer = null)
    {
        if (DataContext is AgentCardViewModel vm)
            RaiseEvent(new CardEventArgs(ev, this) { Card = vm, View = this, Pointer = pointer });
    }

    private readonly TranslateTransform _slide = new();
    private Point? _pressPoint;
    private bool _lifted;
    private INotifyPropertyChanged? _subscribed;

    /// <summary>The element whose left edge is the connector anchor.</summary>
    public Control AnchorElement => CardBorder;

    private ScrollViewer? _scroller;      // the cards column (x:Name CardsScroller in MainWindow)
    private Tween? _heightTween;          // the running height tween on the embedded log
    private int _expandGeneration;        // invalidates the completion of a superseded tween
    private bool _animating;              // a height tween is running on the embedded log

    public AgentCard()
    {
        InitializeComponent();
        RenderTransform = _slide;

        TitleRow.AddHandler(PointerPressedEvent, TitleRow_PointerPressed, RoutingStrategies.Tunnel);
        TitleRow.AddHandler(PointerMovedEvent, TitleRow_PointerMoved, RoutingStrategies.Tunnel);
        TitleRow.AddHandler(PointerReleasedEvent, (_, _) => _pressPoint = null, RoutingStrategies.Tunnel);
        CardBorder.PointerReleased += Card_PointerReleased;
        // Clicks inside the embedded log (tool rows, scrollbar, text) must not toggle the card.
        ExpandedRegion.PointerReleased += (_, e) => e.Handled = true;
        DismissButton.Click += DismissButton_Click;

        Loaded += OnLoaded;
        Unloaded += (_, _) => DetachScroller();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        AttachScroller();
        ApplyExpanded(animate: false);
        ApplyModelChip();

        if (!AnimateNew) return;
        Opacity = 0;
        Tween.Start(this, 0, 1, TimeSpan.FromMilliseconds(380), t =>
        {
            Opacity = Math.Min(1, t * 380 / 320);
            _slide.X = 28 * (1 - Tween.CubicOut(t));
        });
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnVmChanged;
        _subscribed = DataContext as INotifyPropertyChanged;
        if (_subscribed is not null) _subscribed.PropertyChanged += OnVmChanged;
        ApplySelected();
        ApplyModelChip();
        if (IsLoaded) ApplyExpanded(animate: false);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentCardViewModel.IsExpanded))
        {
            ApplySelected();
            ApplyExpanded(animate: AnimateNew);
        }
        else if (e.PropertyName == nameof(AgentCardViewModel.HasModel))
        {
            ApplyModelChip();
        }
        else if (e.PropertyName == nameof(AgentCardViewModel.IsDismissing))
        {
            CardBorder.Opacity = DataContext is AgentCardViewModel { IsDismissing: true } ? 0.55 : 1;
        }
        else if (e.PropertyName == nameof(AgentCardViewModel.IsRemoving) && DataContext is AgentCardViewModel { IsRemoving: true })
        {
            FadeOut();
        }
    }

    private void ApplyModelChip() =>
        ModelChip.Classes.Set("NoModel", DataContext is AgentCardViewModel { HasModel: false });

    /// <summary>The theme's TerminalCard style shows the hovered look with class "selected" (expanded or being dragged).</summary>
    private void ApplySelected()
    {
        bool expanded = DataContext is AgentCardViewModel { IsExpanded: true };
        CardBorder.Classes.Set("selected", _lifted || expanded);
        CardBorder.Classes.Set("expanded", expanded);
    }

    /// <summary>Lifted look while dragging: bright frame and glow from the card style.</summary>
    public void SetLifted(bool lifted)
    {
        _lifted = lifted;
        ApplySelected();
    }

    // ------------------------------------------------------------------ dismiss

    /// <summary>The agent was dismissed: fade the card and fold its height away before the list drops it.</summary>
    private void FadeOut()
    {
        if (!AnimateNew || !IsLoaded) return;
        IsHitTestVisible = false;
        double h = Bounds.Height, startOpacity = Opacity;
        var margin = Margin;
        ClipToBounds = true;
        Tween.Start(this, 0, 1, TimeSpan.FromMilliseconds(260), t => Opacity = startOpacity * (1 - t));
        Tween.Start(this, 0, 1, TimeSpan.FromMilliseconds(380), t =>
        {
            Height = h * (1 - t);
            Margin = new Thickness(margin.Left, margin.Top * (1 - t), margin.Right, margin.Bottom * (1 - t));
        }, Tween.CubicIn, delay: TimeSpan.FromMilliseconds(40));
    }

    private void DismissButton_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Raise(DismissRequestedEvent);
    }

    // ------------------------------------------------------------------ drag handle

    private void TitleRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        bool onDismiss = e.Source is Visual s && (ReferenceEquals(s, DismissButton) || DismissButton.IsVisualAncestorOf(s));
        bool left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        _pressPoint = onDismiss || !left ? null : e.GetPosition(this);
    }

    private void TitleRow_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressPoint is not Point p || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var now = e.GetPosition(this);
        if (Math.Abs(now.Y - p.Y) < 4 && Math.Abs(now.X - p.X) < 4) return;
        _pressPoint = null;
        Raise(DragRequestedEvent, e);
    }

    private void Card_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || e.Handled) return;
        if (!new Rect(CardBorder.Bounds.Size).Contains(e.GetPosition(CardBorder))) return;
        if (DataContext is AgentCardViewModel)
        {
            Raise(ClickedEvent);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ in-place expansion

    private bool IsVmExpanded => DataContext is AgentCardViewModel { IsExpanded: true };

    private void AttachScroller()
    {
        DetachScroller();
        _scroller = this.FindAncestorOfType<ScrollViewer>();
        if (_scroller is not null) _scroller.SizeChanged += OnScrollerSizeChanged;
    }

    private void DetachScroller()
    {
        if (_scroller is not null) _scroller.SizeChanged -= OnScrollerSizeChanged;
        _scroller = null;
    }

    /// <summary>Window resized: an expanded log follows the viewport without animating.</summary>
    private void OnScrollerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ExpandedRegion.IsVisible && IsVmExpanded && !_animating)
            SetLogHeight(TargetLogHeight());
    }

    private double TargetLogHeight()
    {
        double viewport = _scroller?.Viewport.Height ?? 0;
        return Math.Max(ExpandedMinHeight, Math.Round(viewport * ExpandedFraction));
    }

    /// <summary>Cancels any running height tween and pins the height.</summary>
    private void SetLogHeight(double height)
    {
        _heightTween?.Cancel();
        _heightTween = null;
        EmbeddedLog.Height = height;
        _animating = false;
    }

    private void ApplyExpanded(bool animate)
    {
        int gen = ++_expandGeneration;
        bool expanded = IsVmExpanded;

        if (expanded)
        {
            ExpandedRegion.IsVisible = true;
            double target = TargetLogHeight();
            if (!animate)
            {
                SetLogHeight(target);
                ScrollIntoView();
                return;
            }

            SetLogHeight(0);
            _animating = true;
            _heightTween = Tween.Start(this, 0, target, ExpandDuration, h => EmbeddedLog.Height = h, Tween.CubicOut, () =>
            {
                if (gen != _expandGeneration) return;
                SetLogHeight(target);
                ScrollIntoView();
            });
            // Start following the card as soon as it begins to grow so the title row stays in view.
            Dispatcher.UIThread.Post(() => { if (gen == _expandGeneration) this.BringIntoView(); }, DispatcherPriority.Loaded);
        }
        else
        {
            if (!ExpandedRegion.IsVisible) { SetLogHeight(0); return; }
            if (!animate)
            {
                SetLogHeight(0);
                ExpandedRegion.IsVisible = false;
                return;
            }

            double from = double.IsNaN(EmbeddedLog.Height) ? EmbeddedLog.Bounds.Height : EmbeddedLog.Height;
            _heightTween?.Cancel();
            _animating = true;
            _heightTween = Tween.Start(this, from, 0, CollapseDuration, h => EmbeddedLog.Height = h, Tween.CubicIn, () =>
            {
                if (gen != _expandGeneration) return;
                SetLogHeight(0);
                ExpandedRegion.IsVisible = false;
            });
        }
    }

    /// <summary>Scrolls the cards column so the whole expanded card is visible (after layout has applied the new height).</summary>
    private void ScrollIntoView()
    {
        Dispatcher.UIThread.Post(() => { if (IsVmExpanded && IsLoaded) this.BringIntoView(); }, DispatcherPriority.Loaded);
    }
}

/// <summary>Payload of the card action events: the card's view model, its view, and (for a drag) the pointer event.</summary>
public sealed class CardEventArgs : RoutedEventArgs
{
    public CardEventArgs(RoutedEvent routedEvent, object source) : base(routedEvent, source) { }
    public AgentCardViewModel Card { get; init; } = null!;
    public AgentCard View { get; init; } = null!;
    public PointerEventArgs? Pointer { get; init; }
}
