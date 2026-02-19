using Microsoft.Extensions.AI;
using JiTTest.Models;
using JiTTest.LLM;
using JiTTest.Compilation;
using JiTTest.Configuration;

namespace JiTTest.Pipeline;

/// <summary>
/// Uses an LLM to generate xUnit tests for each mutant, with Roslyn compilation validation.
/// </summary>
public class TestGenerator(IChatClient chatClient, RoslynCompiler compiler, JiTTestConfig config)
{
    public async Task<GeneratedTest> GenerateAsync(Mutant mutant, string originalFileContent)
    {
        var result = new GeneratedTest { ForMutant = mutant };
        
        // Extract using directives from the original source file
        var sourceUsings = RoslynCompiler.ExtractUsingDirectives(originalFileContent);

        // Automatically inject the source file's namespace as a required using so the LLM
        // always includes it (e.g. "using MyProject.Models;" when testing WeatherForecast.cs)
        var sourceNamespace = RoslynCompiler.ExtractNamespace(originalFileContent);
        if (!string.IsNullOrEmpty(sourceNamespace))
        {
            var namespaceUsing = $"using {sourceNamespace};";
            if (!sourceUsings.Any(u => u.Equals(namespaceUsing, StringComparison.OrdinalIgnoreCase)))
                sourceUsings.Insert(0, namespaceUsing);
        }

        var globalUsings = compiler.GlobalUsingNamespaces;
        var messages = PromptTemplates.GetTestGenerationPrompt(mutant, originalFileContent, sourceUsings, globalUsings);

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"[TestGen] Generating test for mutant {mutant.Id}...");
            if (sourceNamespace is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[TestGen] Source namespace: {sourceNamespace}");
            }
            if (sourceUsings.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[TestGen] Extracted {sourceUsings.Count} using directive(s) from source");
            }
            Console.ResetColor();
        }

        var response = await chatClient.GetResponseAsync(messages);
        var testCode = LlmResponseParser.ExtractCSharpCode(response.Text ?? "");
        result.TestCode = testCode;

        if (config.Verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[TestGen] Generated test code for {mutant.Id}:");
            Console.WriteLine(new string('─', 60));
            Console.WriteLine(testCode);
            Console.WriteLine(new string('─', 60));
            Console.ResetColor();
        }

        // Roslyn compilation check — auto-fix missing usings first
        var (success, errors) = compiler.Compile(testCode);
        if (!success)
        {
            var (fixedCode, wasFixed) = compiler.AutoFixUsings(testCode, errors, sourceUsings);
            if (wasFixed)
            {
                testCode = fixedCode;
                result.TestCode = testCode;
                (success, errors) = compiler.Compile(testCode);
                
                if (config.Verbose)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine($"[TestGen] Auto-fixed usings for {mutant.Id}");
                    Console.ResetColor();
                }
            }
        }
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

            // On the final retry, escalate: regenerate from scratch with prior error context
            // rather than asking the fixer to patch an already-broken file.
            List<ChatMessage> fixMessages;
            if (retry == config.MaxRetries - 1 && config.MaxRetries > 1)
            {
                var priorErrors = string.Join(", ", errors.Take(3).Select(e => e.Split('(')[0].Trim()));
                var escalationHint = $"Your previous attempt failed to compile ({priorErrors}). " +
                    "Rewrite the test from scratch using only public API. " +
                    (string.IsNullOrEmpty(mutant.AccessibilityHint) ? "" : mutant.AccessibilityHint + " ") +
                    "Return ONLY complete C# code with no markdown fences.";
                var escalatedMessages = PromptTemplates.GetTestGenerationPrompt(
                    mutant, originalFileContent, sourceUsings, globalUsings);
                escalatedMessages.Add(new(Microsoft.Extensions.AI.ChatRole.User, escalationHint));
                fixMessages = escalatedMessages;
            }
            else
            {
                fixMessages = PromptTemplates.GetCompilationFixPrompt(testCode, errors, mutant);
            }
            response = await chatClient.GetResponseAsync(fixMessages);
            testCode = LlmResponseParser.ExtractCSharpCode(response.Text ?? "");

            if (config.Verbose)
            {
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.WriteLine($"[TestGen] Retry {result.RetryCount} generated code:");
                Console.WriteLine(new string('─', 60));
                Console.WriteLine(testCode);
                Console.WriteLine(new string('─', 60));
                Console.ResetColor();
            }

            // Auto-fix missing usings on retry output too
            var (retrySuccess, retryErrors) = compiler.Compile(testCode);
            if (!retrySuccess)
            {
                var (fixedCode, wasFixed) = compiler.AutoFixUsings(testCode, retryErrors, sourceUsings);
                if (wasFixed)
                {
                    testCode = fixedCode;
                    (retrySuccess, retryErrors) = compiler.Compile(testCode);
                }
            }

            result.TestCode = testCode;
            success = retrySuccess;
            errors = retryErrors;
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
