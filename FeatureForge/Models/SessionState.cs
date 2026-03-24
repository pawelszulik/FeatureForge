namespace FeatureForge.Models;

public record SessionState(
    string Feature,
    string Module,
    string Date,
    List<AgentState> Agents,
    string FeatureTitle = "",
    string InterviewContext = "",
    Dictionary<string, string>? PerAgentInterviewContext = null
);
