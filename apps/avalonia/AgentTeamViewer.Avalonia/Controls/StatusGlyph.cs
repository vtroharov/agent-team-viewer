using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;

namespace AgentTeamViewer.Controls;

public enum StatusGlyphMode
{
    /// <summary>Per-status glyph set: spinning arc, pulsing dot, check, cross, rings.</summary>
    Glyph,
    /// <summary>At-a-glance indicator: green dot = ready, animated gear = working, red dot = failed, dim hollow dot = gone.</summary>
    Ready,
}

/// <summary>
/// A small vector glyph that reflects a status word. Active states animate:
/// running/tool spin, thinking pulses, waiting blinks slowly. Everything else is static.
/// Drawn in a 12-unit design space (origin at the centre) scaled to the control size.
/// </summary>
public class StatusGlyph : Control
{
    public static readonly StyledProperty<string> StatusProperty =
        AvaloniaProperty.Register<StatusGlyph, string>(nameof(Status), "idle");

    public static readonly StyledProperty<double> GlyphSizeProperty =
        AvaloniaProperty.Register<StatusGlyph, double>(nameof(GlyphSize), 12.0);

    /// <summary>Overrides the status colour when set.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StatusGlyph, IBrush?>(nameof(Fill));

    /// <summary>
    /// Glyph (default): the per-status glyph set (spinning arc, check, cross, rings).
    /// Ready: the at-a-glance indicator; green dot = ready, animated gear = working, red dot = failed, dim hollow dot = gone.
    /// </summary>
    public static readonly StyledProperty<StatusGlyphMode> ModeProperty =
        AvaloniaProperty.Register<StatusGlyph, StatusGlyphMode>(nameof(Mode), StatusGlyphMode.Glyph);

    /// <summary>Ready mode only: draw a small dark tick inside the green dot and a cross inside the red dot.</summary>
    public static readonly StyledProperty<bool> SecondaryMarkProperty =
        AvaloniaProperty.Register<StatusGlyph, bool>(nameof(SecondaryMark), true);

    // Shared geometry in design units (12 x 12 box, centre at 0,0).
    private static readonly StreamGeometry ArcRun = Arc(5, -90, 270);
    private static readonly StreamGeometry ArcToolA = Arc(5, -90, 110);
    private static readonly StreamGeometry ArcToolB = Arc(5, 90, 110);
    private static readonly StreamGeometry Check = Polyline(-3.75, 0.25, -1.0, 3.0, 4.0, -3.0);
    private static readonly StreamGeometry GearUnit = Gear(8);   // outer radius 1, scaled when drawn

    private readonly FrameDriver _frames;
    private string _kind = "idle";
    private string _raw = "idle";
    private ReadyState _group = ReadyState.Ready;

    // Prepared on change, never per frame.
    private IBrush _brush = Brushes.Transparent;
    private IBrush _glowBrush = Brushes.Transparent;
    private ImmutablePen? _pen;
    private ImmutablePen? _markPen;
    private double _spinPeriod;            // seconds per turn, 0 = none
    private double _pulseFrom = 1, _pulseTo = 1, _pulseHalf; // 0 half = no pulse
    private double _angle;                 // degrees
    private double _alpha = 1;

    static StatusGlyph()
    {
        AffectsMeasure<StatusGlyph>(GlyphSizeProperty);
        AffectsRender<StatusGlyph>(GlyphSizeProperty);
        FocusableProperty.OverrideDefaultValue<StatusGlyph>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<StatusGlyph>(false);
        VerticalAlignmentProperty.OverrideDefaultValue<StatusGlyph>(VerticalAlignment.Center);
    }

    public StatusGlyph()
    {
        _frames = new FrameDriver(this, Tick);
        Prepare();
    }

    public string Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    public double GlyphSize
    {
        get => GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public StatusGlyphMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public bool SecondaryMark
    {
        get => GetValue(SecondaryMarkProperty);
        set => SetValue(SecondaryMarkProperty, value);
    }

    /// <summary>Amber gear only for a literal tool status; the generic words running/thinking/working/pending are cyan.</summary>
    private bool IsToolRaw => _raw is "tool";

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty)
        {
            var raw = (change.NewValue as string)?.Trim().ToLowerInvariant() ?? string.Empty;
            _raw = raw;
            _kind = StatusPalette.Normalize(raw);
            _group = StatusPalette.ReadyGroup(raw);
            Prepare();
        }
        else if (change.Property == ModeProperty || change.Property == FillProperty
                 || change.Property == SecondaryMarkProperty)
        {
            Prepare();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Prepare(); // resources are resolvable now
        _frames.OnAttached();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.OnDetached();
    }

    protected override Size MeasureOverride(Size availableSize) => new(GlyphSize, GlyphSize);

    #region Preparation

    private void Prepare()
    {
        _spinPeriod = 0;
        _pulseHalf = 0;
        _angle = 0;
        _alpha = 1;

        if (Mode == StatusGlyphMode.Ready) PrepareReady();
        else PrepareGlyph();

        if (_spinPeriod > 0 || _pulseHalf > 0) _frames.Request();
        else _frames.Stop();
        InvalidateVisual();
    }

    private static ImmutablePen Pen(IBrush brush, double thickness)
        => new(brush.ToImmutable(), thickness, null, PenLineCap.Round, PenLineJoin.Round);

    private void PrepareGlyph()
    {
        _brush = Fill ?? StatusPalette.BrushFor(_kind);
        _glowBrush = Brushes.Transparent;
        _markPen = null;
        switch (_kind)
        {
            case "running":
                _pen = Pen(_brush, 1.9);
                _spinPeriod = 1.15;
                break;
            case "tool":
                _pen = Pen(_brush, 1.9);
                _spinPeriod = 0.85;
                break;
            case "thinking":
                _pen = null;
                (_pulseFrom, _pulseTo, _pulseHalf) = (0.35, 1.0, 0.7);
                break;
            case "waiting":
                _pen = Pen(_brush, 1.5);
                (_pulseFrom, _pulseTo, _pulseHalf) = (1.0, 0.4, 1.3);
                break;
            case "done":
                _pen = Pen(_brush, 2.0);
                break;
            case "failed":
                _pen = Pen(_brush, 2.0);
                break;
            case "gone":
                _pen = null;
                break;
            default: // idle
                _pen = Pen(_brush, 1.25);
                break;
        }
    }

    private void PrepareReady()
    {
        switch (_group)
        {
            case ReadyState.Ready:
            {
                var color = SolidOr(Fill, Tokens.Resolve("GreenColor", Tokens.Green));
                _brush = Tokens.Solid(color);
                _glowBrush = Tokens.RadialGlow(color, 0.55);
                _markPen = Pen(Tokens.Solid(Tokens.Resolve("BgTerminalColor", Tokens.BgTerminal)), 1.4);
                break;
            }
            case ReadyState.Failed:
            {
                var color = SolidOr(Fill, Tokens.Resolve("RedColor", Tokens.Red));
                _brush = Tokens.Solid(color);
                _glowBrush = Tokens.RadialGlow(color, 0.55);
                _markPen = Pen(Tokens.Solid(Tokens.Resolve("BgTerminalColor", Tokens.BgTerminal)), 1.4);
                break;
            }
            case ReadyState.Working:
            {
                var color = SolidOr(Fill, IsToolRaw
                    ? Tokens.Resolve("AmberColor", Tokens.Amber)
                    : Tokens.Resolve("CyanColor", Tokens.Cyan));
                _brush = Tokens.Solid(color);
                _glowBrush = Tokens.RadialGlow(color, 0.35);
                _markPen = null;
                _spinPeriod = IsToolRaw ? 1.6 : 2.6;
                if (_kind == "thinking") (_pulseFrom, _pulseTo, _pulseHalf) = (0.5, 1.0, 0.8);
                break;
            }
            default:
            {
                var faint = Fill ?? Tokens.ResolveBrush("TextFaintBrush", Tokens.TextFaint);
                _brush = faint;
                _glowBrush = Brushes.Transparent;
                _pen = Pen(faint, 1.25);
                _markPen = null;
                break;
            }
        }
    }

    private static Color SolidOr(IBrush? fill, Color fallback)
        => fill is ISolidColorBrush s ? s.Color : fallback;

    #endregion

    private bool Tick(TimeSpan now)
    {
        var t = now.TotalSeconds;
        if (_spinPeriod > 0) _angle = t % _spinPeriod / _spinPeriod * 360.0;
        if (_pulseHalf > 0)
        {
            var phase = 0.5 - 0.5 * Math.Cos(Math.PI * t / _pulseHalf);
            _alpha = _pulseFrom + (_pulseTo - _pulseFrom) * phase;
        }

        InvalidateVisual();
        return _spinPeriod > 0 || _pulseHalf > 0;
    }

    public override void Render(DrawingContext dc)
    {
        var s = Math.Min(Bounds.Width, Bounds.Height);
        if (s <= 0) return;

        var scale = s / 12.0;
        var baseMatrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(Bounds.Width / 2, Bounds.Height / 2);

        if (_alpha < 1)
        {
            using (dc.PushOpacity(_alpha)) Draw(dc, baseMatrix, s);
        }
        else
        {
            Draw(dc, baseMatrix, s);
        }
    }

    private void Draw(DrawingContext dc, Matrix baseMatrix, double s)
    {
        if (Mode == StatusGlyphMode.Ready) RenderReady(dc, baseMatrix, s);
        else RenderGlyph(dc, baseMatrix);
    }

    private void RenderGlyph(DrawingContext dc, Matrix baseMatrix)
    {
        const double r = 5.0;

        switch (_kind)
        {
            case "running":
                using (dc.PushTransform(Matrix.CreateRotation(Rad(_angle)) * baseMatrix))
                    dc.DrawGeometry(null, _pen, ArcRun);
                break;

            case "tool":
                using (dc.PushTransform(Matrix.CreateRotation(Rad(_angle)) * baseMatrix))
                {
                    dc.DrawGeometry(null, _pen, ArcToolA);
                    dc.DrawGeometry(null, _pen, ArcToolB);
                }
                break;

            case "thinking":
                using (dc.PushTransform(baseMatrix))
                    dc.DrawEllipse(_brush, null, default, r * 0.6, r * 0.6);
                break;

            case "waiting":
                using (dc.PushTransform(baseMatrix))
                    dc.DrawEllipse(null, _pen, default, r, r);
                break;

            case "done":
                using (dc.PushTransform(baseMatrix))
                    dc.DrawGeometry(null, _pen, Check);
                break;

            case "failed":
                using (dc.PushTransform(baseMatrix))
                {
                    const double k = r * 0.62;
                    dc.DrawLine(_pen!, new Point(-k, -k), new Point(k, k));
                    dc.DrawLine(_pen!, new Point(-k, k), new Point(k, -k));
                }
                break;

            case "gone":
                using (dc.PushTransform(baseMatrix))
                    dc.DrawEllipse(_brush, null, default, r * 0.32, r * 0.32);
                break;

            default: // idle
                using (dc.PushTransform(baseMatrix))
                    dc.DrawEllipse(null, _pen, default, r, r);
                break;
        }
    }

    private void RenderReady(DrawingContext dc, Matrix baseMatrix, double s)
    {
        const double dotR = 12 * 0.40; // ~10 px dot at GlyphSize 12 (design units)
        var mark = SecondaryMark && s >= 10;

        using (dc.PushTransform(baseMatrix))
        {
            switch (_group)
            {
                case ReadyState.Ready:
                    dc.DrawEllipse(_glowBrush, null, default, dotR * 2.1, dotR * 2.1);
                    dc.DrawEllipse(_brush, null, default, dotR, dotR);
                    if (mark) dc.DrawGeometry(null, _markPen, ReadyTick);
                    break;

                case ReadyState.Failed:
                    dc.DrawEllipse(_glowBrush, null, default, dotR * 2.1, dotR * 2.1);
                    dc.DrawEllipse(_brush, null, default, dotR, dotR);
                    if (mark)
                    {
                        const double k = dotR * 0.45;
                        dc.DrawLine(_markPen!, new Point(-k, -k), new Point(k, k));
                        dc.DrawLine(_markPen!, new Point(-k, k), new Point(k, -k));
                    }
                    break;

                case ReadyState.Working:
                    dc.DrawEllipse(_glowBrush, null, default, 12 * 0.62, 12 * 0.62);
                    using (dc.PushTransform(Matrix.CreateScale(12 * 0.48, 12 * 0.48) * Matrix.CreateRotation(Rad(_angle))))
                        dc.DrawGeometry(_brush, null, GearUnit);
                    break;

                default:
                    dc.DrawEllipse(null, _pen, default, dotR * 0.8, dotR * 0.8);
                    break;
            }
        }
    }

    // Tick inside the green dot: k = dotR * 0.55 = 2.64 design units.
    private static readonly StreamGeometry ReadyTick = Polyline(-2.376, 0.132, -0.66, 1.848, 2.508, -1.848);

    private static double Rad(double deg) => deg * Math.PI / 180.0;

    #region Geometry

    private static StreamGeometry Polyline(params double[] xy)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(xy[0], xy[1]), false);
            for (int i = 2; i < xy.Length; i += 2) g.LineTo(new Point(xy[i], xy[i + 1]));
            g.EndFigure(false);
        }

        return geo;
    }

    private static StreamGeometry Arc(double r, double startDeg, double sweepDeg)
    {
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            var a0 = Rad(startDeg);
            var a1 = Rad(startDeg + sweepDeg);
            g.BeginFigure(new Point(r * Math.Cos(a0), r * Math.Sin(a0)), false);
            g.ArcTo(new Point(r * Math.Cos(a1), r * Math.Sin(a1)), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise);
            g.EndFigure(false);
        }

        return geo;
    }

    /// <summary>A gear outline with unit outer radius and a centre hole (even-odd).</summary>
    private static StreamGeometry Gear(int teeth)
    {
        const double ro = 1.0;
        var ri = ro * 0.74;
        var hole = ro * 0.30;
        var step = 360.0 / teeth;
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.SetFillRule(FillRule.EvenOdd);
            for (int i = 0; i < teeth; i++)
            {
                var a = i * step;
                var p0 = Polar(ri, a - step * 0.06);
                var p1 = Polar(ro, a + step * 0.06);
                var p2 = Polar(ro, a + step * 0.40);
                var p3 = Polar(ri, a + step * 0.52);
                if (i == 0) g.BeginFigure(p0, true); else g.LineTo(p0);
                g.LineTo(p1);
                g.LineTo(p2);
                g.LineTo(p3);
            }

            g.EndFigure(true);

            g.BeginFigure(new Point(hole, 0), true);
            g.ArcTo(new Point(-hole, 0), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            g.ArcTo(new Point(hole, 0), new Size(hole, hole), 0, false, SweepDirection.Clockwise);
            g.EndFigure(true);
        }

        return geo;
    }

    private static Point Polar(double r, double deg)
    {
        var a = Rad(deg);
        return new Point(r * Math.Cos(a), r * Math.Sin(a));
    }

    #endregion
}
