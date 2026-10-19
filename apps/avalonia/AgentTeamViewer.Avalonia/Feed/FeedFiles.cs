using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTeamViewer.Feed;

/// <summary>File-level conventions of a session directory: which files are event chunks, and the control channel.</summary>
public static class FeedFiles
{
    private static readonly Regex ChunkName = new(@"^\d+\.jsonl$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public const string ControlFileName = "control.jsonl";

    /// <summary>Event chunk files (000001.jsonl ...) only; control.jsonl and anything else is not part of the event stream.</summary>
    public static string[] Chunks(string directory) =>
        Directory.GetFiles(directory, "*.jsonl").Where(p => ChunkName.IsMatch(Path.GetFileName(p))).ToArray();

    /// <summary>
    /// Appends one dismiss request to the session's control.jsonl (append only, shared with the mod) and
    /// returns the request id. Throws on I/O failure.
    /// </summary>
    public static string AppendDismiss(string sessionDirectory, string agentId)
    {
        var id = Guid.NewGuid().ToString();
        var line = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["k"] = "dismiss",
            ["id"] = id,
            ["agent"] = agentId,
        }) + "\n";
        var path = Path.Combine(sessionDirectory, ControlFileName);
        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new UTF8Encoding(false).GetBytes(line);
        fs.Write(bytes, 0, bytes.Length);
        return id;
    }
}
