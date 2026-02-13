using Microsoft.Extensions.AI;
using AspireWithDapr.JiTTest.Models;

namespace AspireWithDapr.JiTTest.LLM;

/// <summary>
/// Prompt templates for each JiTTest pipeline stage.
/// Each returns a list of ChatMessages (system + few-shot + user).
/// </summary>
public static class PromptTemplates
{
    public static List<ChatMessage> GetIntentInferencePrompt(ChangeSet changeSet)
    {
        var diffText = FormatDiff(changeSet);

        return
        [
            new(ChatRole.System, """
                You are an expert code reviewer specializing in .NET/C# applications.
                Analyze the following code diff and produce a JSON object describing the developer's intent.

                Respond ONLY with valid JSON matching this schema:
                {
                  "description": "string — what the change does",
                  "behaviorChanges": ["string — specific behavior modifications"],
                  "riskAreas": ["string — what could go wrong"],
                  "affectedMethods": ["string — fully qualified method names affected"]
                }

                Example for a temperature boundary change:
                {
                  "description": "Adjusted the freezing temperature threshold from -5 to -6",
                  "behaviorChanges": ["GetSummaryForTemperature now returns 'Bracing' instead of 'Freezing' for -5°C"],
                  "riskAreas": ["Off-by-one at the Freezing/Bracing boundary"],
                  "affectedMethods": ["WeatherUtilities.GetSummaryForTemperature"]
                }
                """),
            new(ChatRole.User, $"""
                Analyze this code change:

                {diffText}
                """)
        ];
    }

    public static List<ChatMessage> GetMutantGenerationPrompt(IntentSummary intent, ChangeSet changeSet, string accessibilityMap = "")
    {
        var diffText = FormatDiff(changeSet);

        return
        [
            new(ChatRole.System, """
                You are a mutation testing expert. Given a code change and its inferred intent, generate
                realistic faults (mutants) that a developer might accidentally introduce.

                Each mutant must be a PLAUSIBLE mistake — not a random operator flip. Think about:
                - Off-by-one errors at boundaries
                - Wrong comparison operator (< vs <=)
                - Swapped logical operators (&& vs ||)
                - Incorrect string comparison
                - Missing null checks
                - Wrong variable used in similar context

                ═══ TESTABILITY — PRIORITIZATION ═══
                PRIORITIZE mutants that target PUBLICLY ACCESSIBLE code (easier to test):
                - Public properties and their initializers (e.g. `public string City { get; init; } = ""`)
                - Public computed properties (e.g. `public int TemperatureF => 32 + ...`)
                - Public static collections (e.g. `public static readonly List<string> Cities = [...]`)
                - Public static methods and constants

                ALSO GENERATE mutants for private/protected methods when they contain
                important bugs like off-by-one errors, boundary issues, or logic flaws.
                These CAN be tested indirectly:
                - Protected `ExecuteAsync` in BackgroundService → testable via public `StartAsync()`
                - Private methods called from public methods → testable via the public caller
                - Code that reads public collections → testable via the collection contents

                Aim for a MIX: at least 2 public-target mutants + 1-2 private/protected if relevant.

                Respond ONLY with a JSON array of mutants matching this schema:
                [
                  {
                    "id": "M001",
                    "description": "string — what the mutation does",
                    "rationale": "string — why this is a plausible fault",
                    "targetFile": "string — relative file path",
                    "originalCode": "string — exact original code to replace",
                    "mutatedCode": "string — the mutated replacement",
                    "lineStart": number,
                    "lineEnd": number
                  }
                ]

                Generate 3-5 mutants. The originalCode must be an EXACT substring of the source file.

                Example mutant for a temperature utility:
                {
                  "id": "M001",
                  "description": "Changed boundary from '< -5' to '<= -5' in GetSummaryForTemperature",
                  "rationale": "Off-by-one error — temperature -5 would be classified as Freezing instead of Bracing",
                  "targetFile": "AspireWithDapr.Shared/WeatherUtilities.cs",
                  "originalCode": "< -5",
                  "mutatedCode": "<= -5",
                  "lineStart": 20,
                  "lineEnd": 20
                }
                """),
            new(ChatRole.User, $"""
                ## Inferred Intent
                {intent.Description}

                Behavior changes: {string.Join("; ", intent.BehaviorChanges)}
                Risk areas: {string.Join("; ", intent.RiskAreas)}
                Affected methods: {string.Join(", ", intent.AffectedMethods)}
                {(string.IsNullOrEmpty(accessibilityMap) ? "" : $"""

                ## Accessibility Map (PRIORITIZE members marked TESTABLE, but also consider INDIRECT)
                {accessibilityMap}
                """)}
                ## Code Change
                {diffText}
                """)
        ];
    }

    public static List<ChatMessage> GetTestGenerationPrompt(Mutant mutant, string originalFileContent)
    {
        return
        [
            new(ChatRole.System, """
                You are an expert .NET test author. Write an xUnit test that:
                1. PASSES against the original code
                2. FAILS against the mutated code

                The test must be a complete, self-contained C# file with:
                - ALL necessary using directives at the very top
                - A public test class with EXACTLY ONE [Fact] method
                - Clear assertions targeting the exact behavior the mutant breaks

                CRITICAL: Write EXACTLY ONE [Fact] method. Do NOT write multiple test methods.
                Multiple tests cause partial failures that abort the entire run.

                ═══ USING DIRECTIVES — ALWAYS INCLUDE THESE ═══
                Every test file MUST start with the using directives it needs. Common ones:
                  using Xunit;                                        // Always required
                  using NSubstitute;                                   // When mocking interfaces
                  using Microsoft.Extensions.Logging;                  // When ILogger<T> is needed
                  using Microsoft.Extensions.Logging.Abstractions;     // For NullLogger<T>
                  using System;                                        // For Random, DateTime, etc.
                  using System.Collections.Generic;                    // For List<T>, Dictionary etc.
                  using System.Linq;                                   // For LINQ methods
                  using System.Threading;                              // For CancellationToken
                  using System.Threading.Tasks;                        // For Task, async
                Add the namespace of the code under test (e.g. using AspireWithDapr.Shared;)
                If the code uses Dapr: using Dapr.Client;

                ═══ ACCESSIBILITY RULES — MUST FOLLOW ═══
                - You can ONLY call public methods and access public properties/fields
                - 'protected' methods (like BackgroundService.ExecuteAsync) are NOT accessible
                - 'private' methods are NOT accessible — you CANNOT call them even with an instance
                - If a mutant targets code inside a private/protected method, test it INDIRECTLY
                  through a public method that calls it, or test the public data it affects
                - If the mutant affects a static collection (e.g. SharedCollections.Cities),
                  test the collection directly — it IS public
                - NEVER try to call .ExecuteAsync() on a BackgroundService — it's protected

                EXAMPLES OF ERRORS TO AVOID:
                ✗ svc.GetTimeZone("Berlin")         — GetTimeZone is private, will NOT compile
                ✗ svc.ExecuteAsync(token)            — ExecuteAsync is protected, will NOT compile
                ✗ Summary = new[] { ... }            — static readonly field, cannot assign
                ✓ SharedCollections.Cities.Contains(x) — public static property, OK
                ✓ new WeatherForecast { City = "X" }  — public property, OK
                ✓ WeatherUtilities.GetSummary(...)     — public static method, OK

                ═══ COMPILATION RULES ═══
                - Use explicit types, not 'var' for ambiguous types
                - The test class MUST be public and non-static
                - Do NOT use 'async void' — use 'async Task'
                - Close all string literals and interpolations properly
                - Use Assert.Equal(expected, actual, precision) for floating-point
                - A static readonly field CANNOT be assigned to — only read from it

                ═══ MUST PASS ON ORIGINAL — CRITICAL ═══
                Your test MUST pass against the ORIGINAL code exactly as shown below.
                Do NOT assume standard formulas or behaviors — READ the actual source code.
                For computed properties (like TemperatureF), REPLICATE THE EXACT FORMULA
                in your test as the expected value — do NOT try to pre-compute the result.
                
                Example for `public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);`:
                  var forecast = new WeatherForecast { TemperatureC = 25 };
                  int expected = 32 + (int)(25 / 0.5556);  // replicate the formula!
                  Assert.Equal(expected, forecast.TemperatureF);
                
                This way the test always matches the original code and only breaks when
                the formula is mutated. NEVER hardcode a precomputed number like 77 — always
                use the same arithmetic expression from the source code.

                ═══ MOCKING WITH NSUBSTITUTE ═══
                When the class under test takes constructor dependencies:
                  var logger = Substitute.For<ILogger<MyService>>();
                  var daprClient = Substitute.For<DaprClient>();
                  var svc = new MyService(daprClient, logger);
                For ILogger, you can also use:
                  var logger = NullLogger<MyService>.Instance;
                NSubstitute patterns:
                  mock.SomeMethod(Arg.Any<string>()).Returns("value");
                  mock.Received().SomeMethod(Arg.Is<string>(s => s.Contains("x")));
                NEVER use Moq (Mock<T>, It.Is, It.IsAny) — only NSubstitute (Substitute.For, Arg.Any, Arg.Is)

                ═══ STRATEGY — CHOOSE THE SIMPLEST APPROACH ═══
                1. If mutant targets public static methods/properties → test directly
                2. If mutant targets public static collections → assert collection contents
                3. If mutant targets public record/class property defaults → instantiate and assert
                4. If mutant targets code in a DI class → mock dependencies with NSubstitute
                5. If mutant targets protected/private code → DO NOT call the private method!
                   Instead: test indirectly through the class's public API.
                   IMPORTANT EXAMPLES:
                   - BackgroundService has protected ExecuteAsync, but public StartAsync().
                     Use a short CancellationToken to run one iteration:
                       var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                       await svc.StartAsync(cts.Token);
                       // ... then assert on observable side effects or exceptions
                   - If private method reads from a public collection, test the collection.
                   - If private method sets a public property, instantiate and check the property.
                   - To catch index-out-of-range in a loop, run the service briefly and
                     check if it throws via Assert.ThrowsAsync or try/catch.

                Respond ONLY with the complete C# test file. No markdown, no explanation.
                """),
            new(ChatRole.User, $"""
                ## Mutant to catch
                ID: {mutant.Id}
                Description: {mutant.Description}
                File: {mutant.TargetFile}
                Original code: {mutant.OriginalCode}
                Mutated code: {mutant.MutatedCode}
                {(string.IsNullOrEmpty(mutant.AccessibilityHint) ? "" : $"\n## ACCESSIBILITY WARNING\n{mutant.AccessibilityHint}\n")}
                ## Original file content
                {originalFileContent}

                Write an xUnit test that passes against the original code above but fails when
                "{mutant.OriginalCode}" is replaced with "{mutant.MutatedCode}".
                """)
        ];
    }

    public static List<ChatMessage> GetCompilationFixPrompt(string testCode, string[] errors)
    {
        var errorList = string.Join("\n", errors.Select(e => $"  - {e}"));

        return
        [
            new(ChatRole.System, """
                You are a C# compilation error fixer. The following test code has compilation errors.
                Fix the errors and return the complete corrected C# file.
                
                Available frameworks: xUnit 2.9, NSubstitute 5.3, Microsoft.Extensions.Logging.Abstractions.
                Use NSubstitute (Substitute.For<T>()) for interface mocking.
                Use NullLogger<T>.Instance for ILogger dependencies.
                Do NOT use Moq or any other mocking library.

                COMMON FIXES:
                - CS1061 "does not contain a definition for 'X'" → The method is probably private.
                  REMOVE the call entirely and rewrite the test to use only public API.
                  Do NOT just rename the method — it does not exist publicly.
                - CS0122 "inaccessible due to its protection level" → The method is protected/private.
                  REMOVE the call entirely and test via observable public outcomes.
                - CS0198 "static readonly field cannot be assigned" → Remove the assignment.
                  Read from the field instead of writing to it.
                - CS1929 "does not contain a definition for 'Contains'" with MemoryExtensions →
                  Use .ToList().Contains() or .AsEnumerable().Contains() instead.
                - CS0117 "does not contain a definition for 'X'" → The field/property is private.
                  Do NOT access private static fields.
                
                Respond ONLY with the corrected C# code. No markdown fences, no explanation.
                """),
            new(ChatRole.User, $"""
                ## Test code with errors
                {testCode}

                ## Compilation errors
                {errorList}

                Fix all compilation errors and return the complete corrected file.
                """)
        ];
    }

    public static List<ChatMessage> GetAssessmentPrompt(Mutant mutant, string testCode, string changeContext)
    {
        return
        [
            new(ChatRole.System, """
                You are a software testing assessor. A mutation-catching test has already been
                EXECUTED and VERIFIED:
                - It PASSES against the original code
                - It FAILS against the mutated code
                This execution evidence is confirmed — do not dispute it.

                Your job is ONLY to assess whether this represents a meaningful regression test:
                - Does the mutant simulate a plausible developer mistake?
                - Does the test catch a behavioral change that matters (not purely cosmetic)?

                Answer with ONLY a JSON object:
                {
                  "isTruePositive": true/false,
                  "confidence": "HIGH" | "MEDIUM" | "LOW",
                  "reasoning": "string — brief explanation"
                }

                ACCEPT (isTruePositive: true) when:
                - The mutant represents a plausible developer mistake (off-by-one, wrong operator, etc.)
                - The behavior change could affect end users or downstream systems
                - The test exercises the specific code path the mutant alters

                REJECT (isTruePositive: false) ONLY when:
                - The mutant is semantically equivalent to the original (dead code, no observable difference)
                - The fault is purely in comments, logging format strings, or whitespace
                - The test is vacuous (no meaningful assertions)

                When in doubt, ACCEPT. A test that catches a real code change is valuable.
                """),
            new(ChatRole.User, $"""
                ## Mutant
                Description: {mutant.Description}
                Rationale: {mutant.Rationale}
                Original code: `{mutant.OriginalCode}`
                Mutated code:  `{mutant.MutatedCode}`

                ## Execution Evidence (confirmed)
                - Test PASSES on original code ✅
                - Test FAILS on mutated code ❌
                This means the test definitively catches this mutation.

                ## Generated Test
                {testCode}

                ## Source File Context
                {changeContext}

                Is this a true positive regression catch?
                """)
        ];
    }

    private static string FormatDiff(ChangeSet changeSet)
    {
        var parts = new List<string>();
        foreach (var file in changeSet.Files)
        {
            parts.Add($"### File: {file.FilePath}");
            foreach (var hunk in file.Hunks)
            {
                parts.Add($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@");
                if (!string.IsNullOrEmpty(hunk.BeforeContent))
                    parts.Add($"--- (before)\n{hunk.BeforeContent}");
                if (!string.IsNullOrEmpty(hunk.AfterContent))
                    parts.Add($"+++ (after)\n{hunk.AfterContent}");
                if (!string.IsNullOrEmpty(hunk.Context))
                    parts.Add($"Context:\n{hunk.Context}");
            }
            if (!string.IsNullOrEmpty(file.FullFileContent))
            {
                parts.Add($"\n### Full file content ({file.FilePath}):");
                parts.Add(file.FullFileContent);
            }
        }
        return string.Join("\n\n", parts);
    }
}
