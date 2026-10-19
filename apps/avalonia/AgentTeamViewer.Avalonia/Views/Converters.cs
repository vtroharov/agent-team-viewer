using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>Converter instances for compiled bindings: <c>{x:Static v:Converters.StatusBrush}</c>.</summary>
public static class Converters
{
    public static readonly IValueConverter StatusBrush = new StatusWordToBrushConverter();
    public static readonly IValueConverter ToolStatusWord = new ToolStatusToWordConverter();
    public static readonly IValueConverter JsonPretty = new JsonPrettyConverter();
    public static readonly IValueConverter DepthMargin = new DepthToMarginConverter { Step = 22 };
    /// <summary>int (or collection count) is zero -> true.</summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int, bool>(n => n == 0);
}

/// <summary>Maps a tool status to the status word the theme's status brushes / glyph understand.</summary>
public sealed class ToolStatusToWordConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ToolStatus.Running => "tool",
        ToolStatus.Done => "done",
        ToolStatus.Failed => "failed",
        _ => "idle",
    };
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Looks up "Status.&lt;word&gt;" in application resources, falling back to TextDimBrush, then a literal.</summary>
public sealed class StatusWordToBrushConverter : IValueConverter
{
    private static readonly IBrush Fallback = new ImmutableSolidColorBrush(Color.FromRgb(0x7F, 0xA6, 0xB8));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var word = value switch
        {
            string s => s,
            ToolStatus.Running => "tool",
            ToolStatus.Done => "done",
            ToolStatus.Failed => "failed",
            _ => "idle",
        };
        var app = Application.Current;
        if (app is null) return Fallback;
        var variant = app.ActualThemeVariant;
        if (app.TryGetResource("Status." + word, variant, out var b) && b is IBrush brush) return brush;
        if (app.TryGetResource("TextDimBrush", variant, out var d) && d is IBrush dim) return dim;
        return Fallback;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Depth (int) -> left margin Thickness for nested cards.</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public double Step { get; set; } = 22;
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int d = value is int i ? i : 0;
        return new Thickness(d * Step, 0, 0, 0);
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Pretty-prints a compact JSON string with newlines/indent for the expanded tool input.</summary>
public sealed class JsonPrettyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(s);
            return System.Text.Json.JsonSerializer.Serialize(doc.RootElement, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }
        catch (Exception) { return s; }   // trimmed JSON may be cut mid-value: show raw
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
