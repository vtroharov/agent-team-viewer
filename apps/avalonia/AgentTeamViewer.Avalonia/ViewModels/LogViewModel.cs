using System.Collections.ObjectModel;
using AgentTeamViewer.Model;

namespace AgentTeamViewer.ViewModels;

/// <summary>The full log of one agent, as shown in the lead panel or an expanded card.</summary>
public sealed class LogViewModel : ObservableObject
{
    private readonly AgentState _agent;

    public LogViewModel(AgentState agent)
    {
        _agent = agent;
        _agent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AgentState.DisplayName) or nameof(AgentState.Name) or nameof(AgentState.Description)) Raise(nameof(Title));
            if (e.PropertyName is nameof(AgentState.Tag) or nameof(AgentState.Type) or nameof(AgentState.IsTeammate) or nameof(AgentState.ModelLabel)) Raise(nameof(Subtitle));
            if (e.PropertyName is nameof(AgentState.ModelLabel)) Raise(nameof(Model));
            if (e.PropertyName is nameof(AgentState.Status)) Raise(nameof(Status));
            if (e.PropertyName is nameof(AgentState.ActivityLine)) Raise(nameof(ActivityLine));
        };
    }

    public AgentState Agent => _agent;
    public string Id => _agent.Id;
    public ObservableCollection<LogBlock> Blocks => _agent.Blocks;

    public string Title => _agent.IsMain ? "LEAD · Claude" : _agent.DisplayName.ToUpperInvariant();
    public string Subtitle => _agent.IsMain ? "" : $"{_agent.Tag} · {_agent.ModelLabel} · {_agent.Id}";
    /// <summary>Short LLM model label ("opus-5-5" or "model unknown").</summary>
    public string Model => _agent.ModelLabel;
    public string Status => _agent.Status;
    public string ActivityLine => _agent.ActivityLine ?? "";
}
