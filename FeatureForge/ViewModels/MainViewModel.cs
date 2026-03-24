using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FeatureForge.Models;
using FeatureForge.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Win32;

namespace FeatureForge.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly LlmService _llm;
    private readonly TfsService _tfs;
    private readonly ExportService _export;
    private readonly ProjectContextService _projectContext;
    private readonly string _tfsPat;

    private string _loadedContext = string.Empty;

    public ObservableCollection<AgentTabViewModel> Agents { get; }
    public List<string> AvailableModules { get; private set; }
    public List<Models.ProjectProfile> AvailableProjects { get; }

    [ObservableProperty]
    private Models.ProjectProfile _selectedProject = null!;

    [ObservableProperty]
    private string _moduleName = string.Empty;

    partial void OnModuleNameChanged(string value)
    {
        foreach (var agent in Agents)
            agent.ModuleName = value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InterviewCommand))]
    private string _featureTitle = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InterviewCommand))]
    private string _featureText = string.Empty;

    partial void OnFeatureTitleChanged(string value)
    {
        foreach (var agent in Agents)
            agent.FeatureTitle = value;
    }

    partial void OnFeatureTextChanged(string value)
    {
        foreach (var agent in Agents)
            agent.FeatureText = value;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(InterviewCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveToDiskCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateTfsTasksCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private double _progress;

    // --- Wywiad ---
    public System.Collections.ObjectModel.ObservableCollection<Models.InterviewQuestion> InterviewQuestions { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<Models.AgentInterviewSelection> AgentInterviewSelections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptInterviewCommand))]
    private bool _isInterviewVisible;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartInterviewCommand))]
    private bool _isAgentSelectionVisible;

    [ObservableProperty]
    private bool _isInterviewCompleted;

    private string _interviewContext = string.Empty;
    private Dictionary<string, string> _perAgentInterviewContext = [];
    private string? _currentSavePath;

    public bool AllApproved => Agents.All(a => a.IsApproved);

    public MainViewModel(LlmService llm, TfsService tfs, ExportService export, ProjectContextService projectContext, IConfiguration config, ProfileLoaderService profileLoader)
    {
        _llm = llm;
        _tfs = tfs;
        _export = export;
        _projectContext = projectContext;
        _tfsPat = config["Tfs:PersonalAccessToken"] ?? string.Empty;

        AvailableProjects = [.. profileLoader.Profiles];
        _selectedProject = AvailableProjects[0];

        var profile = SelectedProject;
        AvailableModules = profile.Modules.Select(m => m.Name).ToList();
        _tfs.SetEpicIds(profile.Modules.Where(m => m.TfsId.HasValue).ToDictionary(m => m.Name, m => m.TfsId!.Value));
        _projectContext.SetDocsPath(profile.DocsPath);
        _tfs.Configure(profile.Tfs, _tfsPat);

        Agents = new ObservableCollection<AgentTabViewModel>(
            profile.Agents.Select(def => new AgentTabViewModel(def, llm, projectContext))
        );

        foreach (var agent in Agents)
        {
            agent.PropertyChanged += OnAgentPropertyChanged;
            agent.OnApproved = () => _ = AutoSaveStateAsync();
        }

        RebuildAgentSelections(profile);
    }

    partial void OnSelectedProjectChanged(Models.ProjectProfile value)
    {
        // Przeładuj moduły
        AvailableModules = value.Modules.Select(m => m.Name).ToList();
        OnPropertyChanged(nameof(AvailableModules));
        ModuleName = string.Empty;

        // Przeładuj agentów
        foreach (var agent in Agents)
            agent.PropertyChanged -= OnAgentPropertyChanged;
        Agents.Clear();
        foreach (var def in value.Agents)
        {
            var vm = new AgentTabViewModel(def, _llm, _projectContext);
            vm.PropertyChanged += OnAgentPropertyChanged;
            vm.OnApproved = () => _ = AutoSaveStateAsync();
            Agents.Add(vm);
        }

        // Zaktualizuj serwisy
        _projectContext.SetDocsPath(value.DocsPath);
        _tfs.SetEpicIds(value.Modules.Where(m => m.TfsId.HasValue).ToDictionary(m => m.Name, m => m.TfsId!.Value));
        _tfs.Configure(value.Tfs, _tfsPat);

        // Reset stanu
        FeatureTitle = string.Empty;
        FeatureText = string.Empty;
        StatusMessage = string.Empty;
        Progress = 0;
        _loadedContext = string.Empty;
        _interviewContext = string.Empty;
        _perAgentInterviewContext = [];
        _currentSavePath = null;
        IsInterviewVisible = false;
        IsAgentSelectionVisible = false;
        IsInterviewCompleted = false;
        InterviewQuestions.Clear();
        RebuildAgentSelections(value);
        OnPropertyChanged(nameof(AllApproved));
    }

    private void OnAgentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentTabViewModel.IsApproved))
            OnPropertyChanged(nameof(AllApproved));
    }

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(FeatureTitle) || string.IsNullOrWhiteSpace(FeatureText)) return;

        IsGenerating = true;
        Progress = 0;
        StatusMessage = "Generowanie...";

        foreach (var agent in Agents)
        {
            agent.IsApproved = false;
            agent.HasError = false;
        }

        try
        {
            _loadedContext = await _projectContext.LoadContextAsync(ModuleName);

            // Budujemy etapy pipeline na podstawie DependsOn — topologiczne grupowanie
            var remaining = Agents.ToList();
            var completed = new HashSet<string>();
            int done = 0;

            while (remaining.Count > 0)
            {
                // Agenci gotowi do uruchomienia: wszystkie ich zależności już ukończone
                var phase = remaining
                    .Where(a => a.Definition.DependsOn.All(dep => completed.Contains(dep)))
                    .ToList();

                if (phase.Count == 0)
                    break; // cykl lub błąd definicji — wyjdź, żeby nie zawisnąć

                var phaseTasks = phase.Select(async agentVm =>
                {
                    agentVm.IsLoading = true;
                    try
                    {
                        var priorContext = BuildPriorContext(agentVm.Definition.DependsOn, Agents);
                        var agentInterview = _perAgentInterviewContext.TryGetValue(agentVm.Definition.Slug, out var ctx) ? ctx : _interviewContext;
                        var result = await _llm.GenerateAsync(agentVm.Definition, FeatureTitle, FeatureText, _loadedContext, priorContext, agentInterview);
                        agentVm.ApplyResult(result);
                        agentVm.StatusMessage = string.Empty;
                    }
                    catch (Exception ex)
                    {
                        agentVm.HasError = true;
                        agentVm.StatusMessage = $"Błąd: {ex.Message}";
                    }
                    finally
                    {
                        agentVm.IsLoading = false;
                        Interlocked.Increment(ref done);
                        Progress = (double)done / Agents.Count * 100;
                    }
                });

                await Task.WhenAll(phaseTasks);

                foreach (var a in phase)
                {
                    completed.Add(a.Definition.Slug);
                    remaining.Remove(a);
                }
            }

            _currentSavePath = await _export.SaveStateAsync(FeatureTitle, FeatureText, ModuleName, Agents, _interviewContext, _perAgentInterviewContext.Count > 0 ? _perAgentInterviewContext : null);
            var savedPath = await _export.SaveAsync(FeatureTitle, FeatureText, Agents);
            StatusMessage = $"Gotowe. Zapisano: {savedPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private bool CanGenerate() => !IsGenerating && !string.IsNullOrWhiteSpace(FeatureTitle) && !string.IsNullOrWhiteSpace(FeatureText);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Interview()
    {
        if (string.IsNullOrWhiteSpace(FeatureTitle) || string.IsNullOrWhiteSpace(FeatureText)) return;

        var hasPerAgent = AgentInterviewSelections.Any(s => s.HasInterviewPrompt);
        var hasGlobal = !string.IsNullOrWhiteSpace(SelectedProject.InterviewSystemPrompt);

        if (!hasPerAgent && !hasGlobal)
        {
            StatusMessage = "Brak promptu wywiadu dla tego projektu.";
            return;
        }

        InterviewQuestions.Clear();
        _interviewContext = string.Empty;
        _perAgentInterviewContext = [];
        IsInterviewCompleted = false;

        if (hasPerAgent)
        {
            // Pokaż panel wyboru agentów
            IsAgentSelectionVisible = true;
            IsInterviewVisible = false;
        }
        else
        {
            // Fallback: globalny wywiad (stare zachowanie)
            _ = RunGlobalInterviewAsync();
        }
    }

    private async Task RunGlobalInterviewAsync()
    {
        IsGenerating = true;
        StatusMessage = "Generowanie pytań wywiadu...";
        try
        {
            var questions = await _llm.GenerateQuestionInterviewAsync(SelectedProject.InterviewSystemPrompt, FeatureTitle, FeatureText);
            InterviewQuestions.Clear();
            foreach (var q in questions)
                InterviewQuestions.Add(q);

            IsInterviewVisible = true;
            StatusMessage = $"Wygenerowano {questions.Count} pytań. Uzupełnij odpowiedzi i zatwierdź.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd wywiadu: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private bool CanStartInterview() => !IsGenerating && IsAgentSelectionVisible;

    [RelayCommand(CanExecute = nameof(CanStartInterview))]
    private async Task StartInterviewAsync()
    {
        var selected = AgentInterviewSelections
            .Where(s => s.IsSelected && s.HasInterviewPrompt)
            .ToList();

        if (selected.Count == 0)
        {
            IsAgentSelectionVisible = false;
            return;
        }

        IsAgentSelectionVisible = false;
        IsGenerating = true;
        StatusMessage = "Generowanie pytań wywiadu...";

        try
        {
            var tasks = selected.Select(async sel =>
            {
                var questions = await _llm.GenerateQuestionInterviewAsync(sel.Definition.InterviewPrompt!, FeatureTitle, FeatureText);
                return (sel.Definition.Slug, sel.Definition.Name, questions);
            });

            var results = await Task.WhenAll(tasks);

            InterviewQuestions.Clear();
            int idCounter = 1;
            foreach (var (slug, name, questions) in results)
            {
                bool first = true;
                foreach (var q in questions)
                {
                    InterviewQuestions.Add(new Models.InterviewQuestion
                    {
                        Id = $"q{idCounter++}",
                        Question = q.Question,
                        DefaultAnswer = q.DefaultAnswer,
                        AgentSlug = slug,
                        AgentName = name,
                        IsFirstInGroup = first
                    });
                    first = false;
                }
            }

            IsInterviewVisible = true;
            StatusMessage = $"Wygenerowano {InterviewQuestions.Count} pytań. Uzupełnij odpowiedzi i zatwierdź.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd wywiadu: {ex.Message}";
        }
        finally
        {
            IsGenerating = false;
        }
    }

    [RelayCommand]
    private void CancelAgentSelection()
    {
        IsAgentSelectionVisible = false;
    }

    [RelayCommand(CanExecute = nameof(IsInterviewVisible))]
    private void AcceptInterview()
    {
        foreach (var q in InterviewQuestions)
        {
            if (string.IsNullOrWhiteSpace(q.Answer))
                q.Answer = q.DefaultAnswer;
        }

        // Grupuj per agent jeśli wywiad per-agent, wpp buduj globalny kontekst
        var perAgent = InterviewQuestions.Any(q => !string.IsNullOrEmpty(q.AgentSlug));
        if (perAgent)
        {
            _perAgentInterviewContext = InterviewQuestions
                .GroupBy(q => q.AgentSlug)
                .ToDictionary(
                    g => g.Key,
                    g =>
                    {
                        var sb = new System.Text.StringBuilder();
                        foreach (var q in g)
                            sb.AppendLine($"P: {q.Question}\nO: {q.Answer}\n");
                        return sb.ToString();
                    });
            _interviewContext = string.Empty;
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (var q in InterviewQuestions)
                sb.AppendLine($"P: {q.Question}\nO: {q.Answer}\n");
            _interviewContext = sb.ToString();
            _perAgentInterviewContext = [];
        }

        IsInterviewVisible = false;
        IsInterviewCompleted = true;
        StatusMessage = "Wywiad zatwierdzony. Możesz teraz generować.";
    }

    [RelayCommand]
    private void SkipInterview()
    {
        _interviewContext = string.Empty;
        _perAgentInterviewContext = [];
        IsInterviewVisible = false;
        IsAgentSelectionVisible = false;
        IsInterviewCompleted = false;
        StatusMessage = string.Empty;
    }

    private void RebuildAgentSelections(Models.ProjectProfile profile)
    {
        AgentInterviewSelections.Clear();
        foreach (var agent in profile.Agents.Where(a => a.InterviewPrompt is not null))
            AgentInterviewSelections.Add(new Models.AgentInterviewSelection(agent));
    }

    private static string BuildPriorContext(string[] dependsOn, IEnumerable<AgentTabViewModel> agents)
    {
        if (dependsOn.Length == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var agent in agents.Where(a => dependsOn.Contains(a.Definition.Slug)))
        {
            var fields = agent.Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToList();
            if (fields.Count == 0) continue;

            sb.AppendLine($"### {agent.AgentName}");
            var dict = fields.ToDictionary(f => f.Label, f => f.Value);
            sb.AppendLine("```json");
            sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(dict,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            sb.AppendLine("```");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    [RelayCommand]
    private void Clear()
    {
        FeatureTitle = string.Empty;
        FeatureText = string.Empty;
        ModuleName = string.Empty;
        foreach (var agent in Agents)
        {
            foreach (var field in agent.Fields)
                field.Value = string.Empty;
            agent.Notes = string.Empty;
            agent.IsApproved = false;
            agent.HasError = false;
            agent.StatusMessage = string.Empty;
            agent.DocsUpdateStatus = string.Empty;
        }
        StatusMessage = string.Empty;
        Progress = 0;
        _loadedContext = string.Empty;
        _interviewContext = string.Empty;
        _perAgentInterviewContext = [];
        _currentSavePath = null;
        IsInterviewVisible = false;
        IsAgentSelectionVisible = false;
        IsInterviewCompleted = false;
        InterviewQuestions.Clear();
    }

    private async Task AutoSaveStateAsync()
    {
        if (string.IsNullOrWhiteSpace(FeatureTitle)) return;
        try
        {
            _currentSavePath = await _export.SaveStateAsync(
                FeatureTitle, FeatureText, ModuleName, Agents,
                _interviewContext,
                _perAgentInterviewContext.Count > 0 ? _perAgentInterviewContext : null,
                _currentSavePath);
        }
        catch { /* auto-save w tle – nie przerywamy pracy użytkownika */ }
    }

    [RelayCommand]
    private void Load()
    {
        var dialog = new OpenFolderDialog { Title = "Wybierz folder sesji" };
        if (dialog.ShowDialog() != true) return;

        var state = _export.LoadState(dialog.FolderName);
        if (state == null) { StatusMessage = "Nie znaleziono pliku stanu."; return; }

        FeatureTitle = state.FeatureTitle;
        FeatureText = state.Feature;
        ModuleName = state.Module;

        foreach (var agent in Agents)
        {
            var agentState = state.Agents.FirstOrDefault(a => a.Slug == agent.Definition.Slug);
            if (agentState == null) continue;
            agent.ApplyResult(agentState.Fields);
            agent.Notes = agentState.Notes;
            agent.IsApproved = agentState.IsApproved;
            agent.HasError = agentState.HasError;
            if (agentState.HasError)
                agent.StatusMessage = "Błąd z poprzedniej sesji – wygeneruj ponownie";
        }

        _interviewContext = state.InterviewContext ?? string.Empty;
        _perAgentInterviewContext = state.PerAgentInterviewContext ?? [];
        IsInterviewCompleted = !string.IsNullOrEmpty(_interviewContext) || _perAgentInterviewContext.Count > 0;
        _currentSavePath = dialog.FolderName;
        StatusMessage = $"Wczytano: {state.FeatureTitle}";
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveToDiskAsync()
    {
        StatusMessage = "Zapisuję na dysk...";
        try
        {
            var path = await _export.SaveAsync(FeatureTitle, FeatureText, Agents);
            StatusMessage = $"Zapisano: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd zapisu: {ex.Message}";
        }
    }

    private bool CanSave() => !IsGenerating;

    [RelayCommand(CanExecute = nameof(CanCreateTfs))]
    private async Task CreateTfsTasksAsync()
    {
        StatusMessage = "Tworzę strukturę w TFS...";
        try
        {
            await _tfs.CreateFeatureWithTasksAsync(ModuleName, FeatureTitle, Agents);
            StatusMessage = "Utworzono Feature + Tasks w TFS.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Błąd TFS: {ex.Message}";
        }
    }

    private bool CanCreateTfs() => !IsGenerating && _tfs.IsConfigured;
}
