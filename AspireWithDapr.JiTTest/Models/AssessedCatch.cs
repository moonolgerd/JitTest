namespace AspireWithDapr.JiTTest.Models;

/// <summary>
/// A candidate catch that has been evaluated by rule-based and LLM assessors.
/// </summary>
public class AssessedCatch
{
    /// <summary>Whether this catch is accepted after assessment.</summary>
    public bool IsAccepted { get; set; }

    /// <summary>"PASS" or "REJECT: reason".</summary>
    public string RuleBasedResult { get; set; } = "";

    /// <summary>LLM assessment reasoning.</summary>
    public string LlmAssessment { get; set; } = "";

    /// <summary>HIGH, MEDIUM, or LOW.</summary>
    public string Confidence { get; set; } = "";

    public ExecutionResult CandidateCatch { get; set; } = null!;
}
