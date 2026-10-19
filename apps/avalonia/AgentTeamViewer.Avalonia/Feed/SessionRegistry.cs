using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;

namespace AgentTeamViewer.Feed;

/// <summary>One Claude Code session record from %USERPROFILE%\.claude\sessions\&lt;pid&gt;.json.</summary>
public sealed record SessionRecord(
    string SessionId, string? Name, string? NameSource, string? Cwd, int Pid,
    string? Status, long UpdatedAt, string? Entrypoint, bool IsLive)
{
    public bool IsUserNamed => string.Equals(NameSource, "user", StringComparison.OrdinalIgnoreCase);
    public bool IsBusy => string.Equals(Status, "busy", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the per-process session records Claude Code keeps, so the viewer can show a session's
/// real name (which the user may change with /rename) and list live sessions that have no feed yet.
/// Re-reads on a FileSystemWatcher event plus a 2 s poll; everything runs on the dispatcher because
/// the files are tiny. Malformed files are skipped. A record is live when its pid is a running
/// process or it was updated within the last 60 s.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private static readonly Regex RecordName = new(@"^\d+\.json$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private const long LiveWindowMs = 60_000;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _poll;
    private readonly Dictionary<string, SessionRecord> _bySessionId = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _refreshQueued;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");

    public string Directory { get; }

    /// <summary>Raised on the dispatcher after any pass in which a record was added, removed or changed.</summary>
    public event Action? Changed;

    public SessionRegistry(Dispatcher dispatcher, string? directory = null)
    {
        _dispatcher = dispatcher;
        Directory = directory ?? DefaultDirectory;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                _watcher = new FileSystemWatcher(Directory, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                _watcher.Changed += (_, _) => QueueRefresh();
                _watcher.Created += (_, _) => QueueRefresh();
                _watcher.Deleted += (_, _) => QueueRefresh();
                _watcher.Renamed += (_, _) => QueueRefresh();
                _watcher.EnableRaisingEvents = true;
            }
        }
        catch (Exception) { _watcher = null; /* poll only */ }
        _poll.Start();
    }

    public IReadOnlyCollection<SessionRecord> All => _bySessionId.Values;

    public IEnumerable<SessionRecord> Live => _bySessionId.Values.Where(r => r.IsLive);

    public SessionRecord? Find(string? sessionId) =>
        string.IsNullOrEmpty(sessionId) ? null : _bySessionId.GetValueOrDefault(sessionId);

    private void QueueRefresh()
    {
        // Watcher events arrive on a pool thread and in bursts; coalesce onto the dispatcher.
        if (_refreshQueued) return;
        _refreshQueued = true;
        _dispatcher.Post(() => { _refreshQueued = false; Refresh(); }, DispatcherPriority.Background);
    }

    /// <summary>Re-reads every record; raises <see cref="Changed"/> if anything differs.</summary>
    public void Refresh()
    {
        var fresh = new Dictionary<string, SessionRecord>(StringComparer.OrdinalIgnoreCase);
        if (System.IO.Directory.Exists(Directory))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                if (!RecordName.IsMatch(Path.GetFileName(path))) continue;   // skip <pid>.<hash>.key and friends
                var rec = ReadRecord(path, now);
                if (rec is null) continue;
                // Two records for one session id (restart in place): keep the newest.
                if (!fresh.TryGetValue(rec.SessionId, out var existing) || rec.UpdatedAt > existing.UpdatedAt)
                    fresh[rec.SessionId] = rec;
            }
        }

        bool changed = fresh.Count != _bySessionId.Count;
        if (!changed)
        {
            foreach (var (id, rec) in fresh)
            {
                if (!_bySessionId.TryGetValue(id, out var old) || old != rec) { changed = true; break; }
            }
        }
        if (!changed) return;

        _bySessionId.Clear();
        foreach (var (id, rec) in fresh) _bySessionId[id] = rec;
        Changed?.Invoke();
    }

    private static SessionRecord? ReadRecord(string path, long now)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var sessionId = Str(r, "sessionId");
            if (string.IsNullOrEmpty(sessionId)) return null;
            int pid = (int)(Num(r, "pid") ?? 0);
            long updated = Num(r, "updatedAt") ?? 0;
            bool live = (pid > 0 && IsProcessRunning(pid)) || (updated > 0 && now - updated < LiveWindowMs);
            return new SessionRecord(sessionId, Str(r, "name"), Str(r, "nameSource"), Str(r, "cwd"), pid,
                Str(r, "status"), updated, Str(r, "entrypoint"), live);
        }
        catch (Exception) { return null; }   // partial write, malformed JSON, locked file: try again next pass
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception) { return false; }   // ArgumentException (no such pid) or access denied
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (long)d;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
        return null;
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
