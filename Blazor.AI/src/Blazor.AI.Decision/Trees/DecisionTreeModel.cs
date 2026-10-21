namespace Blazor.AI.Decision;

/// <summary>A node of a decision tree: either a question (with yes/no children) or a leaf outcome.</summary>
public sealed class DecisionTreeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Human label, e.g. "Income above 10M?".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Boolean expression for question nodes; null for leaves.</summary>
    public string? Condition { get; set; }

    public DecisionTreeNode? Yes { get; set; }

    public DecisionTreeNode? No { get; set; }

    /// <summary>Outcome for leaf nodes.</summary>
    public string? Outcome { get; set; }

    /// <summary>Optional share of historical cases reaching this node (for visual weight).</summary>
    public double? Coverage { get; set; }

    public bool IsLeaf => Condition is null;

    public static DecisionTreeNode Leaf(string outcome, double? coverage = null) => new() { Label = outcome, Outcome = outcome, Coverage = coverage };

    public static DecisionTreeNode Question(string label, string condition, DecisionTreeNode yes, DecisionTreeNode no, double? coverage = null) =>
        new() { Label = label, Condition = condition, Yes = yes, No = no, Coverage = coverage };

    /// <summary>Walks the tree; returns the leaf and the path taken.</summary>
    public (DecisionTreeNode Leaf, IReadOnlyList<(DecisionTreeNode Node, bool Answer)> Path) Evaluate(IReadOnlyDictionary<string, object?> facts)
    {
        var path = new List<(DecisionTreeNode, bool)>();
        var node = this;
        while (!node.IsLeaf)
        {
            var answer = RuleEngine.Compile(node.Condition!).EvaluateBool(facts);
            path.Add((node, answer));
            var next = answer ? node.Yes : node.No;
            if (next is null)
            {
                break;
            }

            node = next;
        }

        return (node, path);
    }

    public IEnumerable<DecisionTreeNode> Descendants()
    {
        yield return this;
        foreach (var child in new[] { Yes, No }.Where(c => c is not null))
        {
            foreach (var d in child!.Descendants())
            {
                yield return d;
            }
        }
    }

    /// <summary>A churn-risk tree used by demos.</summary>
    public static DecisionTreeNode ChurnSample() =>
        Question("Contract month-to-month?", "contract = 'monthly'",
            Question("Tenure under 6 months?", "tenureMonths < 6",
                Leaf("High churn risk", 0.18),
                Question("Support tickets ≥ 3?", "tickets >= 3", Leaf("High churn risk", 0.07), Leaf("Medium churn risk", 0.15), 0.22),
                0.4),
            Question("Monthly charges > 120k?", "monthlyCharges > 120000",
                Leaf("Medium churn risk", 0.12),
                Leaf("Low churn risk", 0.48),
                0.6),
            1.0);
}
