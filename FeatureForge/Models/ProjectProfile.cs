namespace FeatureForge.Models;

public record TfsProjectConfig
{
    public string CollectionUrl { get; init; } = "";
    public string Project { get; init; } = "";
    public string AreaPath { get; init; } = "";
    public string IterationPath { get; init; } = "";
    public Dictionary<string, string> CustomFields { get; init; } = [];
    public string Tag { get; init; } = "FeatureForge-Generated";
}

public record ProjectProfile
{
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public AgentDefinition[] Agents { get; init; } = [];
    public ModuleConfig[] Modules { get; init; } = [];
    public string DocsPath { get; init; } = "";
    public TfsProjectConfig Tfs { get; init; } = new();
    public string InterviewSystemPrompt { get; init; } = "";
}
