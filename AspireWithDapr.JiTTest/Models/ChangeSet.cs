namespace AspireWithDapr.JiTTest.Models;

/// <summary>
/// Represents a parsed set of code changes from git diff.
/// </summary>
public class ChangeSet
{
    public List<ChangedFile> Files { get; set; } = [];
    public string Summary { get; set; } = "";
}

/// <summary>
/// Represents a single changed file with its diff hunks.
/// </summary>
public class ChangedFile
{
    public string FilePath { get; set; } = "";
    public List<Hunk> Hunks { get; set; } = [];
    public string FullFileContent { get; set; } = "";
}

/// <summary>
/// Represents a single diff hunk with before/after content and surrounding context.
/// </summary>
public class Hunk
{
    public int OldStart { get; set; }
    public int OldCount { get; set; }
    public int NewStart { get; set; }
    public int NewCount { get; set; }
    public string BeforeContent { get; set; } = "";
    public string AfterContent { get; set; } = "";

    /// <summary>±20 lines surrounding the change for LLM context.</summary>
    public string Context { get; set; } = "";
}
