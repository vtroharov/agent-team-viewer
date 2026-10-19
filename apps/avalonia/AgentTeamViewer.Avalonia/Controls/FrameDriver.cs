using Avalonia;
using Avalonia.Controls;

namespace AgentTeamViewer.Controls;

/// <summary>
/// Drives a per-frame callback through <see cref="TopLevel.RequestAnimationFrame"/> without
/// allocating: the delegate is created once. The tick returns true while it wants more frames.
/// </summary>
internal sealed class FrameDriver
{
    private readonly Visual _owner;
    private readonly Func<TimeSpan, bool> _tick;
    private readonly Action<TimeSpan> _callback;
    private bool _pending;
    private bool _wanted;
    private TimeSpan _last = TimeSpan.MinValue;

    public FrameDriver(Visual owner, Func<TimeSpan, bool> tick)
    {
        _owner = owner;
        _tick = tick;
        _callback = OnFrame;
    }

    public bool Running => _wanted;

    /// <summary>Starts (or keeps) the loop. Safe to call every frame.</summary>
    public void Request()
    {
        _wanted = true;
        if (_pending) return;
        var top = TopLevel.GetTopLevel(_owner);
        if (top is null) return; // retried from OnAttached
        _pending = true;
        top.RequestAnimationFrame(_callback);
    }

    public void Stop()
    {
        _wanted = false;
        _pending = false;
    }

    /// <summary>Call when the owner attaches to a visual tree: resumes if a loop was wanted.</summary>
    public void OnAttached()
    {
        _pending = false;
        if (_wanted) Request();
    }

    public void OnDetached() => _pending = false;

    private void OnFrame(TimeSpan t)
    {
        if (t == _last) return; // stale duplicate chain after a re-attach
        _last = t;
        _pending = false;
        if (!_wanted) return;
        if (_tick(t)) Request();
        else _wanted = false;
    }
}
