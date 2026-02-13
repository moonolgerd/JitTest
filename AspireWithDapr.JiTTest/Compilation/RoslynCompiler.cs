using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Text.RegularExpressions;

namespace AspireWithDapr.JiTTest.Compilation;

/// <summary>
/// Compiles C# test code in-memory using Roslyn, returning success/failure + diagnostics.
/// Supports multiple build output paths to gather references from all target projects.
/// Includes auto-fix for common missing using directives.
/// </summary>
public class RoslynCompiler
{
    private readonly List<MetadataReference> _references;

    /// <summary>
    /// Maps type/identifier names (from CS0103/CS0246 errors) to the using directive they need.
    /// </summary>
    private static readonly Dictionary<string, string> s_usingMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Substitute"] = "using NSubstitute;",
        ["Arg"] = "using NSubstitute;",
        ["SubstituteExtensions"] = "using NSubstitute;",
        ["NullLogger"] = "using Microsoft.Extensions.Logging.Abstractions;",
        ["NullLoggerFactory"] = "using Microsoft.Extensions.Logging.Abstractions;",
        ["ILogger"] = "using Microsoft.Extensions.Logging;",
        ["ILogger<>"] = "using Microsoft.Extensions.Logging;",
        ["ILoggerFactory"] = "using Microsoft.Extensions.Logging;",
        ["LogLevel"] = "using Microsoft.Extensions.Logging;",
        ["DaprClient"] = "using Dapr.Client;",
        ["IServiceCollection"] = "using Microsoft.Extensions.DependencyInjection;",
        ["IServiceProvider"] = "using Microsoft.Extensions.DependencyInjection;",
        ["IHost"] = "using Microsoft.Extensions.Hosting;",
        ["BackgroundService"] = "using Microsoft.Extensions.Hosting;",
        ["WeatherForecast"] = "using AspireWithDapr.Shared;",
        ["WeatherUtilities"] = "using AspireWithDapr.Shared;",
        ["SharedCollections"] = "using AspireWithDapr.Shared;",
        ["SharedConstants"] = "using AspireWithDapr.Shared;",
        ["CancellationToken"] = "using System.Threading;",
        ["CancellationTokenSource"] = "using System.Threading;",
        ["Random"] = "using System;",
        ["TimeZoneInfo"] = "using System;",
        ["DateTime"] = "using System;",
        ["DateTimeOffset"] = "using System;",
        ["TimeSpan"] = "using System;",
        ["Guid"] = "using System;",
        ["Math"] = "using System;",
        ["Task"] = "using System.Threading.Tasks;",
        ["ArgumentNullException"] = "using System;",
        ["ArgumentException"] = "using System;",
        ["ArgumentOutOfRangeException"] = "using System;",
        ["InvalidOperationException"] = "using System;",
        ["List"] = "using System.Collections.Generic;",
        ["Dictionary"] = "using System.Collections.Generic;",
        ["Enumerable"] = "using System.Linq;",
        ["Contains"] = "using System.Linq;",
        ["Select"] = "using System.Linq;",
        ["Where"] = "using System.Linq;",
        ["ToList"] = "using System.Linq;",
        ["Any"] = "using System.Linq;",
        ["All"] = "using System.Linq;",
        ["First"] = "using System.Linq;",
        ["FirstOrDefault"] = "using System.Linq;",
        ["Count"] = "using System.Linq;",
    };

    public RoslynCompiler(string[]? projectBuildOutputPaths = null, bool verbose = false)
    {
        _references = LoadReferences(projectBuildOutputPaths, verbose);
    }

    /// <summary>
    /// Compile the given C# source code. Returns (success, errors).
    /// </summary>
    public (bool Success, string[] Errors) Compile(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        var compilation = CSharpCompilation.Create(
            assemblyName: $"JiTTest_{Guid.NewGuid():N}",
            syntaxTrees: [syntaxTree],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (result.Success) return (true, []);

        var errors = result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Id}: {d.GetMessage()} (Line {d.Location.GetLineSpan().StartLinePosition.Line + 1})")
            .ToArray();

        return (false, errors);
    }

    /// <summary>
    /// Attempt to auto-fix missing using directives based on CS0103/CS0246 errors.
    /// Returns the (possibly patched) source code and whether any fixes were applied.
    /// </summary>
    public (string FixedCode, bool WasFixed) AutoFixUsings(string sourceCode, string[] errors)
    {
        var missingUsings = new HashSet<string>();
        var codeModified = false;

        foreach (var error in errors)
        {
            // CS0103: The name 'X' does not exist in the current context
            // CS0246: The type or namespace name 'X' could not be found
            var match = Regex.Match(error, @"CS0(?:103|246):.*?'([^']+)'");
            if (match.Success)
            {
                var typeName = match.Groups[1].Value;
                if (s_usingMap.TryGetValue(typeName, out var usingDirective))
                {
                    if (!sourceCode.Contains(usingDirective, StringComparison.OrdinalIgnoreCase))
                        missingUsings.Add(usingDirective);
                }
            }

            // CS0122: 'Type.Method()' is inaccessible due to its protection level
            // Common case: LLM calls ExecuteAsync directly on BackgroundService
            // Fix: Replace .ExecuteAsync( with .StartAsync(
            if (error.Contains("CS0122") && error.Contains("ExecuteAsync"))
            {
                sourceCode = Regex.Replace(sourceCode,
                    @"\.ExecuteAsync\s*\(",
                    ".StartAsync(");
                codeModified = true;
            }

            // CS1674: type used in a using statement must implement 'System.IDisposable'
            // Fix: Remove the 'using' keyword from the variable declaration
            if (error.Contains("CS1674"))
            {
                // Transform: using var x = new Foo(); → var x = new Foo();
                // Transform: using (var x = new Foo()) { ... } → { var x = new Foo(); ... }
                sourceCode = Regex.Replace(sourceCode,
                    @"\busing\s+(var\s+\w+\s*=)",
                    "$1");
                sourceCode = Regex.Replace(sourceCode,
                    @"\busing\s*\((var\s+\w+\s*=.*?)\)\s*\{",
                    "{ $1;");
                codeModified = true;
            }

            // CS1929: 'string[]' does not contain a definition for 'Contains' and the best
            // extension method overload 'MemoryExtensions.Contains<string>(Span<string>, string)'
            // Fix: array.Contains(x) → array.AsEnumerable().Contains(x)
            if (error.Contains("CS1929") && error.Contains("Contains") && error.Contains("MemoryExtensions"))
            {
                // Replace patterns like: someArray.Contains( → someArray.AsEnumerable().Contains(
                // We look for identifier.Contains( where the identifier is likely an array
                sourceCode = Regex.Replace(sourceCode,
                    @"(\b\w+)\.Contains\(",
                    match2 =>
                    {
                        // Only fix if it references a known array-like variable or field
                        return $"{match2.Groups[1].Value}.AsEnumerable().Contains(";
                    });
                if (!sourceCode.Contains("using System.Linq;", StringComparison.OrdinalIgnoreCase))
                    missingUsings.Add("using System.Linq;");
                codeModified = true;
            }
        }

        if (missingUsings.Count == 0 && !codeModified)
            return (sourceCode, false);

        // Insert usings at the correct position:
        // - Before any file-scoped namespace or namespace declaration
        // - After existing using directives
        // This avoids CS1529 (using must precede other elements)
        var lines = sourceCode.Split('\n').ToList();
        var insertIndex = 0;

        // Find the last existing 'using' line that comes before any namespace or class
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("using ") && !trimmed.StartsWith("using ("))
            {
                insertIndex = i + 1;
            }
            else if (trimmed.StartsWith("namespace ") || trimmed.StartsWith("public ") ||
                     trimmed.StartsWith("internal ") || trimmed.StartsWith("[assembly"))
            {
                break;
            }
        }

        // If no usings found, insert at the very top
        var usingLines = string.Join("\n", missingUsings);
        lines.Insert(insertIndex, usingLines);
        return (string.Join("\n", lines), true);
    }

    private static List<MetadataReference> LoadReferences(string[]? projectBuildOutputPaths, bool verbose)
    {
        var refs = new List<MetadataReference>(); 

        // Add .NET runtime references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Collections.Concurrent.dll",
            "System.Linq.dll",
            "System.Threading.Tasks.dll",
            "System.Threading.dll",
            "System.Console.dll",
            "System.ObjectModel.dll",
            "System.Runtime.Extensions.dll",
            "System.ComponentModel.Primitives.dll",
            "System.ComponentModel.dll",
            "System.TimeZoneInfo.dll",
            "System.Globalization.dll",
            "System.Text.RegularExpressions.dll",
            "System.Linq.Expressions.dll",
            "System.Collections.Immutable.dll",
            "System.Memory.dll",
            "netstandard.dll",
            "mscorlib.dll",
            "System.Private.CoreLib.dll"
        };

        foreach (var asm in runtimeAssemblies)
        {
            var path = Path.Combine(runtimeDir, asm);
            if (File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        // Add xunit references
        AddAssemblyByType(refs, typeof(Xunit.Assert));
        AddAssemblyByType(refs, typeof(Xunit.FactAttribute));

        // Add common System types
        AddAssemblyByType(refs, typeof(TimeZoneInfo));
        AddAssemblyByType(refs, typeof(DateTime));
        AddAssemblyByType(refs, typeof(Uri));

        // Add NSubstitute mocking framework so LLM-generated tests can mock interfaces
        AddAssemblyByType(refs, typeof(NSubstitute.Substitute));
        AddAssemblyByType(refs, typeof(NSubstitute.SubstituteExtensions));

        // Add DI/logging abstractions so tests can create NullLogger, mock IServiceProvider etc.
        AddAssemblyByType(refs, typeof(Microsoft.Extensions.Logging.ILogger));
        AddAssemblyByType(refs, typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger));
        AddAssemblyByType(refs, typeof(Microsoft.Extensions.DependencyInjection.IServiceCollection));
        AddAssemblyByType(refs, typeof(Microsoft.Extensions.Hosting.IHost));
        AddAssemblyByType(refs, typeof(Microsoft.Extensions.Hosting.BackgroundService));

        // Add project references from ALL build output paths (deduplicated by assembly filename)
        var loadedAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Track already-loaded assembly filenames to avoid duplicates
        foreach (var r in refs)
        {
            if (r is PortableExecutableReference pe && pe.FilePath is not null)
                loadedAssemblyNames.Add(Path.GetFileName(pe.FilePath));
        }

        foreach (var buildOutputPath in projectBuildOutputPaths ?? [])
        {
            if (!Directory.Exists(buildOutputPath)) continue;

            foreach (var dll in Directory.GetFiles(buildOutputPath, "*.dll", SearchOption.AllDirectories))
            {
                // Skip native runtimes, ref assemblies, test assemblies, and localization satellites
                if (dll.Contains("\\runtimes\\") || dll.Contains("\\ref\\") ||
                    dll.EndsWith(".Test.dll") || dll.EndsWith(".resources.dll"))
                    continue;

                var fileName = Path.GetFileName(dll);
                if (!loadedAssemblyNames.Add(fileName))
                    continue; // Already loaded an assembly with this name

                try
                {
                    refs.Add(MetadataReference.CreateFromFile(dll));
                }
                catch
                {
                    // Skip problematic DLLs
                }
            }
        }

        if (verbose)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Roslyn] Loaded {refs.Count} assembly references");
            var typeRefs = new[] { "NSubstitute", "Logging", "DependencyInjection", "Hosting" };
            foreach (var r in refs.OfType<PortableExecutableReference>())
            {
                var name = Path.GetFileName(r.FilePath ?? "");
                if (typeRefs.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"    ✓ {name}");
            }
            Console.ResetColor();
        }

        return refs;
    }

    private static void AddAssemblyByType(List<MetadataReference> refs, Type type)
    {
        var location = type.Assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            refs.Add(MetadataReference.CreateFromFile(location));
        }
    }
}
