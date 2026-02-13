using System.Text.Json;
using System.Text.Json.Serialization;

namespace AspireWithDapr.JiTTest.Configuration;

/// <summary>
/// Configuration for the JiTTest pipeline, loaded from jittest-config.json with CLI overrides.
/// </summary>
public class JiTTestConfig
{
    [JsonPropertyName("ollama-endpoint")]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434/v1";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "qwen2.5-coder:32b-instruct-q4_K_M";

    [JsonPropertyName("diff-source")]
    public string DiffSource { get; set; } = "staged";

    [JsonPropertyName("mutate-targets")]
    public List<string> MutateTargets { get; set; } =
    [
        "**/AspireWithDapr.Shared/**/*.cs",
        "**/AspireWithDapr.ApiService/**/*.cs"
    ];

    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } =
    [
        "**/Program.cs",
        "**/obj/**",
        "**/bin/**"
    ];

    [JsonPropertyName("max-mutants-per-change")]
    public int MaxMutantsPerChange { get; set; } = 5;

    [JsonPropertyName("max-retries")]
    public int MaxRetries { get; set; } = 2;

    [JsonPropertyName("confidence-threshold")]
    public string ConfidenceThreshold { get; set; } = "MEDIUM";

    [JsonPropertyName("reporters")]
    public List<string> Reporters { get; set; } = ["console"];

    [JsonPropertyName("temp-directory")]
    public string TempDirectory { get; set; } = ".jittest-temp";

    [JsonPropertyName("max-parallel")]
    public int MaxParallel { get; set; } = 3;

    /// <summary>Absolute path to the git repository root (resolved at runtime).</summary>
    [JsonIgnore]
    public string RepositoryRoot { get; set; } = "";

    /// <summary>Whether to show verbose LLM prompt/response output.</summary>
    [JsonIgnore]
    public bool Verbose { get; set; }

    /// <summary>Run diff extraction and intent inference only.</summary>
    [JsonIgnore]
    public bool DryRun { get; set; }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Load config from a JSON file. Returns defaults if file not found.
    /// </summary>
    public static JiTTestConfig Load(string? configPath)
    {
        configPath ??= FindConfigFile();

        if (configPath is null || !File.Exists(configPath))
        {
            return new JiTTestConfig();
        }

        var json = File.ReadAllText(configPath);
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        // Support both root-level and nested "jittest-config" section
        JsonElement configElement = doc.RootElement;
        if (doc.RootElement.TryGetProperty("jittest-config", out var nested))
        {
            configElement = nested;
        }

        return configElement.Deserialize<JiTTestConfig>(s_jsonOptions) ?? new JiTTestConfig();
    }

    /// <summary>
    /// Search upward from current directory for jittest-config.json.
    /// </summary>
    private static string? FindConfigFile()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "jittest-config.json");
            if (File.Exists(candidate)) return candidate;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
