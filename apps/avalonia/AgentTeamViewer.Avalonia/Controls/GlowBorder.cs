using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AgentTeamViewer.Controls;

/// <summary>
/// A Border with a soft outer glow (its BoxShadow). The glow is drawn by the border outside
/// the content, so streaming text inside never forces it to re-render.
/// </summary>
public class GlowBorder : Border
{
    private static readonly TimeSpan FlashRise = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan FlashTotal = TimeSpan.FromMilliseconds(350);

    public static readonly StyledProperty<Color> GlowColorProperty =
        AvaloniaProperty.Register<GlowBorder, Color>(nameof(GlowColor), Tokens.Cyan);

    /// <summary>0 = no glow, 1 = strong. Drives the shadow's alpha.</summary>
    public static readonly StyledProperty<double> GlowIntensityProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(GlowIntensity), 0.55, coerce: (_, v) => Math.Clamp(v, 0, 1));

    public static readonly StyledProperty<double> GlowRadiusProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(GlowRadius), 18.0);

    /// <summary>Slow glow pulse around <see cref="GlowIntensity"/>.</summary>
    public static readonly StyledProperty<bool> BreathingProperty =
        AvaloniaProperty.Register<GlowBorder, bool>(nameof(Breathing), false);

    public static readonly StyledProperty<TimeSpan> BreathePeriodProperty =
        AvaloniaProperty.Register<GlowBorder, TimeSpan>(nameof(BreathePeriod), TimeSpan.FromSeconds(2.6));

    private readonly FrameDriver _frames;
    private double _level;               // intensity currently applied
    private bool _flashing;
    private bool _flashStarted;
    private TimeSpan _flashStart;

    static GlowBorder()
    {
        BackgroundProperty.OverrideDefaultValue<GlowBorder>(Tokens.Solid(Tokens.BgTerminal));
        BorderBrushProperty.OverrideDefaultValue<GlowBorder>(Tokens.Solid(Tokens.Cyan));
        BorderThicknessProperty.OverrideDefaultValue<GlowBorder>(new Thickness(1));
        CornerRadiusProperty.OverrideDefaultValue<GlowBorder>(new CornerRadius(6));
    }

    public GlowBorder()
    {
        _frames = new FrameDriver(this, Tick);
        _level = GlowIntensity;
        ApplyShadow(_level);
    }

    public Color GlowColor
    {
        get => GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    public double GlowIntensity
    {
        get => GetValue(GlowIntensityProperty);
        set => SetValue(GlowIntensityProperty, value);
    }

    public double GlowRadius
    {
        get => GetValue(GlowRadiusProperty);
        set => SetValue(GlowRadiusProperty, value);
    }

    public bool Breathing
    {
        get => GetValue(BreathingProperty);
        set => SetValue(BreathingProperty, value);
    }

    public TimeSpan BreathePeriod
    {
        get => GetValue(BreathePeriodProperty);
        set => SetValue(BreathePeriodProperty, value);
    }

    /// <summary>Briefly (about 350 ms) doubles the glow, then returns to the resting state.</summary>
    public void Flash()
    {
        _flashing = true;
        _flashStarted = false;
        _frames.Request();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlowColorProperty || change.Property == GlowRadiusProperty
            || change.Property == GlowIntensityProperty)
        {
            // While animating, the next frame recomputes the level from the new rest value.
            if (change.Property == GlowIntensityProperty && !_frames.Running) _level = GlowIntensity;
            ApplyShadow(_level);
        }
        else if (change.Property == BreathingProperty || change.Property == BreathePeriodProperty)
        {
            if (Breathing) _frames.Request();
            else if (!_flashing)
            {
                _frames.Stop();
                _level = GlowIntensity;
                ApplyShadow(_level);
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frames.OnAttached();
        if (Breathing) _frames.Request();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frames.OnDetached();
    }

    private bool Tick(TimeSpan now)
    {
        var rest = GlowIntensity;
        var level = rest;
        var more = false;

        if (Breathing)
        {
            var lo = Math.Max(0.0, rest * 0.55);
            var hi = Math.Min(1.0, rest * 1.45 + 0.05);
            var period = Math.Max(BreathePeriod.TotalSeconds, 0.4);
            var phase = 0.5 - 0.5 * Math.Cos(2 * Math.PI * now.TotalSeconds / period);
            level = lo + (hi - lo) * phase;
            more = true;
        }

        if (_flashing)
        {
            if (!_flashStarted)
            {
                _flashStarted = true;
                _flashStart = now;
            }

            var el = now - _flashStart;
            if (el >= FlashTotal)
            {
                _flashing = false;
            }
            else
            {
                var peak = Math.Min(1.0, Math.Max(rest * 2.0, rest + 0.3));
                double f;
                if (el < FlashRise)
                {
                    var u = el / FlashRise;
                    f = 1 - (1 - u) * (1 - u);              // quadratic ease out up
                }
                else
                {
                    var u = (el - FlashRise) / (FlashTotal - FlashRise);
                    f = 1 - u * u;                          // quadratic ease in down
                }

                level = Math.Max(level, rest + (peak - rest) * f);
                more = true;
            }
        }

        _level = level;
        ApplyShadow(level);
        return more;
    }

    private void ApplyShadow(double intensity)
    {
        var c = GlowColor;
        BoxShadow = new BoxShadows(new BoxShadow
        {
            OffsetX = 0,
            OffsetY = 0,
            Blur = GlowRadius,
            Spread = 0,
            Color = Color.FromArgb((byte)Math.Clamp(intensity * c.A, 0, 255), c.R, c.G, c.B),
        });
    }
}
