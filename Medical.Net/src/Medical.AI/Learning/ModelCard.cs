using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Medical.AI.Learning;

/// <summary>Performance in one subgroup (e.g. sex, age band, site).</summary>
public sealed record SubgroupMetrics(string Attribute, string Group, BinaryMetrics Metrics);

/// <summary>
/// A model card (Mitchell et al., FAT* 2019) for a clinical prediction model: intended use, data, overall and subgroup
/// performance with the largest gaps, limitations. Render with <see cref="ToMarkdown"/> or <see cref="ToJson"/>.
/// </summary>
public sealed record ModelCard
{
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0";
    public string? Owner { get; init; }
    public DateOnly Date { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public required string IntendedUse { get; init; }
    public IReadOnlyList<string> OutOfScope { get; init; } = [];
    public string? TrainingData { get; init; }
    public string? EvaluationData { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public required BinaryMetrics Overall { get; init; }
    public IReadOnlyList<SubgroupMetrics> Subgroups { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public IReadOnlyList<string> EthicalConsiderations { get; init; } = [];

    /// <summary>Largest AUC difference between groups of each attribute.</summary>
    public IReadOnlyDictionary<string, double> AucGaps => Subgroups.GroupBy(s => s.Attribute)
        .Where(g => g.Count() > 1 && g.All(s => !double.IsNaN(s.Metrics.Auc)))
        .ToDictionary(g => g.Key, g => Math.Round(g.Max(s => s.Metrics.Auc) - g.Min(s => s.Metrics.Auc), 4));

    /// <summary>Subgroups whose AUC is more than <paramref name="tolerance"/> below the overall AUC or with fewer than <paramref name="minN"/> cases.</summary>
    public IReadOnlyList<string> Concerns(double tolerance = 0.05, int minN = 50) => Subgroups
        .SelectMany(s => new[]
        {
            s.Metrics.Auc < Overall.Auc - tolerance ? $"{s.Attribute}={s.Group}: AUC {s.Metrics.Auc:0.###} vs overall {Overall.Auc:0.###}" : null,
            s.Metrics.N < minN ? $"{s.Attribute}={s.Group}: only {s.Metrics.N} cases — estimates unreliable" : null,
            s.Metrics.Ece > Overall.Ece + 0.05 ? $"{s.Attribute}={s.Group}: miscalibrated (ECE {s.Metrics.Ece:0.###})" : null,
        })
        .OfType<string>().ToList();

    /// <summary>Builds a card by evaluating predictions overall and per subgroup.</summary>
    public static ModelCard Create(string name, string intendedUse, IReadOnlyList<double> predictions, IReadOnlyList<bool> labels,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? subgroups = null, double threshold = 0.5)
    {
        var subs = new List<SubgroupMetrics>();
        foreach (var (attr, groups) in subgroups ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (groups.Count != predictions.Count) throw new ArgumentException($"Subgroup '{attr}' has {groups.Count} values for {predictions.Count} predictions.");
            foreach (var g in groups.Distinct().Order(StringComparer.Ordinal))
            {
                var idx = Enumerable.Range(0, groups.Count).Where(i => groups[i] == g).ToList();
                subs.Add(new SubgroupMetrics(attr, g, ModelEvaluation.Evaluate(idx.Select(i => predictions[i]).ToList(), idx.Select(i => labels[i]).ToList(), threshold)));
            }
        }
        return new ModelCard { Name = name, IntendedUse = intendedUse, Overall = ModelEvaluation.Evaluate(predictions, labels, threshold), Subgroups = subs };
    }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    });

    public string ToMarkdown()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"# Model card: {Name} (v{Version})").AppendLine();
        sb.AppendLine(c, $"Date: {Date:yyyy-MM-dd}{(Owner is null ? "" : $" · Owner: {Owner}")}").AppendLine();
        sb.AppendLine("## Intended use").AppendLine(IntendedUse).AppendLine();
        if (OutOfScope.Count > 0) { sb.AppendLine("## Out of scope"); foreach (var s in OutOfScope) sb.AppendLine(c, $"- {s}"); sb.AppendLine(); }
        if (TrainingData is not null || EvaluationData is not null)
        {
            sb.AppendLine("## Data");
            if (TrainingData is not null) sb.AppendLine(c, $"- Training: {TrainingData}");
            if (EvaluationData is not null) sb.AppendLine(c, $"- Evaluation: {EvaluationData}");
            sb.AppendLine();
        }
        if (Features.Count > 0) sb.AppendLine("## Features").AppendLine(string.Join(", ", Features)).AppendLine();
        sb.AppendLine("## Performance").AppendLine("| Group | n | Prevalence | AUC | Brier | ECE | Sensitivity | Specificity |").AppendLine("|---|---|---|---|---|---|---|---|");
        void Row(string g, BinaryMetrics m) => sb.AppendLine(c, $"| {g} | {m.N} | {m.Prevalence:0.###} | {m.Auc:0.###} | {m.Brier:0.###} | {m.Ece:0.###} | {m.Sensitivity:0.###} | {m.Specificity:0.###} |");
        Row("**Overall**", Overall);
        foreach (var s in Subgroups) Row($"{s.Attribute} = {s.Group}", s.Metrics);
        sb.AppendLine();
        if (AucGaps.Count > 0) { sb.AppendLine("Largest AUC gap: " + string.Join(", ", AucGaps.Select(g => string.Create(c, $"{g.Key} {g.Value:0.###}")))).AppendLine(); }
        var concerns = Concerns();
        if (concerns.Count > 0) { sb.AppendLine("## Subgroup concerns"); foreach (var s in concerns) sb.AppendLine(c, $"- {s}"); sb.AppendLine(); }
        if (Limitations.Count > 0) { sb.AppendLine("## Limitations"); foreach (var s in Limitations) sb.AppendLine(c, $"- {s}"); sb.AppendLine(); }
        if (EthicalConsiderations.Count > 0) { sb.AppendLine("## Ethical considerations"); foreach (var s in EthicalConsiderations) sb.AppendLine(c, $"- {s}"); sb.AppendLine(); }
        return sb.ToString();
    }
}
