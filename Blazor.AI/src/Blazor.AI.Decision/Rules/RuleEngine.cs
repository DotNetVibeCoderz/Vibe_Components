using System.Collections.Concurrent;

namespace Blazor.AI.Decision;

/// <summary>A business rule: when <see cref="Condition"/> is true, run <see cref="Actions"/> (assignments separated by ';').</summary>
public sealed class BusinessRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "New rule";

    public string? Description { get; set; }

    /// <summary>A boolean <see cref="Expression"/>, e.g. <c>age >= 18 and country in ('ID', 'MY')</c>.</summary>
    public string Condition { get; set; } = "true";

    /// <summary>Assignments, e.g. <c>discount = 0.1; tier = 'gold'; reasons = reasons + ' loyal'</c>.</summary>
    public string Actions { get; set; } = string.Empty;

    /// <summary>Higher runs first.</summary>
    public int Priority { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Stop evaluating further rules after this one fires.</summary>
    public bool StopOnMatch { get; set; }
}

public enum RuleMode
{
    /// <summary>Every matching rule fires, in priority order; later rules see earlier assignments.</summary>
    AllMatches,

    /// <summary>Only the highest-priority matching rule fires.</summary>
    FirstMatch,
}

/// <summary>What happened to one rule during evaluation.</summary>
public sealed record RuleTrace(BusinessRule Rule, bool Matched, IReadOnlyDictionary<string, object?> Assigned, string? Error = null);

public sealed record RuleEvaluation(IReadOnlyDictionary<string, object?> Facts, IReadOnlyList<RuleTrace> Trace)
{
    public IEnumerable<BusinessRule> Fired => Trace.Where(t => t.Matched).Select(t => t.Rule);

    public object? this[string name] => Facts.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Evaluates rule sets. Parsed expressions are cached, so repeated evaluations are cheap.</summary>
public sealed class RuleEngine
{
    private static readonly ConcurrentDictionary<string, Expression> Cache = new(StringComparer.Ordinal);

    public RuleEvaluation Evaluate(IEnumerable<BusinessRule> rules, IReadOnlyDictionary<string, object?> facts, RuleMode mode = RuleMode.AllMatches)
    {
        var state = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in facts)
        {
            state[key] = Expression.Normalize(value);
        }

        var trace = new List<RuleTrace>();
        foreach (var rule in rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority))
        {
            try
            {
                if (!Compile(rule.Condition).EvaluateBool(state))
                {
                    trace.Add(new RuleTrace(rule, false, new Dictionary<string, object?>()));
                    continue;
                }

                var assigned = ApplyActions(rule.Actions, state);
                trace.Add(new RuleTrace(rule, true, assigned));
                if (mode == RuleMode.FirstMatch || rule.StopOnMatch)
                {
                    break;
                }
            }
            catch (FormatException ex)
            {
                trace.Add(new RuleTrace(rule, false, new Dictionary<string, object?>(), ex.Message));
            }
        }

        return new RuleEvaluation(state, trace);
    }

    /// <summary>Validates a condition or action list; returns <c>null</c> when it is fine.</summary>
    public static string? Validate(string text, bool isActions = false)
    {
        try
        {
            if (isActions)
            {
                foreach (var (_, expression) in ParseActions(text))
                {
                    Compile(expression);
                }
            }
            else
            {
                Compile(text);
            }

            return null;
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
    }

    public static Expression Compile(string source) => Cache.GetOrAdd(source, Expression.Parse);

    private static Dictionary<string, object?> ApplyActions(string actions, Dictionary<string, object?> state)
    {
        var assigned = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (variable, expression) in ParseActions(actions))
        {
            var value = Compile(expression).Evaluate(state);
            state[variable] = value;
            assigned[variable] = value;
        }

        return assigned;
    }

    private static IEnumerable<(string Variable, string Expression)> ParseActions(string actions)
    {
        foreach (var part in actions.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || (eq + 1 < part.Length && part[eq + 1] == '='))
            {
                throw new FormatException($"Action \"{part}\" must look like name = expression.");
            }

            var name = part[..eq].Trim();
            if (!name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.'))
            {
                throw new FormatException($"\"{name}\" is not a valid variable name.");
            }

            yield return (name, part[(eq + 1)..].Trim());
        }
    }
}
