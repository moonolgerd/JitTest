using System.Collections.Concurrent;
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
/// Stages 4-6 run with configurable parallelism (max-parallel) for significantly faster execution.
/// </summary>
public class PipelineOrchestrator(JiTTestConfig config)
{
    private static readonly Lock s_consoleLock = new();
    private readonly Dictionary<string, TimeSpan> _stageTimes = [];

    public async Task<int> RunAsync()
    {
        var sw = Stopwatch.StartNew();
        var parallelism = Math.Max(1, config.MaxParallel);

        // ── Stage 1: Extract diff ────────────────────────────────────
        var stageSw = Stopwatch.StartNew();
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
        _stageTimes["1-Diff"] = stageSw.Elapsed;

        // ── Static Analysis: Detect suspicious patterns in changed code ──
        var suspiciousPatterns = SuspiciousPatternDetector.Detect(changeSet);
        if (suspiciousPatterns.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  \u26a0 {suspiciousPatterns.Count} suspicious pattern(s) detected in changed code:");
            foreach (var p in suspiciousPatterns)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"    \u26a0 [{p.Pattern}] ");
                Console.ResetColor();
                Console.WriteLine($"{p.File}:{p.Line} \u2014 {p.Description}");
            }
            Console.ResetColor();
        }

        // ── Build target project if testing uncommitted changes ──────
        if (config.DiffSource.ToLowerInvariant() is "uncommitted" or "staged")
        {
            var buildSw = Stopwatch.StartNew();
            Console.Write("  Building target project with changes... ");
            var buildSuccess = BuildTargetProjects(config.RepositoryRoot, changeSet);
            Console.ForegroundColor = buildSuccess ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(buildSuccess ? "OK" : "FAILED (using existing DLLs)");
            Console.ResetColor();
            _stageTimes["Build"] = buildSw.Elapsed;
        }

        // ── Stage 2: Infer intent ────────────────────────────────────
        stageSw = Stopwatch.StartNew();
        PrintStage("Stage 2/6", "Inferring change intent...");
        var chatClient = OllamaClientFactory.Create(config);
        var intentInferrer = new IntentInferrer(chatClient, config);
        var intent = await intentInferrer.InferAsync(changeSet);

        Console.WriteLine($"  Intent: {intent.Description[..Math.Min(80, intent.Description.Length)]}");
        _stageTimes["2-Intent"] = stageSw.Elapsed;

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
        stageSw = Stopwatch.StartNew();
        PrintStage("Stage 3/6", "Generating realistic mutants...");
        var mutantGenerator = new MutantGenerator(chatClient, config);
        var mutants = await mutantGenerator.GenerateAsync(intent, changeSet);

        if (mutants.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  No mutants generated. Nothing to test.");
            Console.ResetColor();
            PrintTimings(sw.Elapsed);
            return 0;
        }

        // Prioritize testable mutants: public first, then allow some private/protected
        // Private/protected methods CAN be tested indirectly (e.g. BackgroundService.StartAsync
        // invokes protected ExecuteAsync), so we don't discard them entirely.
        var publicMutants = mutants.Where(m =>
            m.ContainingMember is null || m.ContainingMemberIsPublic).ToList();
        var nonPublicMutants = mutants.Where(m =>
            m.ContainingMember is not null && !m.ContainingMemberIsPublic).ToList();

        // Allow up to 2 non-public mutants through (they may be testable indirectly)
        const int maxNonPublic = 2;
        var selectedNonPublic = nonPublicMutants.Take(maxNonPublic).ToList();

        if (config.Verbose && nonPublicMutants.Count > 0)
        {
            foreach (var m in selectedNonPublic)
            {
                var vis = m.ContainingMemberIsProtected ? "protected" : "private";
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [Filter] Keeping {m.Id}: inside {vis} method '{m.ContainingMember}' — will attempt indirect testing");
                Console.ResetColor();
            }
            foreach (var m in nonPublicMutants.Skip(maxNonPublic))
            {
                var vis = m.ContainingMemberIsProtected ? "protected" : "private";
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [Filter] Skipping {m.Id}: inside {vis} method '{m.ContainingMember}' — quota reached");
                Console.ResetColor();
            }
        }

        mutants = [.. publicMutants, .. selectedNonPublic];

        Console.WriteLine($"  {publicMutants.Count} public + {selectedNonPublic.Count} private/protected mutant(s) selected" +
            (nonPublicMutants.Count > selectedNonPublic.Count ? $" (skipped {nonPublicMutants.Count - selectedNonPublic.Count})" : "") + ".");

        _stageTimes["3-Mutants"] = stageSw.Elapsed;

        // ── Stage 4: Generate tests (parallel) ──────────────────────
        stageSw = Stopwatch.StartNew();
        PrintStage("Stage 4/6", $"Generating catching tests... (parallelism: {parallelism})");
        
        // Find build outputs from ALL projects (needed for Dapr, logging, etc. references)
        var buildOutputPaths = FindAllBuildOutputs(config.RepositoryRoot);
        if (config.Verbose && buildOutputPaths.Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Found {buildOutputPaths.Length} build output path(s) for Roslyn references.");
            Console.ResetColor();
        }
        
        var compiler = new RoslynCompiler(buildOutputPaths, config.Verbose);
        var testGenerator = new TestGenerator(chatClient, compiler, config);
        var tests = new ConcurrentBag<GeneratedTest>();

        await Parallel.ForEachAsync(mutants,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (mutant, ct) =>
            {
                var sourceContent = changeSet.Files
                    .FirstOrDefault(f => f.FilePath.EndsWith(mutant.TargetFile, StringComparison.OrdinalIgnoreCase))
                    ?.FullFileContent ?? "";

                var test = await testGenerator.GenerateAsync(mutant, sourceContent);
                tests.Add(test);
            });

        var allTests = tests.ToList();
        var compiledTests = allTests.Where(t => t.CompilationSuccess).ToList();
        Console.WriteLine($"  Generated {allTests.Count} test(s), {compiledTests.Count} compiled successfully.");
        _stageTimes["4-TestGen"] = stageSw.Elapsed;

        if (compiledTests.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  No tests compiled successfully. Consider relaxing the model or retries.");
            Console.ResetColor();
            PrintTimings(sw.Elapsed);
            return 0;
        }

        // ── Stage 5: Execute tests (parallel with shadow copies) ────
        stageSw = Stopwatch.StartNew();
        PrintStage("Stage 5/6", $"Executing tests (original → mutated)... (parallelism: {parallelism})");
        var testExecutor = new TestExecutor(config);
        var candidateCatches = new ConcurrentBag<ExecutionResult>();

        await Parallel.ForEachAsync(compiledTests,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (test, ct) =>
            {
                var result = await testExecutor.ExecuteAsync(test);
                if (result.IsCandidateCatch)
                    candidateCatches.Add(result);
            });

        var candidateList = candidateCatches.ToList();
        Console.WriteLine($"  {candidateList.Count} candidate catch(es) from {compiledTests.Count} test(s).");
        _stageTimes["5-Exec"] = stageSw.Elapsed;

        if (candidateList.Count == 0)
        {
            ConsoleReporter.Report([], sw.Elapsed, suspiciousPatterns);
            PrintTimings(sw.Elapsed);
            return suspiciousPatterns.Count > 0 ? 1 : 0;
        }

        // ── Stage 6: Assess catches (parallel) ─────────────────────
        stageSw = Stopwatch.StartNew();
        PrintStage("Stage 6/6", $"Assessing catches... (parallelism: {parallelism})");
        var assessor = new Assessor(chatClient, config);
        var assessed = new ConcurrentBag<AssessedCatch>();

        await Parallel.ForEachAsync(candidateList,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism },
            async (candidate, ct) =>
            {
                var assessment = await assessor.AssessAsync(candidate, changeSet);
                assessed.Add(assessment);
            });

        var assessedList = assessed.ToList();
        var accepted = assessedList.Where(a => a.IsAccepted).ToList();
        Console.WriteLine($"  {accepted.Count} accepted catch(es) from {candidateList.Count} candidate(s).");
        _stageTimes["6-Assess"] = stageSw.Elapsed;

        // ── Reporting ────────────────────────────────────────────────
        sw.Stop();

        if (config.Reporters.Contains("console", StringComparer.OrdinalIgnoreCase))
            ConsoleReporter.Report(assessedList, sw.Elapsed, suspiciousPatterns);

        if (config.Reporters.Contains("markdown", StringComparer.OrdinalIgnoreCase))
        {
            var mdPath = Path.Combine(config.RepositoryRoot, "jittest-report.md");
            MarkdownReporter.Report(assessedList, sw.Elapsed, mdPath, suspiciousPatterns);
            Console.WriteLine($"\nMarkdown report: {mdPath}");
        }

        PrintTimings(sw.Elapsed);
        return accepted.Count > 0 ? 1 : 0;
    }

    /// <summary>
    /// Find ALL build output directories across the repo so Roslyn has references
    /// to every project's assemblies (Shared, ApiService, Publisher, etc.).
    /// </summary>
    private static string[] FindAllBuildOutputs(string repoRoot)
    {
        var results = new List<string>();

        try
        {
            var binDirs = Directory.GetDirectories(repoRoot, "bin", SearchOption.AllDirectories)
                .Where(d => !d.Contains("\\obj\\") && !d.Contains(".JiTTest"))
                .ToList();

            foreach (var binDir in binDirs)
            {
                var debugDir = Path.Combine(binDir, "Debug");
                if (Directory.Exists(debugDir))
                {
                    results.Add(debugDir);
                    continue;
                }

                var releaseDir = Path.Combine(binDir, "Release");
                if (Directory.Exists(releaseDir))
                    results.Add(releaseDir);
            }
        }
        catch
        {
            // Ignore errors
        }

        return [.. results];
    }

    private static bool BuildTargetProjects(string repoRoot, ChangeSet changeSet)
    {
        try
        {
            // Find unique project directories from changed files
            var projectDirs = changeSet.Files
                .Select(f => Path.GetDirectoryName(Path.Combine(repoRoot, f.FilePath)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var projectDir in projectDirs)
            {
                // Look for .csproj file
                var csprojFiles = Directory.GetFiles(projectDir!, "*.csproj");
                if (csprojFiles.Length == 0) continue;

                var csproj = csprojFiles[0];
                
                // Build the project
                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{csproj}\" --no-restore",
                    WorkingDirectory = projectDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null) return false;

                process.WaitForExit(30000); // 30 second timeout
                if (process.ExitCode != 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintStage(string stage, string message)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"[{stage}] ");
        Console.ResetColor();
        Console.WriteLine(message);
    }

    private void PrintTimings(TimeSpan total)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("── Stage Timings ───────────────────────────────────────");
        foreach (var (stage, elapsed) in _stageTimes)
        {
            var pct = total.TotalMilliseconds > 0
                ? (elapsed.TotalMilliseconds / total.TotalMilliseconds * 100).ToString("F0")
                : "0";
            Console.WriteLine($"  {stage,-14} {elapsed.TotalSeconds,7:F1}s  ({pct}%)");
        }
        Console.WriteLine($"  {"Total",-14} {total.TotalSeconds,7:F1}s");
        Console.WriteLine($"  Parallelism:   {Math.Max(1, config.MaxParallel)}");
        Console.ResetColor();
    }
}
