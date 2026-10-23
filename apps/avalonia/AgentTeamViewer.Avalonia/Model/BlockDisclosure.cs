using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTeamViewer.Model;

public enum DisclosureKind
{
    None,       // short and plain: no arrow, shown as before
    Message,    // a message from another agent: "sender: summary", body is Markdown
    Note,       // a multi-line note or system row: first line, full text when open
    Thinking,   // a thinking block: first (or, while streaming, latest) line, full text when open
}

/// <summary>How a log block collapses: the header shown while closed, the body shown when open.</summary>
/// <param name="OpenLabel">The header shown while open, for kinds whose body repeats the first line.</param>
public sealed record Disclosure(DisclosureKind Kind, string Header, string OpenLabel, string Body, string? Note, string? Sender);

/// <summary>
/// Works out how a delivery, note, notice or thinking block collapses. Plain .NET, no UI types.
/// Teammate and agent messages arrive wrapped as <c>&lt;teammate-message teammate_id="x" summary="y"&gt;...</c>
/// (or <c>&lt;agent-message from="x"&gt;</c>), sometimes with a lead-in sentence and a boilerplate
/// "this came from another Claude session" paragraph, and sometimes with an idle-notification JSON body.
/// </summary>
public static class BlockDisclosure
{
    private static readonly Regex MessageTag = new(
        @"<(?<t>teammate|agent)-message(?<attrs>[^>]*)>(?<body>.*?)(?:</\k<t>-message>|\z)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Attr = new(@"(?<k>[\w-]+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
    private const int HeaderMax = 160;

    public static readonly Disclosure NotCollapsible = new(DisclosureKind.None, "", "", "", null, null);

    public static Disclosure Parse(LogBlock b)
    {
        var text = b.Text ?? "";
        switch (b.Kind)
        {
            case BlockKind.Thinking:
            {
                var line = b.IsTail ? LogBlock.LastLine(text, HeaderMax) : FirstLine(text, HeaderMax);
                return new Disclosure(DisclosureKind.Thinking, line, "thinking", text, null, null);
            }
            case BlockKind.Delivery:
            case BlockKind.Notice:
            case BlockKind.Prompt:
            {
                var m = MessageTag.Match(text);
                if (m.Success) return Message(m, text, b.From);
                if (b.Kind == BlockKind.Delivery) return Delivery(text, b.From);
                if (b.Kind == BlockKind.Notice) return Note(text, b.Tag);
                return NotCollapsible;
            }
        }
        return NotCollapsible;
    }

    // ------------------------------------------------------------------ messages

    private static Disclosure Message(Match m, string full, string? fallbackSender)
    {
        string? sender = null, summary = null;
        foreach (Match a in Attr.Matches(m.Groups["attrs"].Value))
        {
            var k = a.Groups["k"].Value.ToLowerInvariant();
            if (k is "teammate_id" or "from") sender ??= a.Groups["v"].Value;
            else if (k == "summary") summary = a.Groups["v"].Value;
        }
        var body = m.Groups["body"].Value.Trim();

        // Idle notifications (and other JSON payloads) read better as their result text.
        if (TryJson(body, out var jsonSender, out var jsonSummary, out var result, out var pretty))
        {
            sender ??= jsonSender;
            summary ??= jsonSummary;
            body = result ?? "```json\n" + pretty + "\n```";
            summary ??= result is null ? null : FirstSentence(result);
        }

        // What surrounds the tag: lead-in sentences are dropped, the "this came from another session" boilerplate
        // becomes the delivery note, anything else stays visible after the body.
        var outside = (full[..m.Index] + "\n\n" + full[(m.Index + m.Length)..]);
        var note = new List<string>();
        var extra = new List<string>();
        foreach (var para in Regex.Split(outside, @"\r?\n\s*\r?\n"))
        {
            var p = para.Trim();
            if (p.Length == 0 || IsLeadIn(p)) continue;
            if (IsBoilerplate(p)) note.Add(p); else extra.Add(p);
        }
        if (extra.Count > 0) body = body + "\n\n" + string.Join("\n\n", extra);

        sender ??= string.IsNullOrEmpty(fallbackSender) ? "agent" : fallbackSender;
        var header = sender + ": " + (string.IsNullOrWhiteSpace(summary) ? FirstLine(body, HeaderMax) : OneLine(summary, HeaderMax));
        return new Disclosure(DisclosureKind.Message, header, header, body, note.Count == 0 ? null : string.Join("\n\n", note), sender);
    }

    private static Disclosure Delivery(string text, string? from)
    {
        var body = text.Trim();
        var sender = string.IsNullOrEmpty(from) ? "agent" : from;
        bool collapse = body.Contains('\n') || body.Length > 100;
        if (!collapse) return NotCollapsible;
        var header = sender + ": " + FirstLine(body, HeaderMax);
        return new Disclosure(DisclosureKind.Message, header, header, body, null, sender);
    }

    private static Disclosure Note(string text, string? tag)
    {
        var t = text.Trim();
        if (!t.Contains('\n') && t.Length <= 140) return NotCollapsible;
        var label = "· " + (string.IsNullOrEmpty(tag) ? "note" : tag);
        return new Disclosure(DisclosureKind.Note, label + ": " + FirstLine(t, HeaderMax), label, t, null, null);
    }

    private static bool IsLeadIn(string p) =>
        p.StartsWith("Another Claude session sent a message", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("A teammate sent a message", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoilerplate(string p) =>
        p.StartsWith("This came from another Claude session", StringComparison.OrdinalIgnoreCase)
        || p.Contains("other Claude session", StringComparison.OrdinalIgnoreCase)
        || p.Contains("another Claude session", StringComparison.OrdinalIgnoreCase);

    private static bool TryJson(string body, out string? sender, out string? summary, out string? result, out string pretty)
    {
        sender = summary = result = null;
        pretty = body;
        if (!body.StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            string? Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            sender = Str("from");
            summary = Str("summary");
            result = Str("result");
            pretty = JsonSerializer.Serialize(root, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            return Str("type") == "idle_notification" || result is not null;
        }
        catch (JsonException) { return false; }
    }

    // ------------------------------------------------------------------ text helpers

    /// <summary>The first non-empty line, without leading Markdown marks, cut to <paramref name="max"/> characters.</summary>
    public static string FirstLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '>', '-', '*', ' ').Trim();
            if (Regex.IsMatch(line, @"^</?[\w-]+>$")) continue;          // a bare wrapper tag such as <system-reminder>
            if (line.Length > 0) return OneLine(line, max);
        }
        return "";
    }

    public static string OneLine(string s, int max)
    {
        var t = Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length > max ? t[..(max - 1)] + "…" : t;
    }

    /// <summary>The first sentence of a text (up to the first period, question or exclamation mark followed by a space).</summary>
    public static string FirstSentence(string s)
    {
        var line = FirstLine(s, 400);
        var m = Regex.Match(line, @"^.+?[.!?](?=\s|$)");
        return m.Success ? m.Value : line;
    }
}
