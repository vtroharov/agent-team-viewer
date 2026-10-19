using System.IO;
using System.Text.Json;

namespace AgentTeamViewer.Model;

/// <summary>
/// Persists the user's manual card order per session in
/// LocalApplicationData/AgentTeamViewer.Avalonia/order/&lt;sessionKey&gt;.json as {"order":["a2","a1",...]}.
/// Ids that no longer exist are simply ignored by the reader.
/// </summary>
public static class CardOrderStore
{
    public static string Directory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentTeamViewer.Avalonia", "order");

    private static string PathFor(string sessionKey)
    {
        var safe = string.Concat(sessionKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Directory, safe + ".json");
    }

    /// <summary>Agent id -> rank (0 = top within its sibling group). Empty when nothing was saved.</summary>
    public static Dictionary<string, int> Load(string sessionKey)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var path = PathFor(sessionKey);
            if (!File.Exists(path)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("order", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } id && !result.ContainsKey(id))
                        result[id] = i++;
            }
        }
        catch (Exception) { /* malformed or unreadable: start fresh */ }
        return result;
    }

    public static void Save(string sessionKey, IReadOnlyDictionary<string, int> ranks)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var ordered = ranks.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
            var json = JsonSerializer.Serialize(new { order = ordered }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(PathFor(sessionKey), json);
        }
        catch (Exception) { /* best effort; the in-memory order still applies for this run */ }
    }
}
