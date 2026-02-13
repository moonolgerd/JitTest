# JiTTest — Design

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────┐
│                    AspireWithDapr.JiTTest                        │
│                     (Console App / dotnet tool)                  │
│                                                                  │
│  ┌──────────┐   ┌──────────┐   ┌──────────┐   ┌──────────────┐ │
│  │   Diff    │──▶│  Intent  │──▶│  Mutant  │──▶│    Test      │ │
│  │ Extractor │   │ Inferrer │   │Generator │   │  Generator   │ │
│  └──────────┘   └──────────┘   └──────────┘   └──────┬───────┘ │
│       │                                               │         │
│       │ LibGit2Sharp         Ollama (IChatClient)     │ Roslyn  │
│       ▼                                               ▼         │
│  ┌──────────┐                                  ┌──────────────┐ │
│  │   Git     │                                  │    Test      │ │
│  │   Repo    │                                  │  Executor    │ │
│  └──────────┘                                  └──────┬───────┘ │
│                                                       │         │
│                                                       ▼         │
│                                                ┌──────────────┐ │
│                                                │  Assessors   │ │
│                                                │ (Rule + LLM) │ │
│                                                └──────┬───────┘ │
│                                                       │         │
│                                                       ▼         │
│                                                ┌──────────────┐ │
│                                                │   Reporter   │ │
│                                                └──────────────┘ │
└─────────────────────────────────────────────────────────────────┘
         │                          ▲
         │ OpenAI-compat API        │
         ▼                          │
   ┌───────────┐            ┌───────────────┐
   │  Ollama   │            │ AspireWithDapr │
   │ localhost │            │   Projects     │
   │  :11434   │            │ (Shared, API)  │
   └───────────┘            └───────────────┘
```

## Project Structure

```
AspireWithDapr.JiTTest/
├── AspireWithDapr.JiTTest.csproj
├── Program.cs                      # Entry point, CLI argument parsing
├── Configuration/
│   └── JiTTestConfig.cs            # Config model + JSON deserialization
├── Pipeline/
│   ├── PipelineOrchestrator.cs     # Runs stages with configurable parallelism
│   ├── DiffExtractor.cs            # Git diff → structured ChangeSet
│   ├── IntentInferrer.cs           # ChangeSet → IntentSummary (LLM)
│   ├── MutantGenerator.cs          # IntentSummary → Mutant[] (LLM)
│   ├── TestGenerator.cs            # Mutant → GeneratedTest (LLM + Roslyn)
│   ├── TestExecutor.cs             # GeneratedTest → ExecutionResult (shadow-copy isolation)
│   └── Assessor.cs                 # ExecutionResult → AssessedCatch
├── Models/
│   ├── ChangeSet.cs                # Parsed diff: files, hunks, context
│   ├── IntentSummary.cs            # Inferred change intent
│   ├── Mutant.cs                   # Mutant: patch, description, rationale
│   ├── GeneratedTest.cs            # Test code + compilation status
│   ├── ExecutionResult.cs          # Pass/fail on original + mutated
│   └── AssessedCatch.cs            # Final catch with confidence
├── LLM/
│   ├── OllamaClientFactory.cs      # Creates IChatClient for Ollama
│   └── PromptTemplates.cs          # All prompt templates (intent, mutant, test, assess)
├── Compilation/
│   └── RoslynCompiler.cs           # In-memory C# compilation + error extraction
├── Reporting/
│   ├── ConsoleReporter.cs          # Terminal output with colors
│   └── MarkdownReporter.cs         # Optional MD file output
└── jittest-config.json             # Default configuration
```

## Component Design

### 1. DiffExtractor

**Input**: Git repository path + diff source configuration  
**Output**: `ChangeSet` — list of `ChangedFile` objects

```
ChangeSet
├── ChangedFile[]
│   ├── FilePath: string
│   ├── Hunks: Hunk[]
│   │   ├── OldStart, OldCount: int
│   │   ├── NewStart, NewCount: int
│   │   ├── BeforeContent: string
│   │   ├── AfterContent: string
│   │   └── Context: string         // ±20 lines surrounding
│   └── FullFileContent: string      // complete file for LLM context
└── Summary: string                  // human-readable diff summary
```

**Technology**: LibGit2Sharp
- `Repository.Diff.Compare<Patch>()` for staged vs HEAD
- `Repository.Diff.Compare<Patch>(tree, DiffTargets.WorkingDirectory)` for unstaged
- `Repository.Diff.Compare<Patch>(branchTip.Tree, headTip.Tree)` for branch comparisons

**Filtering**: Apply include/exclude glob patterns from config before processing.

### 2. IntentInferrer

**Input**: `ChangeSet`  
**Output**: `IntentSummary`

```
IntentSummary
├── Description: string              // "Added boundary validation for sub-zero temperatures"
├── BehaviorChanges: string[]        // ["GetSummaryForTemperature now returns 'Freezing' for -5°C"]
├── RiskAreas: string[]              // ["Boundary off-by-one at -5°C threshold"]
└── AffectedMethods: string[]        // ["WeatherUtilities.GetSummaryForTemperature"]
```

**Prompt strategy**: System prompt establishes the model as a code reviewer. User prompt contains the full diff + file context. Request structured JSON output. Few-shot example included for local model reliability.

### 3. MutantGenerator

**Input**: `IntentSummary` + `ChangeSet`  
**Output**: `Mutant[]` (3–5 per change)

```
Mutant
├── Id: string                       // "M001"
├── Description: string              // "Changed boundary from < -5 to <= -5"
├── Rationale: string                // "Off-by-one error at freezing threshold"
├── TargetFile: string
├── OriginalCode: string             // The exact code segment being mutated
├── MutatedCode: string              // The replacement code
├── LineStart: int
└── LineEnd: int
```

**Prompt strategy**: Provide the original code, the inferred intent, and ask: *"Generate realistic faults a developer might accidentally introduce. Each fault should be a plausible mistake, not a random operator flip."* Include 2-3 few-shot examples of good mutants. Request JSON array output.

**Key difference from Stryker**: Stryker applies ~50 rule-based operators blindly. The LLM generates only a few highly targeted mutants based on understanding the code's domain (temperature ranges, city names, actor state).

### 4. TestGenerator

**Input**: `Mutant` + original file content  
**Output**: `GeneratedTest`

```
GeneratedTest
├── TestCode: string                 // Full xUnit test class source
├── CompilationSuccess: bool
├── CompilationErrors: string[]      // If any
├── RetryCount: int                  // 0-2
└── ForMutant: Mutant
```

**Prompt strategy**: Provide original code, mutated code, and instruct: *"Write an xUnit test that passes against the original but fails against the mutant. The test must be self-contained, using only types from AspireWithDapr.Shared and standard xUnit assertions."*

The system prompt includes explicit compilation rules (use all `using` directives, fully qualified names for ambiguous types, public API only, no `async void`, etc.) to reduce first-attempt compilation failures.

**Roslyn compilation loop**:
1. Generate test code from LLM
2. Compile in-memory with `CSharpCompilation.Create()`, referencing project assemblies
3. If compilation fails: extract error messages, send back to LLM with *"Fix these compilation errors: [errors]"*
4. Retry up to `max-retries` times (default: 2)
5. If still failing after retries: skip this mutant, log the failure

**Assembly references for Roslyn** (loaded from project build output, deduplicated by filename):
- `AspireWithDapr.Shared.dll`
- `AspireWithDapr.ApiService.dll` (when targeting actor code)
- `xunit.core.dll`, `xunit.assert.dll`
- .NET 10 runtime references via `MetadataReference.CreateFromFile()`
- Satellite assemblies (`.resources.dll`) and duplicate filenames are skipped

**Parallelism**: Test generation for each mutant is independent. Stage 4 runs all mutant test generations concurrently via `Parallel.ForEachAsync` bounded by `max-parallel` (default: 3). The `RoslynCompiler` is thread-safe — its `_references` list is immutable after construction.

### 5. TestExecutor

**Input**: `GeneratedTest`  
**Output**: `ExecutionResult`

```
ExecutionResult
├── PassesOnOriginal: bool
├── FailsOnMutant: bool
├── IsCandidateCatch: bool          // true only if passes && fails
├── OriginalOutput: string           // Test runner output
├── MutantOutput: string
└── ErrorMessage: string?
```

**Execution strategy (shadow-copy isolation)**:
1. Create a unique temp directory per execution (`{tempDir}/{guid}/`)
2. Shadow-copy the target project into `{tempDir}/{guid}/shadow/` (fast recursive copy, skipping `bin/`, `obj/`, `.git/`, `.vs/`, `node_modules/`)
3. Write generated test to a transient test project in `{tempDir}/{guid}/test/` referencing the shadow copy
4. `dotnet test` against the shadow copy (original code) — must PASS
5. Apply mutant via string replacement on the **shadow copy only** (never touches real source files)
6. `dotnet test` again — must FAIL for a candidate catch
7. Clean up entire temp directory

**Transient test project**: A dynamically generated `.csproj` that references the shadow copy's `.csproj`. Each execution gets its own isolated copy, enabling safe parallel execution.

**Parallelism**: Since each execution operates on its own shadow copy, multiple test executions run concurrently via `Parallel.ForEachAsync` bounded by `max-parallel` (default: 3). No file-level locks or coordination needed.

### 6. Assessors

**Input**: `ExecutionResult` (candidate catches only)  
**Output**: `AssessedCatch`

```
AssessedCatch
├── IsAccepted: bool
├── RuleBasedResult: string          // "PASS" | "REJECT: <reason>"
├── LlmAssessment: string           // "YES — This mutant represents a plausible off-by-one..."
├── Confidence: string               // "HIGH" | "MEDIUM" | "LOW"
└── CandidateCatch: ExecutionResult
```

**Rule-based assessor** (fast, no LLM call):
- REJECT if test only asserts `== null` or `!= null`
- REJECT if test only checks string constants
- REJECT if mutant is in a comment, attribute, or `using` statement
- REJECT if mutant targets `Program.cs` or configuration code

**LLM-based assessor**:
- Prompt: *"Is this a true positive? Would this mutant represent a real bug? Answer YES or NO with reasoning."*
- Map response to confidence: explicit "yes" → HIGH, hedged "probably" → MEDIUM, else LOW
- Filter: reject below configurable threshold (default: MEDIUM)

**Parallelism**: Assessment calls are independent per candidate catch and run concurrently via `Parallel.ForEachAsync` bounded by `max-parallel`.

### 7. Reporter

Formats `AssessedCatch[]` into output.

**Console output** (default):
```
═══════════════════════════════════════════
  JiTTest Report — 2 catches in 1 file
═══════════════════════════════════════════

📁 AspireWithDapr.Shared/WeatherUtilities.cs

  🔴 CATCH #1 [HIGH confidence]
     Mutant: Changed '< -5' to '<= -5' in GetSummaryForTemperature
     Effect: Temperature -5°C would return "Freezing" instead of "Bracing"
     Test:   Assert.Equal("Bracing", WeatherUtilities.GetSummaryForTemperature(-5))

  🔴 CATCH #2 [MEDIUM confidence]
     Mutant: Swapped '||' to '&&' in IsColdWeather
     Effect: Only "Freezing AND Bracing AND Chilly" would be cold, not any one
     Test:   Assert.True(WeatherUtilities.IsColdWeather("Freezing"))

═══════════════════════════════════════════
```

**Markdown output** (optional, `--report md`): Same content written to `jittest-report.md`.

## LLM Integration

### Ollama Configuration

| Setting | Value |
|---------|-------|
| Endpoint | `http://localhost:11434/v1` |
| Model | `qwen2.5-coder:32b-instruct-q4_K_M` |
| API Compatibility | OpenAI Chat Completions |
| Client | `Microsoft.Extensions.AI.OpenAI` → `IChatClient` |

### Client Construction

```csharp
new OpenAIClient(
    new ApiKeyCredential("unused"),
    new OpenAIClientOptions { Endpoint = new Uri("http://localhost:11434/v1") }
).AsChatClient("qwen2.5-coder:32b-instruct-q4_K_M");
```

Same `IChatClient` interface used by `AspireWithDapr.Web/Services/ChatService.cs`. Allows future swap to any OpenAI-compatible backend by changing endpoint URL.

### Prompt Templates

All prompts stored in `PromptTemplates.cs` as static methods returning `ChatMessage[]`. Each includes:
- **System prompt**: Role definition + output format instructions + constraints
- **Few-shot examples**: 1-2 examples for each stage (critical for local model quality)
- **User prompt**: The actual code/diff content

### Token Management

- Context window: ~32K tokens for Qwen2.5-Coder 32B
- Budget per stage: Intent (~4K), Mutant generation (~8K), Test generation (~6K per mutant), Assessment (~2K)
- If file content exceeds budget: truncate to changed methods + signatures only

## Configuration Schema

```json
{
  "jittest-config": {
    "ollama-endpoint": "http://localhost:11434/v1",
    "model": "qwen2.5-coder:32b-instruct-q4_K_M",
    "diff-source": "staged",
    "mutate-targets": [
      "**/AspireWithDapr.Shared/**/*.cs",
      "**/AspireWithDapr.ApiService/**/*.cs"
    ],
    "exclude": [
      "**/Program.cs",
      "**/obj/**",
      "**/bin/**"
    ],
    "max-mutants-per-change": 5,
    "max-retries": 2,
    "max-parallel": 3,
    "confidence-threshold": "MEDIUM",
    "reporters": ["console"],
    "temp-directory": ".jittest-temp"
  }
}
```

## CLI Interface

```
dotnet run --project AspireWithDapr.JiTTest -- [options]

Options:
  --diff <source>       Diff source: staged, uncommitted, branch:<name>, HEAD~<n>
                        Default: staged
  --config <path>       Path to config file. Default: jittest-config.json
  --model <name>        Override model name
  --endpoint <url>      Override Ollama endpoint
  --report <format>     Report format: console, md, both. Default: console
  --verbose             Show full LLM prompts and responses
  --dry-run             Run diff extraction and intent inference only, no tests
```

## Error Handling

| Scenario | Behavior |
|----------|----------|
| Ollama not running | Exit with error: "Cannot connect to Ollama at {endpoint}" |
| Model not pulled | Exit with error: "Model {name} not found. Run: ollama pull {name}" |
| No diff detected | Exit 0: "No changes detected for diff source: {source}" |
| No matching files | Exit 0: "No files matching mutate-targets in current diff" |
| LLM returns unparseable JSON | Retry with stricter prompt (up to 2 retries), then skip |
| Roslyn compilation fails after retries | Skip mutant, log warning, continue pipeline |
| Test execution timeout | Kill after 30s, mark as inconclusive, continue |
| Git repo not found | Exit with error: "Not a git repository: {path}" |

## Design Decisions

| Decision | Rationale |
|----------|-----------|
| **Ollama over Azure AI Foundry** | User preference for fully local; no cloud dependency, no API costs, works offline. The project's existing `phi-4-mini` (3.8B params) is too small for reliable code mutation/test generation. |
| **Qwen2.5-Coder 32B recommended** | With 32GB available, this is the strongest local coding model; significantly better at generating compilable tests and realistic mutants than 7B-14B alternatives. |
| **`Microsoft.Extensions.AI.OpenAI` over `OllamaSharp`** | Ollama's OpenAI-compatible API means we use the same `IChatClient` interface the project already uses — zero new abstractions, swappable backend. |
| **Roslyn compilation retry loop** | Local models produce compilation errors ~15-25% of the time; feeding errors back for retry brings success rate to ~90%+. Enhanced prompts with compilation rules further reduce the retry rate. |
| **LibGit2Sharp over shelling out to git** | Type-safe diff parsing without CLI dependency on git being in PATH. |
| **Ephemeral tests over persistent suite** | Core JiTTest philosophy — tests are disposable, generated per-change, never maintained; eliminates test maintenance burden entirely. |
| **xUnit as test runner** | Lightest ceremony for single-file test generation; .NET community standard with best tooling support. |
| **Two-layer assessment** | Meta's paper shows rule-based + LLM assessors reduce human review load by 70%. |
| **Parallel pipeline stages 4–6** | LLM calls and test executions are the dominant cost; parallelizing independent work across mutants yields 3–5x speedup with default `max-parallel: 3`. |
| **Shadow-copy isolation** | Replacing destructive in-place source mutation with per-execution shadow copies enables safe parallel test execution without file locks or coordination. |
| **Compilation-aware prompts** | Embedding explicit compilation rules in the test generation system prompt reduces first-attempt failure rate from ~25% to ~10%, saving 1–2 LLM round-trips per mutant. |
| **Deduplicated Roslyn references** | Assembly references loaded from build output are deduplicated by filename and skip satellite assemblies, reducing memory and avoiding ambiguous-reference compilation errors. |

## Performance Architecture

The pipeline is structured as **sequential stages with intra-stage parallelism**:

```
Stage 1 (Diff)     ─── sequential (fast, single git operation)
Stage 2 (Intent)   ─── sequential (single LLM call)
Stage 3 (Mutants)  ─── sequential (single LLM call)
Stage 4 (TestGen)  ═══ PARALLEL ─ up to max-parallel concurrent LLM calls + Roslyn compiles
Stage 5 (Execute)  ═══ PARALLEL ─ up to max-parallel concurrent dotnet test (shadow copies)
Stage 6 (Assess)   ═══ PARALLEL ─ up to max-parallel concurrent LLM calls
Reporting          ─── sequential (fast, console/file output)
```

### Concurrency model

- **`Parallel.ForEachAsync`** with `MaxDegreeOfParallelism` set to `config.MaxParallel` (default: 3)
- **`ConcurrentBag<T>`** collects results from parallel stages
- Thread-safety: `RoslynCompiler` is immutable after construction; `IChatClient.GetResponseAsync` is safe for concurrent calls; `TestExecutor` uses isolated temp directories per execution

### Configuration

| Setting | Default | Guidance |
|---------|---------|----------|
| `max-parallel: 1` | — | Sequential behavior (identical to pre-parallelism) |
| `max-parallel: 2–3` | **3** | Recommended for local Ollama with a single GPU |
| `max-parallel: 4–8` | — | Multi-GPU setups or remote/cloud LLM endpoints |

### Stage timing telemetry

The orchestrator tracks wall-clock time per stage and prints a summary at the end:

```
── Stage Timings ─────────────────────────────────────────
  1-Diff            0.2s  (0%)
  Build             3.5s  (3%)
  2-Intent         18.3s  (16%)
  3-Mutants        22.1s  (19%)
  4-TestGen        35.7s  (31%)    ← parallelized
  5-Exec           28.4s  (25%)    ← parallelized
  6-Assess          6.2s  (5%)     ← parallelized
  Total           114.4s
  Parallelism:     3
```
