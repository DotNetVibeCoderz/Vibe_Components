namespace Blazor.AI.Decision;

/// <summary>A scorecard factor: <see cref="Value"/> from 0 (no risk) to 10, multiplied by <see cref="Weight"/>.</summary>
public sealed class RiskFactor(string name, int value, double weight)
{
    public string Name { get; set; } = name;

    public int Value { get; set; } = value;

    public double Weight { get; set; } = weight;
}

/// <summary>A criterion of an eligibility program.</summary>
public sealed class EligibilityCriterion
{
    public string Name { get; set; } = "Criterion";

    /// <summary>Boolean expression over the applicant's facts.</summary>
    public string Expression { get; set; } = "true";

    /// <summary>Required criteria must pass; optional ones add to a score.</summary>
    public bool Required { get; set; } = true;

    /// <summary>Shown to the applicant when the criterion fails.</summary>
    public string? HowToMeet { get; set; }
}
