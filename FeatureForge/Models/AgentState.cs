namespace FeatureForge.Models;

public record AgentState(
    string Slug,
    Dictionary<string, string> Fields,
    string Notes,
    bool IsApproved,
    bool HasError
);
