using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.Pipeline;

namespace AspireWithDapr.JiTTest.Reporting;

/// <summary>
/// Prints JiTTest results to the console with colors.
/// </summary>
public static class ConsoleReporter
{
    public static void Report(List<AssessedCatch> catches, TimeSpan elapsed, List<SuspiciousPattern>? suspiciousPatterns = null, bool verbose = false)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("═══════════════════════════════════════════════════════");

        var accepted = catches.Where(c => c.IsAccepted).ToList();
        var warningCount = suspiciousPatterns?.Count ?? 0;

        if (accepted.Count == 0 && warningCount == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  JiTTest Report — No regressions detected ✅");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.ResetColor();
            Console.WriteLine($"\nCompleted in {elapsed.TotalSeconds:F1}s");
            return;
        }

        if (accepted.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            var fileCount = accepted.Select(c => c.CandidateCatch.GeneratedTest.ForMutant.TargetFile).Distinct().Count();
            Console.WriteLine($"  JiTTest Report — {accepted.Count} catch(es) in {fileCount} file(s) 🔴");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  JiTTest Report — {warningCount} warning(s) ⚠");
        }

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

                // Show first assertion line as a compact test summary (or full code in verbose mode)
                if (verbose)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("\n     Generated Test Code:");
                    Console.WriteLine("     " + new string('─', 60));
                    Console.ResetColor();
                    
                    var codeLines = c.CandidateCatch.GeneratedTest.TestCode.Split('\n');
                    foreach (var line in codeLines)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkCyan;
                        Console.WriteLine($"     {line}");
                    }
                    
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine("     " + new string('─', 60));
                    Console.ResetColor();
                }
                else
                {
                    var assertLine = c.CandidateCatch.GeneratedTest.TestCode
                        .Split('\n')
                        .FirstOrDefault(l => l.Contains("Assert."))
                        ?.Trim();

                    if (assertLine is not null)
                        Console.WriteLine($"     Test:   {assertLine}");
                }

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

        // Show suspicious patterns (static analysis warnings)
        if (suspiciousPatterns is { Count: > 0 })
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"⚠ Static Analysis — {suspiciousPatterns.Count} suspicious pattern(s) in changed code:");
            Console.ResetColor();

            foreach (var p in suspiciousPatterns)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"\n  ⚠ [{p.Pattern}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{p.File}:{p.Line} ");
                Console.ResetColor();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"     Code: {p.Code}");
                Console.ResetColor();
                Console.WriteLine($"     {p.Description}");
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("═══════════════════════════════════════════════════════");
            Console.ResetColor();
        }

        Console.WriteLine($"\nCompleted in {elapsed.TotalSeconds:F1}s");
    }
}
