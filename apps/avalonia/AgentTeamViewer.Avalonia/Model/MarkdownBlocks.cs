using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentTeamViewer.Model;

// A small, UI-framework-free Markdown tree. Plain .NET plus the Markdig parser: no WPF or Avalonia types,
// so other front ends can copy this file verbatim and render the records however they like.

public enum MdAlign { Default, Left, Center, Right }

/// <summary>Block-level node.</summary>
public abstract record MdBlock;
public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;
public sealed record MdTableCell(IReadOnlyList<MdInline> Inlines);
public sealed record MdTable(
    IReadOnlyList<MdTableCell> Header,
    IReadOnlyList<IReadOnlyList<MdTableCell>> Rows,
    IReadOnlyList<MdAlign> Alignments) : MdBlock;
public sealed record MdCodeBlock(string? Language, string Text) : MdBlock;
/// <summary>Each item is the list of blocks inside that item.</summary>
public sealed record MdBulletList(IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdOrderedList(int Start, IReadOnlyList<IReadOnlyList<MdBlock>> Items) : MdBlock;
public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
public sealed record MdThematicBreak : MdBlock;

/// <summary>Inline node.</summary>
public abstract record MdInline;
public sealed record MdText(string Text) : MdInline;
public sealed record MdBold(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdItalic(IReadOnlyList<MdInline> Content) : MdInline;
public sealed record MdCode(string Text) : MdInline;
public sealed record MdLink(IReadOnlyList<MdInline> Content, string Url) : MdInline;
public sealed record MdLineBreak : MdInline;

/// <summary>
/// Parses Markdown text into the record tree. Results are cached per text. Parsing never throws:
/// anything Markdig cannot make sense of (an unfinished table, an unclosed code fence while the text is
/// still streaming) comes out as what it parses to, or as plain text.
/// </summary>
public static class MarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoLinks()
        .Build();

    private const int CacheLimit = 600;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<MdBlock>> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> Order = new();

    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<MdBlock>();
        lock (Gate)
        {
            if (Cache.TryGetValue(text, out var hit)) return hit;
        }

        IReadOnlyList<MdBlock> result;
        try
        {
            var doc = Markdown.Parse(text, Pipeline);
            result = Blocks(doc);
        }
        catch (Exception)
        {
            result = new MdBlock[] { new MdParagraph(new MdInline[] { new MdText(text) }) };
        }

        lock (Gate)
        {
            if (Cache.TryAdd(text, result))
            {
                Order.Enqueue(text);
                while (Order.Count > CacheLimit) Cache.Remove(Order.Dequeue());
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ blocks

    private static List<MdBlock> Blocks(ContainerBlock container)
    {
        var list = new List<MdBlock>();
        foreach (var b in container) AddBlock(b, list);
        return list;
    }

    private static void AddBlock(Block b, List<MdBlock> into)
    {
        switch (b)
        {
            case HeadingBlock h:
                into.Add(new MdHeading(Math.Clamp(h.Level, 1, 6), Inlines(h.Inline)));
                break;
            case ParagraphBlock p:
                into.Add(new MdParagraph(Inlines(p.Inline)));
                break;
            case FencedCodeBlock f:
                into.Add(new MdCodeBlock(string.IsNullOrWhiteSpace(f.Info) ? null : f.Info!.Trim(), LinesText(f)));
                break;
            case CodeBlock c:                                  // indented code
                into.Add(new MdCodeBlock(null, LinesText(c)));
                break;
            case ListBlock l:
            {
                var items = new List<IReadOnlyList<MdBlock>>();
                foreach (var item in l)
                    if (item is ListItemBlock li) items.Add(Blocks(li));
                if (l.IsOrdered)
                {
                    int start = int.TryParse(l.OrderedStart, out var s) ? s : 1;
                    into.Add(new MdOrderedList(start, items));
                }
                else into.Add(new MdBulletList(items));
                break;
            }
            case QuoteBlock q:
                into.Add(new MdQuote(Blocks(q)));
                break;
            case ThematicBreakBlock:
                into.Add(new MdThematicBreak());
                break;
            case Table t:
                into.Add(ToTable(t));
                break;
            case HtmlBlock html:                               // raw HTML is shown as text, never interpreted
                into.Add(new MdParagraph(new MdInline[] { new MdText(LinesText(html)) }));
                break;
            case ContainerBlock cb:
                foreach (var child in cb) AddBlock(child, into);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                into.Add(new MdParagraph(Inlines(leaf.Inline)));
                break;
        }
    }

    private static string LinesText(LeafBlock block)
    {
        var lines = block.Lines.Lines;
        if (lines is null) return "";
        var parts = new List<string>(block.Lines.Count);
        for (int i = 0; i < block.Lines.Count; i++) parts.Add(lines[i].Slice.ToString());
        return string.Join("\n", parts).TrimEnd('\r', '\n');
    }

    private static MdTable ToTable(Table t)
    {
        var header = new List<MdTableCell>();
        var rows = new List<IReadOnlyList<MdTableCell>>();
        foreach (var rowBlock in t)
        {
            if (rowBlock is not TableRow row) continue;
            var cells = new List<MdTableCell>();
            foreach (var cellBlock in row)
                if (cellBlock is TableCell cell) cells.Add(new MdTableCell(CellInlines(cell)));
            if (row.IsHeader && header.Count == 0) header = cells;
            else rows.Add(cells);
        }

        int cols = Math.Max(header.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        var aligns = new List<MdAlign>(cols);
        for (int i = 0; i < cols; i++)
        {
            var a = i < t.ColumnDefinitions.Count ? t.ColumnDefinitions[i].Alignment : null;
            aligns.Add(a switch
            {
                TableColumnAlign.Left => MdAlign.Left,
                TableColumnAlign.Center => MdAlign.Center,
                TableColumnAlign.Right => MdAlign.Right,
                _ => MdAlign.Default,
            });
        }
        return new MdTable(header, rows, aligns);
    }

    /// <summary>A cell holds blocks (normally one paragraph); flatten them into one inline run.</summary>
    private static List<MdInline> CellInlines(TableCell cell)
    {
        var result = new List<MdInline>();
        foreach (var b in cell)
        {
            if (result.Count > 0) result.Add(new MdLineBreak());
            if (b is LeafBlock { Inline: not null } leaf) result.AddRange(Inlines(leaf.Inline));
        }
        return result;
    }

    // ------------------------------------------------------------------ inlines

    private static List<MdInline> Inlines(ContainerInline? container)
    {
        var list = new List<MdInline>();
        if (container is null) return list;
        for (var i = container.FirstChild; i is not null; i = i.NextSibling) AddInline(i, list);
        return list;
    }

    private static void AddInline(Inline i, List<MdInline> into)
    {
        switch (i)
        {
            case LiteralInline lit:
                into.Add(new MdText(lit.Content.ToString()));
                break;
            case CodeInline code:
                into.Add(new MdCode(code.Content));
                break;
            case LineBreakInline:                              // soft and hard breaks both keep the author's line breaks
                into.Add(new MdLineBreak());
                break;
            case EmphasisInline em:
            {
                var content = Inlines(em);
                if (em.DelimiterChar is '*' or '_')
                    into.Add(em.DelimiterCount >= 2 ? new MdBold(content) : new MdItalic(content));
                else into.AddRange(content);                   // ~~strike~~ and friends: show the content
                break;
            }
            case AutolinkInline auto:
                into.Add(new MdLink(new MdInline[] { new MdText(auto.Url) }, auto.Url));
                break;
            case LinkInline link:
            {
                var content = Inlines(link);
                if (link.IsImage)                              // no images in a log: show the alt text
                {
                    into.Add(new MdText(content.Count == 0 ? "[image]" : "[image: " + Plain(content) + "]"));
                    break;
                }
                if (content.Count == 0) content.Add(new MdText(link.Url ?? ""));
                into.Add(string.IsNullOrEmpty(link.Url) ? new MdItalic(content) : new MdLink(content, link.Url!));
                break;
            }
            case HtmlInline html:
                into.Add(new MdText(html.Tag));
                break;
            case HtmlEntityInline ent:
                into.Add(new MdText(ent.Transcoded.ToString()));
                break;
            case ContainerInline c:
                into.AddRange(Inlines(c));
                break;
        }
    }

    /// <summary>The visible text of some inlines with no formatting.</summary>
    public static string Plain(IEnumerable<MdInline> inlines)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(IEnumerable<MdInline> xs)
        {
            foreach (var x in xs)
            {
                switch (x)
                {
                    case MdText t: sb.Append(t.Text); break;
                    case MdCode c: sb.Append(c.Text); break;
                    case MdLineBreak: sb.Append(' '); break;
                    case MdBold b: Walk(b.Content); break;
                    case MdItalic it: Walk(it.Content); break;
                    case MdLink l: Walk(l.Content); break;
                }
            }
        }
        Walk(inlines);
        return sb.ToString();
    }
}
