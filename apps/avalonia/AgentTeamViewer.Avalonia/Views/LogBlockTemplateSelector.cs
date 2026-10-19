using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.Views;

/// <summary>Picks a template per <see cref="BlockKind"/>. Templates are set from LogView.axaml resources.</summary>
public sealed class LogBlockTemplateSelector : IDataTemplate
{
    public IDataTemplate? Prompt { get; set; }
    public IDataTemplate? Delivery { get; set; }
    public IDataTemplate? Notice { get; set; }
    public IDataTemplate? Thinking { get; set; }
    public IDataTemplate? Text { get; set; }
    public IDataTemplate? Tool { get; set; }
    public IDataTemplate? TurnStart { get; set; }
    public IDataTemplate? TurnEnd { get; set; }

    public bool Match(object? data) => data is LogBlock;

    public Control? Build(object? data)
    {
        if (data is not LogBlock b) return null;
        var template = b.Kind switch
        {
            BlockKind.Prompt => Prompt,
            BlockKind.Delivery => Delivery,
            BlockKind.Notice => Notice,
            BlockKind.Thinking => Thinking,
            BlockKind.Text => Text,
            BlockKind.Tool => Tool,
            BlockKind.TurnStart => TurnStart,
            BlockKind.TurnEnd => TurnEnd,
            _ => Text,
        };
        return template?.Build(data);
    }
}
