using System.Text;

namespace AgentTeamViewer.Model;

public enum BlockKind
{
    Prompt,      // row door=prompt (the user's line)
    Delivery,    // row door=delivery (incoming from another agent)
    Notice,      // row door=notice|note|hook-context|attachment|command
    Thinking,    // chunk type=thinking (coalesced)
    Text,        // chunk type=text (coalesced)
    Tool,        // tool start (+ end paired by id)
    TurnStart,   // turn phase=start
    TurnEnd,     // turn phase=end
}

public enum ToolStatus { Running, Done, Failed }

/// <summary>
/// One visual block in an agent's log. Chunk blocks grow as consecutive chunks of the same
/// type arrive; tool blocks are updated in place when their end event arrives.
/// </summary>
public sealed class LogBlock : ObservableObject
{
    private readonly StringBuilder _text = new();
    private string? _textCache;
    private ToolStatus _toolStatus;
    private string? _result;
    private long? _durationMs;
    private bool _isExpanded;
    private bool _isTruncated;

    public LogBlock(BlockKind kind, long t)
    {
        Kind = kind;
        T = t;
    }

    public BlockKind Kind { get; }
    /// <summary>Feed time (Unix ms) of the event that created the block.</summary>
    public long T { get; }

    /// <summary>Main text: the prompt, the coalesced model output, the turn text, or the tool summary.</summary>
    public string Text => _textCache ??= _text.ToString();

    /// <summary>Who sent a delivery row / message (agent name or id).</summary>
    public string? From { get; init; }
    /// <summary>Row door for notices, turn reason for turn ends.</summary>
    public string? Tag { get; init; }

    // ---- tool-specific ----
    public string? ToolId { get; init; }
    public string? ToolName { get; init; }
    public string? ToolInput { get; init; }

    public ToolStatus ToolStatus { get => _toolStatus; set { if (Set(ref _toolStatus, value)) { Raise(nameof(Summary)); Raise(nameof(StatusText)); } } }
    public string? Result { get => _result; set { if (Set(ref _result, value)) Raise(nameof(HasDetails)); } }
    public long? DurationMs { get => _durationMs; set { if (Set(ref _durationMs, value)) { Raise(nameof(DurationText)); Raise(nameof(StatusText)); } } }
    public bool IsTruncated { get => _isTruncated; set => Set(ref _isTruncated, value); }
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public bool HasDetails => !string.IsNullOrEmpty(ToolInput) || !string.IsNullOrEmpty(Result);

    public string DurationText => DurationMs is long ms ? FormatDuration(ms) : "";

    public string StatusText => Kind switch
    {
        BlockKind.Tool => ToolStatus switch
        {
            ToolStatus.Running => "running",
            ToolStatus.Done => DurationMs is null ? "done" : $"done {DurationText}",
            _ => DurationMs is null ? "failed" : $"failed {DurationText}",
        },
        BlockKind.TurnEnd => DurationMs is null ? Tag ?? "" : $"{Tag} {DurationText}",
        _ => "",
    };

    /// <summary>Appends streamed text (chunk coalescing).</summary>
    public void Append(string more)
    {
        if (string.IsNullOrEmpty(more)) return;
        _text.Append(more);
        _textCache = null;
        Raise(nameof(Text));
        Raise(nameof(Summary));
    }

    public void SetText(string text)
    {
        _text.Clear();
        _text.Append(text);
        _textCache = null;
        Raise(nameof(Text));
        Raise(nameof(Summary));
    }

    /// <summary>One line, suited to the compact agent card.</summary>
    public string Summary
    {
        get
        {
            const int Max = 96;
            switch (Kind)
            {
                case BlockKind.Prompt: return "> " + OneLine(Text, Max - 2);
                case BlockKind.Delivery: return $"← {From ?? "?"}: {OneLine(Text, Max - 4)}";
                case BlockKind.Notice: return $"· {OneLine(Text, Max - 2)}";
                case BlockKind.Thinking: return "∴ " + LastLine(Text, Max - 2);
                case BlockKind.Text: return LastLine(Text, Max);
                case BlockKind.Tool:
                {
                    var glyph = ToolStatus switch { ToolStatus.Running => "▸", ToolStatus.Done => "✓", _ => "✗" };
                    var label = string.IsNullOrEmpty(Text) ? (ToolName ?? "tool") : Text;
                    return $"{glyph} {OneLine(label, Max - 2)}";
                }
                case BlockKind.TurnStart: return "┌ " + OneLine(Text, Max - 2);
                case BlockKind.TurnEnd: return "└ turn " + StatusText;
            }
            return OneLine(Text, Max);
        }
    }

    public static string OneLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        bool lastWasSpace = false;
        foreach (var ch in s)
        {
            bool ws = char.IsWhiteSpace(ch);
            if (ws) { if (lastWasSpace) continue; sb.Append(' '); lastWasSpace = true; }
            else { sb.Append(ch); lastWasSpace = false; }
            if (sb.Length > max) break;
        }
        var r = sb.ToString().Trim();
        return r.Length > max ? r[..(max - 1)] + "…" : r;
    }

    /// <summary>The last non-empty line of a growing text, so a card shows what is being written now.</summary>
    public static string LastLine(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int end = s.Length;
        while (end > 0 && char.IsWhiteSpace(s[end - 1])) end--;
        if (end == 0) return "";
        int start = s.LastIndexOf('\n', end - 1) + 1;
        var line = s[start..end];
        return line.Length > max ? "…" + line[^(max - 1)..] : line;
    }

    public static string FormatDuration(long ms)
    {
        if (ms < 1000) return $"{ms}ms";
        if (ms < 60_000) return $"{ms / 1000.0:0.0}s";
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.Hours > 0 ? $"{(int)ts.TotalHours}h{ts.Minutes:00}m" : $"{ts.Minutes}m{ts.Seconds:00}s";
    }
}
