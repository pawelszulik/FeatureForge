using System.Text;
using FeatureForge.Models;
using FeatureForge.ViewModels;
using Microsoft.Extensions.Configuration;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Polly;
using Polly.Retry;

namespace FeatureForge.Services;

public class TfsService
{
    private string _collectionUrl;
    private string _project;
    private string _areaPath;
    private string _iterationPath;
    private string _tag = "HMS-Generated";
    private VssConnection? _connection;
    private readonly AsyncRetryPolicy _retryPolicy;
    private Dictionary<string, string> _runtimeCustomFields = [];

    private readonly IConfiguration _config;
    private Dictionary<string, int> _epicIds = [];

    public void SetEpicIds(Dictionary<string, int> epicIds) => _epicIds = epicIds;

    /// <summary>Rekonfiguruje serwis TFS dla wybranego projektu.</summary>
    public void Configure(Models.TfsProjectConfig tfsConfig, string pat)
    {
        _project = tfsConfig.Project;
        _areaPath = tfsConfig.AreaPath;
        _iterationPath = tfsConfig.IterationPath;
        _tag = tfsConfig.Tag;
        _runtimeCustomFields = tfsConfig.CustomFields;

        if (_collectionUrl != tfsConfig.CollectionUrl && !string.IsNullOrWhiteSpace(tfsConfig.CollectionUrl) && !string.IsNullOrWhiteSpace(pat))
        {
            _collectionUrl = tfsConfig.CollectionUrl;
            var creds = new VssBasicCredential(string.Empty, pat);
            _connection = new VssConnection(new Uri(_collectionUrl), creds);
        }
    }

    public TfsService(IConfiguration config)
    {
        _config = config;
        _collectionUrl = config["Tfs:CollectionUrl"] ?? string.Empty;
        _project = config["Tfs:Project"] ?? string.Empty;
        _areaPath = config["Tfs:AreaPath"] ?? string.Empty;
        _iterationPath = config["Tfs:IterationPath"] ?? string.Empty;

        var pat = config["Tfs:PersonalAccessToken"];

        _retryPolicy = Policy
            .Handle<Exception>(ex => ex is not ArgumentException)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetry: (ex, delay, attempt, _) =>
                    System.Diagnostics.Debug.WriteLine($"TFS retry {attempt} po {delay}: {ex.Message}")
            );

        if (!string.IsNullOrWhiteSpace(_collectionUrl) && !string.IsNullOrWhiteSpace(pat))
        {
            var creds = new VssBasicCredential(string.Empty, pat);
            _connection = new VssConnection(new Uri(_collectionUrl), creds);
        }
    }

    public bool IsConfigured => _connection is not null;

    public async Task CreateFeatureWithTasksAsync(
        string moduleName,
        string featureName,
        IEnumerable<AgentTabViewModel> agents)
    {
        if (_connection is null)
            throw new InvalidOperationException("TFS nie jest skonfigurowane. Sprawdź Tfs:CollectionUrl i Tfs:PersonalAccessToken.");

        ValidatePaths();

        var agentList = agents.ToList();
        var featureDocs = agentList.Where(a => a.Definition.TfsRole == TfsRole.FeatureDoc).ToList();
        var tasks = agentList.Where(a => a.Definition.TfsRole == TfsRole.Task).ToList();
        var userStories = agentList.Where(a => a.Definition.TfsRole == TfsRole.UserStory).ToList();

        var title = string.IsNullOrWhiteSpace(moduleName)
            ? featureName
            : $"{moduleName} - {featureName}";

        _epicIds.TryGetValue(moduleName, out var epicId);
        var featureId = await CreateFeatureItemAsync(title, featureDocs, epicId > 0 ? epicId : null);

        foreach (var agent in tasks)
            await CreateChildWorkItemAsync(agent, featureName, featureId, "Task");

        foreach (var agent in userStories)
            await CreateChildWorkItemAsync(agent, featureName, featureId, "User Story");
    }

    private async Task<int> CreateFeatureItemAsync(string title, List<AgentTabViewModel> featureDocs, int? parentEpicId)
    {
        var description = BuildFeatureDescription(featureDocs);

        var patch = new JsonPatchDocument
        {
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Description", Value = description },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.AreaPath", Value = _areaPath },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.IterationPath", Value = _iterationPath },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = _tag },
        };

        if (parentEpicId.HasValue)
        {
            patch.Add(new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/relations/-",
                Value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = $"{_collectionUrl.TrimEnd('/')}/_apis/wit/workItems/{parentEpicId.Value}"
                }
            });
        }

        // Dodaj custom fields — klucz w CustomFields profilu: "{slug}_{fieldLabel}"
        foreach (var agent in featureDocs)
        {
            foreach (var field in agent.Fields)
            {
                var key = $"{agent.Definition.Slug}_{field.Label}";
                if (!_runtimeCustomFields.TryGetValue(key, out var tfsFieldRef))
                    continue;
                if (string.IsNullOrWhiteSpace(tfsFieldRef) || string.IsNullOrWhiteSpace(field.Value))
                    continue;

                patch.Add(new JsonPatchOperation
                {
                    Operation = Operation.Add,
                    Path = $"/fields/{tfsFieldRef}",
                    Value = SanitizeFieldValue(field.Value)
                });
            }
        }

        WorkItem? created = null;
        await _retryPolicy.ExecuteAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var client = await _connection!.GetClientAsync<WorkItemTrackingHttpClient>(cts.Token);
            created = await client.CreateWorkItemAsync(patch, _project, "Feature", cancellationToken: cts.Token);
        });

        return created?.Id ?? throw new InvalidOperationException("Nie udało się uzyskać ID utworzonego Feature.");
    }

    private async Task CreateChildWorkItemAsync(AgentTabViewModel agent, string featureName, int parentId, string workItemType)
    {
        var title = $"[{agent.AgentName}] {featureName}";
        var description = BuildDescription(agent.Fields);
        var parentUrl = $"{_collectionUrl.TrimEnd('/')}/_apis/wit/workItems/{parentId}";

        var patch = new JsonPatchDocument
        {
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Description", Value = description },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.AreaPath", Value = _areaPath },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.IterationPath", Value = _iterationPath },
            new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Tags", Value = agent.AgentName },
            new JsonPatchOperation
            {
                Operation = Operation.Add,
                Path = "/relations/-",
                Value = new
                {
                    rel = "System.LinkTypes.Hierarchy-Reverse",
                    url = parentUrl
                }
            },
        };

        await _retryPolicy.ExecuteAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var client = await _connection!.GetClientAsync<WorkItemTrackingHttpClient>(cts.Token);
            await client.CreateWorkItemAsync(patch, _project, workItemType, cancellationToken: cts.Token);
        });
    }

    private void ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(_areaPath))
            throw new ArgumentException("TFS_ERROR: Tfs:AreaPath nie jest skonfigurowane w appsettings.");
        if (string.IsNullOrWhiteSpace(_iterationPath))
            throw new ArgumentException("TFS_ERROR: Tfs:IterationPath nie jest skonfigurowane w appsettings.");
    }

    private static string SanitizeFieldValue(string value) =>
        new string(value.Where(c => c != '\0' && (c >= 0x20 || c == '\n' || c == '\r' || c == '\t')).ToArray());

    private static string BuildFeatureDescription(IEnumerable<AgentTabViewModel> featureDocs)
    {
        var sb = new StringBuilder();
        foreach (var agent in featureDocs)
        {
            sb.AppendLine($"<h2>{agent.AgentName}</h2>");
            foreach (var field in agent.Fields)
            {
                sb.AppendLine($"<h3>{field.Label}</h3>");
                sb.AppendLine(FormatFieldAsHtml(field.Value));
            }
        }
        return sb.ToString();
    }

    private static string BuildDescription(IEnumerable<AgentField> fields)
    {
        var sb = new StringBuilder();
        foreach (var field in fields)
        {
            sb.AppendLine($"<h3>{field.Label}</h3>");
            sb.AppendLine(FormatFieldAsHtml(field.Value));
        }
        return sb.ToString();
    }

    private static string FormatFieldAsHtml(string value)
    {
        var trimmed = value.Trim();

        // JSON array → lista HTML
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
        {
            try
            {
                var items = System.Text.Json.JsonSerializer.Deserialize<List<string>>(trimmed);
                if (items is { Count: > 0 })
                {
                    var ul = new StringBuilder("<ul>");
                    foreach (var item in items)
                        ul.Append($"<li>{System.Net.WebUtility.HtmlEncode(item.Trim())}</li>");
                    ul.Append("</ul>");
                    return ul.ToString();
                }
            }
            catch { /* nie JSON array — fallthrough */ }
        }

        // Linie zaczynające się od "- " lub "* " → lista HTML
        var lines = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1 && lines.All(l => l.TrimStart().StartsWith("- ") || l.TrimStart().StartsWith("* ")))
        {
            var ul = new StringBuilder("<ul>");
            foreach (var line in lines)
                ul.Append($"<li>{System.Net.WebUtility.HtmlEncode(line.TrimStart().TrimStart('-', '*').Trim())}</li>");
            ul.Append("</ul>");
            return ul.ToString();
        }

        // Zwykły tekst z enterami → paragrafy
        var paragraphs = trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (paragraphs.Length > 1)
        {
            var sb = new StringBuilder();
            foreach (var p in paragraphs)
                sb.Append($"<p>{System.Net.WebUtility.HtmlEncode(p.Trim())}</p>");
            return sb.ToString();
        }

        return $"<p>{System.Net.WebUtility.HtmlEncode(trimmed)}</p>";
    }
}
