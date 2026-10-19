using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;

namespace AgentTeamViewer.Views;

/// <summary>
/// A tiny frame-driven tween (one double, one callback per frame) used for the card slide-in,
/// the in-place expand/collapse, the dismiss fold and the drag shifts. Runs on
/// <see cref="TopLevel.RequestAnimationFrame"/>, so it is vsync-paced on every platform.
/// </summary>
public sealed class Tween
{
    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);
    public static double CubicIn(double t) => t * t * t;

    private readonly TopLevel? _top;
    private readonly double _from, _to;
    private readonly TimeSpan _duration, _delay;
    private readonly Func<double, double> _ease;
    private readonly Action<double> _apply;
    private readonly Action? _completed;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _cancelled;

    private Tween(Visual host, double from, double to, TimeSpan duration, TimeSpan delay, Func<double, double>? ease, Action<double> apply, Action? completed)
    {
        _top = TopLevel.GetTopLevel(host);
        _from = from; _to = to; _duration = duration; _delay = delay;
        _ease = ease ?? (t => t);
        _apply = apply;
        _completed = completed;
    }

    /// <summary>Starts a tween. Without a TopLevel (host not attached) it jumps to the end value at once.</summary>
    public static Tween Start(Visual host, double from, double to, TimeSpan duration, Action<double> apply,
        Func<double, double>? ease = null, Action? completed = null, TimeSpan delay = default)
    {
        var tw = new Tween(host, from, to, duration, delay, ease, apply, completed);
        if (tw._top is null) { apply(to); completed?.Invoke(); tw._cancelled = true; return tw; }
        apply(from);
        tw._top.RequestAnimationFrame(tw.Frame);
        return tw;
    }

    /// <summary>Stops without calling the completion callback; the last applied value stays.</summary>
    public void Cancel() => _cancelled = true;

    private void Frame(TimeSpan _)
    {
        if (_cancelled) return;
        var elapsed = _clock.Elapsed - _delay;
        double t = elapsed <= TimeSpan.Zero ? 0 : Math.Min(1, elapsed / _duration);
        _apply(_from + (_to - _from) * _ease(t));
        if (t >= 1) { _cancelled = true; _completed?.Invoke(); return; }
        _top!.RequestAnimationFrame(Frame);
    }
}
