# JiTTest — Implementation Tasks

## Prerequisites

- [ ] **P-1**: Install Ollama — `winget install Ollama.Ollama`
- [ ] **P-2**: Pull recommended model — `ollama pull qwen2.5-coder:32b-instruct-q4_K_M`
- [ ] **P-3**: Verify Ollama serves at `http://localhost:11434/v1/chat/completions`

---

## Phase 1: Project Scaffolding

### Task 1.1: Create JiTTest console project

- Create `AspireWithDapr.JiTTest/AspireWithDapr.JiTTest.csproj` targeting `net10.0`
- Add NuGet packages:
  - `Microsoft.Extensions.AI.OpenAI`
  - `LibGit2Sharp`
  - `Microsoft.CodeAnalysis.CSharp`
  - `Microsoft.CodeAnalysis.Common`
  - `xunit` (for test compilation references)
  - `xunit.assert`
  - `xunit.runner.utility`
  - `Microsoft.Extensions.FileGlobbing`
  - `System.CommandLine` (CLI parsing)
- Add project references:
  - `AspireWithDapr.Shared`
  - `AspireWithDapr.ApiService`
- **Acceptance**: `dotnet build AspireWithDapr.JiTTest` succeeds

### Task 1.2: Register in solution

- Add the project to `AspireWithDapr.slnx`
- **Acceptance**: `dotnet build AspireWithDapr.slnx` succeeds

### Task 1.3: Create folder structure

- Create directories: `Configuration/`, `Pipeline/`, `Models/`, `LLM/`, `Compilation/`, `Reporting/`
- Create placeholder files with namespace declarations
- **Acceptance**: Project compiles with empty class stubs

### Task 1.4: Configuration system

- Implement `JiTTestConfig.cs` — POCO matching `jittest-config.json` schema
- Create default `jittest-config.json` at project root
- Implement config loading: JSON file → CLI overrides → defaults
- **Acceptance**: Config loads from file, CLI args override values

### Task 1.5: CLI entry point

- Implement `Program.cs` with `System.CommandLine` parsing
- Options: `--diff`, `--config`, `--model`, `--endpoint`, `--report`, `--verbose`, `--dry-run`
- Wire up `PipelineOrchestrator` invocation
- **Acceptance**: `dotnet run -- --help` shows all options

**Estimated effort**: 3–4 hours

---

## Phase 2: LLM Integration

### Task 2.1: Ollama client factory

- Implement `OllamaClientFactory.cs`
- Create `OpenAIClient` with Ollama endpoint, wrap as `IChatClient`
- Include health check: verify model is available before pipeline starts
- **Acceptance**: Can send a test prompt via `IChatClient` and receive a response

### Task 2.2: Prompt templates

- Implement `PromptTemplates.cs` with static methods for each pipeline stage:
  - `GetIntentInferencePrompt(ChangeSet changeSet) → ChatMessage[]`
  - `GetMutantGenerationPrompt(IntentSummary intent, ChangeSet changeSet) → ChatMessage[]`
  - `GetTestGenerationPrompt(Mutant mutant, string originalCode) → ChatMessage[]`
  - `GetCompilationFixPrompt(string testCode, string[] errors) → ChatMessage[]`
  - `GetAssessmentPrompt(Mutant mutant, string testCode, string changeContext) → ChatMessage[]`
- Include few-shot examples for each prompt (2 examples minimum)
- All prompts request structured JSON output with explicit schema
- **Acceptance**: Each prompt template produces valid `ChatMessage[]` with system + user messages

### Task 2.3: LLM response parsing

- Implement JSON extraction from LLM responses (handle markdown code fences, preamble text)
- Implement retry logic for unparseable responses (re-prompt with stricter instructions)
- **Acceptance**: Can parse intent, mutant array, and test code from model responses

**Estimated effort**: 4–5 hours

---

## Phase 3: Diff Extraction

### Task 3.1: Diff extractor

- Implement `DiffExtractor.cs` using LibGit2Sharp
- Support diff sources: staged, unstaged, branch comparison, `HEAD~N`
- Parse into `ChangeSet` model with full hunk data + ±20 lines context
- **Acceptance**: Running against a staged change produces correct `ChangeSet`

### Task 3.2: File filtering

- Apply glob patterns from `mutate-targets` and `exclude` config using `Microsoft.Extensions.FileGlobbing`
- Only process files matching include patterns that don't match exclude patterns
- **Acceptance**: Changing `Program.cs` produces empty filtered result; changing `WeatherUtilities.cs` is included

**Estimated effort**: 2–3 hours

---

## Phase 4: Core Pipeline Stages

### Task 4.1: Intent inferrer

- Implement `IntentInferrer.cs`
- Send `ChangeSet` through intent prompt template to `IChatClient`
- Parse response into `IntentSummary` model
- **Acceptance**: Given a diff of `WeatherUtilities.cs`, produces a sensible intent summary

### Task 4.2: Mutant generator

- Implement `MutantGenerator.cs`
- Send `IntentSummary` + `ChangeSet` through mutant prompt to `IChatClient`
- Parse response into `Mutant[]` (3–5 mutants)
- Validate each mutant: file exists, line numbers valid, original code matches
- **Acceptance**: Generates mutants with valid patches for a known change

### Task 4.3: Roslyn compiler

- Implement `RoslynCompiler.cs`
- Compile test code in-memory with `CSharpCompilation.Create()`
- Load assembly references from project build outputs + xUnit packages
- Return compilation success/failure + diagnostics
- **Acceptance**: A valid xUnit test compiles successfully; an invalid one returns clear errors

### Task 4.4: Test generator

- Implement `TestGenerator.cs`
- Send `Mutant` + original code through test prompt to `IChatClient`
- Compile result with `RoslynCompiler`
- On failure: feed errors back to LLM via compilation fix prompt, retry up to `max-retries`
- **Acceptance**: Generates compilable xUnit tests for known mutants with ≥70% first-attempt compilation rate

### Task 4.5: Test executor

- Implement `TestExecutor.cs`
- Create a transient test project in `temp/` directory with necessary references
- Write generated test to temp file, run `dotnet test`
- Apply mutant patch (file string replacement), rebuild + retest
- Revert patch, clean up temp files
- Determine candidate catch status
- **Acceptance**: A well-crafted test passes on original code and fails on mutated code

### Task 4.6: Pipeline orchestrator

- Implement `PipelineOrchestrator.cs` — sequential execution of all stages
- Handle early exits (no diff, no matching files, dry-run mode)
- Collect results from each stage, pass to next
- Error isolation: failure in one mutant/test doesn't stop the pipeline
- **Acceptance**: Full pipeline runs end-to-end with console output at each stage

**Estimated effort**: 10–12 hours

---

## Phase 5: Assessment & Reporting

### Task 5.1: Rule-based assessor

- Implement rule-based checks in `Assessor.cs`:
  - Reject: test only asserts null/not-null
  - Reject: test only checks string constant equality
  - Reject: mutant targets comments, attributes, or `using` statements
  - Reject: mutant targets excluded files (Program.cs, config)
- **Acceptance**: Known trivial catches are rejected

### Task 5.2: LLM-based assessor

- Implement LLM assessment in `Assessor.cs`
- Send mutant + test + change context through assessment prompt
- Parse binary YES/NO response + confidence mapping
- Filter below configured threshold
- **Acceptance**: Realistic mutants assessed as YES/HIGH; trivial ones as NO/LOW

### Task 5.3: Console reporter

- Implement `ConsoleReporter.cs` with colored terminal output
- Show: file, each catch (mutant description, test snippet, confidence), summary
- Exit code: 0 if no catches, 1 if catches found
- **Acceptance**: Output matches design spec format

### Task 5.4: Markdown reporter

- Implement `MarkdownReporter.cs` — write `jittest-report.md`
- Same content as console but formatted as markdown with code blocks
- **Acceptance**: Generated markdown renders correctly

**Estimated effort**: 4–5 hours

---

## Phase 6: Integration & Polish

### Task 6.1: End-to-end dry run

- Test the full pipeline against a real change in `WeatherUtilities.cs`
- Verify: diff extracted → intent inferred → mutants generated → tests compiled → tests executed → catches assessed → report produced
- Fix any issues discovered
- **Acceptance**: Pipeline produces meaningful catches for a boundary change

### Task 6.2: No-op change validation

- Run against whitespace-only, comment-only, and formatting-only changes
- Verify zero false catches
- **Acceptance**: No false positives for non-behavioral changes

### Task 6.3: Error resilience testing

- Test with Ollama stopped → clear error message
- Test with wrong model name → clear error message
- Test with no git changes → clean exit
- Test with malformed LLM output → graceful retry/skip
- **Acceptance**: All error scenarios produce helpful messages, no crashes

### Task 6.4: Git pre-commit hook (optional)

- Create `.githooks/pre-commit` script that runs `dotnet run --project AspireWithDapr.JiTTest -- --diff staged`
- Document setup: `git config core.hooksPath .githooks`
- **Acceptance**: Committing a change to target files triggers JiTTest automatically

### Task 6.5: GitHub Actions workflow (optional)

- Create `.github/workflows/jittest.yml`
- Trigger on PR, run against `--diff branch:main`
- Post results as PR comment using `gh` CLI
- Requires Ollama setup in CI (or fallback to cloud model endpoint)
- **Acceptance**: PR triggers JiTTest, results appear as comment

**Estimated effort**: 4–6 hours

---

## Phase Summary

| Phase | Description | Estimated Effort |
|-------|-------------|-----------------|
| Prerequisites | Ollama + model setup | 30 min |
| Phase 1 | Project scaffolding | 3–4 hours |
| Phase 2 | LLM integration | 4–5 hours |
| Phase 3 | Diff extraction | 2–3 hours |
| Phase 4 | Core pipeline | 10–12 hours |
| Phase 5 | Assessment & reporting | 4–5 hours |
| Phase 6 | Integration & polish | 4–6 hours |
| **Total** | | **~28–35 hours** |

## Task Dependencies

```
P-1, P-2, P-3 ──▶ Task 2.1

Task 1.1 ──▶ Task 1.2 ──▶ Task 1.3 ──▶ Task 1.4 ──▶ Task 1.5

Task 1.3 ──▶ Task 2.1 ──▶ Task 2.2 ──▶ Task 2.3

Task 1.3 ──▶ Task 3.1 ──▶ Task 3.2

Task 2.3 + Task 3.2 ──▶ Task 4.1 ──▶ Task 4.2 ──▶ Task 4.4

Task 1.3 ──▶ Task 4.3 ──▶ Task 4.4 ──▶ Task 4.5

Task 4.5 + Task 4.6 ──▶ Task 5.1 ──▶ Task 5.2 ──▶ Task 5.3 ──▶ Task 5.4

Task 5.4 ──▶ Task 6.1 ──▶ Task 6.2 ──▶ Task 6.3

Task 6.3 ──▶ Task 6.4 (optional)
Task 6.3 ──▶ Task 6.5 (optional)
```

**Parallelizable**: Phase 2 (LLM) and Phase 3 (Diff) can be developed in parallel after Phase 1 scaffolding is complete.
