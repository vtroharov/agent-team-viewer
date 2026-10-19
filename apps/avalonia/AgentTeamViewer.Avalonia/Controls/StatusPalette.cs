using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AgentTeamViewer.Controls;

/// <summary>
/// Colour tokens as C# constants. These mirror Theme/Colors.axaml and are used as
/// fallbacks by the controls when the theme dictionaries are not merged (design time,
/// Gallery hosted elsewhere). Prefer the AXAML resources in views.
/// </summary>
public static class Tokens
{
    public static readonly Color BgDeep = Color.FromRgb(0x0B, 0x10, 0x20);
    public static readonly Color BgPanel = Color.FromRgb(0x0E, 0x15, 0x26);
    public static readonly Color BgTerminal = Color.FromRgb(0x07, 0x0B, 0x16);
    public static readonly Color Cyan = Color.FromRgb(0x4F, 0xD9, 0xFF);
    public static readonly Color CyanDim = Color.FromRgb(0x2A, 0x7A, 0x99);
    public static readonly Color CyanGlow = Color.FromArgb(0x8C, 0x4F, 0xD9, 0xFF);
    public static readonly Color TextPrimary = Color.FromRgb(0xD8, 0xF3, 0xFF);
    public static readonly Color TextDim = Color.FromRgb(0x7F, 0xA6, 0xB8);
    public static readonly Color TextThinking = Color.FromRgb(0x6E, 0x8E, 0xA0);
    public static readonly Color TextFaint = Color.FromRgb(0x3F, 0x55, 0x66);
    public static readonly Color Amber = Color.FromRgb(0xFF, 0xB3, 0x47);
    public static readonly Color Green = Color.FromRgb(0x4C, 0xE0, 0xA6);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x5C, 0x7A);
    public static readonly Color UserPrompt = Color.FromRgb(0xB3, 0x88, 0xFF);

    /// <summary>Looks a Color up in application resources, falling back to the constant.</summary>
    public static Color Resolve(string key, Color fallback)
    {
        try
        {
            var app = Application.Current;
            if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var v))
            {
                if (v is Color c) return c;
                if (v is ISolidColorBrush b) return b.Color;
            }
        }
        catch { /* no application / resources not merged */ }
        return fallback;
    }

    /// <summary>Looks a Brush up in application resources, falling back to a brush of the colour.</summary>
    public static IBrush ResolveBrush(string key, Color fallback)
    {
        try
        {
            var app = Application.Current;
            if (app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b)
                return b;
        }
        catch { /* ignore */ }
        return Solid(fallback);
    }

    public static ImmutableSolidColorBrush Solid(Color c) => new(c);

    public static Color WithAlpha(Color c, double alpha)
        => Color.FromArgb((byte)Math.Clamp(alpha * 255.0, 0, 255), c.R, c.G, c.B);

    /// <summary>A radial glow: colour at <paramref name="alpha"/> in the middle, fading to transparent at the rim.</summary>
    internal static IBrush RadialGlow(Color color, double alpha, double solidUntil = 0.35)
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(WithAlpha(color, alpha), solidUntil));
        b.GradientStops.Add(new GradientStop(WithAlpha(color, alpha * 0.45), 0.6));
        b.GradientStops.Add(new GradientStop(WithAlpha(color, 0), 1.0));
        return b.ToImmutable();
    }
}

/// <summary>Coarse at-a-glance grouping of a status, used by <see cref="StatusGlyph"/> in Ready mode.</summary>
public enum ReadyState
{
    /// <summary>Finished or idle and ready for more work: done, completed, idle, waiting.</summary>
    Ready,
    /// <summary>Busy: running, thinking, tool, working, pending.</summary>
    Working,
    /// <summary>failed, killed, error.</summary>
    Failed,
    /// <summary>gone or unknown.</summary>
    Gone,
}

/// <summary>
/// Status word helpers: normalises feed words to the eight canonical ones and resolves colours.
/// Canonical: running, thinking, tool, waiting, done, failed, idle, gone.
/// </summary>
public static class StatusPalette
{
    /// <summary>
    /// Groups a status for the ready/working/failed indicator. Goes through <see cref="Normalize"/>,
    /// except that the raw words pending/starting count as Working (the agent is about to run),
    /// whereas Normalize maps them to idle for colour purposes.
    /// </summary>
    public static ReadyState ReadyGroup(string? status)
    {
        var raw = status?.Trim().ToLowerInvariant();
        if (raw is "pending" or "starting") return ReadyState.Working;
        return Normalize(status) switch
        {
            "done" or "idle" or "waiting" => ReadyState.Ready,
            "running" or "thinking" or "tool" => ReadyState.Working,
            "failed" => ReadyState.Failed,
            _ => ReadyState.Gone,
        };
    }

    public static string Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "idle";
        var s = status.Trim().ToLowerInvariant();
        return s switch
        {
            "running" or "thinking" or "tool" or "waiting" or "done" or "failed" or "idle" or "gone" => s,
            "completed" or "complete" or "finished" or "success" => "done",
            "killed" or "error" or "aborted" or "refusal" => "failed",
            "working" or "busy" => "tool",
            "pending" or "starting" or "ready" => "idle",
            "removed" or "dropped" => "gone",
            _ => "idle",
        };
    }

    public static bool IsActive(string? status)
    {
        var s = Normalize(status);
        return s is "running" or "thinking" or "tool";
    }

    public static Color ColorFor(string? status)
    {
        var s = Normalize(status);
        return s switch
        {
            "running" => Tokens.Resolve("CyanColor", Tokens.Cyan),
            "thinking" => Tokens.Resolve("TextDimColor", Tokens.TextDim),
            "tool" => Tokens.Resolve("AmberColor", Tokens.Amber),
            "waiting" => Tokens.Resolve("TextDimColor", Tokens.TextDim),
            "done" => Tokens.Resolve("GreenColor", Tokens.Green),
            "failed" => Tokens.Resolve("RedColor", Tokens.Red),
            "gone" => Tokens.Resolve("TextFaintColor", Tokens.TextFaint),
            _ => Tokens.Resolve("CyanDimColor", Tokens.CyanDim),
        };
    }

    public static IBrush BrushFor(string? status)
    {
        var s = Normalize(status);
        return Tokens.ResolveBrush("Status." + s, ColorFor(s));
    }
}

/// <summary>Status string in, Brush out. Unknown statuses map to the idle brush.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => StatusPalette.BrushFor(value?.ToString());

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
