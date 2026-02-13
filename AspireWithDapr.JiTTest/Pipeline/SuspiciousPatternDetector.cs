using System.Text.RegularExpressions;
using AspireWithDapr.JiTTest.Models;

namespace AspireWithDapr.JiTTest.Pipeline;

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
