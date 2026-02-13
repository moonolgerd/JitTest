using System.Diagnostics;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Executes generated tests against original and mutated code using a transient test project.
/// </summary>
public class TestExecutor(JiTTestConfig config)
{
    private const int TestTimeoutMs = 30_000;

    public async Task<ExecutionResult> ExecuteAsync(GeneratedTest test)
    {
        var result = new ExecutionResult { GeneratedTest = test };

        if (!test.CompilationSuccess)
        {
            result.ErrorMessage = "Test did not compile — skipping execution.";
            return result;
        }

        var tempDir = Path.Combine(config.RepositoryRoot, config.TempDirectory, Guid.NewGuid().ToString("N"));

        try
        {
            // Set up transient test project
            SetupTransientProject(tempDir, test.TestCode);

            // Step 1: Run test against ORIGINAL code → must PASS
            var (exitCode1, output1) = await RunDotnetTest(tempDir);
            result.OriginalOutput = output1;
            result.PassesOnOriginal = exitCode1 == 0;

            if (!result.PassesOnOriginal)
            {
                result.ErrorMessage = "Test does not pass on original code — not a valid catching test.";
                return result;
            }

            // Step 2: Apply mutant to source file
            var sourceFilePath = ResolveSourceFile(test.ForMutant.TargetFile);
            if (sourceFilePath is null)
            {
                result.ErrorMessage = $"Source file not found: {test.ForMutant.TargetFile}";
                return result;
            }

            var originalContent = await File.ReadAllTextAsync(sourceFilePath);
            try
            {
                var mutatedContent = originalContent.Replace(
                    test.ForMutant.OriginalCode, test.ForMutant.MutatedCode);

                if (mutatedContent == originalContent)
                {
                    result.ErrorMessage = "Mutant patch did not modify the file — originalCode not found.";
                    return result;
                }

                await File.WriteAllTextAsync(sourceFilePath, mutatedContent);

                // Step 3: Run test against MUTATED code → must FAIL
                var (exitCode2, output2) = await RunDotnetTest(tempDir);
                result.MutantOutput = output2;
                result.FailsOnMutant = exitCode2 != 0;
            }
            finally
            {
                // Always revert the mutation
                await File.WriteAllTextAsync(sourceFilePath, originalContent);
            }

            if (config.Verbose)
            {
                var status = result.IsCandidateCatch ? "🎯 CATCH" : "⚪ No catch";
                Console.ForegroundColor = result.IsCandidateCatch ? ConsoleColor.Magenta : ConsoleColor.Gray;
                Console.WriteLine($"[Exec] {status} for mutant {test.ForMutant.Id} " +
                    $"(original: {(result.PassesOnOriginal ? "PASS" : "FAIL")}, " +
                    $"mutant: {(result.FailsOnMutant ? "FAIL" : "PASS")})");
                Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Execution error: {ex.Message}";
        }
        finally
        {
            // Cleanup temp directory
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort */ }
        }

        return result;
    }

    private void SetupTransientProject(string tempDir, string testCode)
    {
        Directory.CreateDirectory(tempDir);

        // Create a minimal test project
        var csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{Path.GetFullPath(Path.Combine(config.RepositoryRoot, "AspireWithDapr.Shared", "AspireWithDapr.Shared.csproj"))}" />
              </ItemGroup>
            </Project>
            """;

        File.WriteAllText(Path.Combine(tempDir, "JiTTest.Temp.csproj"), csproj);
        File.WriteAllText(Path.Combine(tempDir, "CatchingTest.cs"), testCode);
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetTest(string projectDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test --no-restore --verbosity quiet",
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var output = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var completed = await Task.Run(() => process.WaitForExit(TestTimeoutMs));
        if (!completed)
        {
            process.Kill(entireProcessTree: true);
            return (-1, output + "\n[TIMEOUT] Test execution exceeded 30 seconds.");
        }

        return (process.ExitCode, output.ToString());
    }

    private string? ResolveSourceFile(string targetFile)
    {
        // Try direct path
        var fullPath = Path.Combine(config.RepositoryRoot, targetFile);
        if (File.Exists(fullPath)) return fullPath;

        // Try finding by filename
        var fileName = Path.GetFileName(targetFile);
        var candidates = Directory.GetFiles(config.RepositoryRoot, fileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")))
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }
}
