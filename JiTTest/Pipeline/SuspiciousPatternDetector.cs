using System.Text.RegularExpressions;
using JiTTest.Models;

namespace JiTTest.Pipeline;

/// <summary>
/// Lightweight static analysis that scans changed code for suspicious patterns
/// that mutation testing cannot catch (because the bug is in the original code).
/// These are reported as warnings alongside mutation catches.
/// </summary>
public static class SuspiciousPatternDetector
{
    /// <summary>
    /// Scan all changed files for suspicious patterns and return warnings.
    /// Only scans new/changed lines (AfterContent in hunks).
    /// </summary>
    public static List<SuspiciousPattern> Detect(ChangeSet changeSet)
    {
        var warnings = new List<SuspiciousPattern>();

        foreach (var file in changeSet.Files)
        {
            // ── Cross-hunk analysis: detect finally block removal ──
            DetectFinallyRemoval(file, warnings);

            // ── Cross-hunk analysis: detect cleanup/reset code in try without finally ──
            DetectCleanupNotInFinally(file, warnings);

            // Scan hunk after-content (new lines) for suspicious patterns
            foreach (var hunk in file.Hunks)
            {
                var newCode = hunk.AfterContent;
                if (string.IsNullOrWhiteSpace(newCode)) continue;

                var lines = newCode.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var lineNum = hunk.NewStart + i;

                    // ── Off-by-one: .Count+1 or .Length+1 used as upper bound ──
                    var oboMatch = Regex.Match(line, @"\.(?:Count|Length)\s*\+\s*1");
                    if (oboMatch.Success)
                    {
                        warnings.Add(new SuspiciousPattern
                        {
                            File = file.FilePath,
                            Line = lineNum,
                            Code = line.Trim(),
                            Pattern = "OFF_BY_ONE",
                            Description = $"`.Count+1` or `.Length+1` used as upper bound — " +
                                          $"`Random.Next(0, list.Count+1)` or array indexing with `Count+1` " +
                                          $"can produce an out-of-range index. Did you mean `.Count`?"
                        });
                    }

                    // ── Off-by-one: <=  with .Count or .Length ──
                    var leqMatch = Regex.Match(line, @"<=\s*\w+\.(?:Count|Length)\b");
                    if (leqMatch.Success && !line.Contains(">="))
                    {
                        warnings.Add(new SuspiciousPattern
                        {
                            File = file.FilePath,
                            Line = lineNum,
                            Code = line.Trim(),
                            Pattern = "OFF_BY_ONE",
                            Description = "`<= collection.Count/Length` in a loop/condition is likely " +
                                          "an off-by-one error. Did you mean `<`?"
                        });
                    }

                    // ── Hardcoded magic numbers for collection bounds ──
                    var magicNext = Regex.Match(line, @"\.Next\(\s*\d+\s*,\s*(\d+)\s*\)");
                    if (magicNext.Success && int.TryParse(magicNext.Groups[1].Value, out var upper) && upper > 10)
                    {
                        warnings.Add(new SuspiciousPattern
                        {
                            File = file.FilePath,
                            Line = lineNum,
                            Code = line.Trim(),
                            Pattern = "MAGIC_NUMBER",
                            Description = $"Hardcoded upper bound `{upper}` in `Random.Next()` — " +
                                          $"consider using `.Count` to avoid index mismatch if collection size changes."
                        });
                    }

                    // ── String comparison without StringComparison ──
                    if (Regex.IsMatch(line, @"==\s*""[^""]+""") &&
                        !line.Contains("StringComparison") &&
                        !line.Contains("switch") && !line.Contains("=>") &&
                        !line.TrimStart().StartsWith("//"))
                    {
                        // Only flag if it looks like a user-facing string comparison
                        // Skip switch arms, assignments, etc.
                        if (Regex.IsMatch(line, @"if\s*\(.*==\s*"""))
                        {
                            warnings.Add(new SuspiciousPattern
                            {
                                File = file.FilePath,
                                Line = lineNum,
                                Code = line.Trim(),
                                Pattern = "STRING_COMPARE",
                                Description = "String comparison using `==` without `StringComparison` — " +
                                              "may fail with different cultures or casing."
                            });
                        }
                    }

                    // ── Potential null dereference after nullable access ──
                    var nullDeref = Regex.Match(line, @"(\w+)\?\.(\w+).*\1\.(\w+)");
                    if (nullDeref.Success)
                    {
                        warnings.Add(new SuspiciousPattern
                        {
                            File = file.FilePath,
                            Line = lineNum,
                            Code = line.Trim(),
                            Pattern = "NULL_DEREF",
                            Description = $"Variable `{nullDeref.Groups[1].Value}` accessed with `?.` (nullable) " +
                                          $"and then with `.` (non-nullable) on the same line — potential NullReferenceException."
                        });
                    }
                }
            }
        }

        return warnings;
    }

    /// <summary>
    /// Detect when a `finally` block was removed in the diff.
    /// Code that was guaranteed to run (cleanup, state reset, Dispose) will no longer
    /// execute when exceptions occur — a common source of resource leaks and stuck state.
    /// </summary>
    private static void DetectFinallyRemoval(ChangedFile file, List<SuspiciousPattern> warnings)
    {
        foreach (var hunk in file.Hunks)
        {
            var beforeLines = hunk.BeforeContent?.Split('\n') ?? [];
            var afterLines = hunk.AfterContent?.Split('\n') ?? [];

            var hadFinally = beforeLines.Any(l => Regex.IsMatch(l, @"\bfinally\b"));
            var hasFinally = afterLines.Any(l => Regex.IsMatch(l, @"\bfinally\b"));

            if (!hadFinally || hasFinally) continue;

            // Extract what was inside the finally block
            var finallyBody = ExtractFinallyBody(beforeLines);
            var bodyDescription = string.IsNullOrWhiteSpace(finallyBody)
                ? ""
                : $" The `finally` block contained: `{finallyBody.Trim()}`";

            warnings.Add(new SuspiciousPattern
            {
                File = file.FilePath,
                Line = hunk.NewStart,
                Code = "finally { ... } block removed",
                Pattern = "FINALLY_REMOVED",
                Description = $"A `finally` block was removed from this change.{bodyDescription} " +
                              "Code in `finally` is guaranteed to run even when exceptions occur. " +
                              "Without it, cleanup/state-reset may not execute on error paths, " +
                              "leading to resource leaks, stuck UI state, or unclosed connections."
            });
        }
    }

    /// <summary>
    /// Detect state-resetting code (= false, = null, = 0, Dispose, Close) inside a try block
    /// when there is no finally block. This pattern often indicates cleanup code that should
    /// be in a finally block to ensure it runs on exception paths.
    /// </summary>
    private static void DetectCleanupNotInFinally(ChangedFile file, List<SuspiciousPattern> warnings)
    {
        // Analyze the full after-content of the file via hunk context
        foreach (var hunk in file.Hunks)
        {
            var afterContent = hunk.AfterContent;
            if (string.IsNullOrWhiteSpace(afterContent)) continue;

            var lines = afterContent.Split('\n');

            // Look for try-catch blocks that lack a finally block with cleanup/reset code inside try
            var insideTry = false;
            var insideCatch = false;
            var braceDepth = 0;
            var tryBraceDepth = 0;
            var hasCatch = false;
            var hasFinally = false;
            var resetLines = new List<(int lineIndex, string code)>();

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                // Track try/catch/finally keywords
                if (Regex.IsMatch(line, @"\btry\b\s*\{?"))
                {
                    insideTry = true;
                    insideCatch = false;
                    hasCatch = false;
                    hasFinally = false;
                    tryBraceDepth = braceDepth;
                    resetLines.Clear();
                }
                else if (Regex.IsMatch(line, @"\bcatch\b"))
                {
                    insideTry = false;
                    insideCatch = true;
                    hasCatch = true;
                }
                else if (Regex.IsMatch(line, @"\bfinally\b"))
                {
                    insideTry = false;
                    insideCatch = false;
                    hasFinally = true;
                }

                // Track braces
                braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');

                // When we return to the try-block's brace depth, the try-catch-finally is complete
                if (braceDepth <= tryBraceDepth && (insideTry || insideCatch || hasCatch) && !hasFinally)
                {
                    // Report any state-reset code found in the try block without finally
                    if (hasCatch && resetLines.Count > 0 && !hasFinally)
                    {
                        foreach (var (lineIndex, code) in resetLines)
                        {
                            var lineNum = hunk.NewStart + lineIndex;
                            warnings.Add(new SuspiciousPattern
                            {
                                File = file.FilePath,
                                Line = lineNum,
                                Code = code,
                                Pattern = "CLEANUP_NOT_IN_FINALLY",
                                Description = "State-reset or cleanup code is inside a `try` block but there is no `finally` block. " +
                                              "If an exception occurs, this code will be skipped. " +
                                              "Consider moving it to a `finally` block to ensure it always executes."
                            });
                        }
                    }
                    insideTry = false;
                    insideCatch = false;
                }

                // Detect state-resetting code inside try block
                if (insideTry && IsStateResetCode(line))
                {
                    resetLines.Add((i, line));
                }
            }
        }
    }

    /// <summary>
    /// Check if a line of code looks like state-resetting/cleanup code.
    /// </summary>
    private static bool IsStateResetCode(string line)
    {
        // Boolean resets: isLoading = false, _isRunning = false, etc.
        if (Regex.IsMatch(line, @"\b\w+\s*=\s*false\s*;"))
            return true;

        // Null assignments: result = null, _connection = null
        if (Regex.IsMatch(line, @"\b\w+\s*=\s*null\s*;"))
            return true;

        // Zero assignments: count = 0, _retries = 0
        if (Regex.IsMatch(line, @"\b\w+\s*=\s*0\s*;"))
            return true;

        // Dispose/Close calls
        if (Regex.IsMatch(line, @"\.\s*(?:Dispose|Close|Release|Reset)\s*\("))
            return true;

        return false;
    }

    /// <summary>
    /// Extract the body of a finally block from diff lines.
    /// </summary>
    private static string ExtractFinallyBody(string[] lines)
    {
        var inFinally = false;
        var depth = 0;
        var bodyLines = new List<string>();

        foreach (var line in lines)
        {
            if (Regex.IsMatch(line, @"\bfinally\b"))
            {
                inFinally = true;
                depth = 0;
                continue;
            }

            if (!inFinally) continue;

            depth += line.Count(c => c == '{') - line.Count(c => c == '}');

            var trimmed = line.Trim();
            if (trimmed != "{" && trimmed != "}" && !string.IsNullOrWhiteSpace(trimmed))
            {
                bodyLines.Add(trimmed);
            }

            if (depth <= 0 && bodyLines.Count > 0)
                break;
        }

        return string.Join("; ", bodyLines);
    }
}

/// <summary>
/// A suspicious code pattern detected by static analysis on changed code.
/// </summary>
public class SuspiciousPattern
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Code { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string Description { get; set; } = "";
}
