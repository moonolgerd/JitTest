using System.Text;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.Pipeline;

namespace AspireWithDapr.JiTTest.Reporting;

/// <summary>
/// Writes JiTTest results to a markdown file.
/// </summary>
public static class MarkdownReporter
{
    public static void Report(List<AssessedCatch> catches, TimeSpan elapsed, string outputPath, List<SuspiciousPattern>? suspiciousPatterns = null)
    {
        var sb = new StringBuilder();
        var accepted = catches.Where(c => c.IsAccepted).ToList();

        sb.AppendLine("# JiTTest Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ");
        sb.AppendLine($"Duration: {elapsed.TotalSeconds:F1}s  ");
        sb.AppendLine($"Catches: **{accepted.Count}**");
        sb.AppendLine();

        if (accepted.Count == 0)
        {
            sb.AppendLine("> ✅ No regressions detected.");
            File.WriteAllText(outputPath, sb.ToString());
            return;
        }

        sb.AppendLine("---");

        var byFile = accepted.GroupBy(c => c.CandidateCatch.GeneratedTest.ForMutant.TargetFile);

        foreach (var group in byFile)
        {
            sb.AppendLine();
            sb.AppendLine($"## 📁 {group.Key}");

            var catchNum = 0;
            foreach (var c in group)
            {
                catchNum++;
                var mutant = c.CandidateCatch.GeneratedTest.ForMutant;

                sb.AppendLine();
                sb.AppendLine($"### 🔴 Catch #{catchNum} [{c.Confidence}]");
                sb.AppendLine();
                sb.AppendLine($"**Mutant**: {mutant.Description}  ");
                sb.AppendLine($"**Rationale**: {mutant.Rationale}  ");
                sb.AppendLine($"**Lines**: {mutant.LineStart}–{mutant.LineEnd}");
                sb.AppendLine();
                sb.AppendLine("**Original code:**");
                sb.AppendLine($"```csharp");
                sb.AppendLine(mutant.OriginalCode);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("**Mutated code:**");
                sb.AppendLine($"```csharp");
                sb.AppendLine(mutant.MutatedCode);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("**Generated test:**");
                sb.AppendLine("```csharp");
                sb.AppendLine(c.CandidateCatch.GeneratedTest.TestCode);
                sb.AppendLine("```");

                if (!string.IsNullOrEmpty(c.LlmAssessment) && c.LlmAssessment != "Skipped — rule-based rejection.")
                {
                    sb.AppendLine();
                    sb.AppendLine($"> {c.LlmAssessment}");
                }
            }
        }

        // Suspicious patterns section
        if (suspiciousPatterns is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## ⚠ Static Analysis — {suspiciousPatterns.Count} suspicious pattern(s)");
            sb.AppendLine();

            foreach (var p in suspiciousPatterns)
            {
                sb.AppendLine($"### ⚠ [{p.Pattern}] {p.File}:{p.Line}");
                sb.AppendLine();
                sb.AppendLine("```csharp");
                sb.AppendLine(p.Code);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine(p.Description);
                sb.AppendLine();
            }
        }

        File.WriteAllText(outputPath, sb.ToString());
    }
}
