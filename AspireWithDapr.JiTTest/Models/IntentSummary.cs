namespace AspireWithDapr.JiTTest.Models;

/// <summary>
/// LLM-inferred intent of a code change.
/// </summary>
public class IntentSummary
{
    /// <summary>Human-readable description of what the change does.</summary>
    public string Description { get; set; } = "";

    /// <summary>Specific behavior changes introduced by the diff.</summary>
    public List<string> BehaviorChanges { get; set; } = [];

    /// <summary>Areas where the change could introduce regressions.</summary>
    public List<string> RiskAreas { get; set; } = [];

    /// <summary>Methods/properties directly affected by the change.</summary>
    public List<string> AffectedMethods { get; set; } = [];
}
