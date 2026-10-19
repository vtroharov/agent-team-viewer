using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>
/// Selectable text for the logs: drag or double-click to select, Ctrl+C to copy, and a context menu with
/// "Copy" (the selection) and "Copy block" (the whole block's raw text). Raises <see cref="SelectionChangedEvent"/>
/// (bubbling) so the owning <see cref="LogView"/> can hold auto-scroll while something is selected.
/// </summary>
public class LogText : SelectableTextBlock
{
    public static readonly RoutedEvent<RoutedEventArgs> SelectionChangedEvent =
        RoutedEvent.Register<LogText, RoutedEventArgs>("SelectionChanged", RoutingStrategies.Bubble);

    /// <summary>Raw text "Copy block" puts on the clipboard. Unset: derived from the block in DataContext.</summary>
    public static readonly StyledProperty<string?> BlockSourceProperty =
        AvaloniaProperty.Register<LogText, string?>(nameof(BlockSource));

    public string? BlockSource { get => GetValue(BlockSourceProperty); set => SetValue(BlockSourceProperty, value); }

    // Same look as TextBlock so the theme's text classes (body, prompt, md-h1, ...) apply.
    protected override Type StyleKeyOverride => typeof(TextBlock);

    public bool HasSelection => SelectionEnd != SelectionStart;

    private readonly MenuItem _copy = new() { Header = "Copy" };
    private readonly MenuItem _copyBlock = new() { Header = "Copy block" };

    public LogText()
    {
        Cursor = new Cursor(StandardCursorType.Ibeam);
        var menu = new MenuFlyout { Items = { _copy, _copyBlock } };
        menu.Opening += (_, _) => _copy.IsEnabled = HasSelection;
        _copy.Click += (_, _) => Copy();
        _copyBlock.Click += async (_, _) => await CopyTextAsync(this, RawBlockText());
        ContextFlyout = menu;
        SelectionBrush = Application.Current?.TryGetResource("LogSelectionBrush", ActualThemeVariant, out var b) == true && b is IBrush brush
            ? brush
            : new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse("#594FD9FF"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty)
            RaiseEvent(new RoutedEventArgs(SelectionChangedEvent) { Source = this });
    }

    /// <summary>The whole block as raw text (Markdown source for Markdown blocks).</summary>
    public string RawBlockText()
    {
        if (BlockSource is { } src) return src;
        return DataContext is LogBlock b ? RawText(b) : Text ?? "";
    }

    public static string RawText(LogBlock b) => b.Kind switch
    {
        BlockKind.Tool => string.Join("\n", new[] { b.Text, b.ToolInput, b.Result }.Where(s => !string.IsNullOrEmpty(s))),
        BlockKind.Notice => string.IsNullOrEmpty(b.Tag) ? b.Text : b.Tag + ": " + b.Text,
        BlockKind.Delivery => string.IsNullOrEmpty(b.From) ? b.Text : "from " + b.From + ":\n" + b.Text,
        _ => b.Text,
    };

    public static async Task CopyTextAsync(Visual from, string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(from)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        catch (Exception) { /* clipboard busy: nothing to do */ }
    }
}
