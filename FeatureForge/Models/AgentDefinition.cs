namespace FeatureForge.Models;

public record AgentDefinition
{
    /// <summary>
    /// Wyświetlana nazwa agenta (np. "Architekt", "Tech Lead"). Używana jako tytuł zakładki w UI.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Unikalny identyfikator agenta w ramach profilu (np. "architekt", "tech-lead").
    /// Używany w <see cref="DependsOn"/> do definiowania zależności między agentami.
    /// </summary>
    public string Slug { get; init; } = "";

    /// <summary>
    /// Nazwy pól wyjściowych, które agent ma wypełnić (np. "Opis architektury", "Decyzje techniczne").
    /// LLM zwraca JSON z kluczami odpowiadającymi tym nazwom; każde pole trafia do osobnego <see cref="AgentField"/>.
    /// </summary>
    public string[] FieldNames { get; init; } = [];

    /// <summary>
    /// Prompt systemowy wysyłany jako pierwsza wiadomość w ChatHistory.
    /// Definiuje rolę, styl odpowiedzi i format wyjściowy agenta (JSON z polami z <see cref="FieldNames"/>).
    /// </summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>
    /// Rola w Azure DevOps, do której przypisywane są work itemy tworzone przez tego agenta.
    /// </summary>
    public TfsRole TfsRole { get; init; } = TfsRole.Task;

    /// <summary>
    /// Opcjonalny prompt używany do aktualizacji dokumentacji projektowej po wygenerowaniu wyników.
    /// Jeśli ustawiony, <see cref="Services.ProjectContextService"/> zapisuje zaproponowane zmiany jako pliki <c>.proposed.md</c>.
    /// </summary>
    public string? UpdateDocsPrompt { get; init; }

    /// <summary>
    /// Slugi agentów, których wyniki muszą być gotowe przed uruchomieniem tego agenta.
    /// Określa kolejność faz generowania w <see cref="ViewModels.MainViewModel"/>.
    /// </summary>
    public string[] DependsOn { get; init; } = [];

    /// <summary>
    /// Opcjonalny prompt używany podczas kroku wywiadu (Interview).
    /// Jeśli ustawiony, LLM generuje na jego podstawie listę pytań doprecyzowujących do użytkownika.
    /// </summary>
    public string? InterviewPrompt { get; init; }
}
