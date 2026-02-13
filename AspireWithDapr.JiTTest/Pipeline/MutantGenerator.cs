using Microsoft.Extensions.AI;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.LLM;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Uses an LLM to generate realistic code mutants based on intent and diff.
/// </summary>
public class MutantGenerator(IChatClient chatClient, JiTTestConfig config)
{
    public async Task<List<Mutant>> GenerateAsync(IntentSummary intent, ChangeSet changeSet)
    {
        var messages = PromptTemplates.GetMutantGenerationPrompt(intent, changeSet);

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("[Mutant] Sending prompt to LLM...");
            Console.ResetColor();
        }

        var response = await chatClient.GetResponseAsync(messages);
        var text = response.Text ?? "";

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[Mutant] Response: {text[..Math.Min(500, text.Length)]}...");
            Console.ResetColor();
        }

        var mutants = LlmResponseParser.ParseJson<List<Mutant>>(text);
        if (mutants is null || mutants.Count == 0)
        {
            // Retry
            messages.Add(new ChatMessage(ChatRole.User,
                "Your response was not valid JSON. Respond with ONLY a JSON array of mutant objects."));

            response = await chatClient.GetResponseAsync(messages);
            text = response.Text ?? "";
            mutants = LlmResponseParser.ParseJson<List<Mutant>>(text);
        }

        if (mutants is null) return [];

        // Validate and limit
        var validated = new List<Mutant>();
        foreach (var mutant in mutants.Take(config.MaxMutantsPerChange))
        {
            if (ValidateMutant(mutant, changeSet))
            {
                validated.Add(mutant);
            }
            else if (config.Verbose)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Mutant] Skipping invalid mutant {mutant.Id}: originalCode not found in file");
                Console.ResetColor();
            }
        }

        return validated;
    }

    /// <summary>
    /// Validate that a mutant's originalCode actually exists in the target file.
    /// </summary>
    private static bool ValidateMutant(Mutant mutant, ChangeSet changeSet)
    {
        if (string.IsNullOrEmpty(mutant.OriginalCode)) return false;
        if (string.IsNullOrEmpty(mutant.MutatedCode)) return false;
        if (mutant.OriginalCode == mutant.MutatedCode) return false;

        var targetFile = changeSet.Files.FirstOrDefault(f =>
            f.FilePath.Equals(mutant.TargetFile, StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.EndsWith(mutant.TargetFile, StringComparison.OrdinalIgnoreCase));

        if (targetFile is null) return false;

        return targetFile.FullFileContent.Contains(mutant.OriginalCode);
    }
}
