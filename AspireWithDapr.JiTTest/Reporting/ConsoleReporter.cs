using AspireWithDapr.JiTTest.Models;

namespace AspireWithDapr.JiTTest.Reporting;

/// <summary>
/// Prints JiTTest results to the console with colors.
/// </summary>
public static class ConsoleReporter
{
    public static void Report(List<AssessedCatch> catches, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("═══════════════════════════════════════════════════════");

        var accepted = catches.Where(c => c.IsAccepted).ToList();

        if (accepted.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  JiTTest Report — No regressions detected ✅");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine($"\nCompleted in {elapsed.TotalSeconds:F1}s");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Red;
        var fileCount = accepted.Select(c => c.CandidateCatch.GeneratedTest.ForMutant.TargetFile).Distinct().Count();
        Console.WriteLine($"  JiTTest Report — {accepted.Count} catch(es) in {fileCount} file(s) 🔴");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.ResetColor();

        var byFile = accepted.GroupBy(c => c.CandidateCatch.GeneratedTest.ForMutant.TargetFile);

        foreach (var group in byFile)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"📁 {group.Key}");
            Console.ResetColor();

            var catchNum = 0;
            foreach (var c in group)
            {
                catchNum++;
                var mutant = c.CandidateCatch.GeneratedTest.ForMutant;

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"\n  🔴 CATCH #{catchNum} ");
                Console.ForegroundColor = c.Confidence switch
                {
                    "HIGH" => ConsoleColor.Red,
                    "MEDIUM" => ConsoleColor.Yellow,
                    _ => ConsoleColor.Gray
                };
                Console.WriteLine($"[{c.Confidence} confidence]");
                Console.ResetColor();

                Console.WriteLine($"     Mutant: {mutant.Description}");

                if (!string.IsNullOrEmpty(mutant.Rationale))
                    Console.WriteLine($"     Effect: {mutant.Rationale}");

                // Show first assertion line as a compact test summary
                var assertLine = c.CandidateCatch.GeneratedTest.TestCode
                    .Split('\n')
                    .FirstOrDefault(l => l.Contains("Assert."))
                    ?.Trim();

                if (assertLine is not null)
                    Console.WriteLine($"     Test:   {assertLine}");

                if (!string.IsNullOrEmpty(c.LlmAssessment) && c.LlmAssessment != "Skipped — rule-based rejection.")
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"     Assessment: {c.LlmAssessment[..Math.Min(120, c.LlmAssessment.Length)]}");
                    Console.ResetColor();
                }
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("═══════════════════════════════════════════════════════");
        Console.ResetColor();
        Console.WriteLine($"\nCompleted in {elapsed.TotalSeconds:F1}s");
    }
}
