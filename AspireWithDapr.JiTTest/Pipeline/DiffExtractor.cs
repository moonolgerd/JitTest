using LibGit2Sharp;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using AspireWithDapr.JiTTest.Configuration;
using AspireWithDapr.JiTTest.Models;

namespace AspireWithDapr.JiTTest.Pipeline;

/// <summary>
/// Extracts code changes from git, producing a structured ChangeSet.
/// </summary>
public class DiffExtractor
{
    private readonly JiTTestConfig _config;
    private const int ContextLines = 20;

    public DiffExtractor(JiTTestConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Extract changes based on the configured diff source.
    /// </summary>
    public ChangeSet Extract()
    {
        using var repo = new Repository(_config.RepositoryRoot);

        var patch = _config.DiffSource.ToLowerInvariant() switch
        {
            "staged" => GetStagedChanges(repo),
            "uncommitted" => GetUnstagedChanges(repo),
            var s when s.StartsWith("branch:") => GetBranchChanges(repo, s["branch:".Length..]),
            var s when s.StartsWith("head~") => GetHeadChanges(repo, int.Parse(s["head~".Length..])),
            _ => GetStagedChanges(repo)
        };

        var changeSet = ParsePatch(patch, repo);
        return FilterFiles(changeSet);
    }

    private Patch GetStagedChanges(Repository repo)
    {
        return repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.Index);
    }

    private Patch GetUnstagedChanges(Repository repo)
    {
        return repo.Diff.Compare<Patch>(repo.Head.Tip?.Tree, DiffTargets.WorkingDirectory);
    }

    private Patch GetBranchChanges(Repository repo, string branchName)
    {
        var branch = repo.Branches[branchName]
            ?? throw new InvalidOperationException($"Branch '{branchName}' not found.");
        return repo.Diff.Compare<Patch>(branch.Tip.Tree, repo.Head.Tip?.Tree);
    }

    private Patch GetHeadChanges(Repository repo, int count)
    {
        var oldCommit = repo.Head.Tip;
        for (var i = 0; i < count && oldCommit?.Parents.Any() == true; i++)
        {
            oldCommit = oldCommit.Parents.First();
        }
        return repo.Diff.Compare<Patch>(oldCommit?.Tree, repo.Head.Tip?.Tree);
    }

    private ChangeSet ParsePatch(Patch patch, Repository repo)
    {
        var changeSet = new ChangeSet();
        var summaryLines = new List<string>();

        foreach (var change in patch)
        {
            if (change.IsBinaryComparison) continue;

            var filePath = change.Path;
            var fullPath = Path.Combine(_config.RepositoryRoot, filePath);

            string fullContent;
            try
            {
                fullContent = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
            }
            catch
            {
                fullContent = "";
            }

            var fileLines = fullContent.Split('\n');
            var changedFile = new ChangedFile
            {
                FilePath = filePath,
                FullFileContent = fullContent
            };

            // Parse the patch content to extract hunks
            var patchText = change.Patch;
            var hunks = ParseHunks(patchText, fileLines);
            changedFile.Hunks.AddRange(hunks);

            changeSet.Files.Add(changedFile);
            summaryLines.Add($"  {change.Status}: {filePath} (+{change.LinesAdded}/-{change.LinesDeleted})");
        }

        changeSet.Summary = summaryLines.Count > 0
            ? $"Changes in {summaryLines.Count} file(s):\n{string.Join("\n", summaryLines)}"
            : "No changes detected.";

        return changeSet;
    }

    private static List<Hunk> ParseHunks(string patchText, string[] fileLines)
    {
        var hunks = new List<Hunk>();
        var lines = patchText.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            if (lines[i].StartsWith("@@"))
            {
                var hunk = ParseHunkHeader(lines[i]);

                var before = new List<string>();
                var after = new List<string>();

                i++;
                while (i < lines.Length && !lines[i].StartsWith("@@"))
                {
                    if (lines[i].StartsWith('-'))
                        before.Add(lines[i][1..]);
                    else if (lines[i].StartsWith('+'))
                        after.Add(lines[i][1..]);
                    else if (lines[i].StartsWith(' '))
                    {
                        before.Add(lines[i][1..]);
                        after.Add(lines[i][1..]);
                    }
                    i++;
                }

                hunk.BeforeContent = string.Join("\n", before);
                hunk.AfterContent = string.Join("\n", after);

                // Extract surrounding context (±20 lines)
                var contextStart = Math.Max(0, hunk.NewStart - 1 - ContextLines);
                var contextEnd = Math.Min(fileLines.Length, hunk.NewStart - 1 + hunk.NewCount + ContextLines);
                hunk.Context = string.Join("\n", fileLines[contextStart..contextEnd]);

                hunks.Add(hunk);
            }
            else
            {
                i++;
            }
        }

        return hunks;
    }

    private static Hunk ParseHunkHeader(string header)
    {
        // Format: @@ -oldStart,oldCount +newStart,newCount @@
        var hunk = new Hunk();
        var match = System.Text.RegularExpressions.Regex.Match(
            header, @"@@ -(\d+),?(\d*) \+(\d+),?(\d*) @@");

        if (match.Success)
        {
            hunk.OldStart = int.Parse(match.Groups[1].Value);
            hunk.OldCount = match.Groups[2].Value.Length > 0 ? int.Parse(match.Groups[2].Value) : 1;
            hunk.NewStart = int.Parse(match.Groups[3].Value);
            hunk.NewCount = match.Groups[4].Value.Length > 0 ? int.Parse(match.Groups[4].Value) : 1;
        }

        return hunk;
    }

    private ChangeSet FilterFiles(ChangeSet changeSet)
    {
        var includeMatcher = new Matcher();
        foreach (var pattern in _config.MutateTargets)
        {
            includeMatcher.AddInclude(pattern);
        }

        var excludeMatcher = new Matcher();
        foreach (var pattern in _config.Exclude)
        {
            excludeMatcher.AddInclude(pattern); // Exclude patterns are added as includes to a separate matcher
        }

        var filtered = new ChangeSet { Summary = changeSet.Summary };

        foreach (var file in changeSet.Files)
        {
            // Check inclusion
            var includeResult = includeMatcher.Match(file.FilePath);
            if (!includeResult.HasMatches) continue;

            // Check exclusion
            var excludeResult = excludeMatcher.Match(file.FilePath);
            if (excludeResult.HasMatches) continue;

            filtered.Files.Add(file);
        }

        if (filtered.Files.Count != changeSet.Files.Count)
        {
            filtered.Summary += $"\n  (Filtered: {changeSet.Files.Count} → {filtered.Files.Count} files)";
        }

        return filtered;
    }
}
