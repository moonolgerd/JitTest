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

        // OpenAI SDK adds /chat/completions automatically, so strip it if present
        var baseEndpoint = StripChatCompletionsPath(endpoint);

        var client = new OpenAIClient(
            new ApiKeyCredential(apiKey),
            new OpenAIClientOptions { Endpoint = new Uri(baseEndpoint) }
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
        return endpoint.Contains("models.github.ai", StringComparison.OrdinalIgnoreCase) ||
               endpoint.Contains("inference.ai.azure.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strip /chat/completions from endpoint if present.
    /// OpenAI SDK adds this path automatically.
    /// </summary>
    private static string StripChatCompletionsPath(string endpoint)
    {
        var trimmed = endpoint.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Substring(0, trimmed.Length - "/chat/completions".Length);
        }
        return endpoint;
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
            http.DefaultRequestHeaders.Add("User-Agent", "JitTest/1.0");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            
            // Only add api-version for Azure endpoints
            if (endpoint.Contains("inference.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            {
                http.DefaultRequestHeaders.Add("api-version", "2024-05-01-preview");
            }
            
            // Ensure endpoint ends with /chat/completions
            var testEndpoint = NormalizeGitHubModelsEndpoint(endpoint);
            
            if (config.Verbose)
            {
                Console.Error.WriteLine($"[Debug] Testing endpoint: {testEndpoint}");
                Console.Error.WriteLine($"[Debug] Using token: {token.Substring(0, Math.Min(20, token.Length))}...");
            }
                
            // Send a minimal test request
            var testBody = System.Text.Json.JsonSerializer.Serialize(new
            {
                model = config.Model,
                messages = new[] { new { role = "user", content = "test" } },
                max_tokens = 1
            });
            
            var content = new StringContent(testBody, System.Text.Encoding.UTF8, "application/json");
            var response = await http.PostAsync(testEndpoint, content);
            
            if (!response.IsSuccessStatusCode && config.Verbose)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                Console.Error.WriteLine($"[Debug] GitHub Models health check failed: {response.StatusCode}");
                Console.Error.WriteLine($"[Debug] Response: {errorBody}");
            }
            
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (config.Verbose)
        {
            Console.Error.WriteLine($"[Debug] GitHub Models health check exception: {ex.Message}");
            return false;
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
