using System.Diagnostics;
using AspireWithDapr.JiTTest.Configuration;
using AspireWithDapr.JiTTest.Compilation;
using AspireWithDapr.JiTTest.LLM;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.Reporting;
using Microsoft.Extensions.AI;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Orchestrates the full JiTTest pipeline: Diff → Intent → Mutants → Tests → Execute → Assess → Report.
/// </summary>
public class PipelineOrchestrator(JiTTestConfig config)
{
    public async Task<int> RunAsync()
    {
        var sw = Stopwatch.StartNew();

        // ── Stage 1: Extract diff ────────────────────────────────────
        PrintStage("Stage 1/6", "Extracting diff...");
        var diffExtractor = new DiffExtractor(config);
        var changeSet = diffExtractor.Extract();

        if (changeSet.Files.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("No C# changes found matching configured targets. Nothing to do.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"  Found {changeSet.Files.Count} file(s) with changes.");

        // ── Stage 2: Infer intent ────────────────────────────────────
        PrintStage("Stage 2/6", "Inferring change intent...");
        var chatClient = OllamaClientFactory.Create(config);
        var intentInferrer = new IntentInferrer(chatClient, config);
        var intent = await intentInferrer.InferAsync(changeSet);

        Console.WriteLine($"  Intent: {intent.Description[..Math.Min(80, intent.Description.Length)]}");

        if (config.DryRun)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("\n[DRY RUN] Stopping after intent inference.");
            Console.WriteLine($"\nBehavior changes: {string.Join(", ", intent.BehaviorChanges)}");
            Console.WriteLine($"Risk areas: {string.Join(", ", intent.RiskAreas)}");
            Console.ResetColor();
            return 0;
        }

        // ── Stage 3: Generate mutants ────────────────────────────────
        PrintStage("Stage 3/6", "Generating realistic mutants...");
        var mutantGenerator = new MutantGenerator(chatClient, config);
        var mutants = await mutantGenerator.GenerateAsync(intent, changeSet);

        if (mutants.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  No valid mutants generated. Nothing to test.");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"  Generated {mutants.Count} mutant(s).");

        // ── Stage 4: Generate tests ──────────────────────────────────
        PrintStage("Stage 4/6", "Generating catching tests...");
        var compiler = new RoslynCompiler(Path.Combine(config.RepositoryRoot, "AspireWithDapr.Shared", "bin", "Debug", "net10.0"));
        var testGenerator = new TestGenerator(chatClient, compiler, config);
        var tests = new List<GeneratedTest>();

        foreach (var mutant in mutants)
        {
            var sourceContent = changeSet.Files
                .FirstOrDefault(f => f.FilePath.EndsWith(mutant.TargetFile, StringComparison.OrdinalIgnoreCase))
                ?.FullFileContent ?? "";

            var test = await testGenerator.GenerateAsync(mutant, sourceContent);
            tests.Add(test);
        }

        var compiledTests = tests.Where(t => t.CompilationSuccess).ToList();
        Console.WriteLine($"  Generated {tests.Count} test(s), {compiledTests.Count} compiled successfully.");

        if (compiledTests.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  No tests compiled successfully. Consider relaxing the model or retries.");
            Console.ResetColor();
            return 0;
        }

        // ── Stage 5: Execute tests ───────────────────────────────────
        PrintStage("Stage 5/6", "Executing tests (original → mutated)...");
        var testExecutor = new TestExecutor(config);
        var candidateCatches = new List<ExecutionResult>();

        foreach (var test in compiledTests)
        {
            var result = await testExecutor.ExecuteAsync(test);
            if (result.IsCandidateCatch)
                candidateCatches.Add(result);
        }

        Console.WriteLine($"  {candidateCatches.Count} candidate catch(es) from {compiledTests.Count} test(s).");

        if (candidateCatches.Count == 0)
        {
            ConsoleReporter.Report([], sw.Elapsed);
            return 0;
        }

        // ── Stage 6: Assess catches ─────────────────────────────────
        PrintStage("Stage 6/6", "Assessing catches...");
        var assessor = new Assessor(chatClient, config);
        var assessed = new List<AssessedCatch>();

        foreach (var candidate in candidateCatches)
        {
            var assessment = await assessor.AssessAsync(candidate, changeSet);
            assessed.Add(assessment);
        }

        var accepted = assessed.Where(a => a.IsAccepted).ToList();
        Console.WriteLine($"  {accepted.Count} accepted catch(es) from {candidateCatches.Count} candidate(s).");

        // ── Reporting ────────────────────────────────────────────────
        sw.Stop();

        if (config.Reporters.Contains("console", StringComparer.OrdinalIgnoreCase))
            ConsoleReporter.Report(assessed, sw.Elapsed);

        if (config.Reporters.Contains("markdown", StringComparer.OrdinalIgnoreCase))
        {
            var mdPath = Path.Combine(config.RepositoryRoot, "jittest-report.md");
            MarkdownReporter.Report(assessed, sw.Elapsed, mdPath);
            Console.WriteLine($"\nMarkdown report: {mdPath}");
        }

        return accepted.Count > 0 ? 1 : 0;
    }

    private static void PrintStage(string stage, string message)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"[{stage}] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }
}
