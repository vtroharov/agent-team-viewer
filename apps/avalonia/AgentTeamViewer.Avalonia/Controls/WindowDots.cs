using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentTeamViewer.Controls;

/// <summary>The three small coloured dots in a panel's title bar.</summary>
public class WindowDots : Control
{
    public static readonly StyledProperty<double> DotSizeProperty =
        AvaloniaProperty.Register<WindowDots, double>(nameof(DotSize), 8.0);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<WindowDots, double>(nameof(Spacing), 6.0);

    public static readonly StyledProperty<IBrush?> FirstBrushProperty =
        AvaloniaProperty.Register<WindowDots, IBrush?>(nameof(FirstBrush), Tokens.Solid(Tokens.Red));

    public static readonly StyledProperty<IBrush?> SecondBrushProperty =
        AvaloniaProperty.Register<WindowDots, IBrush?>(nameof(SecondBrush), Tokens.Solid(Tokens.Amber));

    public static readonly StyledProperty<IBrush?> ThirdBrushProperty =
        AvaloniaProperty.Register<WindowDots, IBrush?>(nameof(ThirdBrush), Tokens.Solid(Tokens.Green));

    /// <summary>True renders the dots at 55% opacity (for compact cards).</summary>
    public static readonly StyledProperty<bool> DimProperty =
        AvaloniaProperty.Register<WindowDots, bool>(nameof(Dim), false);

    static WindowDots()
    {
        AffectsMeasure<WindowDots>(DotSizeProperty, SpacingProperty);
        AffectsRender<WindowDots>(DotSizeProperty, SpacingProperty, FirstBrushProperty,
            SecondBrushProperty, ThirdBrushProperty, DimProperty);
        FocusableProperty.OverrideDefaultValue<WindowDots>(false);
        IsHitTestVisibleProperty.OverrideDefaultValue<WindowDots>(false);
        VerticalAlignmentProperty.OverrideDefaultValue<WindowDots>(VerticalAlignment.Center);
    }

    public double DotSize
    {
        get => GetValue(DotSizeProperty);
        set => SetValue(DotSizeProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public IBrush? FirstBrush
    {
        get => GetValue(FirstBrushProperty);
        set => SetValue(FirstBrushProperty, value);
    }

    public IBrush? SecondBrush
    {
        get => GetValue(SecondBrushProperty);
        set => SetValue(SecondBrushProperty, value);
    }

    public IBrush? ThirdBrush
    {
        get => GetValue(ThirdBrushProperty);
        set => SetValue(ThirdBrushProperty, value);
    }

    public bool Dim
    {
        get => GetValue(DimProperty);
        set => SetValue(DimProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(DotSize * 3 + Spacing * 2, DotSize);

    public override void Render(DrawingContext dc)
    {
        if (Dim)
        {
            using (dc.PushOpacity(0.55)) DrawDots(dc);
        }
        else
        {
            DrawDots(dc);
        }
    }

    private void DrawDots(DrawingContext dc)
    {
        var d = DotSize;
        var r = d / 2;
        var y = Bounds.Height / 2;
        var step = d + Spacing;

        dc.DrawEllipse(FirstBrush, null, new Point(r, y), r, r);
        dc.DrawEllipse(SecondBrush, null, new Point(r + step, y), r, r);
        dc.DrawEllipse(ThirdBrush, null, new Point(r + step * 2, y), r, r);
    }
}
