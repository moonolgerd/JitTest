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
        Console.WriteLine($"  Provider: {LlmClientFactory.GetProviderName(config)}");
        Console.WriteLine($"  Endpoint: {config.LlmEndpoint ?? config.OllamaEndpoint}");
        Console.ResetColor();
    }

    // Health check LLM provider
    var providerName = LlmClientFactory.GetProviderName(config);
    Console.Write($"Checking {providerName} connectivity... ");
    var chatClient = LlmClientFactory.Create(config);
    if (await LlmClientFactory.HealthCheckAsync(config))
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
        
        if (providerName == "GitHub Models")
        {
            Console.Error.WriteLine($"Cannot reach GitHub Models endpoint or model '{config.Model}' is not available.");
            Console.Error.WriteLine("Ensure you have a valid GitHub token set via 'github-token' in config or GITHUB_TOKEN environment variable.");
        }
        else
        {
            Console.Error.WriteLine($"Cannot reach Ollama at {config.LlmEndpoint ?? config.OllamaEndpoint} or model '{config.Model}' is not available.");
            Console.Error.WriteLine("Run: ollama pull " + config.Model);
        }
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
