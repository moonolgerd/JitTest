using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using JiTTest.Configuration;

namespace JiTTest.LLM;

/// <summary>
/// Creates an IChatClient that talks to LLM providers (Ollama or GitHub Models) via OpenAI-compatible API.
/// </summary>
public static class LlmClientFactory
{
    private const string DefaultOllamaEndpoint = "http://localhost:11434/v1";

    /// <summary>
    /// Build an IChatClient for the configured LLM endpoint and model.
    /// Supports both Ollama (local) and GitHub Models (cloud).
    /// </summary>
    public static IChatClient Create(JiTTestConfig config)
    {
        var endpoint = GetEndpoint(config);
        var apiKey = GetApiKey(config, endpoint);

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(endpoint) }
        );

        return client.GetChatClient(config.Model).AsIChatClient();
    }

    /// <summary>
    /// Verify the LLM endpoint is reachable and the model is available.
    /// </summary>
    public static async Task<bool> HealthCheckAsync(JiTTestConfig config)
    {
        var endpoint = GetEndpoint(config);
        
        // GitHub Models detection
        if (IsGitHubModels(endpoint))
        {
            return await HealthCheckGitHubModelsAsync(config, endpoint);
        }
        
        // Ollama detection
        return await HealthCheckOllamaAsync(endpoint, config.Model);
    }

    /// <summary>
    /// Get the effective endpoint from the configuration.
    /// </summary>
    public static string GetEndpoint(JiTTestConfig config)
    {
        // Priority: llm-endpoint > ollama-endpoint (for backward compatibility)
        return config.LlmEndpoint ?? config.OllamaEndpoint ?? DefaultOllamaEndpoint;
    }

    private static string GetApiKey(JiTTestConfig config, string endpoint)
    {
        // GitHub Models requires a token
        if (IsGitHubModels(endpoint))
        {
            // Try config, then environment variable
            var token = config.GitHubToken ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException(
                    "GitHub Models requires authentication. Set 'github-token' in config or GITHUB_TOKEN environment variable.");
            }
            return token;
        }
        
        // Ollama doesn't require authentication
        return "unused";
    }

    private static bool IsGitHubModels(string endpoint)
    {
        return endpoint.Contains("github.ai", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> HealthCheckGitHubModelsAsync(JiTTestConfig config, string endpoint)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            
            var token = config.GitHubToken ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token))
                return false;

            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
            
            // Ensure endpoint ends with /chat/completions
            var testEndpoint = NormalizeGitHubModelsEndpoint(endpoint);
                
            // Send a minimal test request
            var testBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = config.Model,
                messages = new[] { new { role = "user", content = "test" } },
                max_tokens = 1
            });
            
            var content = new StringContent(testBody, System.Text.Encoding.UTF8, "application/json");
            var response = await http.PostAsync(testEndpoint, content);
            
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensure GitHub Models endpoint ends with /chat/completions path.
    /// GitHub Models expects the full path including /chat/completions.
    /// </summary>
    private static string NormalizeGitHubModelsEndpoint(string endpoint)
    {
        var normalized = endpoint.TrimEnd('/');
        if (!normalized.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/chat/completions";
        }
        return normalized;
    }

    private static async Task<bool> HealthCheckOllamaAsync(string endpoint, string model)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // Ollama exposes /api/tags to list models
            var baseUrl = endpoint.Replace("/v1", "");
            var response = await http.GetAsync($"{baseUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            return body.Contains(model.Split(':')[0], StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get a user-friendly provider name for display.
    /// </summary>
    public static string GetProviderName(JiTTestConfig config)
    {
        var endpoint = GetEndpoint(config);
        return IsGitHubModels(endpoint) ? "GitHub Models" : "Ollama";
    }
}
