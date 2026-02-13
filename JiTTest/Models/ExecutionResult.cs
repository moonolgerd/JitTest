namespace AspireWithDapr.JiTTest.Models;

/// <summary>
/// Result of executing a generated test against original and mutated code.
/// </summary>
public class ExecutionResult
{
    /// <summary>Whether the test passes against the unmodified original code.</summary>
    public bool PassesOnOriginal { get; set; }

    /// <summary>Whether the test fails against the mutated code.</summary>
    public bool FailsOnMutant { get; set; }

    /// <summary>True only when PassesOnOriginal && FailsOnMutant.</summary>
    public bool IsCandidateCatch => PassesOnOriginal && FailsOnMutant;

    public string OriginalOutput { get; set; } = "";
    public string MutantOutput { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public GeneratedTest GeneratedTest { get; set; } = null!;
}
