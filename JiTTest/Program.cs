using System.CommandLine;
using System.CommandLine.Parsing;
using JiTTest.Configuration;
using JiTTest.LLM;
using JiTTest.Pipeline;

// ── CLI definition using System.CommandLine ──────────────────────────

var configOption = new Option<string?>("--config") { Description = "Path to jittest-config.json" };
var diffSourceOption = new Option<string?>("--diff-source") { Description = "Git diff source: staged, uncommitted, branch:<name>, HEAD~N" };
var modelOption = new Option<string?>("--model") { Description = "Ollama model name override" };
var verboseOption = new Option<bool>("--verbose") { Description = "Show LLM prompt/response details" };
var dryRunOption = new Option<bool>("--dry-run") { Description = "Stop after intent inference (no mutants or tests)" };

var rootCommand = new RootCommand("JiTTest — Ephemeral catching tests from LLM-generated mutants")
{
    configOption, diffSourceOption, modelOption, verboseOption, dryRunOption
};

rootCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var configPath = parseResult.GetValue(configOption);
    var diffSource = parseResult.GetValue(diffSourceOption);
    var model = parseResult.GetValue(modelOption);
    var verbose = parseResult.GetValue(verboseOption);
    var dryRun = parseResult.GetValue(dryRunOption);
    // Load config and apply CLI overrides
    var config = JiTTestConfig.Load(configPath);
    if (diffSource is not null) config.DiffSource = diffSource;
    if (model is not null) config.Model = model;
    config.Verbose = verbose;
    config.DryRun = dryRun;

    // Resolve repository root
    if (string.IsNullOrEmpty(config.RepositoryRoot))
        config.RepositoryRoot = FindGitRoot(Directory.GetCurrentDirectory())
            ?? Directory.GetCurrentDirectory();

    // Print banner
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("""

         ╔═══════════════════════════════╗
         ║  JiTTest — Catching Tests     ║
         ║  LLM-Driven Mutation Testing  ║
         ╚═══════════════════════════════╝
    """);
    Console.ResetColor();

    if (verbose)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Repository: {config.RepositoryRoot}");
        Console.WriteLine($"  Diff source: {config.DiffSource}");
        Console.WriteLine($"  Model: {config.Model}");
        Console.WriteLine($"  Endpoint: {config.OllamaEndpoint}");
        Console.ResetColor();
    }

    // Health check Ollama
    Console.Write("Checking Ollama connectivity... ");
    var chatClient = OllamaClientFactory.Create(config);
    if (await OllamaClientFactory.HealthCheckAsync(config))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("OK");
        Console.ResetColor();
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("FAILED");
        Console.ResetColor();
        Console.Error.WriteLine($"Cannot reach Ollama at {config.OllamaEndpoint} or model '{config.Model}' is not available.");
        Console.Error.WriteLine("Run: ollama pull " + config.Model);
        Environment.ExitCode = 2;
        return;
    }

    // Run pipeline
    var orchestrator = new PipelineOrchestrator(config);
    Environment.ExitCode = await orchestrator.RunAsync();
});

return await rootCommand.Parse(args).InvokeAsync();

// ── Helper ───────────────────────────────────────────────────────────

static string? FindGitRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
