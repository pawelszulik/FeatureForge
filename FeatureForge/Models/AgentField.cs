using CommunityToolkit.Mvvm.ComponentModel;

namespace FeatureForge.Models;

public partial class AgentField : ObservableObject
{
    public string Label { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    public AgentField(string label) => Label = label;
}
