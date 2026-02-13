using Microsoft.Extensions.AI;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.LLM;
using AspireWithDapr.JiTTest.Compilation;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Uses an LLM to generate xUnit tests for each mutant, with Roslyn compilation validation.
/// </summary>
public class TestGenerator(IChatClient chatClient, RoslynCompiler compiler, JiTTestConfig config)
{
    public async Task<GeneratedTest> GenerateAsync(Mutant mutant, string originalFileContent)
    {
        var result = new GeneratedTest { ForMutant = mutant };
        var messages = PromptTemplates.GetTestGenerationPrompt(mutant, originalFileContent);

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[TestGen] Generating test for mutant {mutant.Id}...");
            Console.ResetColor();
        }

        var response = await chatClient.GetResponseAsync(messages);
        var testCode = LlmResponseParser.ExtractCSharpCode(response.Text ?? "");
        result.TestCode = testCode;

        // Roslyn compilation check
        var (success, errors) = compiler.Compile(testCode);
        result.CompilationSuccess = success;
        result.CompilationErrors = [.. errors];

        // Retry loop if compilation fails
        for (var retry = 0; retry < config.MaxRetries && !result.CompilationSuccess; retry++)
        {
            result.RetryCount = retry + 1;

            if (config.Verbose)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[TestGen] Compilation failed, retry {result.RetryCount}/{config.MaxRetries}...");
                foreach (var err in errors.Take(5))
                    Console.WriteLine($"  {err}");
                Console.ResetColor();
            }

            var fixMessages = PromptTemplates.GetCompilationFixPrompt(testCode, errors);
            response = await chatClient.GetResponseAsync(fixMessages);
            testCode = LlmResponseParser.ExtractCSharpCode(response.Text ?? "");
            result.TestCode = testCode;

            (success, errors) = compiler.Compile(testCode);
            result.CompilationSuccess = success;
            result.CompilationErrors = [.. errors];
        }

        if (config.Verbose)
        {
            var status = result.CompilationSuccess ? "✅ Compiled" : "❌ Failed";
            Console.ForegroundColor = result.CompilationSuccess ? ConsoleColor.Green : ConsoleColor.Red;
            Console.WriteLine($"[TestGen] {status} (retries: {result.RetryCount})");
            Console.ResetColor();
        }

        return result;
    }
}
