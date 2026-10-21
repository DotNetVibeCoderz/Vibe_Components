using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Blazor.AI.Abstractions;
using Blazor.AI.Core;
using Blazor.AI.Core.Services;
using Gravicode.Science.GraviFrame;

namespace Blazor.AI.RAG;

/// <summary>A query over a table, planned by the model and executed exactly by GraviFrame.</summary>
public sealed record TableQuery(string Operation, string? Column, string? GroupBy, string? FilterColumn, string? FilterOperator, string? FilterValue, int Top = 10, bool Descending = true);

/// <summary>The computed answer to a spreadsheet question.</summary>
public sealed record TableAnswer(string Question, TableQuery Query, IReadOnlyList<(string Key, double Value)> Rows, double? Scalar, string Explanation);

/// <summary>
/// Answers questions about a table without letting the model do arithmetic: the model turns the question into a
/// <see cref="TableQuery"/> (sum, mean, count, min, max, median, top) and the numbers are computed with GraviFrame.
/// </summary>
public sealed class SpreadsheetAnalyst(AiTextService ai)
{
    private static readonly string[] Operations = ["sum", "mean", "count", "min", "max", "median", "top", "describe"];

    public static string Profile(DataFrame frame, int sampleRows = 5)
    {
        var builder = new StringBuilder();
        builder.Append(frame.RowCount).Append(" rows × ").Append(frame.ColumnCount).AppendLine(" columns.");
        foreach (var column in frame.Columns)
        {
            builder.Append("- ").Append(column.Name).Append(" (").Append(column.DataType).Append(')');
            if (column is NumericSeries numeric)
            {
                builder.Append(CultureInfo.InvariantCulture, $": min {numeric.Min():0.##}, mean {numeric.Mean():0.##}, max {numeric.Max():0.##}");
            }
            else
            {
                var top = column.ValueCounts().Take(5).Select(v => $"{v.Item1} ({v.Item2})");
                builder.Append(": e.g. ").AppendJoin(", ", top);
            }

            builder.AppendLine();
        }

        builder.AppendLine().AppendLine("Sample rows:").Append(frame.Head(sampleRows).ToString(sampleRows));
        return builder.ToString();
    }

    public async Task<TableAnswer> AskAsync(DataFrame frame, string question, string? provider = null, CancellationToken cancellationToken = default)
    {
        var query = await PlanAsync(frame, question, provider, cancellationToken).ConfigureAwait(false);
        var (rows, scalar) = Execute(frame, query);

        var resultText = scalar is { } s
            ? s.ToString("0.####", CultureInfo.InvariantCulture)
            : string.Join("\n", rows.Select(r => $"{r.Key}: {r.Value.ToString("0.####", CultureInfo.InvariantCulture)}"));
        var system = "You explain spreadsheet results to a business user in 1-3 sentences. Use the computed result exactly; do not recalculate. " +
                     "Answer in the language of the question.";
        var user = $"Question: {question}\nQuery: {Describe(query)}\nComputed result:\n{resultText}";
        var explanation = await ai.CompleteAsync(system, PromptTags.Wrap(PromptTags.Input, user), AiTasks.Insight, provider, null, cancellationToken).ConfigureAwait(false);
        return new TableAnswer(question, query, rows, scalar, explanation);
    }

    public async Task<TableQuery> PlanAsync(DataFrame frame, string question, string? provider, CancellationToken cancellationToken)
    {
        var system = "Translate the question about the table into a query. Reply with JSON only: " +
                     "{\"operation\": \"sum\"|\"mean\"|\"count\"|\"min\"|\"max\"|\"median\"|\"top\"|\"describe\", \"column\": numeric column or null, " +
                     "\"groupBy\": column or null, \"filterColumn\": column or null, \"filterOperator\": \"=\"|\"!=\"|\">\"|\"<\"|\">=\"|\"<=\"|\"contains\" or null, " +
                     "\"filterValue\": string or null, \"top\": number, \"descending\": boolean}. Use exact column names from the profile.";
        var user = "Table profile:\n" + Profile(frame) + "\n" + PromptTags.Wrap(PromptTags.Input, question);
        var node = await ai.GenerateJsonAsync(system, user, AiTasks.Json, provider, cancellationToken).ConfigureAwait(false) as JsonObject;

        var columns = frame.ColumnNames;
        string? Column(string key) => node?[key]?.ToString() is { } name ? columns.FirstOrDefault(c => c.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;

        var operation = node?["operation"]?.ToString()?.ToLowerInvariant();
        var planned = new TableQuery(
            Operations.Contains(operation) ? operation! : string.Empty,
            Column("column"),
            Column("groupBy"),
            Column("filterColumn"),
            node?["filterOperator"]?.ToString(),
            node?["filterValue"]?.ToString(),
            node?["top"] is JsonValue t && t.TryGetValue<int>(out var top) ? Math.Clamp(top, 1, 50) : 10,
            node?["descending"] is not JsonValue d || !d.TryGetValue<bool>(out var desc) || desc);

        // Fill gaps from the question itself, so offline mode and weaker models still produce a sensible query.
        return planned.Operation.Length > 0 && (planned.Column is not null || planned.Operation is "count" or "describe")
            ? planned
            : Heuristic(frame, question, planned);
    }

    public static TableQuery Heuristic(DataFrame frame, string question, TableQuery? partial = null)
    {
        var q = question.ToLowerInvariant();
        var operation = q.Contains("average") || q.Contains("mean") || q.Contains("rata") ? "mean"
            : q.Contains("total") || q.Contains("sum") || q.Contains("jumlah") ? "sum"
            : q.Contains("how many") || q.Contains("count") || q.Contains("berapa banyak") ? "count"
            : q.Contains("highest") || q.Contains("max") || q.Contains("tertinggi") || q.Contains("top") || q.Contains("best") ? "top"
            : q.Contains("lowest") || q.Contains("min") || q.Contains("terendah") ? "min"
            : q.Contains("median") ? "median"
            : "describe";
        var numeric = frame.NumericColumns.Select(c => c.Name).ToList();
        var column = partial?.Column ?? numeric.FirstOrDefault(c => q.Contains(c.ToLowerInvariant())) ?? numeric.FirstOrDefault();
        var groupBy = partial?.GroupBy ?? frame.Columns.Where(c => c is not NumericSeries).Select(c => c.Name)
            .FirstOrDefault(c => q.Contains("by " + c.ToLowerInvariant()) || q.Contains("per " + c.ToLowerInvariant()) || q.Contains(c.ToLowerInvariant()));
        return new TableQuery(operation, column, groupBy, partial?.FilterColumn, partial?.FilterOperator, partial?.FilterValue, partial?.Top ?? 10, operation != "min");
    }

    public static (IReadOnlyList<(string Key, double Value)> Rows, double? Scalar) Execute(DataFrame frame, TableQuery query)
    {
        var mask = Enumerable.Range(0, frame.RowCount).Where(i => Matches(frame, i, query)).ToArray();
        var values = query.Column is not null && frame.HasColumn(query.Column) && frame[query.Column] is NumericSeries ns ? ns.Values : null;

        if (query.Operation == "describe" && values is null)
        {
            return (frame.NumericColumns.Select(c => (c.Name, mask.Select(i => c.Values[i]).Where(double.IsFinite).DefaultIfEmpty(0).Average())).ToArray(), null);
        }

        if (query.GroupBy is not null && frame.HasColumn(query.GroupBy))
        {
            var keys = frame[query.GroupBy];
            var groups = mask.GroupBy(i => Convert.ToString(keys.GetValue(i), CultureInfo.InvariantCulture) ?? "(empty)")
                .Select(g => (Key: g.Key, Value: Aggregate(query.Operation, g.Select(i => values?[i] ?? 1).Where(double.IsFinite).ToArray())));
            groups = query.Descending ? groups.OrderByDescending(g => g.Value) : groups.OrderBy(g => g.Value);
            return (groups.Take(query.Top).ToArray(), null);
        }

        if (query.Operation == "count")
        {
            return ([], mask.Length);
        }

        if (values is null)
        {
            return ([], null);
        }

        if (query.Operation == "top")
        {
            var label = frame.Columns.FirstOrDefault(c => c is not NumericSeries);
            var ordered = mask.Select(i => (Key: label is null ? $"Row {i + 1}" : Convert.ToString(label.GetValue(i), CultureInfo.InvariantCulture) ?? $"Row {i + 1}", Value: values[i]))
                .Where(r => double.IsFinite(r.Value));
            ordered = query.Descending ? ordered.OrderByDescending(r => r.Value) : ordered.OrderBy(r => r.Value);
            return (ordered.Take(query.Top).ToArray(), null);
        }

        return ([], Aggregate(query.Operation, mask.Select(i => values[i]).Where(double.IsFinite).ToArray()));
    }

    private static double Aggregate(string operation, double[] values) => values.Length == 0 ? 0 : operation switch
    {
        "sum" or "top" => values.Sum(),
        "mean" or "describe" => values.Average(),
        "count" => values.Length,
        "min" => values.Min(),
        "max" => values.Max(),
        "median" => Median(values),
        _ => values.Sum(),
    };

    private static double Median(double[] values)
    {
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid];
    }

    private static bool Matches(DataFrame frame, int row, TableQuery query)
    {
        if (query.FilterColumn is null || query.FilterValue is null || !frame.HasColumn(query.FilterColumn))
        {
            return true;
        }

        var cell = frame[query.FilterColumn].GetValue(row);
        var text = Convert.ToString(cell, CultureInfo.InvariantCulture) ?? string.Empty;
        var numeric = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                      & double.TryParse(query.FilterValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var b);
        return query.FilterOperator switch
        {
            ">" => numeric && a > b,
            "<" => numeric && a < b,
            ">=" => numeric && a >= b,
            "<=" => numeric && a <= b,
            "!=" => !text.Equals(query.FilterValue, StringComparison.OrdinalIgnoreCase),
            "contains" => text.Contains(query.FilterValue, StringComparison.OrdinalIgnoreCase),
            _ => text.Equals(query.FilterValue, StringComparison.OrdinalIgnoreCase),
        };
    }

    public static string Describe(TableQuery q) =>
        $"{q.Operation}({q.Column ?? "*"})" + (q.GroupBy is null ? string.Empty : $" by {q.GroupBy}") +
        (q.FilterColumn is null ? string.Empty : $" where {q.FilterColumn} {q.FilterOperator ?? "="} {q.FilterValue}");
}
