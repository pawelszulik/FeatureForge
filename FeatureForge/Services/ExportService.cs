using System.IO;
using System.Text;
using System.Text.Json;
using FeatureForge.Models;
using FeatureForge.ViewModels;

namespace FeatureForge.Services;

public class ExportService
{
    private readonly string _outputRoot;

    public ExportService(string outputRoot = "output")
    {
        _outputRoot = outputRoot;
    }

    public async Task<string> SaveAsync(string featureTitle, string featureText, IEnumerable<AgentTabViewModel> agents)
    {
        var slug = Slugify(featureTitle);
        var dirName = $"{DateTime.Now:yyyy-MM-dd}_{slug}";
        var dirPath = Path.Combine(_outputRoot, dirName);
        Directory.CreateDirectory(dirPath);

        foreach (var agent in agents)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {agent.AgentName}");
            sb.AppendLine();
            foreach (var field in agent.Fields)
            {
                sb.AppendLine($"## {field.Label}");
                sb.AppendLine();
                sb.AppendLine(field.Value);
                sb.AppendLine();
            }
            var filePath = Path.Combine(dirPath, $"{agent.Definition.Slug}.md");
            await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8);
        }

        // summary.json
        var summary = new
        {
            FeatureTitle = featureTitle,
            Feature = featureText,
            Date = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Agents = agents.Select(a => new { a.AgentName, a.IsApproved }).ToArray()
        };
        await File.WriteAllTextAsync(
            Path.Combine(dirPath, "summary.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8
        );

        return Path.GetFullPath(dirPath);
    }

    public async Task<string> SaveStateAsync(
        string featureTitle,
        string featureText,
        string moduleName,
        IEnumerable<AgentTabViewModel> agents,
        string interviewContext = "",
        Dictionary<string, string>? perAgentInterviewContext = null,
        string? existingDirPath = null)
    {
        string dirPath;
        if (existingDirPath != null && Directory.Exists(existingDirPath))
        {
            dirPath = existingDirPath;
        }
        else
        {
            var slug = Slugify(featureTitle);
            var dirName = $"{DateTime.Now:yyyy-MM-dd}_{slug}";
            dirPath = Path.Combine(_outputRoot, dirName);
            Directory.CreateDirectory(dirPath);
        }

        var state = new SessionState(
            Feature: featureText,
            Module: moduleName,
            Date: DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            Agents: agents.Select(a => new AgentState(
                Slug: a.Definition.Slug,
                Fields: a.Fields.ToDictionary(f => f.Label, f => f.Value),
                Notes: a.Notes,
                IsApproved: a.IsApproved,
                HasError: a.HasError
            )).ToList(),
            FeatureTitle: featureTitle,
            InterviewContext: interviewContext,
            PerAgentInterviewContext: perAgentInterviewContext
        );

        await File.WriteAllTextAsync(
            Path.Combine(dirPath, "state.json"),
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }),
            Encoding.UTF8
        );

        return Path.GetFullPath(dirPath);
    }

    public SessionState? LoadState(string dirPath)
    {
        var path = Path.Combine(dirPath, "state.json");
        if (!File.Exists(path)) return null;
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<SessionState>(json);
    }

    private static string Slugify(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLower())
        {
            if (char.IsLetterOrDigit(c)) slug.Append(c);
            else if (c == ' ' || c == '_' || c == '-') slug.Append('-');
        }
        var result = slug.ToString().Trim('-');
        return result.Length > 50 ? result[..50] : result;
    }
}
