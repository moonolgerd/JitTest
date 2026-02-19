using System.Diagnostics;
using System.Text.RegularExpressions;
using JiTTest.Compilation;
using JiTTest.Models;
using JiTTest.Configuration;

namespace JiTTest.Pipeline;

/// <summary>
/// Executes generated tests against original and mutated code using a transient test project.
/// Uses shadow-copy isolation so multiple executions can run in parallel without
/// destructively mutating the real source files.
/// Automatically detects which project contains the mutant target file and shadow-copies it.
/// </summary>
public class TestExecutor(JiTTestConfig config)
{
    private const int TestTimeoutMs = 60_000;
    private static readonly Lock s_consoleLock = new();

    /// <summary>Directories to skip when shadow-copying a project.</summary>
    private static readonly string[] s_skipDirs = ["bin", "obj", ".git", ".vs", "node_modules"];

    /// <summary>
    /// Cached result of <see cref="RoslynCompiler.DiscoverGlobalUsings"/> so that the
    /// repo filesystem is scanned at most once per <see cref="TestExecutor"/> lifetime,
    /// even under high parallelism.
    /// </summary>
    private readonly Lazy<string[]> _cachedGlobalUsings =
        new(() => RoslynCompiler.DiscoverGlobalUsings(null, config.RepositoryRoot),
            isThreadSafe: true);

    /// <summary>
    /// Path to the pre-restored template test project's <c>obj/</c> directory.
    /// Populated by <see cref="PreRestoreTemplateAsync"/> before Stage 5.
    /// When set, both dotnet-test runs use <c>--no-restore</c>, eliminating per-mutant
    /// NuGet restore overhead.
    /// </summary>
    private string? _preRestoredObjDir;

    public async Task<ExecutionResult> ExecuteAsync(GeneratedTest test)
    {
        var result = new ExecutionResult { GeneratedTest = test };

        if (!test.CompilationSuccess)
        {
            result.ErrorMessage = "Test did not compile — skipping execution.";
            return result;
        }

        // Each execution gets its own unique temp directory for full isolation
        var tempRoot = Path.Combine(config.RepositoryRoot, config.TempDirectory, Guid.NewGuid().ToString("N"));

        try
        {
            // Determine which project contains the mutant target file
            var targetProjectDir = FindProjectForFile(config.RepositoryRoot, test.ForMutant.TargetFile);

            // Set up transient test project with shadow copy of the target project
            var testProjectDir = Path.Combine(tempRoot, "test");
            SetupTransientProject(testProjectDir, test.TestCode, tempRoot, targetProjectDir);

            VerboseLog(ConsoleColor.DarkGray, $"[Exec] {test.ForMutant.Id}: Shadow={Path.GetFileName(targetProjectDir ?? "none")}, " +
                $"TestLines={test.TestCode.Split('\n').Length}");

            // Step 1: Run test against ORIGINAL code → must PASS
            // Skip NuGet restore when a pre-restored obj/ has been stamped in.
            var (exitCode1, output1) = await RunDotnetTest(testProjectDir, skipRestore: _preRestoredObjDir is not null);
            result.OriginalOutput = output1;
            result.PassesOnOriginal = exitCode1 == 0;

            if (!result.PassesOnOriginal)
            {
                result.ErrorMessage = "Test does not pass on original code — not a valid catching test.";
                VerboseLog(ConsoleColor.Red, $"[Exec] ✗ {test.ForMutant.Id}: FAILS on original — skipping",
                    output1.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(3));
                return result;
            }

            // Step 2: Apply mutant to the SHADOW COPY (not the real source)
            var shadowSourcePath = ResolveShadowSourceFile(tempRoot, test.ForMutant.TargetFile);
            if (shadowSourcePath is null)
            {
                result.ErrorMessage = $"Source file not found in shadow copy: {test.ForMutant.TargetFile}";
                VerboseLog(ConsoleColor.Red, $"[Exec] ✗ {test.ForMutant.Id}: Shadow file not found: {test.ForMutant.TargetFile}");
                return result;
            }

            var originalContent = await File.ReadAllTextAsync(shadowSourcePath);
            var mutatedContent = originalContent.Replace(
                test.ForMutant.OriginalCode, test.ForMutant.MutatedCode);

            if (mutatedContent == originalContent)
            {
                result.ErrorMessage = "Mutant patch did not modify the file — originalCode not found.";
                VerboseLog(ConsoleColor.Red, $"[Exec] ✗ {test.ForMutant.Id}: Mutation patch not found in shadow file");
                return result;
            }

            await File.WriteAllTextAsync(shadowSourcePath, mutatedContent);

            VerboseLog(ConsoleColor.DarkGray,
                $"[Exec] {test.ForMutant.Id}: Mutating {Path.GetFileName(shadowSourcePath)}: " +
                $"'{Truncate(test.ForMutant.OriginalCode, 40)}' → '{Truncate(test.ForMutant.MutatedCode, 40)}'");

            // Step 3: Run test against MUTATED code → must FAIL
            var (exitCode2, output2) = await RunDotnetTest(testProjectDir, skipRestore: true);
            result.MutantOutput = output2;
            result.FailsOnMutant = exitCode2 != 0;

            if (result.IsCandidateCatch)
            {
                VerboseLog(ConsoleColor.Magenta, $"[Exec] 🎯 CATCH {test.ForMutant.Id} (original: PASS, mutant: FAIL)");
            }
            else
            {
                VerboseLog(ConsoleColor.Gray, $"[Exec] ⚪ No catch {test.ForMutant.Id} (original: PASS, mutant: PASS)",
                    output2.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(2));
            }
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Execution error: {ex.Message}";
            VerboseLog(ConsoleColor.Red, $"[Exec] ✗ {test.ForMutant.Id}: Exception: {ex.Message}");
        }
        finally
        {
            // Cleanup entire temp root for this execution
            CleanupDirectory(tempRoot);
        }

        return result;
    }

    /// <summary>Thread-safe verbose logging to prevent interleaved output from parallel executions.</summary>
    private void VerboseLog(ConsoleColor color, string message, IEnumerable<string>? detail = null)
    {
        if (!config.Verbose) return;
        lock (s_consoleLock)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            if (detail is not null)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                foreach (var line in detail)
                    Console.WriteLine($"    {line.TrimEnd()}");
            }
            Console.ResetColor();
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    /// <summary>
    /// Creates a shadow copy of the target project and sets up the transient test project.
    /// Fixes relative ProjectReferences in the shadow csproj to point back to the real repo.
    /// </summary>
    private void SetupTransientProject(string testProjectDir, string testCode, string tempRoot, string? targetProjectDir)
    {
        Directory.CreateDirectory(testProjectDir);

        // Shadow-copy the target project so we can safely mutate files
        var shadowProjectDir = Path.Combine(tempRoot, "shadow");
        string shadowCsproj;

        if (targetProjectDir is not null && Directory.Exists(targetProjectDir))
        {
            CopyDirectoryFast(targetProjectDir, shadowProjectDir);
            shadowCsproj = FindCsprojInShadow(shadowProjectDir);

            // Fix relative ProjectReferences in the shadow csproj to be absolute
            // (since the shadow is in a temp dir, relative paths would break)
            FixProjectReferences(shadowCsproj, targetProjectDir);
        }
        else
        {
            // Fallback: reference cannot be found, test may fail
            shadowCsproj = Path.GetFullPath(Path.Combine(config.RepositoryRoot, "target.csproj"));
        }

        // Transient test project with NSubstitute for mocking
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
                <PackageReference Include="NSubstitute" Version="5.3.0" />
              </ItemGroup>
              <ItemGroup>
                <ProjectReference Include="{shadowCsproj}" />
              </ItemGroup>
            </Project>
            """;

        File.WriteAllText(Path.Combine(testProjectDir, "JiTTest.Temp.csproj"), csproj);
        File.WriteAllText(Path.Combine(testProjectDir, "CatchingTest.cs"), testCode);

        // Copy the pre-restored obj/ so dotnet test can skip NuGet restore.
        if (_preRestoredObjDir is not null && Directory.Exists(_preRestoredObjDir))
            CopyDirectoryAll(_preRestoredObjDir, Path.Combine(testProjectDir, "obj"));

        // Write project-specific global usings so dotnet test sees the same namespaces
        // as the Roslyn in-memory compilation check.
        WriteProjectGlobalUsings(testProjectDir);
    }

    /// <summary>
    /// Creates a template test project with the same NuGet packages as every transient test project
    /// and runs <c>dotnet restore</c> on it exactly once. The resulting <c>obj/</c> directory is
    /// stamped into every subsequent per-mutant test project so that both <c>dotnet test</c> runs
    /// can use <c>--no-restore</c>, saving 3–10 s per mutant.
    /// Must be called before any <see cref="ExecuteAsync"/> calls.
    /// </summary>
    public async Task PreRestoreTemplateAsync()
    {
        var templateDir = Path.Combine(config.RepositoryRoot, config.TempDirectory, "_template", "test");
        try
        {
            Directory.CreateDirectory(templateDir);

            // Same NuGet packages as the transient test project; same project file name so that
            // the generated JiTTest.Temp.csproj.nuget.g.props/targets filenames match exactly.
            var csproj = """
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
                    <PackageReference Include="NSubstitute" Version="5.3.0" />
                  </ItemGroup>
                </Project>
                """;

            File.WriteAllText(Path.Combine(templateDir, "JiTTest.Temp.csproj"), csproj);

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = templateDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var completed = await Task.Run(() => process.WaitForExit(60_000));
            if (completed && process.ExitCode == 0)
            {
                var objDir = Path.Combine(templateDir, "obj");
                if (Directory.Exists(objDir))
                {
                    _preRestoredObjDir = objDir;
                    VerboseLog(ConsoleColor.DarkGray,
                        "[Exec] Template project pre-restored — both dotnet test runs will use --no-restore");
                }
            }
            else
            {
                VerboseLog(ConsoleColor.Yellow,
                    "[Exec] Template pre-restore failed — first dotnet test run will restore normally");
            }
        }
        catch (Exception ex)
        {
            VerboseLog(ConsoleColor.Yellow, $"[Exec] Template pre-restore exception: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes a GlobalUsings.cs to the transient test project containing any project-specific
    /// global usings discovered in the repo (i.e., those beyond the SDK implicit defaults).
    /// This aligns dotnet test compilation with the in-memory Roslyn check.
    /// </summary>
    private void WriteProjectGlobalUsings(string testProjectDir)
    {
        try
        {
            var discovered = _cachedGlobalUsings.Value;

            // SDK implicit usings are already handled by <ImplicitUsings>enable</ImplicitUsings>
            // so we only need to write any project-specific additions on top.
            var sdkDefaults = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "global using global::System;",
                "global using global::System.Collections.Generic;",
                "global using global::System.IO;",
                "global using global::System.Linq;",
                "global using global::System.Net.Http;",
                "global using global::System.Threading;",
                "global using global::System.Threading.Tasks;",
            };

            var projectSpecific = discovered.Where(u => !sdkDefaults.Contains(u)).ToArray();
            if (projectSpecific.Length == 0) return;

            var content = "// Auto-generated by JiTTest — project-specific global usings\n" +
                          string.Join("\n", projectSpecific) + "\n";
            File.WriteAllText(Path.Combine(testProjectDir, "GlobalUsings.cs"), content);
        }
        catch { /* best effort — don't fail the test run */ }
    }

    /// <summary>
    /// Fix relative ProjectReferences in a shadow-copied .csproj to use absolute paths
    /// pointing back to the real projects in the repo.
    /// </summary>
    private static void FixProjectReferences(string shadowCsprojPath, string originalProjectDir)
    {
        if (!File.Exists(shadowCsprojPath)) return;

        var content = File.ReadAllText(shadowCsprojPath);
        var original = content;

        // Match ProjectReference Include attributes with relative paths
        content = Regex.Replace(content,
            @"<ProjectReference\s+Include=""([^""]+)""",
            match =>
            {
                var relativePath = match.Groups[1].Value;
                // Resolve relative to the ORIGINAL project directory (not the shadow copy)
                var absolutePath = Path.GetFullPath(Path.Combine(originalProjectDir, relativePath));
                return $"<ProjectReference Include=\"{absolutePath}\"";
            });

        if (content != original)
            File.WriteAllText(shadowCsprojPath, content);
    }

    /// <summary>
    /// Find which project directory contains the mutant's target file.
    /// Walks up from the target file to find the nearest .csproj.
    /// </summary>
    private static string? FindProjectForFile(string repoRoot, string targetFile)
    {
        // Build the full path
        var fullPath = Path.Combine(repoRoot, targetFile);
        var dir = Path.GetDirectoryName(fullPath);

        // Walk up directories looking for a .csproj
        while (dir is not null && dir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            var csprojFiles = Directory.GetFiles(dir, "*.csproj");
            if (csprojFiles.Length > 0)
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fallback: try to find by filename match
        var fileName = Path.GetFileName(targetFile);
        try
        {
            var matches = Directory.GetFiles(repoRoot, fileName, SearchOption.AllDirectories)
                .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\"))
                .ToArray();

            if (matches.Length > 0)
            {
                dir = Path.GetDirectoryName(matches[0]);
                while (dir is not null && dir.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
                {
                    var csprojFiles = Directory.GetFiles(dir, "*.csproj");
                    if (csprojFiles.Length > 0)
                        return dir;
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
        }
        catch { /* best effort */ }

        return null;
    }

    /// <summary>
    /// Plain recursive directory copy with no exclusions — used for copying pre-restored obj/ contents.
    /// </summary>
    private static void CopyDirectoryAll(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectoryAll(dir, Path.Combine(destDir, Path.GetFileName(dir)));
    }

    /// <summary>
    /// Fast recursive directory copy that skips bin, obj, .git, etc.
    /// </summary>
    private static void CopyDirectoryFast(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        // Copy files in current directory
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
        }

        // Recurse into subdirectories, skipping large/irrelevant ones
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (s_skipDirs.Contains(dirName, StringComparer.OrdinalIgnoreCase))
                continue;

            CopyDirectoryFast(dir, Path.Combine(destDir, dirName));
        }
    }

    /// <summary>Find the first .csproj in the shadow directory.</summary>
    private static string FindCsprojInShadow(string shadowDir)
    {
        var csprojFiles = Directory.GetFiles(shadowDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojFiles.Length > 0)
            return Path.GetFullPath(csprojFiles[0]);

        // Search one level deep
        csprojFiles = Directory.GetFiles(shadowDir, "*.csproj", SearchOption.AllDirectories);
        return csprojFiles.Length > 0
            ? Path.GetFullPath(csprojFiles[0])
            : Path.Combine(shadowDir, "Project.csproj"); // Fallback — will fail to build
    }

    /// <summary>Resolve a target file path within the shadow copy directory.</summary>
    private static string? ResolveShadowSourceFile(string tempRoot, string targetFile)
    {
        var shadowDir = Path.Combine(tempRoot, "shadow");
        if (!Directory.Exists(shadowDir)) return null;

        // Try combining shadow root with the relative target path
        var fileName = Path.GetFileName(targetFile);
        var candidates = Directory.GetFiles(shadowDir, fileName, SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("obj", "")) && !f.Contains(Path.Combine("bin", "")))
            .ToArray();

        if (candidates.Length == 1) return candidates[0];

        // If multiple matches, try to match by the relative path suffix
        if (candidates.Length > 1)
        {
            var normalized = targetFile.Replace('/', Path.DirectorySeparatorChar);
            var match = candidates.FirstOrDefault(c => c.EndsWith(normalized, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }

        return candidates.FirstOrDefault();
    }

    private static async Task<(int ExitCode, string Output)> RunDotnetTest(string projectDir, bool skipRestore = false)
    {
        var args = skipRestore ? "test --no-restore --verbosity quiet" : "test --verbosity quiet";
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
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
            return (-1, output + "\n[TIMEOUT] Test execution exceeded timeout.");
        }

        return (process.ExitCode, output.ToString());
    }

    private static void CleanupDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { /* best effort */ }
    }
}
