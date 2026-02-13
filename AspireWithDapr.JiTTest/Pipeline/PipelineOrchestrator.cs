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

        // ── Build target project if testing uncommitted changes ──────
        if (config.DiffSource.ToLowerInvariant() is "uncommitted" or "staged")
        {
            Console.Write("  Building target project with changes... ");
            var buildSuccess = BuildTargetProjects(config.RepositoryRoot, changeSet);
            Console.ForegroundColor = buildSuccess ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine(buildSuccess ? "OK" : "FAILED (using existing DLLs)");
            Console.ResetColor();
        }

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
        
        // Find build outputs - look for bin/Debug or bin/Release directories
        var buildOutputPath = FindBuildOutput(config.RepositoryRoot);
        if (config.Verbose && buildOutputPath != null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  Using build output: {buildOutputPath}");
            Console.ResetColor();
        }
        
        var compiler = new RoslynCompiler(buildOutputPath);
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

    private static string? FindBuildOutput(string repoRoot)
    {
        // Look for bin/Debug or bin/Release directories
        var candidates = new[]
        {
            Path.Combine(repoRoot, "bin", "Debug"),
            Path.Combine(repoRoot, "bin", "Release")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Search subdirectories for any bin/Debug or bin/Release
        try
        {
            var binDirs = Directory.GetDirectories(repoRoot, "bin", SearchOption.AllDirectories)
                .Where(d => !d.Contains("\\obj\\"))
                .ToList();

            foreach (var binDir in binDirs)
            {
                var debugDir = Path.Combine(binDir, "Debug");
                if (Directory.Exists(debugDir))
                    return debugDir;

                var releaseDir = Path.Combine(binDir, "Release");
                if (Directory.Exists(releaseDir))
                    return releaseDir;
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
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
}
