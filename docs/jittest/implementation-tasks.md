# JiTTest — Implementation Tasks

## Prerequisites

- [x] **P-1**: Install Ollama — `winget install Ollama.Ollama`
- [x] **P-2**: Pull recommended model — `ollama pull qwen2.5-coder:32b-instruct-q4_K_M`
- [x] **P-3**: Verify Ollama serves at `http://localhost:11434/v1/chat/completions`

---

## Phase 1: Project Scaffolding

### Task 1.1: Create JiTTest console project

- [x] Create `JiTTest/JiTTest.csproj` targeting `net10.0`
- [x] Add NuGet packages:
  - `Microsoft.Extensions.AI.OpenAI`
  - `LibGit2Sharp`
  - `Microsoft.CodeAnalysis.CSharp`
  - `Microsoft.CodeAnalysis.Common`
  - `xunit` (for test compilation references)
  - `xunit.assert`
  - `xunit.runner.utility`
  - `Microsoft.Extensions.FileGlobbing`
  - `System.CommandLine` (CLI parsing)
- **Acceptance**: `dotnet build JiTTest` succeeds ✅

### Task 1.2: Register in solution

- [x] Add the project to `JitTest.slnx`
- **Acceptance**: `dotnet build JitTest.slnx` succeeds ✅

### Task 1.3: Create folder structure

- [x] Create directories: `Configuration/`, `Pipeline/`, `Models/`, `LLM/`, `Compilation/`, `Reporting/`
- [x] Create placeholder files with namespace `JiTTest.*` declarations
- **Acceptance**: Project compiles with empty class stubs ✅

### Task 1.4: Configuration system

- [x] Implement `JiTTestConfig.cs` — POCO matching `jittest-config.json` schema
- [x] All properties use `default!` for reference types (no hardcoded defaults)
- [x] Configuration values must be provided via JSON file or CLI overrides
- [x] Create default `jittest-config.json` at project root with all required settings
- [x] Implement config loading: JSON file → CLI overrides → validation
- **Acceptance**: Config loads from file, CLI args override values, missing required values produce clear errors ✅

### Task 1.5: CLI entry point

- [x] Implement `Program.cs` with `System.CommandLine` parsing
- [x] Options: `--diff`, `--config`, `--model`, `--endpoint`, `--report`, `--verbose`, `--dry-run`
- [x] Wire up `PipelineOrchestrator` invocation
- **Acceptance**: `dotnet run -- --help` shows all options ✅

**Estimated effort**: 3–4 hours

---

## Phase 2: LLM Integration

### Task 2.1: LLM client factory

- [x] Implement `LlmClientFactory.cs`
- [x] Create `OpenAIClient` with configured endpoint (Ollama or GitHub Models), wrap as `IChatClient`
- [x] Support authentication for GitHub Models via token
- [x] Include health check: verify model is available before pipeline starts
- **Acceptance**: Can send a test prompt via `IChatClient` and receive a response ✅

### Task 2.2: Prompt templates

- [x] Implement `PromptTemplates.cs` with static methods for each pipeline stage:
  - `GetIntentInferencePrompt(ChangeSet changeSet) → ChatMessage[]`
  - `GetMutantGenerationPrompt(IntentSummary intent, ChangeSet changeSet) → ChatMessage[]`
  - `GetTestGenerationPrompt(Mutant mutant, string originalCode) → ChatMessage[]`
  - `GetCompilationFixPrompt(string testCode, string[] errors, Mutant? mutant) → ChatMessage[]`
  - `GetAssessmentPrompt(Mutant mutant, string testCode, string changeContext) → ChatMessage[]`
- [x] Include few-shot examples for each prompt (2 examples minimum)
- [x] All prompts request structured JSON output with explicit schema
- [x] Use generic project references (not AspireWithDapr-specific)
- **Acceptance**: Each prompt template produces valid `ChatMessage[]` with system + user messages ✅

### Task 2.3: LLM response parsing

- [x] Implement JSON extraction from LLM responses (handle markdown code fences, preamble text)
- [x] Implement retry logic for unparseable responses (re-prompt with stricter instructions)
- **Acceptance**: Can parse intent, mutant array, and test code from model responses ✅

---

## Phase 3: Diff Extraction

### Task 3.1: Diff extractor

- [x] Implement `DiffExtractor.cs` using LibGit2Sharp
- [x] Support diff sources: staged, unstaged, branch comparison, `HEAD~N`
- [x] Parse into `ChangeSet` model with full hunk data + ±20 lines context
- **Acceptance**: Running against a staged change produces correct `ChangeSet` ✅

### Task 3.2: File filtering

- [x] Apply glob patterns from `mutate-targets` and `exclude` config using `Microsoft.Extensions.FileGlobbing`
- [x] Only process files matching include patterns that don't match exclude patterns
- **Acceptance**: Changing `Program.cs` produces empty filtered result; changing target source files are included ✅

---

## Phase 4: Core Pipeline Stages

### Task 4.1: Intent inferrer

- [x] Implement `IntentInferrer.cs` (namespace: `JiTTest.Pipeline`)
- [x] Send `ChangeSet` through intent prompt template to `IChatClient`
- [x] Parse response into `IntentSummary` model
- **Acceptance**: Given a diff of a source file, produces a sensible intent summary ✅

### Task 4.2: Mutant generator

- [x] Implement `MutantGenerator.cs`
- [x] Send `IntentSummary` + `ChangeSet` through mutant prompt to `IChatClient`
- [x] Parse response into `Mutant[]` (3–5 mutants)
- [x] Validate each mutant: file exists, line numbers valid, original code matches
- [x] Annotate accessibility (public/protected/private) for fields, properties, and methods
- [x] `ExtractVerbatimMatch` aligns LLM-formatted `OriginalCode` to file's actual text
- **Acceptance**: Generates mutants with valid patches for a known change ✅

### Task 4.3: Roslyn compiler

- [x] Implement `RoslynCompiler.cs` (namespace: `JiTTest.Compilation`)
- [x] Compile test code in-memory with `CSharpCompilation.Create()`
- [x] Load assembly references from project build outputs + xUnit packages
- [x] Discover SDK implicit usings + project-specific `GlobalUsings.g.cs`
- [x] `AutoFixUsings` handles CS0246, CS1674, CS0122, CS1929, and more
- [x] Return compilation success/failure + diagnostics
- **Acceptance**: A valid xUnit test compiles successfully; an invalid one returns clear errors ✅

### Task 4.4: Test generator

- [x] Implement `TestGenerator.cs`
- [x] Send `Mutant` + original code through test prompt to `IChatClient`
- [x] Compile result with `RoslynCompiler`
- [x] On failure: feed errors + mutant context back to LLM, retry up to `max-retries`
- [x] Final retry escalates to full regeneration with prior error context
- [x] `Assert.True(true)` placeholders explicitly banned in fix prompt
- **Acceptance**: Generates compilable xUnit tests for known mutants with ≥70% first-attempt compilation rate ✅

### Task 4.5: Test executor

- [x] Implement `TestExecutor.cs`
- [x] Create a transient test project in `temp/` directory with necessary references
- [x] **Shadow-copy isolation**: Copy the target project into an isolated temp directory per execution (skip `bin/`, `obj/`, `.git/`, `.vs/`, `node_modules/`)
- [x] Write generated test to temp file, run `dotnet test` against the shadow copy
- [x] Apply mutant patch to the shadow copy (never mutate real source files), rebuild + retest
- [x] Clean up entire temp directory after execution
- [x] Determine candidate catch status
- **Acceptance**: A well-crafted test passes on original code and fails on mutated code; original source files remain untouched ✅

### Task 4.6: Pipeline orchestrator

- [x] Implement `PipelineOrchestrator.cs` with configurable parallelism
- [x] Handle early exits (no diff, no matching files, dry-run mode)
- [x] **Parallel stages 4–6**: Use `Parallel.ForEachAsync` with `MaxDegreeOfParallelism` = `config.MaxParallel` for test generation, test execution, and assessment
- [x] Collect results via `ConcurrentBag<T>` for thread-safe aggregation
- [x] Per-stage `Stopwatch` timing with summary printed at end
- [x] Error isolation: failure in one mutant/test doesn't stop the pipeline
- **Acceptance**: Full pipeline runs end-to-end; `max-parallel: 1` behaves identically to sequential; `max-parallel: 3` shows ~3x speedup on stages 4–6 ✅

---

## Phase 5: Assessment & Reporting

### Task 5.1: Rule-based assessor

- [x] Implement rule-based checks in `Assessor.cs`:
  - Reject: test only asserts null/not-null
  - Reject: test only checks string constant equality
  - Reject: mutant targets comments, attributes, or `using` statements
  - Reject: mutant targets excluded files (Program.cs, config)
- **Acceptance**: Known trivial catches are rejected ✅

### Task 5.2: LLM-based assessor

- [x] Implement LLM assessment in `Assessor.cs`
- [x] Send mutant + test + change context through assessment prompt
- [x] Parse binary YES/NO response + confidence mapping
- [x] Filter below configured threshold
- **Acceptance**: Realistic mutants assessed as YES/HIGH; trivial ones as NO/LOW ✅

### Task 5.3: Console reporter

- [x] Implement `ConsoleReporter.cs` with colored terminal output
- [x] Show: file, each catch (mutant description, test snippet, confidence), summary
- [x] Exit code: 0 if no catches, 1 if catches found
- **Acceptance**: Output matches design spec format ✅

### Task 5.4: Markdown reporter

- [x] Implement `MarkdownReporter.cs` — write `jittest-report.md`
- [x] Same content as console but formatted as markdown with code blocks
- **Acceptance**: Generated markdown renders correctly ✅

---

## Phase 6: Integration & Polish

### Task 6.1: End-to-end dry run

- [x] Test the full pipeline against a real change in target source files
- [x] Verify: diff extracted → intent inferred → mutants generated → tests compiled → tests executed → catches assessed → report produced
- [x] Fix any issues discovered
- **Acceptance**: Pipeline produces meaningful catches for a boundary change ✅

### Task 6.2: No-op change validation

- [x] Run against whitespace-only, comment-only, and formatting-only changes
- [x] Verify zero false catches
- **Acceptance**: No false positives for non-behavioral changes ✅

### Task 6.3: Error resilience testing

- [x] Test with Ollama stopped → clear error message
- [x] Test with wrong model name → clear error message
- [x] Test with no git changes → clean exit
- [x] Test with malformed LLM output → graceful retry/skip
- **Acceptance**: All error scenarios produce helpful messages, no crashes ✅

### Task 6.4: Git pre-commit hook (optional)

- Create `.githooks/pre-commit` script that runs `dotnet run --project JiTTest -- --diff staged`
- Document setup: `git config core.hooksPath .githooks`
- **Acceptance**: Committing a change to target files triggers JiTTest automatically

### Task 6.5: GitHub Actions workflow (optional)

- [x] Create `.github/workflows/ci.yml`
- Trigger on PR, run against `--diff branch:main`
- Post results as PR comment using `gh` CLI
- Requires Ollama setup in CI (or fallback to cloud model endpoint)
- **Acceptance**: PR triggers JiTTest, results appear as comment

---

## Phase 7: Performance Optimization & Refactoring (Completed)

### Task 7.0: Namespace refactoring

- [x] Made code generic and reusable for any .NET project
- **Acceptance**: All code uses `JiTTest.*` namespaces consistently, builds successfully

### Task 7.1: Configurable parallelism

- [x] Add `max-parallel` config property (default: 3) to `JiTTestConfig.cs` and `jittest-config.json`
- [x] Controls `MaxDegreeOfParallelism` for `Parallel.ForEachAsync` in stages 4–6
- **Acceptance**: `max-parallel: 1` = sequential; `max-parallel: 3` = 3x speedup on LLM-heavy stages

### Task 7.2: Parallel test generation (Stage 4)

- [x] Replace sequential `foreach` with `Parallel.ForEachAsync` bounded by `MaxParallel`
- [x] Thread-safe `ConcurrentBag<GeneratedTest>` collects results
- [x] `RoslynCompiler` is thread-safe (immutable `_references` list, new `CSharpCompilation` per call)
- **Acceptance**: Multiple mutant test generations run concurrently

### Task 7.3: Shadow-copy test executor (Stage 5)

- [x] Replace destructive in-place source mutation with shadow-copy isolation
- [x] Each execution creates `{tempDir}/{guid}/shadow/` with a fast recursive copy (skips bin/obj/.git/.vs/node_modules)
- [x] Mutations applied only to shadow copies; real source files are never modified
- [x] Parallel execution via `Parallel.ForEachAsync` — no file locks needed
- **Acceptance**: `git status` shows no modified files during parallel execution

### Task 7.4: Parallel assessment (Stage 6)

- [x] Replace sequential `foreach` with `Parallel.ForEachAsync` bounded by `MaxParallel`
- [x] Thread-safe `ConcurrentBag<AssessedCatch>` collects results
- **Acceptance**: Multiple LLM assessment calls run concurrently

### Task 7.5: Reduce LLM round-trips

- [x] Enhanced test generation system prompt with 10 explicit compilation rules
- [x] Lists available namespaces so LLM knows what's importable
- [x] Reduces first-attempt compilation failures from ~25% to ~10%
- **Acceptance**: Fewer retry cycles observed in verbose output

### Task 7.6: Optimize Roslyn reference loading

- [x] Deduplicate assembly references by filename using `HashSet<string>`
- [x] Skip `.resources.dll` satellite assemblies
- [x] Reduces memory usage and avoids ambiguous-reference compilation errors
- **Acceptance**: `RoslynCompiler` loads fewer DLLs without losing compilation capability

### Task 7.7: Stage timing telemetry

- [x] Per-stage `Stopwatch` tracks wall-clock time
- [x] `PrintTimings()` displays table with elapsed time, percentage, and parallelism level
- **Acceptance**: Timing summary printed after every pipeline run

---

## Phase 8: Code Generation Quality (v1.6.6, Completed)

### Task 8.1: Fix accessibility annotation for fields and properties

- [x] `AnnotateAccessibility()` previously only checked method declarations; now handles `FieldDeclarationSyntax` and `PropertyDeclarationSyntax` correctly
- [x] `ContainingMemberIsPublic/Protected/Private` flags set accurately for all member kinds
- **Acceptance**: Private fields no longer pass through as "public"; accessibility-based filtering works correctly

### Task 8.2: Improve AccessibilityHint wording

- [x] `AccessibilityHint` property in `Mutant.cs` says "member" instead of "method" (correct for fields/properties)
- [x] PRIVATE hint now reads: "Do NOT reference this field or method by name from the test — test its behaviour indirectly via the public API"
- **Acceptance**: Test generator prompt accurately reflects the mutated member's visibility

### Task 8.3: Per-line NormalizeWhitespace

- [x] `NormalizeWhitespace()` now normalizes each line independently before joining
- [x] Prevents cross-line false positives such as `< -5` (two tokens across line boundary) vs `<-5` (single token)
- **Acceptance**: Whitespace-only or indentation-only diffs do not generate false-positive mutant matches

### Task 8.4: Inject mutant context into compilation fix prompt

- [x] `GetCompilationFixPrompt()` accepts optional `Mutant? mutant` parameter
- [x] When provided, appends the mutant's `AccessibilityHint` to the fix system prompt so the LLM avoids re-introducing the same accessibility error
- [x] `TestGenerator` passes the mutant to every fix-prompt call
- **Acceptance**: Compilation retry success rate improves; fewer repeated CS0122/CS0122 errors on retry

### Task 8.5: Final retry escalation + Assert.True(true) ban

- [x] On the last retry, `TestGenerator` escalates to a **full regeneration** prompt (re-sends `GetTestGenerationPrompt`) rather than patching the same broken code
- [x] Escalation hint includes a concise summary of prior compilation errors to guide the rewrite
- [x] `GetCompilationFixPrompt` explicitly bans `Assert.True(true)` placeholders
- **Acceptance**: Final retry generates fresh test instead of repeatedly patching; vacuous placeholder tests no longer appear

---

## Phase 9: Pipeline Quality Improvements (v1.6.7, Completed)

### Task 9.1: Semantic equivalence detection

- [x] Added `NormalizeNumericLiterals(string code)` to `MutantGenerator.cs`:
  - Removes digit separators (`1_000` → `1000`)
  - Strips type suffixes (`0m`, `1L`, `2.5f`, `3UL` → bare literals)
  - Removes redundant `.0` (`1.0` → `1`, `0.0` → `0`)
- [x] Added `IsSemanticallEquivalent(string original, string mutated)` — normalizes both sides and compares
- [x] `ValidateMutant()` rejects mutants that are semantically equivalent early (before LLM test generation)
- [x] `GetValidationFailureReason()` returns a specific message for semantic equivalence rejections
- **Acceptance**: Mutants like `0` → `0.0m` or `1_000` → `1000` are silently discarded before stage 4; verbose output shows informative rejection reason

### Task 9.2: Fails-on-original recovery prompt

- [x] Added `GetTestRegenFromOriginalFailurePrompt(Mutant, originalFileContent, priorTestCode, failureOutput, sourceUsings, globalUsings)` to `PromptTemplates.cs`
- [x] System prompt explains the test **compiles but fails on original**; instructs the LLM to correct assertion logic using the failure output as ground truth
- [x] Passes full source file, prior test code, and xUnit failure output so the LLM can reason about actual runtime behaviour
- **Acceptance**: Prompt produces a corrected test that passes on original and fails on the mutation

### Task 9.3: TestGenerator.RegenerateAfterOriginalFailureAsync

- [x] New public method on `TestGenerator` accepts the failed `GeneratedTest`, original file content, and xUnit failure output
- [x] Calls `GetTestRegenFromOriginalFailurePrompt`, extracts code, compiles, auto-fixes usings, and applies one fix-retry if still broken
- [x] Verbose logging uses `↩` prefix to distinguish recovery regeneration from normal generation
- **Acceptance**: Method returns a new `GeneratedTest`; if it compiles, it is re-executed by the orchestrator

### Task 9.4: Recovery pass in PipelineOrchestrator (Stage 5b)

- [x] Stage 5 parallel loop now also collects results where `!PassesOnOriginal` into `failedOnOriginal` bag
- [x] After the main parallel execution loop, a **sequential recovery pass** iterates `failedOnOriginal`:
  - Looks up the source file content from `changeSet`
  - Calls `testGenerator.RegenerateAfterOriginalFailureAsync()`
  - Re-executes the recovered test via `testExecutor.ExecuteAsync()`
  - Adds to `candidateCatches` if it becomes a catch
- [x] Verbose output reports per-test recovery outcome (`passes original`, `fails mutant`)
- **Acceptance**: Tests that previously failed on original are given one recovery attempt; successful recoveries flow through stage 6 assessment

### Task 9.5: BankingSample demonstration project

- [x] Created `samples/BankingSample/` class library (`BankAccount.cs`, `LoanCalculator.cs`, `Transaction.cs`)
- [x] Created `samples/BankingSample.Tests/` xUnit project with 28 deliberately incomplete tests (boundary gaps left for JiTTest to find)
- [x] Both projects added to `JitTest.slnx`
- [x] Created `jittest-config.sample.json` scoped to the sample project's source files only
- **Acceptance**: `jittest --config jittest-config.sample.json --diff-source uncommitted` finds catches in the sample project; `dotnet test` on the tests project passes cleanly
