using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace AgentTeamViewer.Controls;

/// <summary>How an edge leaves its source anchor.</summary>
public enum ConnectorShape
{
    /// <summary>Leaves the source heading right, arrives at the target heading left. Default.</summary>
    Horizontal,
    /// <summary>Leaves the source heading down, arrives at the target heading left. For nested cards.</summary>
    Branch,
}

public sealed class ConnectorPulseEventArgs : EventArgs
{
    public ConnectorPulseEventArgs(string fromKey, string toKey)
    {
        FromKey = fromKey;
        ToKey = toKey;
    }

    public string FromKey { get; }
    public string ToKey { get; }
}

/// <summary>
/// Transparent overlay that draws glowing Bezier connectors between named anchors and
/// animates message pulses along them. All points are in this element's coordinate space.
/// Internally a Panel of five stacked layers (glow, line, idle flow, hot overlay, pulses) so the
/// glow can use a real blur effect and only the layers that change are re-recorded each frame.
/// </summary>
public class ConnectorLayer : Panel
{
    private const int TrailLength = 8;
    private const double TrailStep = 0.028;
    private const int FlattenSegments = 40;
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(260);

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<ConnectorLayer, IBrush?>(nameof(LineBrush), Tokens.Solid(Tokens.Cyan));

    public static readonly StyledProperty<IBrush?> GlowBrushProperty =
        AvaloniaProperty.Register<ConnectorLayer, IBrush?>(nameof(GlowBrush), Tokens.Solid(Tokens.CyanGlow));

    public static readonly StyledProperty<double> LineThicknessProperty =
        AvaloniaProperty.Register<ConnectorLayer, double>(nameof(LineThickness), 1.5);

    public static readonly StyledProperty<double> GlowThicknessProperty =
        AvaloniaProperty.Register<ConnectorLayer, double>(nameof(GlowThickness), 9.0);

    /// <summary>Idle-flow cycles per second. 0 disables it.</summary>
    public static readonly StyledProperty<double> FlowSpeedProperty =
        AvaloniaProperty.Register<ConnectorLayer, double>(nameof(FlowSpeed), 1.0);

    public static readonly StyledProperty<TimeSpan> PulseDurationProperty =
        AvaloniaProperty.Register<ConnectorLayer, TimeSpan>(nameof(PulseDuration), TimeSpan.FromMilliseconds(700));

    public static readonly StyledProperty<Color> PulseColorProperty =
        AvaloniaProperty.Register<ConnectorLayer, Color>(nameof(PulseColor), Tokens.TextPrimary);

    /// <summary>
    /// When &gt; 0, an anchor keyed "hub" is treated as the ring's centre and edges attach to
    /// its left rim (as a target) or right rim (as a source).
    /// </summary>
    public static readonly StyledProperty<double> HubRadiusProperty =
        AvaloniaProperty.Register<ConnectorLayer, double>(nameof(HubRadius), 0.0);

    /// <summary>Blur the glow strokes for a softer look. Turn off on weak GPUs.</summary>
    public static readonly StyledProperty<bool> SoftGlowProperty =
        AvaloniaProperty.Register<ConnectorLayer, bool>(nameof(SoftGlow), true);

    private readonly Dictionary<string, Anchor> _anchors = new(StringComparer.Ordinal);
    private readonly Dictionary<(string From, string To), Edge> _edgeMap = new();
    private readonly List<Edge> _edges = new();
    private readonly List<PulseRun> _pulses = new();
    private readonly Stack<PulseRun> _pulsePool = new();
    private int _fadingEdges;

    private readonly Layer _glowLayer, _lineLayer, _flowLayer, _hotLayer, _pulseLayer;
    private readonly FrameDriver _frames;

    private ImmutablePen _glowPen = null!, _linePen = null!, _flowPen = null!, _hotPen = null!, _tempPen = null!, _tempGlowPen = null!;
    private IBrush[] _ghostBrushes = Array.Empty<IBrush>();
    private IBrush _haloBrush = null!;
    private static readonly IBrush CoreBrush = Tokens.Solid(Colors.White);
    private static readonly ImmutableDashStyle TempDash = new(new[] { 3.0, 3.0 }, 0);
    private double _flowPhase;           // 0..1
    private double _flowPeriodPx = 17.2;
    private double _flowDashPx = 3.2;
    private TimeSpan _lastFrame = TimeSpan.MinValue;
    private bool _hadPulses;

    static ConnectorLayer()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<ConnectorLayer>(false);
        FocusableProperty.OverrideDefaultValue<ConnectorLayer>(false);
    }

    public ConnectorLayer()
    {
        _glowLayer = new Layer(this, LayerKind.Glow);
        _lineLayer = new Layer(this, LayerKind.Line);
        _flowLayer = new Layer(this, LayerKind.Flow);
        _hotLayer = new Layer(this, LayerKind.Hot);
        _pulseLayer = new Layer(this, LayerKind.Pulse);
        Children.Add(_glowLayer);
        Children.Add(_lineLayer);
        Children.Add(_flowLayer);
        Children.Add(_hotLayer);
        Children.Add(_pulseLayer);

        _frames = new FrameDriver(this, Tick);
        RebuildPens();
        RebuildPulseBrushes();
        ApplySoftGlow();
    }

    public event EventHandler<ConnectorPulseEventArgs>? PulseCompleted;

    #region Properties

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? GlowBrush
    {
        get => GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public double LineThickness
    {
        get => GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public double GlowThickness
    {
        get => GetValue(GlowThicknessProperty);
        set => SetValue(GlowThicknessProperty, value);
    }

    public double FlowSpeed
    {
        get => GetValue(FlowSpeedProperty);
        set => SetValue(FlowSpeedProperty, value);
    }

    public TimeSpan PulseDuration
    {
        get => GetValue(PulseDurationProperty);
        set => SetValue(PulseDurationProperty, value);
    }

    public Color PulseColor
    {
        get => GetValue(PulseColorProperty);
        set => SetValue(PulseColorProperty, value);
    }

    public double HubRadius
    {
        get => GetValue(HubRadiusProperty);
        set => SetValue(HubRadiusProperty, value);
    }

    public bool SoftGlow
    {
        get => GetValue(SoftGlowProperty);
        set => SetValue(SoftGlowProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == LineBrushProperty || p == GlowBrushProperty || p == LineThicknessProperty || p == GlowThicknessProperty)
        {
            RebuildPens();
            InvalidateEdges();
        }
        else if (p == PulseColorProperty)
        {
            RebuildPulseBrushes();
            RebuildPens();
            InvalidateEdges();
        }
        else if (p == FlowSpeedProperty)
        {
            UpdateFrames();
        }
        else if (p == HubRadiusProperty)
        {
            RelayoutAll();
        }
        else if (p == SoftGlowProperty)
        {
            ApplySoftGlow();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastFrame = TimeSpan.MinValue;
        _frames.OnAttached();
        UpdateFrames();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.OnDetached();
    }

    #endregion

    #region Public API

    /// <summary>Creates or moves an anchor; edges arrive and leave at the same point.</summary>
    public void SetAnchor(string key, Point p) => SetAnchor(key, p, p);

    /// <summary>
    /// Creates or moves an anchor with separate points: <paramref name="inPoint"/> is where
    /// edges arrive (usually the middle of the left side), <paramref name="outPoint"/> where
    /// they leave (usually the middle of the right side).
    /// </summary>
    public void SetAnchor(string key, Point inPoint, Point outPoint)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_anchors.TryGetValue(key, out var a))
        {
            if (a.In == inPoint && a.Out == outPoint) return;
            a.In = inPoint;
            a.Out = outPoint;
        }
        else
        {
            _anchors[key] = new Anchor { In = inPoint, Out = outPoint };
        }

        RelayoutTouching(key);
    }

    public void RemoveAnchor(string key)
    {
        if (_anchors.Remove(key)) RelayoutTouching(key);
    }

    public bool HasAnchor(string key) => _anchors.ContainsKey(key);

    /// <summary>Declares an edge. Idempotent. Drawn once both anchors exist.</summary>
    public void AddEdge(string fromKey, string toKey, ConnectorShape shape = ConnectorShape.Horizontal)
    {
        ArgumentNullException.ThrowIfNull(fromKey);
        ArgumentNullException.ThrowIfNull(toKey);
        if (_edgeMap.TryGetValue((fromKey, toKey), out var existing))
        {
            var changed = existing.Temp || existing.Shape != shape;
            if (existing.Temp)
            {
                // Promote the temporary edge drawn for a pulse to a permanent one.
                existing.Temp = false;
                if (existing.Fading) { existing.Fading = false; _fadingEdges--; }
                existing.Opacity = 1;
            }

            existing.Shape = shape;
            if (changed) Layout(existing);
            return;
        }

        var e = new Edge(fromKey, toKey) { Shape = shape };
        _edgeMap[(fromKey, toKey)] = e;
        _edges.Add(e);
        Layout(e);
        UpdateFrames();
    }

    public void RemoveEdge(string fromKey, string toKey)
    {
        if (_edgeMap.Remove((fromKey, toKey), out var e)) Detach(e);
        InvalidateEdges();
        UpdateFrames();
    }

    public void ClearEdges()
    {
        foreach (var e in _edges) e.Valid = false;
        _edges.Clear();
        _edgeMap.Clear();
        _fadingEdges = 0;
        ReleasePulses();
        InvalidateEdges();
        UpdateFrames();
    }

    /// <summary>Removes every edge, anchor and running pulse: back to an empty layer.</summary>
    public void Clear()
    {
        ClearEdges();
        _anchors.Clear();
    }

    /// <summary>Alias of <see cref="Clear"/>, kept for parity with the WPF API.</summary>
    public void ClearAll() => Clear();

    /// <summary>
    /// Sends a bright dot with a trailing glow from <paramref name="fromKey"/> to
    /// <paramref name="toKey"/> over <see cref="PulseDuration"/> and brightens the edge.
    /// Uses the parent edge in either direction; with no edge, draws a temporary dashed one.
    /// No-op if either anchor is missing.
    /// </summary>
    public void Pulse(string fromKey, string toKey)
    {
        if (fromKey is null || toKey is null) return;
        if (!_anchors.ContainsKey(fromKey) || !_anchors.ContainsKey(toKey)) return;

        var reverse = false;
        if (!_edgeMap.TryGetValue((fromKey, toKey), out var edge))
        {
            if (_edgeMap.TryGetValue((toKey, fromKey), out edge))
            {
                reverse = true;
            }
            else
            {
                edge = new Edge(fromKey, toKey) { Temp = true };
                _edgeMap[(fromKey, toKey)] = edge;
                _edges.Add(edge);
                Layout(edge);
            }
        }

        if (edge.Fading)
        {
            edge.Fading = false;
            _fadingEdges--;
            edge.Opacity = 1;
            InvalidateEdges();
        }

        var pulse = _pulsePool.Count > 0 ? _pulsePool.Pop() : new PulseRun();
        pulse.Reset(edge, reverse, fromKey, toKey);
        edge.ActivePulses++;
        _pulses.Add(pulse);
        UpdateFrames();
    }

    /// <summary>
    /// Maps a point inside <paramref name="element"/> (fractions of its size; 1,0.5 is the
    /// middle of the right side) into this layer's coordinates. NaN,NaN if not connected.
    /// </summary>
    public Point PointFrom(Control element, double xFraction, double yFraction)
    {
        if (element is null || !element.IsAttachedToVisualTree() || !this.IsAttachedToVisualTree())
            return new Point(double.NaN, double.NaN);
        var local = new Point(element.Bounds.Width * xFraction, element.Bounds.Height * yFraction);
        return element.TranslatePoint(local, this) ?? new Point(double.NaN, double.NaN);
    }

    #endregion

    #region Edges

    private sealed class Anchor
    {
        public Point In;
        public Point Out;
    }

    private sealed class Edge
    {
        public Edge(string from, string to)
        {
            From = from;
            To = to;
        }

        public readonly string From;
        public readonly string To;
        public ConnectorShape Shape;
        public bool Temp;
        public bool Valid;
        public bool Fading;
        public TimeSpan FadeStart;
        public int ActivePulses;
        public double Opacity = 1;
        public double HotLevel;
        public double HotApplied;

        public Point P0, P1, P2, P3;
        public StreamGeometry? Geometry;
        public readonly double[] PolyX = new double[FlattenSegments + 1];
        public readonly double[] PolyY = new double[FlattenSegments + 1];
        public readonly double[] Cum = new double[FlattenSegments + 1];

        public double Length => Cum[FlattenSegments];

        /// <summary>Point on the cubic at t in [0,1].</summary>
        public Point At(double t)
        {
            var u = 1 - t;
            var b0 = u * u * u;
            var b1 = 3 * u * u * t;
            var b2 = 3 * u * t * t;
            var b3 = t * t * t;
            return new Point(
                b0 * P0.X + b1 * P1.X + b2 * P2.X + b3 * P3.X,
                b0 * P0.Y + b1 * P1.Y + b2 * P2.Y + b3 * P3.Y);
        }

        /// <summary>Point at arc length s along the flattened curve; idx only moves forward between calls.</summary>
        public Point AtLength(double s, ref int idx)
        {
            if (s <= 0) return new Point(PolyX[0], PolyY[0]);
            if (s >= Cum[FlattenSegments]) return new Point(PolyX[FlattenSegments], PolyY[FlattenSegments]);
            while (idx < FlattenSegments - 1 && Cum[idx + 1] < s) idx++;
            var seg = Cum[idx + 1] - Cum[idx];
            var f = seg > 1e-9 ? (s - Cum[idx]) / seg : 0;
            return new Point(PolyX[idx] + (PolyX[idx + 1] - PolyX[idx]) * f, PolyY[idx] + (PolyY[idx + 1] - PolyY[idx]) * f);
        }
    }

    private void Detach(Edge e)
    {
        _edges.Remove(e);
        if (e.Fading) { e.Fading = false; _fadingEdges--; }
        e.Valid = false;
    }

    private void RelayoutTouching(string key)
    {
        for (int i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            if (e.From == key || e.To == key) Layout(e);
        }
    }

    private void RelayoutAll()
    {
        for (int i = 0; i < _edges.Count; i++) Layout(_edges[i]);
    }

    private void Layout(Edge e)
    {
        if (!_anchors.TryGetValue(e.From, out var from) || !_anchors.TryGetValue(e.To, out var to))
        {
            e.Valid = false;
            e.Geometry = null;
            InvalidateEdges();
            return;
        }

        var a = from.Out;
        var b = to.In;
        var hub = HubRadius;
        if (hub > 0)
        {
            if (e.From == "hub") a = new Point(a.X + hub, a.Y);
            if (e.To == "hub") b = new Point(b.X - hub, b.Y);
        }

        if (double.IsNaN(a.X) || double.IsNaN(a.Y) || double.IsNaN(b.X) || double.IsNaN(b.Y))
        {
            e.Valid = false;
            e.Geometry = null;
            InvalidateEdges();
            return;
        }

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        e.P0 = a;
        e.P3 = b;
        if (e.Shape == ConnectorShape.Branch)
        {
            var hv = Math.Max(Math.Abs(dy) * 0.6, 18);
            var hh = Math.Max(Math.Abs(dx) * 0.6, 18);
            e.P1 = new Point(a.X, a.Y + hv);
            e.P2 = new Point(b.X - hh, b.Y);
        }
        else
        {
            var h = Math.Max(Math.Abs(dx) * 0.5, 36);
            e.P1 = new Point(a.X + h, a.Y);
            e.P2 = new Point(b.X - h, b.Y);
        }

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(e.P0, false);
            g.CubicBezierTo(e.P1, e.P2, e.P3);
            g.EndFigure(false);
        }

        e.Geometry = geo;

        // Flatten once so the idle flow can walk the curve by arc length without allocating.
        double len = 0;
        var prev = e.P0;
        for (int i = 0; i <= FlattenSegments; i++)
        {
            var p = i == 0 ? e.P0 : e.At(i / (double)FlattenSegments);
            if (i > 0) len += Math.Sqrt((p.X - prev.X) * (p.X - prev.X) + (p.Y - prev.Y) * (p.Y - prev.Y));
            e.PolyX[i] = p.X;
            e.PolyY[i] = p.Y;
            e.Cum[i] = len;
            prev = p;
        }

        e.Valid = true;
        InvalidateEdges();
    }

    private void InvalidateEdges()
    {
        _glowLayer.InvalidateVisual();
        _lineLayer.InvalidateVisual();
        _flowLayer.InvalidateVisual();
        _hotLayer.InvalidateVisual();
    }

    private void RebuildPens()
    {
        var lineBrush = (LineBrush ?? Brushes.Transparent).ToImmutable();
        var glowBrush = (GlowBrush ?? Brushes.Transparent).ToImmutable();
        var lt = Math.Max(0.5, LineThickness);
        var gt = Math.Max(lt, GlowThickness);

        _glowPen = new ImmutablePen(glowBrush, gt, null, PenLineCap.Round, PenLineJoin.Round);
        _linePen = new ImmutablePen(lineBrush, lt, null, PenLineCap.Round, PenLineJoin.Round);

        // The flow dashes are drawn as short segments; thickness and spacing follow the WPF dash pattern
        // (1.6 on, 7.0 off, in units of the flow pen thickness).
        var ft = lt + 0.5;
        _flowPen = new ImmutablePen(Tokens.Solid(Tokens.WithAlpha(PulseColor, 0.55)), ft, null, PenLineCap.Round);
        _flowDashPx = 1.6 * ft;
        _flowPeriodPx = 8.6 * ft;

        _hotPen = new ImmutablePen(Tokens.Solid(Tokens.WithAlpha(PulseColor, 0.9)), lt * 2.2, null, PenLineCap.Round, PenLineJoin.Round);

        var tempBrush = lineBrush is ISolidColorBrush sc ? Tokens.Solid(Tokens.WithAlpha(sc.Color, 0.75)) : lineBrush;
        _tempPen = new ImmutablePen(tempBrush, lt, TempDash, PenLineCap.Round);

        var tempGlowBrush = glowBrush is ISolidColorBrush gc
            ? Tokens.Solid(Tokens.WithAlpha(gc.Color, gc.Color.A / 255.0 * 0.5))
            : glowBrush;
        _tempGlowPen = new ImmutablePen(tempGlowBrush, gt * 0.7, null, PenLineCap.Round);
    }

    private void ApplySoftGlow()
    {
        _glowLayer.Effect = SoftGlow ? new ImmutableBlurEffect(6) : null;
        _hotLayer.Effect = SoftGlow ? new ImmutableBlurEffect(4) : null;
    }

    #endregion

    #region Layers

    private enum LayerKind { Glow, Line, Flow, Hot, Pulse }

    private sealed class Layer : Control
    {
        private readonly ConnectorLayer _owner;
        private readonly LayerKind _kind;

        public Layer(ConnectorLayer owner, LayerKind kind)
        {
            _owner = owner;
            _kind = kind;
            IsHitTestVisible = false;
            Focusable = false;
        }

        public override void Render(DrawingContext dc) => _owner.Draw(_kind, dc);
    }

    private void Draw(LayerKind kind, DrawingContext dc)
    {
        switch (kind)
        {
            case LayerKind.Pulse:
                DrawPulses(dc);
                return;
            case LayerKind.Flow:
                DrawFlow(dc);
                return;
        }

        for (int i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            var geo = e.Valid ? e.Geometry : null;
            if (geo is null) continue;

            IPen pen;
            double opacity = e.Opacity;
            switch (kind)
            {
                case LayerKind.Glow:
                    pen = e.Temp ? _tempGlowPen : _glowPen;
                    break;
                case LayerKind.Line:
                    pen = e.Temp ? _tempPen : _linePen;
                    break;
                default: // Hot
                    if (e.HotApplied <= 0.001) continue;
                    pen = _hotPen;
                    opacity = e.HotApplied;
                    break;
            }

            if (opacity < 1)
            {
                using (dc.PushOpacity(opacity)) dc.DrawGeometry(null, pen, geo);
            }
            else
            {
                dc.DrawGeometry(null, pen, geo);
            }
        }
    }

    private void DrawFlow(DrawingContext dc)
    {
        if (FlowSpeed <= 0) return;
        var period = _flowPeriodPx;
        for (int i = 0; i < _edges.Count; i++)
        {
            var e = _edges[i];
            if (!e.Valid || e.Temp) continue;

            if (e.Opacity < 1)
            {
                using (dc.PushOpacity(e.Opacity)) DrawFlowEdge(dc, e, period);
            }
            else
            {
                DrawFlowEdge(dc, e, period);
            }
        }
    }

    private void DrawFlowEdge(DrawingContext dc, Edge e, double period)
    {
        var length = e.Length;
        var idxA = 0;
        var idxB = 0;
        for (var s = _flowPhase * period - period; s < length; s += period)
        {
            var b = s + _flowDashPx;
            if (b <= 0) continue;
            var pa = e.AtLength(s, ref idxA);
            var pb = e.AtLength(b, ref idxB);
            dc.DrawLine(_flowPen, pa, pb);
        }
    }

    private void DrawPulses(DrawingContext dc)
    {
        for (int i = 0; i < _pulses.Count; i++)
        {
            var p = _pulses[i];
            if (!p.Started) continue;

            // Ghosts, farthest first so the head draws on top.
            for (int k = TrailLength; k >= 1; k--)
            {
                var f = 1.0 - (k - 1) / (double)TrailLength;
                var radius = 1.2 + 3.2 * f;
                using (dc.PushTransform(Matrix.CreateTranslation(p.X[k], p.Y[k])))
                    dc.DrawEllipse(_ghostBrushes[k - 1], null, default, radius, radius);
            }

            using (dc.PushTransform(Matrix.CreateTranslation(p.X[0], p.Y[0])))
            {
                dc.DrawEllipse(_haloBrush, null, default, 9, 9);
                dc.DrawEllipse(CoreBrush, null, default, 2.4, 2.4);
            }
        }
    }

    #endregion

    #region Frames and pulses

    private sealed class PulseRun
    {
        public Edge Edge = null!;
        public bool Reverse;
        public bool Started;
        public TimeSpan Start;
        public string FromKey = string.Empty;
        public string ToKey = string.Empty;
        public readonly double[] X = new double[TrailLength + 1];
        public readonly double[] Y = new double[TrailLength + 1];

        public void Reset(Edge edge, bool reverse, string fromKey, string toKey)
        {
            Edge = edge;
            Reverse = reverse;
            Started = false;
            FromKey = fromKey;
            ToKey = toKey;
        }
    }

    private void RebuildPulseBrushes()
    {
        var c = PulseColor;

        var halo = new RadialGradientBrush();
        halo.GradientStops.Add(new GradientStop(Tokens.WithAlpha(c, 0.95), 0.0));
        halo.GradientStops.Add(new GradientStop(Tokens.WithAlpha(c, 0.45), 0.35));
        halo.GradientStops.Add(new GradientStop(Tokens.WithAlpha(c, 0.0), 1.0));
        _haloBrush = halo.ToImmutable();

        var ghosts = new IBrush[TrailLength];
        for (int i = 0; i < TrailLength; i++)
        {
            var f = 1.0 - i / (double)TrailLength;
            var g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(Tokens.WithAlpha(c, 0.55 * f), 0.0));
            g.GradientStops.Add(new GradientStop(Tokens.WithAlpha(c, 0.0), 1.0));
            ghosts[i] = g.ToImmutable();
        }

        _ghostBrushes = ghosts;
    }

    private void ReleasePulses()
    {
        for (int i = 0; i < _pulses.Count; i++)
        {
            var p = _pulses[i];
            p.Edge = null!;
            _pulsePool.Push(p);
        }

        _pulses.Clear();
        _pulseLayer.InvalidateVisual();
    }

    private bool WantsFlow => FlowSpeed > 0 && _edges.Count > 0;

    private void UpdateFrames()
    {
        if (!this.IsAttachedToVisualTree()) return;
        if (WantsFlow || _pulses.Count > 0 || _fadingEdges > 0) _frames.Request();
        else
        {
            _frames.Stop();
            _flowLayer.InvalidateVisual();
        }
    }

    private static double EaseInOut(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
    }

    private bool Tick(TimeSpan now)
    {
        var dt = _lastFrame == TimeSpan.MinValue ? 0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;

        if (WantsFlow)
        {
            _flowPhase = (_flowPhase + dt * FlowSpeed) % 1.0;
            _flowLayer.InvalidateVisual();
        }

        var duration = PulseDuration;
        if (duration <= TimeSpan.Zero) duration = TimeSpan.FromMilliseconds(1);

        for (int i = _pulses.Count - 1; i >= 0; i--)
        {
            var p = _pulses[i];
            if (!p.Started)
            {
                p.Started = true;
                p.Start = now;
            }

            var edge = p.Edge;
            var t = (now - p.Start).Ticks / (double)duration.Ticks;
            if (t >= 1.0 || !edge.Valid)
            {
                FinishPulse(i, now);
                continue;
            }

            var te = EaseInOut(t);
            for (int k = 0; k <= TrailLength; k++)
            {
                var tk = Math.Max(0, te - k * TrailStep);
                var pos = edge.At(p.Reverse ? 1 - tk : tk);
                p.X[k] = pos.X;
                p.Y[k] = pos.Y;
            }

            var hot = Math.Sin(Math.PI * t);
            if (hot > edge.HotLevel) edge.HotLevel = hot;
        }

        // Apply edge brightening and fades.
        var hotChanged = false;
        var fadeChanged = false;
        for (int i = _edges.Count - 1; i >= 0; i--)
        {
            var e = _edges[i];
            var target = e.HotLevel * 0.9;
            e.HotLevel = 0;
            if (target != e.HotApplied)
            {
                e.HotApplied = target;
                hotChanged = true;
            }

            if (e.Fading)
            {
                var f = 1.0 - (now - e.FadeStart).Ticks / (double)FadeDuration.Ticks;
                if (f <= 0)
                {
                    _edgeMap.Remove((e.From, e.To));
                    Detach(e);
                }
                else
                {
                    e.Opacity = f;
                }

                fadeChanged = true;
            }
        }

        if (hotChanged) _hotLayer.InvalidateVisual();
        if (fadeChanged)
        {
            _glowLayer.InvalidateVisual();
            _lineLayer.InvalidateVisual();
            _flowLayer.InvalidateVisual();
        }

        if (_pulses.Count > 0 || _hadPulses) _pulseLayer.InvalidateVisual();
        _hadPulses = _pulses.Count > 0;

        if (!WantsFlow && _pulses.Count == 0 && _fadingEdges == 0)
        {
            _flowLayer.InvalidateVisual();
            return false;
        }

        return true;
    }

    private void FinishPulse(int index, TimeSpan now)
    {
        var p = _pulses[index];
        _pulses.RemoveAt(index);

        var edge = p.Edge;
        edge.ActivePulses--;
        if (edge.Temp && edge.ActivePulses <= 0 && !edge.Fading
            && _edgeMap.TryGetValue((edge.From, edge.To), out var still) && ReferenceEquals(still, edge))
        {
            edge.Fading = true;
            edge.FadeStart = now;
            _fadingEdges++;
        }

        var from = p.FromKey;
        var to = p.ToKey;
        p.Edge = null!;
        _pulsePool.Push(p);
        PulseCompleted?.Invoke(this, new ConnectorPulseEventArgs(from, to));
    }

    #endregion
}
