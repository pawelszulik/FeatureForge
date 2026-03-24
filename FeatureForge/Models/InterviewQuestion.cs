using CommunityToolkit.Mvvm.ComponentModel;

namespace FeatureForge.Models;

public partial class InterviewQuestion : ObservableObject
{
    public string Id { get; init; } = string.Empty;
    public string Question { get; init; } = string.Empty;
    public string DefaultAnswer { get; init; } = string.Empty;
    public string AgentSlug { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public bool IsFirstInGroup { get; init; }

    [ObservableProperty]
    private string _answer = string.Empty;
}
