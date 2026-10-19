using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace AgentTeamViewer.Controls;

/// <summary>
/// The centre hub: concentric rings with a glow. Breathes while the lead thinks and spins
/// faster while a tool runs. The ring is the largest centred square that fits; an optional
/// caption sits under it.
/// </summary>
public class HubRing : Control
{
    private const double D = 200;               // design-space size, centre at (0,0) when drawing

    private const double RestSpinRatio = 1.0;   // one clockwise turn / 28 s at rest
    private const double FastSpinRatio = 3.2;   // while Spin is true
    private const double RestPulseRatio = 1.0;  // centre dot: 2.6 s per inhale+exhale at rest
    private const double FastPulseRatio = 1.7;  // while Breathe is true
    private const double CwSeconds = 28, CcwSeconds = 40;
    private const double DotHalfSeconds = 1.3, BreatheHalfSeconds = 1.4;
    private const double SettleSeconds = 0.55;

    public static readonly StyledProperty<bool> BreatheProperty =
        AvaloniaProperty.Register<HubRing, bool>(nameof(Breathe), false);

    public static readonly StyledProperty<bool> SpinProperty =
        AvaloniaProperty.Register<HubRing, bool>(nameof(Spin), false);

    public static readonly StyledProperty<Color> AccentProperty =
        AvaloniaProperty.Register<HubRing, Color>(nameof(Accent), Tokens.Cyan);

    /// <summary>Small mono label under the ring, e.g. "HUB". Empty hides it.</summary>
    public static readonly StyledProperty<string> CaptionProperty =
        AvaloniaProperty.Register<HubRing, string>(nameof(Caption), string.Empty);

    // Static geometry in design units, centred on the origin.
    private static readonly StreamGeometry Arcs = ArcPair(64);
    private static readonly ImmutableDashStyle TickDash = new(new[] { 0.01, 5.6 }, 0);
    private static readonly ImmutableDashStyle InnerDash = new(new[] { 16.0, 10.0 }, 0);
    private static readonly IBrush DotBrush = Tokens.Solid(Tokens.TextPrimary);
    private static readonly double[] GlowWidths = { 7, 12, 17, 22, 28 };

    private readonly FrameDriver _frames;

    // Pre-built per accent.
    private ImmutablePen _outerPen = null!, _ticksPen = null!, _glowRingPen = null!, _arcsPen = null!, _innerPen = null!;
    private ImmutablePen[] _glowPens = Array.Empty<ImmutablePen>();
    private IBrush _coreBrush = null!;
    private FormattedText? _caption;
    private Color _accent;

    // Phase state (seconds already advanced at each element's own rate).
    private TimeSpan _last = TimeSpan.MinValue;
    private double _spinPhase;       // turns, advanced by dt * ratio / 28
    private double _dotPhase;        // half-periods, advanced by dt * ratio / 1.3
    private double _breathePhase;    // half-periods of the breathe loop
    private double _spinRatio = RestSpinRatio;
    private double _breatheAmount;   // 0..1, eased so Breathe on/off never snaps

    static HubRing()
    {
        AffectsMeasure<HubRing>(CaptionProperty);
        AffectsRender<HubRing>(AccentProperty, CaptionProperty);
        FocusableProperty.OverrideDefaultValue<HubRing>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<HubRing>(false);
    }

    public HubRing()
    {
        _frames = new FrameDriver(this, Tick);
        ApplyAccent();
    }

    public bool Breathe
    {
        get => GetValue(BreatheProperty);
        set => SetValue(BreatheProperty, value);
    }

    public bool Spin
    {
        get => GetValue(SpinProperty);
        set => SetValue(SpinProperty, value);
    }

    public Color Accent
    {
        get => GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public string Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private double RingSize()
    {
        var cap = _caption is null ? 0 : _caption.Height + 6;
        return Math.Max(0, Math.Min(Bounds.Width, Bounds.Height - cap));
    }

    /// <summary>Ring centre y divided by the control height (0.5 when there is no caption). Use with PointFrom for the hub anchor.</summary>
    public double RingCenterYFraction => Bounds.Height > 0 ? RingSize() / 2 / Bounds.Height : 0.5;

    /// <summary>Outer radius of the drawn ring in layout units, for ConnectorLayer.HubRadius.</summary>
    public double RingRadius => RingSize() * 95 / D;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentProperty) ApplyAccent();
        else if (change.Property == CaptionProperty) BuildCaption();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _last = TimeSpan.MinValue;
        _frames.Request();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.Stop();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cap = _caption is null ? 0 : _caption.Height + 6;
        var w = double.IsInfinity(availableSize.Width) ? D : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? D + cap : availableSize.Height;
        var ring = Math.Max(0, Math.Min(w, h - cap));
        return new Size(ring, ring + cap);
    }

    private void ApplyAccent()
    {
        var a = Accent;
        _accent = a;
        var accent = Tokens.Solid(a);
        var faint = Tokens.Solid(Tokens.WithAlpha(a, 0.22));
        var dim = Tokens.Solid(Tokens.WithAlpha(a, 0.55));

        // Near-zero dashes with round caps render as true circles of diameter = thickness at any
        // scale (a longer dash becomes a pill once the ring is scaled up).
        _outerPen = new ImmutablePen(faint, 1.0);
        _ticksPen = new ImmutablePen(dim, 2.4, TickDash, PenLineCap.Round);
        _glowRingPen = new ImmutablePen(accent, 2.0);
        _arcsPen = new ImmutablePen(accent, 2.0, null, PenLineCap.Round);
        _innerPen = new ImmutablePen(dim, 1.0, InnerDash);

        var glowBrush = Tokens.Solid(Tokens.WithAlpha(a, 0.13));
        _glowPens = new ImmutablePen[GlowWidths.Length];
        for (int i = 0; i < GlowWidths.Length; i++)
            _glowPens[i] = new ImmutablePen(glowBrush, GlowWidths[i]);

        var core = new RadialGradientBrush();
        core.GradientStops.Add(new GradientStop(Tokens.WithAlpha(a, 0.55), 0.0));
        core.GradientStops.Add(new GradientStop(Tokens.WithAlpha(a, 0.18), 0.55));
        core.GradientStops.Add(new GradientStop(Tokens.WithAlpha(a, 0.0), 1.0));
        _coreBrush = core.ToImmutable();

        BuildCaption();
        InvalidateVisual();
    }

    private void BuildCaption()
    {
        var text = Caption;
        if (string.IsNullOrEmpty(text))
        {
            _caption = null;
            return;
        }

        _caption = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Cascadia Mono, Consolas, Menlo, Monaco, Courier New")), 10,
            Tokens.Solid(Tokens.WithAlpha(_accent, 0.55)));
    }

    private bool Tick(TimeSpan now)
    {
        var dt = _last == TimeSpan.MinValue ? 0 : Math.Clamp((now - _last).TotalSeconds, 0, 0.1);
        _last = now;

        var spinTarget = Spin ? FastSpinRatio : RestSpinRatio;
        _spinRatio = spinTarget;
        _spinPhase += dt * _spinRatio / CwSeconds;
        if (_spinPhase > 1000) _spinPhase %= 1;

        var pulseRatio = Breathe ? FastPulseRatio : RestPulseRatio;
        _dotPhase += dt * pulseRatio / DotHalfSeconds;

        // Breathe eases in and out over the settle time so toggling never snaps.
        var step = dt / SettleSeconds;
        _breatheAmount = Breathe ? Math.Min(1, _breatheAmount + step) : Math.Max(0, _breatheAmount - step);
        if (_breatheAmount > 0) _breathePhase += dt / BreatheHalfSeconds;

        InvalidateVisual();
        return true;
    }

    private static double Wave(double halfPeriods) => 0.5 - 0.5 * Math.Cos(Math.PI * halfPeriods);

    public override void Render(DrawingContext dc)
    {
        var cap = _caption is null ? 0 : _caption.Height + 6;
        var ring = Math.Min(Bounds.Width, Bounds.Height - cap);
        if (ring <= 0) return;

        var scale = ring / D;
        var cx = Bounds.Width / 2;
        var cy = ring / 2;

        var breathe = _breatheAmount > 0 ? Wave(_breathePhase) * _breatheAmount : 0.0;
        var zoom = 1.0 + 0.05 * breathe;
        var rest = Matrix.CreateScale(scale * zoom, scale * zoom) * Matrix.CreateTranslation(cx, cy);

        var cw = Matrix.CreateRotation(2 * Math.PI * _spinPhase) * rest;
        var ccw = Matrix.CreateRotation(-2 * Math.PI * _spinPhase * CwSeconds / CcwSeconds) * rest;

        // Faint outer ring.
        using (dc.PushTransform(rest))
            dc.DrawEllipse(null, _outerPen, default, 95, 95);

        // Outer dotted ring, clockwise. Rotation pivots on the ring centre (the origin).
        using (dc.PushTransform(cw))
            dc.DrawEllipse(null, _ticksPen, default, 85, 85);

        using (dc.PushTransform(rest))
        {
            // Glow ring: layered strokes stand in for a blur; the glow swells while breathing.
            var glowOpacity = 0.55 + (0.45 + 0.55 * Wave(_breathePhase) - 0.55) * _breatheAmount;
            using (dc.PushOpacity(Math.Clamp(glowOpacity, 0, 1)))
            {
                for (int i = 0; i < _glowPens.Length; i++)
                    dc.DrawEllipse(null, _glowPens[i], default, 73, 73);
            }

            dc.DrawEllipse(null, _glowRingPen, default, 73, 73);
        }

        // Two arcs, clockwise.
        using (dc.PushTransform(cw))
            dc.DrawGeometry(null, _arcsPen, Arcs);

        // Inner dashed ring, anticlockwise.
        using (dc.PushTransform(ccw))
            dc.DrawEllipse(null, _innerPen, default, 52, 52);

        using (dc.PushTransform(rest))
        {
            dc.DrawEllipse(_coreBrush, null, default, 36, 36);

            // Centre dot: pulses 0.85..1.4 on its own clock, with a swelling glow.
            var p = Wave(_dotPhase);
            var dotR = 12 * (0.85 + (1.4 - 0.85) * p);
            var glowA = 0.5 + 0.5 * p;
            var blur = 16 + 24 * p;
            var shadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 0,
                Blur = blur,
                Spread = 0,
                Color = Color.FromArgb((byte)(255 * glowA), _accent.R, _accent.G, _accent.B),
            });
            dc.DrawRectangle(DotBrush, null, new RoundedRect(new Rect(-dotR, -dotR, dotR * 2, dotR * 2), dotR), shadow);
        }

        if (_caption is not null)
            dc.DrawText(_caption, new Point(cx - _caption.Width / 2, ring + 6));
    }

    private static StreamGeometry ArcPair(double r)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            AddArc(g, r, -160, 60);
            AddArc(g, r, 20, 60);
        }

        return geo;
    }

    private static void AddArc(StreamGeometryContext g, double r, double startDeg, double sweepDeg)
    {
        var a0 = startDeg * Math.PI / 180.0;
        var a1 = (startDeg + sweepDeg) * Math.PI / 180.0;
        g.BeginFigure(new Point(r * Math.Cos(a0), r * Math.Sin(a0)), false);
        g.ArcTo(new Point(r * Math.Cos(a1), r * Math.Sin(a1)), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
        g.EndFigure(false);
    }
}
