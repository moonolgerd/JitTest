namespace JiTTest.Models;

/// <summary>
/// An LLM-generated test and its compilation status.
/// </summary>
public class GeneratedTest
{
    /// <summary>Full xUnit test class source code.</summary>
    public string TestCode { get; set; } = "";

    public bool CompilationSuccess { get; set; }
    public List<string> CompilationErrors { get; set; } = [];

    /// <summary>Number of compilation retries attempted (0–2).</summary>
    public int RetryCount { get; set; }

    /// <summary>The mutant this test is designed to catch.</summary>
    public Mutant ForMutant { get; set; } = null!;
}
