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

    public static List<ChatMessage> GetMutantGenerationPrompt(IntentSummary intent, ChangeSet changeSet)
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
                - using Xunit;
                - using AspireWithDapr.Shared; (and other needed usings)
                - A public test class with [Fact] methods
                - Clear, specific assertions that target the exact behavior the mutant breaks

                Respond ONLY with the complete C# test file code. No markdown fences, no explanation.

                Example test for a temperature boundary mutant:
                using Xunit;
                using AspireWithDapr.Shared;

                public class WeatherUtilitiesBoundaryTests
                {
                    [Fact]
                    public void GetSummaryForTemperature_AtNegativeFive_ReturnsBracing()
                    {
                        var result = WeatherUtilities.GetSummaryForTemperature(-5);
                        Assert.Equal("Bracing", result);
                    }
                }
                """),
            new(ChatRole.User, $"""
                ## Mutant to catch
                ID: {mutant.Id}
                Description: {mutant.Description}
                File: {mutant.TargetFile}
                Original code: {mutant.OriginalCode}
                Mutated code: {mutant.MutatedCode}

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
                You are a software testing assessor. Evaluate whether a mutation-catching test
                represents a TRUE POSITIVE — meaning the mutant simulates a real bug that a
                developer could plausibly introduce.

                Answer with ONLY a JSON object:
                {
                  "isTrue Positive": true/false,
                  "confidence": "HIGH" | "MEDIUM" | "LOW",
                  "reasoning": "string — brief explanation"
                }

                TRUE POSITIVE criteria:
                - The mutant represents a plausible developer mistake
                - The test catches meaningful behavioral change (not cosmetic)
                - The fault could lead to a real user-visible bug

                FALSE POSITIVE criteria:
                - The mutant is semantically equivalent to the original
                - The test only checks trivial/constant values
                - The fault is in non-production code (comments, logging)
                """),
            new(ChatRole.User, $"""
                ## Mutant
                {mutant.Description}
                Rationale: {mutant.Rationale}
                Original: {mutant.OriginalCode}
                Mutated: {mutant.MutatedCode}

                ## Generated Test
                {testCode}

                ## Change Context
                {changeContext}

                Is this a true positive?
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
