using Microsoft.Extensions.AI;
using JiTTest.Models;
using JiTTest.LLM;
using JiTTest.Configuration;

namespace JiTTest.Pipeline;

/// <summary>
/// Uses an LLM to infer the developer's intent from a code change.
/// </summary>
public class IntentInferrer(IChatClient chatClient, JiTTestConfig config)
{
    public async Task<IntentSummary> InferAsync(ChangeSet changeSet)
    {
        var messages = PromptTemplates.GetIntentInferencePrompt(changeSet);

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("[Intent] Sending prompt to LLM...");
            Console.ResetColor();
        }

        var response = await chatClient.GetResponseAsync(messages);
        var text = response.Text ?? "";

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[Intent] Response: {text[..Math.Min(500, text.Length)]}...");
            Console.ResetColor();
        }

        var intent = LlmResponseParser.ParseJson<IntentSummary>(text);
        if (intent is not null) return intent;

        // Retry with stricter prompt
        messages.Add(new ChatMessage(ChatRole.User,
            "Your response was not valid JSON. Please respond with ONLY a JSON object matching the schema. No other text."));

        response = await chatClient.GetResponseAsync(messages);
        text = response.Text ?? "";

        return LlmResponseParser.ParseJson<IntentSummary>(text) ?? new IntentSummary
        {
            Description = "Could not parse intent from LLM response.",
            BehaviorChanges = [],
            RiskAreas = ["Unknown — intent inference failed"],
            AffectedMethods = []
        };
    }
}
