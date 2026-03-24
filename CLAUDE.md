# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build FeatureForge/FeatureForge.csproj

# Run (WPF app, requires Windows)
dotnet run --project FeatureForge/FeatureForge.csproj

# Publish
dotnet publish FeatureForge/FeatureForge.csproj -c Release
```

There are no automated tests in this project.

## Configuration

`appsettings.json` holds LLM provider settings and export path. Sensitive values (API keys, PAT) should be moved to **User Secrets** (`dotnet user-secrets`).

LLM providers supported: `Groq`, `Gemini`, `OpenRouter`, `Ollama`. Switch via `Llm:Provider` + `Llm:Model` + `Llm:ApiKey`.

TFS connection and module lists are defined in code via `ProjectProfiles` (not appsettings).

## Architecture

### Multi-Project Profile System
The app supports multiple projects via `ProjectProfiles.All` (in `Models/ProjectProfiles.cs`). Each `ProjectProfile` bundles:
- `Agents: AgentDefinition[]` — project-specific agent set defined in the profile JSON
- `Modules: ModuleConfig[]` — selectable module list (with optional TFS area IDs)
- `DocsPath` — path to `.md` documentation files injected as LLM context
- `Tfs: TfsProjectConfig` — Azure DevOps collection URL, project, area/iteration paths, custom field mappings
- `InterviewSystemPrompt` — prompt for the pre-generation interview step

Profiles are loaded from JSON files in the `Profiles/` directory. Each profile is self-contained and can represent any project type (e.g. a .NET web app, a game backend, an e-commerce platform).

### Data Flow
1. User selects a project profile and module, enters a feature description
2. Optional **Interview** step: LLM generates 5–8 clarifying questions (`InterviewQuestion` list); user fills answers which are appended to the feature context
3. `MainViewModel.GenerateAsync()` runs agents in topological order using parallel `Task.WhenAll` per phase
4. Each `LlmService.GenerateAsync()` call builds a `ChatHistory`: system prompt → optional project docs context → prior-phase outputs → interview answers + feature text
5. Results populate `AgentTabViewModel` instances; user edits/approves tabs, then exports via **Save to disk** (`ExportService`) or **Create TFS tasks** (`TfsService`)

### Agent Dependency Phases
Agents run in phases based on `DependsOn` slugs in each `AgentDefinition`. Example for HMS:
- **Phase 1** (independent): PM, Analityk
- **Phase 2** (needs Phase 1): Architekt
- **Phase 3** (needs 1+2): Tech Lead
- **Phase 4** (needs 1–3): Implementator

Each project profile defines its own agent graph with different roles and field names. `MainViewModel.BuildPriorContext(agent)` collects outputs from dependency agents.

### Key Classes
- `ProjectProfile` — record bundling agents, modules, docs path, TFS config, interview prompt for one project
- `ProjectProfiles` — static list of all registered profiles
- `AgentDefinition` — record: name, slug, field names, system prompt (Polish), TFS role, `DependsOn` slugs
- `InterviewQuestion` — observable model for pre-generation Q&A (id, question, defaultAnswer, answer)
- `AgentTabViewModel` — per-agent observable state: dynamic `AgentField` list, notes, approval flag, busy state
- `LlmService` — SemanticKernel wrapper; `GenerateAsync` and `RefineAsync` return `Dictionary<string, string>` parsed from LLM JSON
- `TfsService` — creates Azure DevOps work items with custom field mappings from `TfsProjectConfig`; Polly retry
- `ExportService` — writes dated directories with one `.md` per agent + `summary.json`
- `ProjectContextService` — loads `.md` docs from `DocsPath`; module subdir files take priority; truncates at 20 000 chars; `SaveDocsUpdateAsync` writes `.proposed.md` files

### UI Patterns
- Dark theme: `#1E1E1E` toolbar, `#2D2D2D` bottom bar
- Main tab control: one `TabItem` per agent, dynamically bound to `AgentTabViewModels`
- `IsGenerating` bool gates the progress bar visibility; `StatusMessage` shows in bottom bar
- All commands use `AsyncRelayCommand` from CommunityToolkit.Mvvm

## Adding a New Project Profile
1. Create `Models/MyProjectAgents.cs` with a `static AgentDefinition[] All` array — define agents with Polish field names, system prompts, and `DependsOn` slugs
2. Register in `ProjectProfiles.All` with appropriate `Modules`, `DocsPath`, and `TfsProjectConfig` (including `CustomFields` mapping agent field keys to Azure DevOps custom field refs)
3. The UI and generation pipeline pick up the new profile automatically

## Language Note
The application UI, prompts, and field names are in **Polish**. Keep that convention when adding new agents or UI strings.
