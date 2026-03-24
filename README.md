# FeatureForge

Desktopowa aplikacja WPF do generowania dokumentacji technicznej feature'ów za pomocą agentów LLM. Każdy agent reprezentuje rolę w zespole (PM, Analityk, Architekt, Tech Lead, Implementator) i generuje swoją część dokumentacji równolegle. Wyniki można wyeksportować do plików Markdown lub bezpośrednio do Azure DevOps jako work items.

---

## Szybki start

### Wymagania

- .NET 9 SDK
- Windows (WPF)
- Klucz API do jednego z obsługiwanych dostawców LLM (OpenRouter, Groq, Gemini lub Ollama lokalnie)

### Uruchomienie

```bash
# Klonowanie i uruchomienie
git clone <repo-url>
cd FeatureForge

# Ustaw klucz API (zalecane przez User Secrets)
dotnet user-secrets set "Llm:ApiKey" "sk-or-..." --project FeatureForge

# Uruchomienie
dotnet run --project FeatureForge/FeatureForge.csproj
```

### Konfiguracja modelu LLM

Edytuj `FeatureForge/appsettings.json`:

```json
{
  "Llm": {
    "Provider": "OpenRouter",
    "Model": "google/gemini-2.0-flash-001:free",
    "ApiKey": "..."
  }
}
```

Obsługiwane providery: `OpenRouter`, `Groq`, `Gemini`, `Ollama`.

---

## Dodawanie nowego projektu

Nie wymaga rekompilacji. Wystarczy:

1. Utwórz plik `FeatureForge/Profiles/<nazwa>.json` (patrz [schemat profilu](#schemat-profilu))
2. Uruchom aplikację — nowy profil pojawi się w dropdownie

Przykładowy profil: [`Profiles/shopflow.json`](FeatureForge/Profiles/shopflow.json)

---

## Schemat profilu

```json
{
  "Name": "NazwaProjektu",
  "Slug": "slug-projektu",
  "DocsPath": "C:\\ścieżka\\do\\docs",
  "InterviewSystemPrompt": "Globalny prompt dla fazy wywiadu...",
  "Modules": [
    { "Name": "NazwaModułu", "TfsId": 42 }
  ],
  "Tfs": {
    "CollectionUrl": "https://dev.azure.com/organizacja",
    "Project": "NazwaProjektu",
    "AreaPath": "NazwaProjektu",
    "IterationPath": "NazwaProjektu\\Sprint",
    "Tag": "Generated",
    "CustomFields": {
      "pm_Cel": "Custom.PM_Cel",
      "pm_Kryteria akceptacji": "Custom.PM_KryteriaAkceptacji"
    }
  },
  "Agents": [
    {
      "Name": "Product Manager",
      "Slug": "pm",
      "FieldNames": ["Cel", "Zakres", "Ryzyka", "Kryteria akceptacji"],
      "TfsRole": "FeatureDoc",
      "DependsOn": [],
      "SystemPrompt": "Jesteś doświadczonym PM-em...",
      "InterviewPrompt": "Wygeneruj 2-3 pytania z perspektywy PM..."
    }
  ]
}
```

### CustomFields — mapowanie pól na Azure DevOps

Klucz w `CustomFields` musi mieć format **`{slug}_{fieldLabel}`**, gdzie `fieldLabel` to dokładna wartość z `FieldNames` agenta (z uwzględnieniem wielkości liter i spacji):

```
"pm_Cel"                        → Custom.PM_Cel
"pm_Kryteria akceptacji"        → Custom.PM_KryteriaAkceptacji
"analityk_Ryzyka bezpieczeństwa"→ Custom.Analityk_RyzykaBezpieczenstwa
"tech-lead_Podział pracy"       → Custom.TechLead_PodzialPracy
```

Wartością jest referencja do pola w Azure DevOps (np. `Custom.PM_Cel`). Pola których klucza nie ma w `CustomFields` są pomijane — brak wpisu nie jest błędem.

### Pola agenta

| Pole | Opis |
|------|------|
| `Slug` | Unikalny identyfikator agenta w obrębie profilu |
| `FieldNames` | Nazwy pól formularza generowanych przez agenta |
| `TfsRole` | Typ work item w TFS: `FeatureDoc`, `Task`, `UserStory` |
| `DependsOn` | Slugi agentów, których output jest przekazywany jako kontekst |
| `SystemPrompt` | Główny prompt LLM — instrukcje dla agenta |
| `InterviewPrompt` | Prompt do generowania pytań wywiadu (opcjonalny) |
| `UpdateDocsPrompt` | Prompt do aktualizacji dokumentacji projektu (opcjonalny) |

### Fazy generowania

Agenci są uruchamiani w fazach na podstawie grafu `DependsOn`. W ramach jednej fazy działają równolegle:

```
Faza 1: pm, analityk          (brak zależności)
Faza 2: architekt             (zależy od pm, analityk)
Faza 3: tech-lead             (zależy od pm, analityk, architekt)
Faza 4: implementator         (zależy od analityk, architekt, tech-lead)
```

---

## Funkcje aplikacji

### Generowanie dokumentacji

1. Wybierz profil projektu i moduł
2. Wpisz tytuł i opis feature'u
3. (Opcjonalnie) uruchom **Wywiad** — agenci zadają pytania doprecyzowujące
4. Kliknij **Generuj wszystkich** — agenci działają równolegle w fazach

### Wywiad (Interview)

Przed generowaniem można przeprowadzić wywiad:
- **Globalny** — jeden zestaw pytań dla całego feature'u (z `InterviewSystemPrompt`)
- **Per agent** — każdy agent z `InterviewPrompt` generuje własne 2–3 pytania

Odpowiedzi są wstrzykiwane do kontekstu LLM przy generowaniu.

### Eksport wyników

- **Zapisz na dysk** — tworzy katalog `output/<data>_<feature>/` z plikami `.md` per agent i `summary.json`
- **Utwórz w TFS** — tworzy Feature + Tasks w Azure DevOps z mapowaniem pól na custom fields

### Kontekst dokumentacji projektu

Aplikacja wczytuje pliki `*.md` z `DocsPath` profilu i wstrzykuje je do kontekstu LLM. Jeśli wybrany jest epic/moduł, priorytet mają pliki z pasującego podkatalogu. Limit łączny: **20 000 znaków** — dalsze pliki są obcinane.

#### Wymagana struktura katalogu `DocsPath`

```
docs/
├── architektura.md          ← wczytywany zawsze (pliki w root, nierekurencyjnie)
├── stack-technologiczny.md  ← wczytywany zawsze
├── konwencje-kodu.md        ← wczytywany zawsze
│
├── Modul2/                  ← podkatalog epica "Modul2" (dopasowanie case-insensitive)
│   ├── mechanika.md         ← wczytywany gdy wybrany epic = "Modul2"
│   └── balans.md
│
└── 1. Modul1/            ← obsługiwane też prefiksy numeryczne, np. "1. Modul1"
    └── schemat-danych.md    ← wczytywany gdy wybrany epic = "Modul1"
```

**Reguły wczytywania:**

1. Jeśli wybrany jest epic — najpierw wczytywane są wszystkie `*.md` z pasującego podkatalogu (rekurencyjnie).
2. Następnie dołączane są pliki `*.md` z roota `DocsPath` (tylko top-level, nierekurencyjnie).
3. Pliki sortowane alfabetycznie, obcinane po przekroczeniu 20 000 znaków (z dopiskiem `[...obcięto]`).
4. Dopasowanie podkatalogu jest **case-insensitive** i **zawierające** — `"modul"` dopasuje `"Modul1"`, `"01. Modul1 System"` itp.

**Format wstrzykiwany do LLM:**

```
## architektura.md
<zawartość pliku>

## Modul2/mechanika.md
<zawartość pliku>
```

**Zalecana zawartość plików root:**

- `architektura.md` — opis warstw projektu, główne moduły, wzorce (DDD/CQRS itp.)
- `stack-technologiczny.md` — używane biblioteki, frameworki, wersje
- `konwencje-kodu.md` — namespace, nazewnictwo, styl kodu, przykłady

**Zalecana zawartość plików per epic/moduł:**

- `schemat-danych.md` — tabele, agregaty, relacje
- `api.md` — endpointy, kontrakty DTO
- `zasady-biznesowe.md` — reguły domenowe specyficzne dla modułu

### Refine (doprecyzowanie)

Każdy agent ma przycisk **Doprecyzuj** — można wpisać notatkę i wysłać ponownie do LLM z prośbą o poprawę.

---

## Konfiguracja

### `appsettings.json`

```json
{
  "Profiles": {
    "Directory": "Profiles"
  },
  "Llm": {
    "Provider": "OpenRouter",
    "Model": "google/gemini-2.0-flash-001:free",
    "Temperature": "0.2",
    "MaxTokens": "4096",
    "ApiKey": "...",
    "OllamaUrl": "http://localhost:11434/v1"
  },
  "Tfs": {
    "PersonalAccessToken": "..."
  }
}
```

### `appsettings.Development.json` (lokalny, gitignored)

Utwórz plik `FeatureForge/appsettings.Development.json` z wrażliwymi kluczami:

```json
{
  "Llm": {
    "ApiKey": "sk-or-..."
  },
  "Tfs": {
    "PersonalAccessToken": "..."
  }
}
```

Plik jest ładowany automatycznie gdy `DOTNET_ENVIRONMENT=Development`.

### User Secrets (alternatywa)

```bash
dotnet user-secrets set "Llm:ApiKey" "sk-or-..." --project FeatureForge
dotnet user-secrets set "Tfs:PersonalAccessToken" "..." --project FeatureForge
```

### Ścieżka do profili

Domyślnie: `Profiles/` obok pliku wykonywalnego. Można zmienić:

```json
{
  "Profiles": {
    "Directory": "D:\\MojeProfils\\Projekty"
  }
}
```

---

## Budowanie i publikacja

```bash
# Debug
dotnet build FeatureForge/FeatureForge.csproj

# Release (self-contained .exe)
dotnet publish FeatureForge/FeatureForge.csproj -c Release -r win-x64 --self-contained
```

---

## Struktura projektu

```
FeatureForge/
├── Models/
│   ├── AgentDefinition.cs       # Record: definicja agenta (deserializowany z JSON)
│   ├── AgentField.cs            # Observable pole formularza (label + value)
│   ├── AgentInterviewSelection.cs
│   ├── AgentState.cs            # Persystencja stanu agenta
│   ├── InterviewQuestion.cs     # Observable pytanie wywiadu
│   ├── ModuleConfig.cs          # Konfiguracja modułu (nazwa + TFS ID)
│   ├── ProjectProfile.cs        # Record: profil projektu + TfsProjectConfig
│   ├── SessionState.cs          # Persystencja sesji
│   └── TfsRole.cs               # Enum: typ work item w TFS
├── Services/
│   ├── ExportService.cs         # Eksport do Markdown + summary.json
│   ├── LlmService.cs            # Wywołania LLM przez Semantic Kernel
│   ├── ProfileLoaderService.cs  # Ładowanie profili z plików JSON
│   ├── ProjectContextService.cs # Wczytywanie dokumentacji projektu z dysku
│   └── TfsService.cs            # Tworzenie work items w Azure DevOps
├── ViewModels/
│   ├── AgentTabViewModel.cs     # Stan zakładki agenta (pola, notatki, approve)
│   └── MainViewModel.cs         # Główna logika: wywiad → generowanie → eksport
├── Profiles/
│   └── shopflow.json            # Profil przykładowy: platforma e-commerce
├── MainWindow.xaml(.cs)         # Główne okno UI (WPF, dark theme)
├── App.xaml.cs                  # DI + konfiguracja
└── appsettings.json             # Konfiguracja LLM + ścieżka profili
```

---

## Zależności

| Biblioteka | Wersja | Rola |
|-----------|--------|------|
| CommunityToolkit.Mvvm | 8.4.0 | MVVM (ObservableObject, RelayCommand) |
| Microsoft.SemanticKernel | 1.72.0 | Abstrakcja LLM (OpenAI-compatible API) |
| Microsoft.SemanticKernel.Connectors.Google | 1.72.0-alpha | Wsparcie Gemini |
| Microsoft.TeamFoundationServer.Client | 19.225.2 | Azure DevOps API |
| Polly | 8.6.5 | Retry + exponential backoff dla TFS |
| Microsoft.Extensions.* | 10.0.x | DI, konfiguracja, user secrets |
<img width="1357" height="929" alt="obraz" src="https://github.com/user-attachments/assets/22139f58-44a0-493a-92c3-dc72bc63a891" />


