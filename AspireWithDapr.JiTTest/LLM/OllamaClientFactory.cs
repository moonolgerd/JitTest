using System.ClientModel;
using Microsoft.Extensions.AI;
using OpenAI;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.LLM;

/// <summary>
/// Creates an IChatClient that talks to Ollama's OpenAI-compatible API.
/// </summary>
public static class OllamaClientFactory
{
    /// <summary>
    /// Build an IChatClient for the configured Ollama endpoint and model.
    /// </summary>
    public static IChatClient Create(JiTTestConfig config)
    {
        var client = new OpenAIClient(
            new ApiKeyCredential("unused"),
            new OpenAIClientOptions { Endpoint = new Uri(config.OllamaEndpoint) }
        );

        return client.GetChatClient(config.Model).AsIChatClient();
    }

    /// <summary>
    /// Verify the Ollama endpoint is reachable and the model is loaded.
    /// </summary>
    public static async Task<bool> HealthCheckAsync(JiTTestConfig config)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // Ollama exposes /api/tags to list models
            var baseUrl = config.OllamaEndpoint.Replace("/v1", "");
            var response = await http.GetAsync($"{baseUrl}/api/tags");
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            return body.Contains(config.Model.Split(':')[0], StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
