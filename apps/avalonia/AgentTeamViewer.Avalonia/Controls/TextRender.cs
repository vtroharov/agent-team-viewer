using Avalonia;
using Avalonia.Media;

namespace AgentTeamViewer.Controls;

/// <summary>
/// Style-settable wrapper for <see cref="RenderOptions"/> text rendering. In Avalonia 11.3 the render
/// options are only reachable through Get/Set methods, which a Style setter cannot target, so the theme
/// sets this attached property on Window and the change handler forwards it (RenderOptions inherits).
/// </summary>
public static class TextRender
{
    public static readonly AttachedProperty<TextRenderingMode> ModeProperty =
        AvaloniaProperty.RegisterAttached<Visual, Visual, TextRenderingMode>("TextRenderMode");

    static TextRender()
    {
        ModeProperty.Changed.AddClassHandler<Visual>((v, e) =>
            RenderOptions.SetTextRenderingMode(v, e.GetNewValue<TextRenderingMode>()));
    }

    public static TextRenderingMode GetMode(Visual v) => v.GetValue(ModeProperty);

    public static void SetMode(Visual v, TextRenderingMode value) => v.SetValue(ModeProperty, value);
}
