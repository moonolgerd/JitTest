using System.Text.Json;
using System.Text.Json.Serialization;

namespace JiTTest.Configuration;

/// <summary>
/// Configuration for the JiTTest pipeline, loaded from jittest-config.json with CLI overrides.
/// </summary>
public class JiTTestConfig
{
    /// <summary>
    /// Legacy Ollama endpoint configuration. Kept for backward compatibility.
    /// New configurations should use LlmEndpoint instead.
    /// </summary>
    [JsonPropertyName("ollama-endpoint")]
    public string? OllamaEndpoint { get; set; }

    /// <summary>
    /// Generic LLM endpoint URL (supports Ollama, GitHub Models, or other OpenAI-compatible APIs).
    /// Takes priority over OllamaEndpoint if both are specified.
    /// </summary>
    [JsonPropertyName("llm-endpoint")]
    public string? LlmEndpoint { get; set; }

    /// <summary>
    /// GitHub token for authentication with GitHub Models.
    /// Can also be set via GITHUB_TOKEN environment variable.
    /// </summary>
    [JsonPropertyName("github-token")]
    public string? GitHubToken { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = default!;

    [JsonPropertyName("diff-source")]
    public string DiffSource { get; set; } = default!;

    [JsonPropertyName("mutate-targets")]
    public List<string> MutateTargets { get; set; } = default!;

    [JsonPropertyName("exclude")]
    public List<string> Exclude { get; set; } = default!;

    [JsonPropertyName("max-mutants-per-change")]
    public int MaxMutantsPerChange { get; set; }

    [JsonPropertyName("max-retries")]
    public int MaxRetries { get; set; }

    [JsonPropertyName("confidence-threshold")]
    public string ConfidenceThreshold { get; set; } = default!;

    [JsonPropertyName("reporters")]
    public List<string> Reporters { get; set; } = default!;

    [JsonPropertyName("temp-directory")]
    public string TempDirectory { get; set; } = default!;

    [JsonPropertyName("max-parallel")]
    public int MaxParallel { get; set; }

    /// <summary>Absolute path to the git repository root (resolved at runtime).</summary>
    [JsonIgnore]
    public string RepositoryRoot { get; set; } = default!;

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
