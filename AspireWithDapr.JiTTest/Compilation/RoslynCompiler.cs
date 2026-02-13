using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AspireWithDapr.JiTTest.Compilation;

/// <summary>
/// Compiles C# test code in-memory using Roslyn, returning success/failure + diagnostics.
/// </summary>
public class RoslynCompiler
{
    private readonly List<MetadataReference> _references;

    public RoslynCompiler(string? projectBuildOutputPath = null)
    {
        _references = LoadReferences(projectBuildOutputPath);
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

    private static List<MetadataReference> LoadReferences(string? projectBuildOutputPath)
    {
        var refs = new List<MetadataReference>();

        // Add .NET runtime references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeAssemblies = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Threading.Tasks.dll",
            "System.Console.dll",
            "System.ObjectModel.dll",
            "System.Runtime.Extensions.dll",
            "System.ComponentModel.Primitives.dll",
            "System.TimeZoneInfo.dll",
            "System.Globalization.dll",
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

        // Add project references from build output
        if (projectBuildOutputPath is not null && Directory.Exists(projectBuildOutputPath))
        {
            foreach (var dll in Directory.GetFiles(projectBuildOutputPath, "*.dll", SearchOption.AllDirectories))
            {
                // Skip native runtimes and test assemblies
                if (dll.Contains("\\runtimes\\") || dll.Contains("\\ref\\") || dll.EndsWith(".Test.dll"))
                    continue;
                    
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
