using System.Text.Json;
using System.Text.RegularExpressions;

namespace JiTTest.LLM;

/// <summary>
/// Parses LLM responses, extracting JSON from potentially noisy output.
/// </summary>
public static partial class LlmResponseParser
{
    /// <summary>
    /// Extract a JSON object or array from an LLM response that may contain
    /// markdown code fences, preamble text, or trailing explanation.
    /// </summary>
    public static T? ParseJson<T>(string response)
    {
        var json = ExtractJson(response);
        if (json is null) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(json, s_options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    /// <summary>
    /// Extract raw JSON string from LLM output.
    /// </summary>
    public static string? ExtractJson(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;

        // Try extracting from markdown code fences first
        var fenceMatch = JsonCodeFenceRegex().Match(response);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups[1].Value.Trim();
        }

        // Try finding JSON object or array boundaries
        var trimmed = response.Trim();

        // Find first { or [
        var objStart = trimmed.IndexOf('{');
        var arrStart = trimmed.IndexOf('[');

        int start;
        char openChar, closeChar;

        if (objStart >= 0 && (arrStart < 0 || objStart < arrStart))
        {
            start = objStart;
            openChar = '{';
            closeChar = '}';
        }
        else if (arrStart >= 0)
        {
            start = arrStart;
            openChar = '[';
            closeChar = ']';
        }
        else
        {
            return null;
        }

        // Find matching closing bracket
        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < trimmed.Length; i++)
        {
            var c = trimmed[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (inString) continue;

            if (c == openChar) depth++;
            else if (c == closeChar) depth--;

            if (depth == 0)
            {
                return trimmed[start..(i + 1)];
            }
        }

        return null;
    }

    /// <summary>
    /// Extract C# code from an LLM response, stripping markdown fences if present.
    /// </summary>
    public static string ExtractCSharpCode(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return response;

        var fenceMatch = CSharpCodeFenceRegex().Match(response);
        if (fenceMatch.Success)
        {
            return fenceMatch.Groups[1].Value.Trim();
        }

        // If it starts with 'using' or 'namespace', assume it's raw code
        var trimmed = response.Trim();
        if (trimmed.StartsWith("using ") || trimmed.StartsWith("namespace "))
        {
            return trimmed;
        }

        // Try generic code fence
        var genericFence = GenericCodeFenceRegex().Match(response);
        if (genericFence.Success)
        {
            return genericFence.Groups[1].Value.Trim();
        }

        return trimmed;
    }

    [GeneratedRegex(@"```(?:json)\s*\n([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex JsonCodeFenceRegex();

    [GeneratedRegex(@"```(?:csharp|cs|c#)\s*\n([\s\S]*?)```", RegexOptions.IgnoreCase)]
    private static partial Regex CSharpCodeFenceRegex();

    [GeneratedRegex(@"```\s*\n([\s\S]*?)```")]
    private static partial Regex GenericCodeFenceRegex();

    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
}
