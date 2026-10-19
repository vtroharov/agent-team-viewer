using System.IO;
using System.Text.Json;

namespace AgentTeamViewer.Feed;

public sealed record SessionInfo(string Directory, string SessionId, string? Cwd, DateTimeOffset? StartedAt, DateTime MetaMtimeUtc)
{
    public string DisplayName => string.IsNullOrEmpty(Cwd)
        ? SessionId
        : $"{Path.GetFileName(Cwd.TrimEnd('\\', '/'))} · {SessionId}";
}

/// <summary>Finds session directories under the feed root (default %USERPROFILE%\.claude\agent-feed).</summary>
public static class SessionLocator
{
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "agent-feed");

    /// <summary>All sessions, newest meta.json first. Directories without meta.json fall back to directory mtime.</summary>
    public static IReadOnlyList<SessionInfo> ListSessions(string? root = null)
    {
        root ??= DefaultRoot;
        var result = new List<SessionInfo>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var info = Describe(dir);
            if (info is not null) result.Add(info);
        }
        result.Sort((a, b) => b.MetaMtimeUtc.CompareTo(a.MetaMtimeUtc));
        return result;
    }

    public static SessionInfo? FindNewest(string? root = null)
    {
        var all = ListSessions(root);
        return all.Count == 0 ? null : all[0];
    }

    /// <summary>Describes one session directory; tolerant of a missing or malformed meta.json.</summary>
    public static SessionInfo? Describe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var meta = Path.Combine(dir, "meta.json");
        string sessionId = Path.GetFileName(dir);
        string? cwd = null;
        DateTimeOffset? started = null;
        DateTime mtime;
        if (File.Exists(meta))
        {
            mtime = File.GetLastWriteTimeUtc(meta);
            try
            {
                using var fs = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                var r = doc.RootElement;
                if (r.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String) sessionId = s.GetString() ?? sessionId;
                if (r.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String) cwd = c.GetString();
                if (r.TryGetProperty("startedAt", out var st))
                {
                    if (st.ValueKind == JsonValueKind.Number && st.TryGetInt64(out var ms)) started = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    else if (st.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(st.GetString(), out var dto)) started = dto;
                }
            }
            catch (Exception) { /* malformed meta: keep defaults */ }
        }
        else
        {
            // No meta yet (or a foreign directory): only count it if it has chunk files.
            if (FeedFiles.Chunks(dir).Length == 0) return null;
            mtime = Directory.GetLastWriteTimeUtc(dir);
        }
        return new SessionInfo(dir, sessionId, cwd, started, mtime);
    }
}
