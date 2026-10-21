using System.Globalization;

namespace Blazor.AI.Decision;

/// <summary>DMN hit policies.</summary>
public enum HitPolicy
{
    /// <summary>The first matching row wins.</summary>
    First,

    /// <summary>Exactly one row may match; several matches is an error.</summary>
    Unique,

    /// <summary>Several rows may match only if they give the same output.</summary>
    Any,

    /// <summary>All matching rows are returned, in order.</summary>
    Collect,

    /// <summary>All matching rows; outputs are summed (numbers).</summary>
    CollectSum,
}

/// <summary>An input column: a name shown in the header and the expression (usually a variable) it tests.</summary>
public sealed record DecisionInput(string Label, string Expression);

public sealed class DecisionRow
{
    /// <summary>One unary test per input: <c>-</c> (any), <c>&gt;= 18</c>, <c>[10..20]</c>, <c>"gold","silver"</c>, <c>not("x")</c>, or a value.</summary>
    public List<string> Conditions { get; set; } = [];

    /// <summary>One output expression per output column (literals or expressions).</summary>
    public List<string> Outputs { get; set; } = [];

    public string? Annotation { get; set; }
}

/// <summary>The result of evaluating a decision table.</summary>
public sealed record DecisionTableResult(IReadOnlyList<int> MatchedRows, IReadOnlyList<IReadOnlyDictionary<string, object?>> Outputs, string? Error = null)
{
    public IReadOnlyDictionary<string, object?>? First => Outputs.Count > 0 ? Outputs[0] : null;
}

/// <summary>A DMN-style decision table with FEEL-like unary tests.</summary>
public sealed class DecisionTable
{
    public string Name { get; set; } = "Decision";

    public HitPolicy HitPolicy { get; set; } = HitPolicy.First;

    public List<DecisionInput> Inputs { get; set; } = [];

    public List<string> OutputNames { get; set; } = [];

    public List<DecisionRow> Rows { get; set; } = [];

    public DecisionTableResult Evaluate(IReadOnlyDictionary<string, object?> facts)
    {
        var values = Inputs.Select(i => RuleEngine.Compile(i.Expression).Evaluate(facts)).ToArray();
        var matched = new List<int>();
        for (var r = 0; r < Rows.Count; r++)
        {
            var row = Rows[r];
            var ok = true;
            for (var c = 0; c < Inputs.Count && ok; c++)
            {
                ok = UnaryTest.Matches(row.Conditions.ElementAtOrDefault(c) ?? "-", values[c], facts);
            }

            if (ok)
            {
                matched.Add(r);
                if (HitPolicy == HitPolicy.First)
                {
                    break;
                }
            }
        }

        var outputs = matched.Select(r => (IReadOnlyDictionary<string, object?>)OutputNames
            .Select((name, i) => (name, value: EvaluateOutput(Rows[r].Outputs.ElementAtOrDefault(i), facts)))
            .ToDictionary(x => x.name, x => x.value, StringComparer.OrdinalIgnoreCase)).ToList();

        switch (HitPolicy)
        {
            case HitPolicy.Unique when matched.Count > 1:
                return new DecisionTableResult(matched, outputs, $"Hit policy UNIQUE violated: rows {string.Join(", ", matched.Select(m => m + 1))} all match.");
            case HitPolicy.Any when outputs.Select(o => string.Join('|', o.Values.Select(Expression.AsString))).Distinct().Count() > 1:
                return new DecisionTableResult(matched, outputs, "Hit policy ANY violated: matching rows give different outputs.");
            case HitPolicy.Any or HitPolicy.Unique when matched.Count > 0:
                return new DecisionTableResult(matched, [outputs[0]]);
            case HitPolicy.CollectSum when outputs.Count > 0:
                var sum = OutputNames.ToDictionary(n => n, n => (object?)outputs.Sum(o => Expression.AsNumber(o[n]) ?? 0), StringComparer.OrdinalIgnoreCase);
                return new DecisionTableResult(matched, [sum]);
            default:
                return new DecisionTableResult(matched, outputs);
        }
    }

    private static object? EvaluateOutput(string? expression, IReadOnlyDictionary<string, object?> facts)
    {
        if (string.IsNullOrWhiteSpace(expression) || expression.Trim() == "-")
        {
            return null;
        }

        // Bare words (e.g. Approve) are treated as text so business users need not quote them.
        return Expression.TryParse(expression, out var parsed, out _) && parsed!.Variables.All(facts.ContainsKey)
            ? parsed.Evaluate(facts)
            : expression.Trim().Trim('"', '\'');
    }

    /// <summary>A sample loan-eligibility table used by demos.</summary>
    public static DecisionTable LoanSample() => new()
    {
        Name = "Loan eligibility",
        HitPolicy = HitPolicy.First,
        Inputs = [new("Credit score", "creditScore"), new("Debt-to-income %", "dti"), new("Employment", "employment")],
        OutputNames = ["decision", "rate"],
        Rows =
        [
            new() { Conditions = ["< 550", "-", "-"], Outputs = ["Decline", "-"], Annotation = "Below minimum score" },
            new() { Conditions = ["-", "> 45", "-"], Outputs = ["Decline", "-"], Annotation = "Debt too high" },
            new() { Conditions = [">= 750", "<= 30", "\"permanent\",\"civil servant\""], Outputs = ["Approve", "7.5"], Annotation = "Prime" },
            new() { Conditions = ["[650..749]", "<= 40", "-"], Outputs = ["Approve", "9.75"], Annotation = "Standard" },
            new() { Conditions = ["-", "-", "-"], Outputs = ["Manual review", "-"], Annotation = "Everything else" },
        ],
    };
}

/// <summary>FEEL-style unary tests for decision table cells.</summary>
public static class UnaryTest
{
    public static bool Matches(string test, object? value, IReadOnlyDictionary<string, object?> facts)
    {
        test = test.Trim();
        if (test is "" or "-")
        {
            return true;
        }

        if (test.StartsWith("not(", StringComparison.OrdinalIgnoreCase) && test.EndsWith(')'))
        {
            return !Matches(test[4..^1], value, facts);
        }

        // Ranges: [a..b], (a..b), [a..b), (a..b]
        if ((test[0] is '[' or '(' or ']') && (test[^1] is ']' or ')' or '[') && test.Contains("..", StringComparison.Ordinal))
        {
            var parts = test[1..^1].Split("..", 2, StringSplitOptions.TrimEntries);
            var low = Value(parts[0], facts);
            var high = Value(parts[1], facts);
            var cmpLow = Expression.Compare(value, low);
            var cmpHigh = Expression.Compare(value, high);
            return (test[0] == '[' ? cmpLow >= 0 : cmpLow > 0) && (test[^1] == ']' ? cmpHigh <= 0 : cmpHigh < 0);
        }

        foreach (var op in new[] { ">=", "<=", "!=", ">", "<", "=" })
        {
            if (test.StartsWith(op, StringComparison.Ordinal))
            {
                var operand = Value(test[op.Length..].Trim(), facts);
                return op switch
                {
                    ">=" => value is not null && Expression.Compare(value, operand) >= 0,
                    "<=" => value is not null && Expression.Compare(value, operand) <= 0,
                    ">" => value is not null && Expression.Compare(value, operand) > 0,
                    "<" => value is not null && Expression.Compare(value, operand) < 0,
                    "!=" => !Expression.ValuesEqual(value, operand),
                    _ => Expression.ValuesEqual(value, operand),
                };
            }
        }

        // Disjunction of values: "gold","silver" or 1, 2, 3
        return SplitList(test).Any(item => Expression.ValuesEqual(value, Value(item, facts)));
    }

    private static object? Value(string text, IReadOnlyDictionary<string, object?> facts)
    {
        text = text.Trim();
        if (text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\''))
        {
            return text[1..^1];
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return Expression.TryParse(text, out var expression, out _) ? expression!.Evaluate(facts) ?? text : text;
    }

    private static IEnumerable<string> SplitList(string text)
    {
        var start = 0;
        var inString = false;
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == quote) inString = false;
            }
            else if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
            }
            else if (c == ',')
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }
}
