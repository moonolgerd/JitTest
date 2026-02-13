using Microsoft.Extensions.AI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AspireWithDapr.JiTTest.Models;
using AspireWithDapr.JiTTest.LLM;
using AspireWithDapr.JiTTest.Configuration;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Uses an LLM to generate realistic code mutants based on intent and diff.
/// Validates mutants exist in the file and annotates accessibility info so
/// the test generator knows whether the code is directly testable.
/// </summary>
public class MutantGenerator(IChatClient chatClient, JiTTestConfig config)
{
    public async Task<List<Mutant>> GenerateAsync(IntentSummary intent, ChangeSet changeSet)
    {
        // Build accessibility map so the LLM knows what's public/private BEFORE generating
        var accessibilityMap = BuildAccessibilityMap(changeSet);
        var messages = PromptTemplates.GetMutantGenerationPrompt(intent, changeSet, accessibilityMap);

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
                // Annotate accessibility info so the test generator prompt can guide the LLM
                AnnotateAccessibility(mutant, changeSet);
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

    /// <summary>
    /// Build a human-readable accessibility map for all changed files using Roslyn.
    /// Shows each member and whether it is public/protected/private so the LLM
    /// can target only testable (public) code.
    /// </summary>
    private static string BuildAccessibilityMap(ChangeSet changeSet)
    {
        var parts = new List<string>();

        foreach (var file in changeSet.Files)
        {
            if (string.IsNullOrEmpty(file.FullFileContent)) continue;

            try
            {
                var tree = CSharpSyntaxTree.ParseText(file.FullFileContent);
                var root = tree.GetRoot();
                var lines = new List<string> { $"### {file.FilePath}" };

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var typeVis = GetVisibility(typeDecl.Modifiers);
                    var typeKind = typeDecl switch
                    {
                        RecordDeclarationSyntax => "record",
                        ClassDeclarationSyntax => "class",
                        StructDeclarationSyntax => "struct",
                        _ => "type"
                    };
                    lines.Add($"  {typeVis} {typeKind} {typeDecl.Identifier.Text}");

                    foreach (var member in typeDecl.Members)
                    {
                        switch (member)
                        {
                            case MethodDeclarationSyntax m:
                                var mVis = GetVisibility(m.Modifiers);
                                var mStatic = m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) ? "static " : "";
                                lines.Add($"    {mVis} {mStatic}{m.Identifier.Text}() \u2192 {(mVis == "public" ? "TESTABLE" : "INDIRECT (test via public API)")}");
                                break;
                            case PropertyDeclarationSyntax p:
                                var pVis = GetVisibility(p.Modifiers);
                                lines.Add($"    {pVis} {p.Identifier.Text} \u2192 {(pVis == "public" ? "TESTABLE" : "INDIRECT (test via public API)")}");
                                break;
                            case FieldDeclarationSyntax f:
                                var fVis = GetVisibility(f.Modifiers);
                                var fStatic = f.Modifiers.Any(mod => mod.IsKind(SyntaxKind.StaticKeyword)) ? "static " : "";
                                var fReadonly = f.Modifiers.Any(mod => mod.IsKind(SyntaxKind.ReadOnlyKeyword)) ? "readonly " : "";
                                var fName = f.Declaration.Variables.FirstOrDefault()?.Identifier.Text ?? "?";
                                var fTestable = fVis == "public" ? "TESTABLE (read-only)" : "INDIRECT (via public API)";
                                lines.Add($"    {fVis} {fStatic}{fReadonly}{fName} → {fTestable}");
                                break;
                        }
                    }
                }

                if (lines.Count > 1)
                    parts.Add(string.Join("\n", lines));
            }
            catch
            {
                // Best-effort — skip files that don't parse
            }
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : "";
    }

    private static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))) return "public";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return "protected";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword))) return "internal";
        return "private";
    }

    /// <summary>
    /// Parse the source file with Roslyn to annotate the mutant with info about
    /// which method contains the original code and whether it is publicly accessible.
    /// This info is passed to the test generation prompt to guide the LLM.
    /// </summary>
    private static void AnnotateAccessibility(Mutant mutant, ChangeSet changeSet)
    {
        var targetFile = changeSet.Files.FirstOrDefault(f =>
            f.FilePath.Equals(mutant.TargetFile, StringComparison.OrdinalIgnoreCase) ||
            f.FilePath.EndsWith(mutant.TargetFile, StringComparison.OrdinalIgnoreCase));

        if (targetFile is null || string.IsNullOrEmpty(targetFile.FullFileContent)) return;

        try
        {
            var tree = CSharpSyntaxTree.ParseText(targetFile.FullFileContent);
            var root = tree.GetRoot();

            // Find the position of the originalCode in the source
            var pos = targetFile.FullFileContent.IndexOf(mutant.OriginalCode, StringComparison.Ordinal);
            if (pos < 0) return;

            // Walk up the syntax tree to find the enclosing method/property
            var node = root.FindNode(new Microsoft.CodeAnalysis.Text.TextSpan(pos, mutant.OriginalCode.Length));

            while (node is not null)
            {
                if (node is MethodDeclarationSyntax method)
                {
                    mutant.ContainingMember = method.Identifier.Text;
                    mutant.ContainingMemberIsPublic = method.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.PublicKeyword));
                    mutant.ContainingMemberIsProtected = method.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.ProtectedKeyword));
                    mutant.ContainingMemberIsPrivate = method.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.PrivateKeyword)) ||
                        !method.Modifiers.Any(m =>
                            m.IsKind(SyntaxKind.PublicKeyword) ||
                            m.IsKind(SyntaxKind.ProtectedKeyword) ||
                            m.IsKind(SyntaxKind.InternalKeyword));
                    break;
                }

                if (node is PropertyDeclarationSyntax prop)
                {
                    mutant.ContainingMember = prop.Identifier.Text;
                    mutant.ContainingMemberIsPublic = prop.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.PublicKeyword));
                    break;
                }

                // Static fields/collections — always publicly accessible via class name
                if (node is FieldDeclarationSyntax field)
                {
                    mutant.ContainingMember = field.Declaration.Variables.FirstOrDefault()?.Identifier.Text;
                    mutant.ContainingMemberIsPublic = field.Modifiers.Any(m =>
                        m.IsKind(SyntaxKind.PublicKeyword));
                    break;
                }

                node = node.Parent;
            }
        }
        catch
        {
            // Best-effort — don't fail mutant generation over parse errors
        }
    }
}
