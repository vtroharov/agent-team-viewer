using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>
/// A collapsible log block (teammate message, multi-line note, thinking): arrow and one header line while
/// closed, the full text below it when open. The look follows <see cref="DisclosureKind"/>.
/// </summary>
public partial class DisclosureBlock : UserControl
{
    private LogBlock? _block;

    public DisclosureBlock()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
    }

    private void Attach()
    {
        if (_block is not null) _block.PropertyChanged -= OnBlockChanged;
        _block = DataContext as LogBlock;
        if (_block is not null) _block.PropertyChanged += OnBlockChanged;
        ApplyKind();
    }

    private void OnBlockChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogBlock.Disclosure)) ApplyKind();
    }

    /// <summary>Text classes per kind: deliveries italic cyan-muted, thinking dim italic, notes small italic. Messages get the left bar.</summary>
    private void ApplyKind()
    {
        var kind = _block?.Disclosure.Kind ?? DisclosureKind.None;
        string cls = kind switch { DisclosureKind.Message => "delivery", DisclosureKind.Thinking => "thinking", _ => "notice" };
        foreach (var t in new[] { HeaderText, PlainBody })
        {
            t.Classes.Set("delivery", cls == "delivery");
            t.Classes.Set("thinking", cls == "thinking");
            t.Classes.Set("notice", cls == "notice");
        }
        bool message = kind == DisclosureKind.Message;
        Frame.BorderThickness = message ? new Thickness(2, 0, 0, 0) : new Thickness(0);
        Frame.Padding = message ? new Thickness(8, 2, 0, 2) : new Thickness(0, 1, 0, 1);
        Frame.Margin = message ? new Thickness(0, 8, 0, 4) : new Thickness(0, 3, 0, 3);
    }
}
