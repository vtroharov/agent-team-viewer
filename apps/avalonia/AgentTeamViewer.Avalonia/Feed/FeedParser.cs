using System.Text.Json;

namespace AgentTeamViewer.Feed;

/// <summary>
/// Turns one JSONL line into a <see cref="FeedEvent"/>. Never throws for unknown kinds or
/// fields; returns null only when the line is not a complete JSON object (partial write).
/// </summary>
public static class FeedParser
{
    public static FeedEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            long t = GetLong(root, "t") ?? 0;
            string kind = GetString(root, "k") ?? "";

            try
            {
                return kind switch
                {
                    "session" => new SessionEvent(t, GetString(root, "sessionId"), GetString(root, "cwd"), GetString(root, "version"), GetString(root, "model")),
                    "agent" => ParseAgent(t, root),
                    "turn" => new TurnEvent(t,
                        GetString(root, "agent") ?? "main",
                        GetString(root, "phase") ?? "start",
                        GetString(root, "turnId"),
                        GetString(root, "text"),
                        GetString(root, "reason"),
                        GetString(root, "answer"),
                        GetLong(root, "durationMs"),
                        GetBool(root, "isAborted") ?? false,
                        GetString(root, "model")),
                    "chunk" => new ChunkEvent(t,
                        GetString(root, "agent") ?? "main",
                        GetString(root, "type") ?? "text",
                        GetString(root, "text") ?? "",
                        GetString(root, "model")),
                    "tool" => new ToolEvent(t,
                        GetString(root, "agent") ?? "main",
                        GetString(root, "phase") ?? "start",
                        GetString(root, "id") ?? "",
                        GetString(root, "name"),
                        GetString(root, "summary"),
                        GetRawJson(root, "input"),
                        GetBool(root, "isError") ?? false,
                        GetLong(root, "durationMs"),
                        GetString(root, "text"),
                        GetBool(root, "truncated") ?? false),
                    "row" => new RowEvent(t,
                        GetString(root, "agent") ?? "main",
                        GetString(root, "door") ?? "notice",
                        GetString(root, "role"),
                        GetString(root, "from"),
                        GetString(root, "text")),
                    "message" => new MessageEvent(t,
                        GetString(root, "from") ?? "main",
                        GetString(root, "to"),
                        GetString(root, "toName"),
                        GetString(root, "text")),
                    "control" => new ControlEvent(t, GetString(root, "id"), GetString(root, "agent") ?? "", GetString(root, "status") ?? "", GetString(root, "reason")),
                    "dismissed" => new DismissedEvent(t, GetString(root, "agent") ?? "", GetString(root, "id"), GetString(root, "summary"), GetString(root, "summarySource")),
                    _ => new UnknownEvent(t, kind, line),
                };
            }
            catch (Exception)
            {
                // A malformed known kind (e.g. wrong value type) is treated as unknown, not fatal.
                return new UnknownEvent(t, kind, line);
            }
        }
    }

    private static AgentEvent? ParseAgent(long t, JsonElement root)
    {
        var id = GetString(root, "id");
        if (string.IsNullOrEmpty(id)) return null;
        return new AgentEvent(t, id,
            GetString(root, "name"),
            GetString(root, "type"),
            GetString(root, "description"),
            GetString(root, "parentId"),
            GetBool(root, "isTeammate") ?? false,
            GetString(root, "status"),
            GetString(root, "model"));
    }

    private static string? GetString(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long? GetLong(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
            if (v.TryGetDouble(out var d)) return (long)d;
        }
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var p)) return p;
        return null;
    }

    private static bool? GetBool(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static string? GetRawJson(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.Null ? null : v.GetRawText();
    }
}
