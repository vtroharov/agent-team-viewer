using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>
/// Renders Markdown text (see <see cref="MarkdownParser"/>) as Avalonia elements: paragraphs, headings, pipe
/// tables, code blocks, lists, quotes, bold/italic/inline code/links. While text is still streaming it
/// re-renders at most every 200 ms. Text styles are the theme's md-* classes; frame and fill brushes are the
/// theme's Md*Brush resources, with built-in fallbacks so it also works before those exist.
/// </summary>
public sealed class MarkdownView : Border
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(200);
    private const double CellMaxWidth = 320;
    private const double ScrollBarRoom = 9;   // keeps the overlay scrollbar of an overflowing table or code block off its last line

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string>(nameof(Text), "");

    /// <summary>Style class of the TextBlock used for body text (body, delivery, ...).</summary>
    public static readonly StyledProperty<string> BaseClassProperty =
        AvaloniaProperty.Register<MarkdownView, string>(nameof(BaseClass), "body");

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string BaseClass { get => GetValue(BaseClassProperty); set => SetValue(BaseClassProperty, value); }

    /// <summary>What "Copy block" copies instead of the rendered text (for a collapsed view of a longer original).</summary>
    public static readonly StyledProperty<string?> CopySourceProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(CopySource));
    public string? CopySource { get => GetValue(CopySourceProperty); set => SetValue(CopySourceProperty, value); }

    private readonly StackPanel _root = new() { Orientation = Orientation.Vertical };
    private readonly DispatcherTimer _timer;
    private DateTime _lastRender = DateTime.MinValue;
    private string? _renderedText;
    private string? _renderedClass;
    private readonly List<LogText> _texts = new();   // every selectable text of the current render

    static MarkdownView()
    {
        TextProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Schedule());
        BaseClassProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Schedule(force: true));
    }

    public MarkdownView()
    {
        Child = _root;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = MinInterval };
        _timer.Tick += (_, _) => { _timer.Stop(); Render(); };
        Loaded += (_, _) => Render();
        Unloaded += (_, _) => _timer.Stop();
    }

    private void Schedule(bool force = false)
    {
        if (!IsLoaded) return;                                  // Loaded renders the current text
        if (force) { _timer.Stop(); Render(); return; }
        if (_timer.IsEnabled) return;
        var since = DateTime.UtcNow - _lastRender;
        if (since >= MinInterval) Render();
        else { _timer.Interval = MinInterval - since; _timer.Start(); }
    }

    private void Render()
    {
        var text = Text ?? "";
        var cls = BaseClass;
        if (text == _renderedText && cls == _renderedClass) return;
        if (_texts.Any(t => t.HasSelection))
        {
            // Streaming must not wipe a selection in progress: try again shortly, once it is gone.
            _timer.Interval = MinInterval;
            _timer.Start();
            return;
        }
        _renderedText = text;
        _renderedClass = cls;
        _lastRender = DateTime.UtcNow;

        _root.Children.Clear();
        _texts.Clear();
        foreach (var b in MarkdownParser.Parse(text)) _root.Children.Add(Build(b, 0));
    }

    // ------------------------------------------------------------------ resources

    private IBrush BrushOf(string key, string fallbackKey, string fallbackColor)
    {
        var app = Application.Current;
        var variant = ActualThemeVariant;
        if (app is not null)
        {
            if (app.TryGetResource(key, variant, out var b) && b is IBrush brush) return brush;
            if (app.TryGetResource(fallbackKey, variant, out var f) && f is IBrush fb) return fb;
        }
        return new ImmutableSolidColorBrush(Color.Parse(fallbackColor));
    }

    private IBrush TableBorder => BrushOf("MdTableBorderBrush", "HairlineBrush", "#403D6C80");
    private IBrush TableHeaderBg => BrushOf("MdTableHeaderBgBrush", "HairlineBrush", "#FF0F192B");
    private IBrush CodeBg => BrushOf("MdCodeBlockBgBrush", "BgDeepBrush", "#FF04070F");
    private IBrush CodeBorder => BrushOf("MdCodeBlockBorderBrush", "HairlineBrush", "#B32A7A99");
    private IBrush InlineCodeBg => BrushOf("MdInlineCodeBgBrush", "BgPanelBrush", "#FF111C2E");
    private IBrush InlineCodeFg => BrushOf("MdCodeTextBrush", "TextDimBrush", "#FFB4CCD9");
    private IBrush LinkBrush => BrushOf("MdLinkBrush", "CyanBrush", "#FF74B4D0");
    private IBrush QuoteBar => BrushOf("MdQuoteBarBrush", "CyanDimBrush", "#FF2A7A99");

    private FontFamily Mono =>
        Application.Current?.TryGetResource("MonoFont", ActualThemeVariant, out var f) == true && f is FontFamily ff ? ff : new FontFamily("Cascadia Mono, Consolas, Menlo");

    /// <summary>A selectable text whose "Copy block" gives the whole block's Markdown source.</summary>
    private LogText NewText()
    {
        var t = new LogText { BlockSource = CopySource ?? _renderedText };
        _texts.Add(t);
        return t;
    }

    // ------------------------------------------------------------------ blocks

    private Control Build(MdBlock block, int depth) => block switch
    {
        MdParagraph p => Paragraph(p.Inlines, BaseClass, new Thickness(0, 3, 0, 3)),
        MdHeading h => Heading(h),
        MdTable t => Table(t),
        MdCodeBlock c => CodeBlock(c),
        MdBulletList l => List(l.Items, null, depth),
        MdOrderedList l => List(l.Items, l.Start, depth),
        MdQuote q => Quote(q, depth),
        MdThematicBreak => new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = TableBorder },
        _ => new TextBlock(),
    };

    private TextBlock Paragraph(IReadOnlyList<MdInline> inlines, string cls, Thickness margin, string? textClass = null)
    {
        var tb = NewText(); tb.Margin = margin;
        tb.Classes.Add(textClass ?? cls);
        AddInlines(tb, inlines, null);
        return tb;
    }

    private Control Heading(MdHeading h)
    {
        int level = Math.Min(h.Level, 3);
        var tb = NewText(); tb.Margin = new Thickness(0, level == 1 ? 10 : 8, 0, 3);
        tb.Classes.Add("md-h" + level);
        AddInlines(tb, h.Inlines, null);
        return tb;
    }

    private Control CodeBlock(MdCodeBlock c)
    {
        var code = NewText(); code.Text = c.Text;
        code.Classes.Add("md-code");

        var scroll = HorizontalScroller(code);
        Control content = scroll;
        if (!string.IsNullOrEmpty(c.Language))
        {
            var lang = new TextBlock { Text = c.Language, Margin = new Thickness(0, 0, 0, 3) };
            lang.Classes.Add("tag");
            var stack = new StackPanel();
            stack.Children.Add(lang);
            stack.Children.Add(scroll);
            content = stack;
        }
        return new Border
        {
            Background = CodeBg,
            BorderBrush = CodeBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 7, 9, 7),
            Margin = new Thickness(0, 4, 0, 6),
            Child = WithCopyButton(content, c.Text),
        };
    }

    /// <summary>A small "copy" button at the top right of a code block; copies the code exactly.</summary>
    private static Control WithCopyButton(Control content, string code)
    {
        var label = new TextBlock { Text = "copy" };
        label.Classes.Add("tag");
        var button = new Button
        {
            Content = label,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -3, -4, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Classes.Add("ghost");
        button.Classes.Add("md-copy-button");
        button.Click += async (_, _) =>
        {
            await LogText.CopyTextAsync(button, code);
            label.Text = "copied";
            await Task.Delay(1200);
            label.Text = "copy";
        };
        var grid = new Grid();
        grid.Children.Add(content);
        grid.Children.Add(button);
        return grid;
    }

    private Control List(IReadOnlyList<IReadOnlyList<MdBlock>> items, int? start, int depth)
    {
        var panel = new StackPanel { Margin = new Thickness(depth == 0 ? 2 : 0, 2, 0, 3) };
        int n = start ?? 1;
        foreach (var item in items)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(start is null ? "18,*" : "26,*") };
            var marker = new TextBlock
            {
                Text = start is null ? "•" : n + ".",
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 3, 6, 0),
            };
            marker.Classes.Add("md-bullet");
            row.Children.Add(marker);

            var body = new StackPanel();
            foreach (var b in item)
                body.Children.Add(b is MdParagraph p ? Paragraph(p.Inlines, BaseClass, new Thickness(0, 3, 0, 1)) : Build(b, depth + 1));
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            panel.Children.Add(row);
            n++;
        }
        return panel;
    }

    private Control Quote(MdQuote q, int depth)
    {
        var stack = new StackPanel();
        foreach (var b in q.Blocks)
            stack.Children.Add(b is MdParagraph p ? Paragraph(p.Inlines, BaseClass, new Thickness(0, 2, 0, 2), "md-quote") : Build(b, depth + 1));
        return new Border
        {
            BorderBrush = QuoteBar,
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(9, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 4),
            Child = stack,
        };
    }

    // ------------------------------------------------------------------ tables

    private Control Table(MdTable t)
    {
        int cols = Math.Max(t.Header.Count, t.Rows.Count == 0 ? 0 : t.Rows.Max(r => r.Count));
        if (cols == 0) return new TextBlock();

        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var allRows = new List<(IReadOnlyList<MdTableCell> Cells, bool Header)>();
        if (t.Header.Count > 0) allRows.Add((t.Header, true));
        foreach (var r in t.Rows) allRows.Add((r, false));

        var border = TableBorder;
        var headerBg = TableHeaderBg;
        for (int r = 0; r < allRows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var (cells, header) = allRows[r];
            for (int c = 0; c < cols; c++)
            {
                var cell = c < cells.Count ? cells[c] : new MdTableCell(Array.Empty<MdInline>());
                var align = c < t.Alignments.Count ? t.Alignments[c] : MdAlign.Default;
                var tb = NewText(); tb.MaxWidth = CellMaxWidth;
                tb.Classes.Add(header ? "md-table-header" : "md-table-cell-text");
                tb.TextAlignment = align switch { MdAlign.Right => TextAlignment.Right, MdAlign.Center => TextAlignment.Center, _ => TextAlignment.Left };
                AddInlines(tb, cell.Inlines, null);

                var box = new Border
                {
                    Padding = new Thickness(9, 4, 9, 4),
                    BorderBrush = border,
                    BorderThickness = new Thickness(0, 0, c == cols - 1 ? 0 : 1, r == allRows.Count - 1 ? 0 : 1),
                    Background = header ? headerBg : Brushes.Transparent,
                    Child = tb,
                };
                Grid.SetRow(box, r);
                Grid.SetColumn(box, c);
                grid.Children.Add(box);
            }
        }

        var frame = new Border
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = true,
            Child = grid,
        };
        var scroll = HorizontalScroller(frame);
        scroll.Margin = new Thickness(0, 5, 0, 7 - ScrollBarRoom);
        return scroll;
    }

    /// <summary>A horizontal-only scroller; the vertical wheel keeps scrolling the log behind it.</summary>
    private static ScrollViewer HorizontalScroller(Control child)
    {
        var sv = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Child = child, Padding = new Thickness(0, 0, 0, ScrollBarRoom) },
            Focusable = false,
        };
        sv.Classes.Add("terminal");
        sv.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        sv.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        return sv;
    }

    // ------------------------------------------------------------------ inlines

    private sealed class LinkMap
    {
        public readonly List<(int Start, int End, string Url)> Ranges = new();
    }

    private void AddInlines(LogText host, IEnumerable<MdInline> inlines, FontWeight? weight)
    {
        var links = new LinkMap();
        int pos = 0;
        AddInlines(host.Inlines!, inlines, weight, links, ref pos);
        if (links.Ranges.Count > 0) WireLinks(host, links);
    }

    private void AddInlines(InlineCollection into, IEnumerable<MdInline> inlines, FontWeight? weight, LinkMap links, ref int pos)
    {
        foreach (var i in inlines)
        {
            switch (i)
            {
                case MdText t:
                    into.Add(new Run(t.Text) { FontWeight = weight ?? FontWeight.Normal });
                    pos += t.Text.Length;
                    break;
                case MdLineBreak:
                    into.Add(new LineBreak());
                    pos += 1;
                    break;
                case MdCode c:
                {
                    // Padding spaces at both ends give the background some breathing room, like a chip.
                    var text = " " + c.Text + " ";
                    into.Add(new Run(text) { FontFamily = Mono, FontSize = 12, Background = InlineCodeBg, Foreground = InlineCodeFg });
                    pos += text.Length;
                    break;
                }
                case MdBold b:
                {
                    var span = new Bold();
                    AddInlines(span.Inlines, b.Content, FontWeight.Bold, links, ref pos);
                    into.Add(span);
                    break;
                }
                case MdItalic it:
                {
                    var span = new Italic();
                    AddInlines(span.Inlines, it.Content, weight, links, ref pos);
                    into.Add(span);
                    break;
                }
                case MdLink l:
                {
                    var span = new Span { Foreground = LinkBrush, TextDecorations = TextDecorations.Underline };
                    int start = pos;
                    AddInlines(span.Inlines, l.Content, weight, links, ref pos);
                    into.Add(span);
                    if (OpenableUri(l.Url) is { } uri) links.Ranges.Add((start, pos, uri.AbsoluteUri));
                    break;
                }
            }
        }
    }

    /// <summary>Inlines take no pointer input, so a link click is resolved by hit-testing the text layout.</summary>
    private static void WireLinks(LogText tb, LinkMap links)
    {
        string? UrlAt(PointerEventArgs e)
        {
            var hit = tb.TextLayout.HitTestPoint(e.GetPosition(tb));
            if (!hit.IsInside) return null;
            foreach (var (s, en, url) in links.Ranges)
                if (hit.TextPosition >= s && hit.TextPosition < en) return url;
            return null;
        }
        // A plain click on a link opens it; a drag (or anything left selected) is a text selection instead.
        Point? pressed = null;
        tb.AddHandler(InputElement.PointerPressedEvent, (_, e) => pressed = e.GetPosition(tb),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        tb.PointerMoved += (_, e) => tb.Cursor = new Cursor(UrlAt(e) is null ? StandardCursorType.Ibeam : StandardCursorType.Hand);
        tb.AddHandler(InputElement.PointerReleasedEvent, async (_, e) =>
        {
            var start = pressed;
            pressed = null;
            if (e.InitialPressMouseButton != MouseButton.Left || start is not Point p0) return;
            var p1 = e.GetPosition(tb);
            if (Math.Abs(p1.X - p0.X) > 3 || Math.Abs(p1.Y - p0.Y) > 3 || tb.HasSelection) return;
            if (UrlAt(e) is not { } url) return;
            try { if (TopLevel.GetTopLevel(tb)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url)); }
            catch (Exception) { /* no handler for the scheme: nothing to do */ }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>Only absolute http and https links are ever opened.</summary>
    private static Uri? OpenableUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u : null;
}
