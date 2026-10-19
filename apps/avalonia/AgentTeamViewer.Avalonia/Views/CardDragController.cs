using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AgentTeamViewer.ViewModels;

namespace AgentTeamViewer.Views;

/// <summary>
/// Vertical drag-to-reorder for the agent cards column. A card (with its nested descendants as
/// one block) follows the pointer vertically, clamped to the list; its siblings slide apart to
/// show the drop slot; release reorders through <see cref="MainViewModel.ReorderSiblings"/>,
/// Esc cancels. Only siblings under the same parent are reordered. The owner is told on every
/// move so connector anchors can follow the moving cards.
/// </summary>
public sealed class CardDragController
{
    private static readonly TimeSpan ShiftDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ReturnDuration = TimeSpan.FromMilliseconds(180);

    private readonly ItemsControl _list;
    private readonly MainViewModel _vm;
    private readonly Action _anchorsChanged;

    private sealed class Block
    {
        public AgentCardViewModel Card = null!;
        public int First;                 // index in Cards
        public int Count;                 // block length (card + descendants)
        public double Top;                // layout top in list coordinates, before any transform
        public double Height;
        public List<Control> Containers = new();
        public TranslateTransform Shift = new();
        public double ShiftTarget;        // 0, +dragHeight or -dragHeight
        public Tween? ShiftTween;
    }

    private sealed class DragState
    {
        public Block Dragged = null!;
        public List<Block> Others = new();   // siblings in current order, excluding the dragged one
        public int OrigIndex;                // index of the dragged among all siblings
        public int Slot;                     // insert position among Others
        public double StartPointerY;
        public TranslateTransform Translate = new();
        public ScaleTransform Scale = new(1, 1);
        public AgentCard View = null!;
        public Tween? ScaleTween;
        public IPointer Pointer = null!;
    }

    private DragState? _drag;

    public CardDragController(ItemsControl list, MainViewModel vm, Action anchorsChanged)
    {
        _list = list;
        _vm = vm;
        _anchorsChanged = anchorsChanged;
    }

    public bool IsDragging => _drag is not null;

    /// <summary>Begins a drag from a card whose title row was pressed and moved.</summary>
    public void Begin(AgentCard view, PointerEventArgs e)
    {
        if (_drag is not null || !_vm.IsDragEnabled) return;
        if (view.DataContext is not AgentCardViewModel card) return;

        var siblings = _vm.SiblingsOf(card);
        if (siblings.Count < 2) return;                 // nothing to reorder against

        var blocks = siblings.Select(MakeBlock).Where(b => b is not null).Select(b => b!).ToList();
        var dragged = blocks.FirstOrDefault(b => ReferenceEquals(b.Card, card));
        if (dragged is null) return;

        var state = new DragState
        {
            Dragged = dragged,
            Others = blocks.Where(b => !ReferenceEquals(b, dragged)).ToList(),
            OrigIndex = blocks.IndexOf(dragged),
            StartPointerY = e.GetPosition(_list).Y,
            View = view,
            Pointer = e.Pointer,
        };
        state.Slot = state.OrigIndex;

        // Lift the dragged block: translate + slight scale, on top, bright frame.
        var group = new TransformGroup();
        group.Children.Add(state.Scale);
        group.Children.Add(state.Translate);
        foreach (var c in dragged.Containers)
        {
            c.RenderTransformOrigin = RelativePoint.Center;
            c.RenderTransform = group;
            c.ZIndex = 100;
        }
        state.ScaleTween = Tween.Start(_list, 1, 1.02, ShiftDuration, s => { state.Scale.ScaleX = s; state.Scale.ScaleY = s; });
        view.SetLifted(true);

        foreach (var b in state.Others)
            foreach (var c in b.Containers) c.RenderTransform = b.Shift;

        _drag = state;
        e.Pointer.Capture(_list);
        _list.PointerMoved += OnPointerMoved;
        _list.PointerReleased += OnPointerReleased;
        _list.PointerCaptureLost += OnCaptureLost;
        _list.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        e.Handled = true;
    }

    /// <summary>Esc during a drag: slide everything back, no reorder.</summary>
    public void Cancel()
    {
        if (_drag is null) return;
        Finish(commit: false);
    }

    private Block? MakeBlock(AgentCardViewModel card)
    {
        int first = _vm.Cards.IndexOf(card);
        int count = _vm.BlockSize(card);
        if (first < 0 || count == 0) return null;
        var b = new Block { Card = card, First = first, Count = count };
        double top = double.MaxValue, bottom = double.MinValue;
        for (int i = first; i < first + count; i++)
        {
            if (_list.ContainerFromIndex(i) is not Control c) continue;
            b.Containers.Add(c);
            var p = c.TranslatePoint(new Point(0, 0), _list) ?? default;
            top = Math.Min(top, p.Y);
            bottom = Math.Max(bottom, p.Y + c.Bounds.Height);
        }
        if (b.Containers.Count == 0) return null;
        b.Top = top;
        b.Height = bottom - top;
        return b;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_drag is null) return;
        if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) { Finish(commit: true); return; }

        var d = _drag.Dragged;
        double dy = e.GetPosition(_list).Y - _drag.StartPointerY;
        // Clamp to the list bounds: the block never leaves the column.
        double minDy = -d.Top;
        double maxDy = Math.Max(minDy, _list.Bounds.Height - (d.Top + d.Height));
        dy = Math.Clamp(dy, minDy, maxDy);
        _drag.Translate.Y = dy;

        // Drop slot = number of other sibling blocks whose centre lies above the dragged block's leading
        // edge: its top while moving up, its bottom while moving down. Using the leading edge (not the
        // centre) lets a tall block (a parent with children) pass a short card before hitting the clamp.
        double leading = dy < 0 ? d.Top + dy : d.Top + dy + d.Height;
        int slot = _drag.Others.Count(o => o.Top + o.Height / 2 < leading);
        // Never report a slot the block has not actually crossed: when moving up only slots at or below
        // the original index can shrink, and vice versa.
        slot = dy < 0 ? Math.Min(slot, _drag.OrigIndex) : Math.Max(slot, _drag.OrigIndex);
        if (slot != _drag.Slot)
        {
            _drag.Slot = slot;
            UpdateShifts();
        }
        _anchorsChanged();
    }

    /// <summary>Others above the dragged that end up below it move down by its height, and vice versa.</summary>
    private void UpdateShifts()
    {
        if (_drag is null) return;
        double h = _drag.Dragged.Height;
        for (int j = 0; j < _drag.Others.Count; j++)
        {
            var o = _drag.Others[j];
            bool wasAbove = j < _drag.OrigIndex;
            double target = wasAbove
                ? (j >= _drag.Slot ? h : 0)
                : (j < _drag.Slot ? -h : 0);
            if (Math.Abs(target - o.ShiftTarget) < 0.5) continue;
            o.ShiftTarget = target;
            o.ShiftTween?.Cancel();
            o.ShiftTween = Tween.Start(_list, o.Shift.Y, target, ShiftDuration, y => { o.Shift.Y = y; _anchorsChanged(); }, Tween.CubicOut);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => Finish(commit: true);

    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        // Capture stolen (e.g. a dialog): treat as cancel rather than leaving transforms around.
        if (_drag is not null) Finish(commit: false);
    }

    private void Finish(bool commit)
    {
        var s = _drag;
        if (s is null) return;
        _drag = null;

        _list.PointerMoved -= OnPointerMoved;
        _list.PointerReleased -= OnPointerReleased;
        _list.PointerCaptureLost -= OnCaptureLost;
        if (ReferenceEquals(s.Pointer.Captured, _list)) s.Pointer.Capture(null);
        _list.Cursor = null;
        s.View.SetLifted(false);
        s.ScaleTween?.Cancel();

        bool reordered = commit && s.Slot != s.OrigIndex;
        if (reordered)
        {
            // Apply the new order; the layout then matches the shifted positions, so clear transforms at once.
            var order = new List<AgentCardViewModel>(s.Others.Count + 1);
            for (int j = 0; j < s.Others.Count; j++)
            {
                if (j == s.Slot) order.Add(s.Dragged.Card);
                order.Add(s.Others[j].Card);
            }
            if (s.Slot >= s.Others.Count) order.Add(s.Dragged.Card);
            ClearTransforms(s);
            _vm.ReorderSiblings(order);
            _anchorsChanged();
            return;
        }

        // Cancel or dropped back in place: slide everything home, then clear.
        double y0 = s.Translate.Y, scale0 = s.Scale.ScaleX;
        Tween.Start(_list, 0, 1, ReturnDuration, t =>
        {
            s.Translate.Y = y0 * (1 - t);
            double sc = scale0 + (1 - scale0) * t;
            s.Scale.ScaleX = sc; s.Scale.ScaleY = sc;
            _anchorsChanged();
        }, Tween.CubicOut, () => { ClearTransforms(s); _anchorsChanged(); });
        foreach (var o in s.Others)
        {
            o.ShiftTween?.Cancel();
            double from = o.Shift.Y;
            o.ShiftTween = Tween.Start(_list, from, 0, ReturnDuration, y => o.Shift.Y = y, Tween.CubicOut);
        }
        _anchorsChanged();
    }

    private static void ClearTransforms(DragState s)
    {
        foreach (var c in s.Dragged.Containers)
        {
            c.RenderTransform = null;
            c.ZIndex = 0;
        }
        foreach (var o in s.Others)
        {
            o.ShiftTween?.Cancel();
            o.Shift.Y = 0;
            foreach (var c in o.Containers) c.RenderTransform = null;
        }
    }
}
