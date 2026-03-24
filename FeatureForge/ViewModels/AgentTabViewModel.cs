using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeatureForge.Models;
using FeatureForge.Services;

namespace FeatureForge.ViewModels;

public partial class AgentTabViewModel : ObservableObject
{
    private readonly LlmService _llm;
    private readonly ProjectContextService _projectContext;

    public AgentDefinition Definition { get; }
    public string AgentName => Definition.Name;
    public bool HasUpdateDocsSupport => Definition.UpdateDocsPrompt is not null;
    public ObservableCollection<AgentField> Fields { get; }

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderBrush))]
    private bool _isApproved;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderBrush))]
    private bool _hasError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefineCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateDocsUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateSingleCommand))]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _docsUpdateStatus = string.Empty;

    public string ModuleName { get; set; } = string.Empty;
    public string FeatureTitle { get; set; } = string.Empty;
    public string FeatureText { get; set; } = string.Empty;
    public Action? OnApproved { get; set; }

    public string HeaderBrush => HasError ? "#EF5350" : IsApproved ? "#2E7D32" : "#424242";

    public AgentTabViewModel(AgentDefinition definition, LlmService llm, ProjectContextService projectContext)
    {
        Definition = definition;
        _llm = llm;
        _projectContext = projectContext;
        Fields = new ObservableCollection<AgentField>(
            definition.FieldNames.Select(f => new AgentField(f))
        );
    }

    public void ApplyResult(Dictionary<string, string> result)
    {
        foreach (var field in Fields)
        {
            if (result.TryGetValue(field.Label, out var value))
                field.Value = value;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefine))]
    private async Task RefineAsync()
    {
        if (string.IsNullOrWhiteSpace(Notes)) return;
        IsLoading = true;
        StatusMessage = "Poprawiam...";
        try
        {
            var context = await _projectContext.LoadContextAsync(ModuleName);
            var result = await _llm.RefineAsync(Definition, Fields, Notes, context);
            ApplyResult(result);
            Notes = string.Empty;
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRefine() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        IsApproved = true;
        StatusMessage = "Zatwierdzone";
        OnApproved?.Invoke();
    }

    private bool CanApprove() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanGenerateSingle))]
    private async Task GenerateSingleAsync()
    {
        IsLoading = true;
        HasError = false;
        StatusMessage = "Generowanie...";
        try
        {
            var context = await _projectContext.LoadContextAsync(ModuleName);
            var result = await _llm.GenerateAsync(Definition, FeatureTitle, FeatureText, context);
            ApplyResult(result);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanGenerateSingle() => !IsLoading;

    [RelayCommand(CanExecute = nameof(CanGenerateDocsUpdate))]
    private async Task GenerateDocsUpdateAsync()
    {
        IsLoading = true;
        DocsUpdateStatus = "Generuję propozycję dokumentacji...";
        try
        {
            var context = await _projectContext.LoadContextAsync(ModuleName);
            var updates = await _llm.GenerateDocsUpdateAsync(Definition, Fields, context);
            await _projectContext.SaveDocsUpdateAsync(updates);
            DocsUpdateStatus = $"Zapisano {updates.Count} plik(ów) .proposed.md";
        }
        catch (Exception ex)
        {
            DocsUpdateStatus = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanGenerateDocsUpdate() => !IsLoading && HasUpdateDocsSupport;
}
