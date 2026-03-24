using CommunityToolkit.Mvvm.ComponentModel;

namespace FeatureForge.Models;

public partial class AgentInterviewSelection : ObservableObject
{
    public AgentDefinition Definition { get; }
    public string Name => Definition.Name;
    public bool HasInterviewPrompt => Definition.InterviewPrompt is not null;

    [ObservableProperty]
    private bool _isSelected = true;

    public AgentInterviewSelection(AgentDefinition definition)
    {
        Definition = definition;
    }
}
