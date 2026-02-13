using Microsoft.Extensions.AI;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.LLM;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Evaluates candidate catches using rule-based and LLM-based assessment.
/// </summary>
public class Assessor(IChatClient chatClient, JiTTestConfig config)
{
    private static readonly HashSet<string> s_validConfidences = ["HIGH", "MEDIUM", "LOW"];

    public async Task<AssessedCatch> AssessAsync(ExecutionResult candidateCatch, ChangeSet changeSet)
    {
        var result = new AssessedCatch { CandidateCatch = candidateCatch };
        var mutant = candidateCatch.GeneratedTest.ForMutant;

        // Step 1: Rule-based assessment
        result.RuleBasedResult = RuleBasedAssess(candidateCatch);

        if (result.RuleBasedResult.StartsWith("REJECT"))
        {
            result.IsAccepted = false;
            result.Confidence = "LOW";
            result.LlmAssessment = "Skipped — rule-based rejection.";
            return result;
        }

        // Step 2: LLM-based annotation (confidence + reasoning only — cannot veto proven catches)
        // Execution evidence is definitive: test PASSES on original, FAILS on mutant.
        // The LLM annotates WHY the catch matters, but the accept/reject decision is ours.
        var changeContext = changeSet.Files
            .FirstOrDefault(f => f.FilePath.EndsWith(mutant.TargetFile, StringComparison.OrdinalIgnoreCase))
            ?.FullFileContent ?? "";

        var messages = PromptTemplates.GetAssessmentPrompt(
            mutant, candidateCatch.GeneratedTest.TestCode, changeContext);

        try
        {
            var response = await chatClient.GetResponseAsync(messages);
            var text = response.Text ?? "";

            var assessment = LlmResponseParser.ParseJson<LlmAssessmentResult>(text);
            if (assessment is not null)
            {
                result.Confidence = s_validConfidences.Contains(assessment.Confidence?.ToUpperInvariant() ?? "")
                    ? assessment.Confidence!.ToUpperInvariant()
                    : "HIGH";
                result.LlmAssessment = assessment.Reasoning ?? "";
            }
            else
            {
                result.Confidence = "HIGH";
                result.LlmAssessment = text;
            }
        }
        catch (Exception ex)
        {
            result.Confidence = "HIGH";
            result.LlmAssessment = $"LLM annotation failed: {ex.Message}";
        }

        // Execution-proven catches are always accepted (rule-based checks already passed above).
        // Confidence is informational only — it tells the user how meaningful the assessor thinks it is.
        result.IsAccepted = true;

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[Assess] ✅ Accepted [{result.Confidence}] — {result.LlmAssessment[..Math.Min(100, result.LlmAssessment.Length)]}");
            Console.ResetColor();
        }

        return result;
    }

    private static string RuleBasedAssess(ExecutionResult candidate)
    {
        var testCode = candidate.GeneratedTest.TestCode;
        var mutant = candidate.GeneratedTest.ForMutant;

        // Reject if mutant targets comments or using statements
        if (mutant.OriginalCode.TrimStart().StartsWith("//") ||
            mutant.OriginalCode.TrimStart().StartsWith("/*") ||
            mutant.OriginalCode.TrimStart().StartsWith("using "))
        {
            return "REJECT: Mutant targets non-executable code (comment or using statement)";
        }

        // Reject if mutant targets attributes
        if (mutant.OriginalCode.TrimStart().StartsWith('['))
        {
            return "REJECT: Mutant targets attribute declaration";
        }

        // Reject if test only checks null
        var assertLines = testCode.Split('\n')
            .Where(l => l.Contains("Assert.", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (assertLines.Length > 0 && assertLines.All(l =>
            l.Contains("Assert.Null") || l.Contains("Assert.NotNull")))
        {
            return "REJECT: Test only checks null/not-null";
        }

        return "PASS";
    }

    private bool MeetsThreshold(string confidence)
    {
        var threshold = config.ConfidenceThreshold.ToUpperInvariant();
        return threshold switch
        {
            "LOW" => true, // Accept everything
            "MEDIUM" => confidence is "HIGH" or "MEDIUM",
            "HIGH" => confidence is "HIGH",
            _ => confidence is "HIGH" or "MEDIUM"
        };
    }

    private class LlmAssessmentResult
    {
        public bool IsTruePositive { get; set; }
        public string? Confidence { get; set; }
        public string? Reasoning { get; set; }
    }
}
