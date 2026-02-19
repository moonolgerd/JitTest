# JiTTest — Copilot Instructions

## What This Project Does
JiTTest is a .NET 10 **dotnet global tool** (`jittest`) that runs an LLM-driven 6-stage catching-test pipeline against git changes. It is **not** a library — the entry point is `JiTTest/Program.cs` using `System.CommandLine`.

## 6-Stage Pipeline (the core mental model)
Each stage lives in `JiTTest/Pipeline/` and is orchestrated by `PipelineOrchestrator.cs`:
1. **DiffExtractor** — reads git via LibGit2Sharp; produces a `ChangeSet` with full file content + hunks
2. **IntentInferrer** — LLM call; JSON → `IntentSummary` (description, behaviorChanges, riskAreas)
3. **MutantGenerator** — LLM call; JSON → `List<Mutant>`; validates `OriginalCode` exists in file; annotates Roslyn-derived accessibility (`ContainingMemberIsPublic/Protected/Private`)
4. **TestGenerator** — LLM call per mutant; Roslyn-compiles result; auto-fixes usings; retries up to `config.MaxRetries`
5. **TestExecutor** — shadow-copies target project to temp dir; runs `dotnet test` against **original**, then applies mutation to shadow, runs again; only `PassesOnOriginal && FailsOnMutant` are candidate catches
6. **Assessor** — LLM call; JSON → `AssessedCatch`; filters false positives by confidence threshold

Stages 4–6 run with `Parallel.ForEachAsync` bounded by `config.MaxParallel`.

## Build & Run
```bash
# Run from repo root
dotnet build JiTTest/JiTTest.csproj

# Pack and install as global tool (required to test end-to-end as a real CLI)
dotnet pack JiTTest/JiTTest.csproj -c Release
dotnet tool install --global --add-source JiTTest/bin/Release JitTest

# Run against this repo itself
jittest --diff-source uncommitted --verbose
```

## LLM Abstraction
All LLM calls use `IChatClient` from `Microsoft.Extensions.AI`. The factory `LLM/LlmClientFactory.cs` returns an `OpenAIClient`-backed `IChatClient` targeting either:
- **Ollama** (local): endpoint from `ollama-endpoint` / `llm-endpoint` config key, API key = `"unused"`
- **GitHub Models** (cloud): endpoint contains `models.github.ai` or `inference.ai.azure.com`; requires `GITHUB_TOKEN` env var or `github-token` config key

When adding new LLM calls, follow the existing pattern: add a static method to `LLM/PromptTemplates.cs` returning `List<ChatMessage>`, then call `chatClient.GetResponseAsync(messages)`.

## Roslyn Compilation (in-memory validation)
`Compilation/RoslynCompiler.cs` compiles generated test code before executing it with `dotnet test`.

Critical rules:
- Constructor takes `projectBuildOutputPaths` (assembled by `FindAllBuildOutputs` in the orchestrator), `verbose`, and `repositoryRoot`
- `DiscoverGlobalUsings()` scans `obj/**/*.GlobalUsings.g.cs` and `**/GlobalUsings.cs` so in-memory compilation matches `dotnet build` for projects with `<ImplicitUsings>enable</ImplicitUsings>`
- `Compile()` prepends a global-usings syntax tree to every compilation — **always inject `_globalUsings` as a separate `SyntaxTree`; do not concatenate text**
- `AutoFixUsings()` handles: missing `using` directives (via `s_usingMap`), CS1674 (`using var` on non-disposable), CS0122 (protected method calls), CS1929 (LINQ on arrays)
- `ExtractNamespace()` is used by `TestGenerator` to auto-prepend `using <SourceNamespace>;` so the LLM always has the correct target namespace

## Shadow-Copy Execution Pattern (TestExecutor)
To mutate source safely in parallel: each test run gets a unique dir under `config.TempDirectory` (`guid/shadow/` + `guid/test/`). The `.csproj` in `guid/test/` references the shadow. `FixProjectReferences()` rewrites relative `<ProjectReference>` paths to absolute paths back into the real repo, so transitive dependencies resolve. A `GlobalUsings.cs` is written into the transient project to mirror project-specific global usings.

## Mutant Accessibility Annotations
`MutantGenerator` uses Roslyn's `CSharpSyntaxTree` to walk containing member declarations and sets `Mutant.ContainingMember*` flags **after** the LLM call. `PipelineOrchestrator` then filters: all public mutants pass through; only up to 2 private/protected mutants are kept (`maxNonPublic = 2`). The `AccessibilityHint` property is passed to the test-gen prompt.

## Config Loading
`JiTTestConfig.Load()` supports both a root-level flat JSON and a `"jittest-config": { }` nested section in the same file. CLI flags override config keys. `RepositoryRoot` is not in JSON — it is resolved at startup by walking up from `cwd` looking for `.git`.

## Key Conventions
- All prompt templates are in `LLM/PromptTemplates.cs` — add new prompts there, never inline
- `LLM/LlmResponseParser.cs` owns all JSON/code extraction from raw LLM text (`ParseJson<T>`, `ExtractCSharpCode`)
- Models in `Models/` are pure data classes (no logic); `[JsonIgnore]` for runtime-only fields
- Exit codes: `0` = no catches, `1` = accepted catches found, `2` = LLM connectivity failure
- Temp files live in `config.TempDirectory` (default: `.jittest-temp`) and are cleaned up per-execution in `finally` blocks
