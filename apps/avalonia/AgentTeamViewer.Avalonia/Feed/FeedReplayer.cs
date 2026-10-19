using System.IO;
using Avalonia.Threading;

namespace AgentTeamViewer.Feed;

/// <summary>
/// Reads a whole session directory up front and replays it at a fixed rate on the dispatcher
/// (used by <c>--demo</c>) so the animations are visible.
/// </summary>
public sealed class FeedReplayer : IDisposable
{
    private readonly List<FeedEvent> _events = new();
    private readonly DispatcherTimer _timer;
    private int _index;

    public event Action<FeedEvent>? EventReceived;
    public event Action? Finished;

    public int Total => _events.Count;
    public int Position => _index;

    public FeedReplayer(string sessionDirectory, double eventsPerSecond = 20)
    {
        foreach (var path in FeedFiles.Chunks(sessionDirectory).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var line in File.ReadLines(path))
            {
                var ev = FeedParser.Parse(line.TrimEnd('\r'));
                if (ev is not null) _events.Add(ev);
            }
        }
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, eventsPerSecond)),
        };
        _timer.Tick += Tick;
    }

    public void Start() => _timer.Start();

    private void Tick(object? sender, EventArgs e)
    {
        if (_index >= _events.Count)
        {
            _timer.Stop();
            Finished?.Invoke();
            return;
        }
        EventReceived?.Invoke(_events[_index++]);
    }

    public void Dispose() => _timer.Stop();
}
