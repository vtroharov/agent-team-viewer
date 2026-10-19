using System.IO;
using System.Text;
using Avalonia.Threading;

namespace AgentTeamViewer.Feed;

/// <summary>
/// Reads a session directory per FEED.md: every closed chunk once, then the last chunk is
/// re-read whenever its length changes (FileSystemWatcher plus a 150 ms poll fallback).
/// Lines already consumed are skipped by line count because new content is a strict
/// prefix-extension; a final line that fails to parse is left for the next pass.
/// Events are raised on the supplied dispatcher, in feed order.
/// </summary>
public sealed class FeedReader : IDisposable
{
    private const int PollMs = 150;

    private readonly string _dir;
    private readonly Dispatcher _dispatcher;
    private readonly AutoResetEvent _wake = new(false);
    private readonly CancellationTokenSource _cts = new();
    private FileSystemWatcher? _watcher;
    private Thread? _thread;

    // Per-chunk progress. Closed chunks are marked Complete and never re-read.
    private sealed class ChunkState
    {
        public int LinesConsumed;
        public long LastLength = -1;
        public bool Complete;
    }
    private readonly Dictionary<string, ChunkState> _chunks = new(StringComparer.OrdinalIgnoreCase);
    private bool _caughtUpRaised;

    /// <summary>One event, raised on the dispatcher.</summary>
    public event Action<FeedEvent>? EventReceived;
    /// <summary>Raised once, after the initial read of everything already on disk.</summary>
    public event Action? CaughtUp;
    /// <summary>Raised when a read error occurs (the reader keeps running).</summary>
    public event Action<string>? Error;

    public string Directory => _dir;

    public FeedReader(string sessionDirectory, Dispatcher dispatcher)
    {
        _dir = sessionDirectory;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_thread is not null) return;
        try
        {
            _watcher = new FileSystemWatcher(_dir, "*.jsonl")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => _wake.Set();
            _watcher.Created += (_, _) => _wake.Set();
            _watcher.Renamed += (_, _) => _wake.Set();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _watcher = null;
            Report($"FileSystemWatcher unavailable, polling only: {ex.Message}");
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "agent-feed tail" };
        _thread.Start();
    }

    private void Loop()
    {
        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            try { Scan(); }
            catch (Exception ex) { Report($"feed scan failed: {ex.Message}"); }

            if (!_caughtUpRaised)
            {
                _caughtUpRaised = true;
                _dispatcher.Post(() => CaughtUp?.Invoke(), DispatcherPriority.Background);
            }
            // Wake early on a watcher notification, or poll after PollMs regardless.
            _wake.WaitOne(PollMs);
        }
    }

    private void Scan()
    {
        if (!System.IO.Directory.Exists(_dir)) return;
        var files = FeedFiles.Chunks(_dir);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        if (files.Length == 0) return;

        var batch = new List<FeedEvent>();
        for (int i = 0; i < files.Length; i++)
        {
            bool isLast = i == files.Length - 1;
            var path = files[i];
            if (!_chunks.TryGetValue(path, out var st))
            {
                st = new ChunkState();
                _chunks[path] = st;
            }
            if (st.Complete) continue;

            long length;
            try { length = new FileInfo(path).Length; }
            catch (IOException) { continue; }

            if (isLast && length == st.LastLength) continue; // unchanged since the last pass

            string content;
            try { content = ReadAll(path); }
            catch (IOException) { continue; }     // writer mid-rewrite; try again next pass
            catch (UnauthorizedAccessException) { continue; }

            st.LastLength = length;
            ConsumeLines(content, st, treatFinalLineAsClosed: !isLast, batch);
            if (!isLast) st.Complete = true;        // closed chunks never change again
        }

        if (batch.Count > 0)
        {
            _dispatcher.Post(() =>
            {
                foreach (var ev in batch) EventReceived?.Invoke(ev);
            }, DispatcherPriority.Background);
        }
    }

    private static string ReadAll(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Splits <paramref name="content"/> into lines, skips the ones already consumed, and parses the
    /// rest. The final line is only consumed if it parses (or the chunk is closed).
    /// </summary>
    private static void ConsumeLines(string content, ChunkState st, bool treatFinalLineAsClosed, List<FeedEvent> batch)
    {
        var lines = content.Split('\n');
        int count = lines.Length;
        bool endsWithNewline = content.Length > 0 && content[^1] == '\n';
        if (endsWithNewline) count--;           // drop the empty tail segment
        if (count < st.LinesConsumed)
        {
            // Shorter than before: the writer is mid-rewrite (truncate + write). Wait for it to grow back.
            return;
        }

        for (int i = st.LinesConsumed; i < count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            bool isFinal = i == count - 1;
            var ev = FeedParser.Parse(line);
            if (ev is null)
            {
                if (isFinal && !treatFinalLineAsClosed)
                {
                    // Partial write of the last line: leave it unconsumed for the next pass.
                    return;
                }
                st.LinesConsumed = i + 1;       // junk mid-file: skip it
                continue;
            }
            batch.Add(ev);
            st.LinesConsumed = i + 1;
        }
    }

    private void Report(string message)
    {
        _dispatcher.Post(() => Error?.Invoke(message));
    }

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Set();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
