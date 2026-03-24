using System.IO;
using System.Net.Http;
using System.Text.Json;
using FeatureForge.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using OpenAI.Chat;
using Polly;
using Polly.Retry;

namespace FeatureForge.Services;

public class LlmService
{
    private readonly Kernel _kernel;
    private readonly string _provider;
    private readonly double _temperature;
    private readonly int _maxTokens;
    private readonly AsyncRetryPolicy _retryPolicy;

    public LlmService(IConfiguration config)
    {
        var builder = Kernel.CreateBuilder();

        _provider = config["Llm:Provider"] ?? "Groq";
        var model = config["Llm:Model"] ?? "llama-3.3-70b-versatile";
        var apiKey = config["Llm:ApiKey"] ?? throw new InvalidOperationException("Brak Llm:ApiKey w konfiguracji.");
        _temperature = double.TryParse(config["Llm:Temperature"], out var t) ? t : 0.2;
        _maxTokens = int.TryParse(config["Llm:MaxTokens"], out var m) ? m : 4096;

        switch (_provider.ToLowerInvariant())
        {
            case "groq":
                builder.AddOpenAIChatCompletion(
                    modelId: model,
                    apiKey: apiKey,
                    endpoint: new Uri("https://api.groq.com/openai/v1")
                );
                break;
            case "gemini":
                builder.AddGoogleAIGeminiChatCompletion(
                    modelId: model,
                    apiKey: apiKey
                );
                break;
            case "openrouter":
                builder.AddOpenAIChatCompletion(
                    modelId: model,
                    apiKey: apiKey,
                    endpoint: new Uri("https://openrouter.ai/api/v1")
                );
                break;
            case "ollama":
                builder.AddOpenAIChatCompletion(
                    modelId: model,
                    apiKey: "ollama",
                    endpoint: new Uri(config["Llm:OllamaUrl"] ?? "http://localhost:11434/v1")
                );
                break;
            default:
                throw new InvalidOperationException($"Nieznany provider LLM: {_provider}");
        }

        _kernel = builder.Build();

        _retryPolicy = Policy
            .Handle<Exception>(ex => IsTransient(ex))
            .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                (ex, delay, attempt, _) =>
                    System.Diagnostics.Debug.WriteLine($"LLM retry {attempt} after {delay}: {ex.Message}"));
    }

    public async Task<List<InterviewQuestion>> GenerateInterviewAsync(
        string interviewSystemPrompt,
        string featureTitle,
        string featureText)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(interviewSystemPrompt);
        history.AddUserMessage($"Tytuł feature'a: {featureTitle}\n\nOpis:\n{featureText}");

        var settings = BuildSettings(null);
        var response = await _retryPolicy.ExecuteAsync(() =>
            chat.GetChatMessageContentAsync(history, settings));

        return ParseInterviewQuestions(response.Content ?? string.Empty);
    }
    public async Task<List<InterviewQuestion>> GenerateQuestionInterviewAsync(
        string interviewSystemPrompt,
        string featureTitle,
        string featureText)
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(interviewSystemPrompt);
        history.AddUserMessage($"Tytuł feature'a: {featureTitle}\n\nOpis:\n{featureText}");

        var settings = BuildInterviewSettings();
        var response = await _retryPolicy.ExecuteAsync(() =>
            chat.GetChatMessageContentAsync(history, settings));

        return ParseInterviewQuestions(response.Content ?? string.Empty);
    }
    private PromptExecutionSettings BuildInterviewSettings()
    {
        var schema = """
                     {
                       "type": "array",
                       "minItems": 5,
                       "maxItems": 8,
                       "items": {
                         "type": "object",
                         "additionalProperties": false,
                         "properties": {
                           "id": {
                             "type": "string",
                             "pattern": "^q[1-8]$"
                           },
                           "question": {
                             "type": "string"
                           },
                           "default": {
                             "type": "string"
                           }
                         },
                         "required": ["id", "question", "default"]
                       }
                     }
                     """;

        var responseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "interview_questions",
            jsonSchema: BinaryData.FromString(schema),
            jsonSchemaIsStrict: true);

        return new OpenAIPromptExecutionSettings
        {
            Temperature = 0.1,
            MaxTokens = _maxTokens,
            TopP = 0.9,
            Seed = 42,
            ResponseFormat = responseFormat,
        };
    }
    public async Task<Dictionary<string, string>> GenerateAsync(
        AgentDefinition def,
        string featureTitle,
        string featureText,
        string projectContext = "",
        string priorContext = "",
        string interviewContext = "")
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        var systemPrompt = def.SystemPrompt;
        history.AddSystemMessage(systemPrompt);

        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            history.AddSystemMessage($"""
                === DOKUMENTACJA PROJEKTU ===
                Poniżej znajduje się dokumentacja projektu. Wykorzystaj ją jako źródło prawdy o architekturze,
                tech stacku, wzorcach i wymaganiach. Twoja odpowiedź MUSI być spójna z tą dokumentacją.

                {projectContext}
                === KONIEC DOKUMENTACJI ===
                """);
        }

        if (!string.IsNullOrWhiteSpace(priorContext))
            history.AddUserMessage($"""
                Wyniki pozostałych ról zespołu (uwzględnij je w swojej odpowiedzi):

                {priorContext}
                """);

        if (!string.IsNullOrWhiteSpace(interviewContext))
            history.AddUserMessage($"""
                Odpowiedzi na pytania doprecyzowujące:

                {interviewContext}
                """);

        history.AddUserMessage($"Tytuł feature'a: {featureTitle}\n\nOpis:\n{featureText}");

        var settings = BuildSettings(def.FieldNames);
        var response = await _retryPolicy.ExecuteAsync(() =>
            chat.GetChatMessageContentAsync(history, settings));
        return ParseJson(response.Content ?? string.Empty, def.FieldNames);
    }

    public async Task<Dictionary<string, string>> RefineAsync(
        AgentDefinition def,
        IEnumerable<AgentField> fields,
        string notes,
        string projectContext = "")
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        var systemPrompt = def.SystemPrompt;
        history.AddSystemMessage(systemPrompt);

        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            history.AddSystemMessage($"""
                === DOKUMENTACJA PROJEKTU ===
                Poniżej znajduje się dokumentacja projektu. Wykorzystaj ją jako źródło prawdy o architekturze,
                tech stacku, wzorcach i wymaganiach. Twoja odpowiedź MUSI być spójna z tą dokumentacją.

                {projectContext}
                === KONIEC DOKUMENTACJI ===
                """);
        }

        var currentJson = new Dictionary<string, string>();
        foreach (var f in fields)
            currentJson[f.Label] = f.Value;
        var currentContent = JsonSerializer.Serialize(currentJson, new JsonSerializerOptions { WriteIndented = true });

        history.AddUserMessage($"""
            Obecna treść (JSON):
            {currentContent}

            Uwagi do poprawki:
            {notes}

            Zwróć poprawioną wersję jako JSON z tymi samymi kluczami.
            """);

        var settings = BuildSettings(def.FieldNames);
        var response = await _retryPolicy.ExecuteAsync(() =>
            chat.GetChatMessageContentAsync(history, settings));
        return ParseJson(response.Content ?? string.Empty, def.FieldNames);
    }

    public async Task<Dictionary<string, string>> GenerateDocsUpdateAsync(
        AgentDefinition def,
        IEnumerable<AgentField> fields,
        string projectContext)
    {
        if (def.UpdateDocsPrompt is null)
            throw new InvalidOperationException($"Agent {def.Name} nie obsługuje trybu Update Docs.");

        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(def.UpdateDocsPrompt);

        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            history.AddSystemMessage($"""
                === DOKUMENTACJA PROJEKTU ===
                Poniżej znajduje się dokumentacja projektu. Wykorzystaj ją jako źródło prawdy o architekturze,
                tech stacku, wzorcach i wymaganiach. Twoja odpowiedź MUSI być spójna z tą dokumentacją.

                {projectContext}
                === KONIEC DOKUMENTACJI ===
                """);
        }

        var fieldsContent = string.Join("\n", fields.Select(f => $"### {f.Label}\n{f.Value}"));
        history.AddUserMessage($"Zrealizowany feature:\n{fieldsContent}");

        var settings = BuildSettings(null);
        var response = await _retryPolicy.ExecuteAsync(() =>
            chat.GetChatMessageContentAsync(history, settings));
        return ParseJsonFreeKeys(response.Content ?? string.Empty);
    }

    /// <summary>
    /// Generuje odpowiedź strumieniowo, wywołując callback dla każdego fragmentu tekstu.
    /// Zwraca pełną odpowiedź po zakończeniu streamu.
    /// </summary>
    public async Task<Dictionary<string, string>> GenerateStreamingAsync(
        AgentDefinition def,
        string featureTitle,
        string featureText,
        Action<string> onChunk,
        string projectContext = "",
        string priorContext = "",
        string interviewContext = "")
    {
        var chat = _kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        var systemPrompt = def.SystemPrompt;
        history.AddSystemMessage(systemPrompt);

        if (!string.IsNullOrWhiteSpace(projectContext))
        {
            history.AddSystemMessage($"""
                === DOKUMENTACJA PROJEKTU ===
                Poniżej znajduje się dokumentacja projektu. Wykorzystaj ją jako źródło prawdy o architekturze,
                tech stacku, wzorcach i wymaganiach. Twoja odpowiedź MUSI być spójna z tą dokumentacją.

                {projectContext}
                === KONIEC DOKUMENTACJI ===
                """);
        }

        if (!string.IsNullOrWhiteSpace(priorContext))
            history.AddUserMessage($"""
                Wyniki pozostałych ról zespołu (uwzględnij je w swojej odpowiedzi):

                {priorContext}
                """);

        history.AddUserMessage($"Tytuł feature'a: {featureTitle}\n\nOpis:\n{featureText}");

        var settings = BuildSettings(def.FieldNames);
        var fullResponse = new System.Text.StringBuilder();

        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings))
        {
            if (chunk.Content is not null)
            {
                fullResponse.Append(chunk.Content);
                onChunk(chunk.Content);
            }
        }

        return ParseJson(fullResponse.ToString(), def.FieldNames);
    }

    private PromptExecutionSettings BuildSettings(string[]? fieldNames)
    {
        if (_provider.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            // Gemini connector — basic settings only
            return new PromptExecutionSettings
            {
                ExtensionData = new Dictionary<string, object>
                {
                    ["temperature"] = _temperature,
                    ["max_tokens"] = _maxTokens
                }
            };
        }

        var settings = new OpenAIPromptExecutionSettings
        {
            Temperature = _temperature,
            MaxTokens = _maxTokens,
            TopP = 0.9,
            Seed = 42
        };

        // Structured output — json_schema if field names known, json_object otherwise
        if (fieldNames is { Length: > 0 })
        {
            settings.ResponseFormat = BuildJsonSchemaFormat(fieldNames);
        }
        else
        {
            settings.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        return settings;
    }

    private static ChatResponseFormat BuildJsonSchemaFormat(string[] fieldNames)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var field in fieldNames)
        {
            properties[field] = new { type = "string" };
            required.Add(field);
        }

        var schema = JsonSerializer.Serialize(new
        {
            type = "object",
            properties,
            required,
            additionalProperties = false
        });

        return ChatResponseFormat.CreateJsonSchemaFormat(
            jsonSchemaFormatName: "agent_output",
            jsonSchema: BinaryData.FromString(schema),
            jsonSchemaIsStrict: true
        );
    }


    private static bool IsTransient(Exception ex)
    {
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("429") || msg.Contains("rate limit")
            || msg.Contains("500") || msg.Contains("502") || msg.Contains("503")
            || msg.Contains("timeout") || msg.Contains("timed out")
            || ex is HttpRequestException or TaskCanceledException;
    }

    private static Dictionary<string, string> ParseJson(string raw, string[] fieldNames)
    {
        // Strip markdown code fences
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0) raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```")) raw = raw[..^3];
            raw = raw.Trim();
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            raw = raw[start..(end + 1)];

        try
        {
            var doc = JsonDocument.Parse(raw);
            var result = new Dictionary<string, string>();
            foreach (var field in fieldNames)
            {
                if (doc.RootElement.TryGetProperty(field, out var el))
                    result[field] = el.ValueKind == JsonValueKind.String
                        ? el.GetString() ?? string.Empty
                        : el.GetRawText();
                else
                    result[field] = string.Empty;
            }
            return result;
        }
        catch
        {
            return fieldNames.ToDictionary(f => f, f => f == fieldNames[0] ? raw : string.Empty);
        }
    }

    private static List<InterviewQuestion> ParseInterviewQuestions(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0) raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```")) raw = raw[..^3];
            raw = raw.Trim();
        }

        var start = raw.IndexOf('[');
        var end = raw.LastIndexOf(']');
        if (start >= 0 && end > start)
            raw = raw[start..(end + 1)];

        try
        {
            var items = JsonSerializer.Deserialize<List<JsonElement>>(raw) ?? [];
            return items.Select(el =>
            {
                var defaultAnswer = el.TryGetProperty("default", out var d) ? d.GetString() ?? string.Empty : string.Empty;
                return new InterviewQuestion
                {
                    Id = el.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Question = el.TryGetProperty("question", out var q) ? q.GetString() ?? string.Empty : string.Empty,
                    DefaultAnswer = defaultAnswer,
                    Answer = defaultAnswer
                };
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static Dictionary<string, string> ParseJsonFreeKeys(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var firstNewline = raw.IndexOf('\n');
            if (firstNewline > 0) raw = raw[(firstNewline + 1)..];
            if (raw.EndsWith("```")) raw = raw[..^3];
            raw = raw.Trim();
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
            raw = raw[start..(end + 1)];

        try
        {
            var doc = JsonDocument.Parse(raw);
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty);
        }
        catch
        {
            return new Dictionary<string, string> { ["error.md"] = raw };
        }
    }
}
