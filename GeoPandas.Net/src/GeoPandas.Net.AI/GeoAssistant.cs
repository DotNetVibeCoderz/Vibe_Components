using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GeoPandas.Net.AI;

/// <summary>A single whitelisted step of a query plan.</summary>
public sealed record PlanStep(string Op, IReadOnlyDictionary<string, JsonElement> Args);

/// <summary>Executable, auditable query plan produced by the LLM (no code is ever executed).</summary>
public sealed record GeoQueryPlan(IReadOnlyList<PlanStep> Steps, string Explanation)
{
    public static GeoQueryPlan Parse(string json)
    {
        var text = ExtractJson(json);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var steps = new List<PlanStep>();
        foreach (var s in root.GetProperty("steps").EnumerateArray())
        {
            var op = s.GetProperty("op").GetString() ?? throw new FormatException("Step without 'op'.");
            var args = s.EnumerateObject().Where(p => p.Name != "op").ToDictionary(p => p.Name, p => p.Value.Clone());
            steps.Add(new PlanStep(op, args));
        }
        var expl = root.TryGetProperty("explanation", out var e) ? e.GetString() ?? "" : "";
        return new GeoQueryPlan(steps, expl);
    }

    /// <summary>Extracts the first JSON object from model output (tolerates ```json fences and prose).</summary>
    internal static string ExtractJson(string s)
    {
        int start = s.IndexOf('{');
        int end = s.LastIndexOf('}');
        if (start < 0 || end <= start) throw new FormatException("The model response does not contain a JSON object.");
        return s[start..(end + 1)];
    }
}

/// <summary>Answer to a natural-language question.</summary>
public sealed record GeoAnswer(string Question, GeoQueryPlan Plan, DataFrame Result, string Explanation);

/// <summary>
/// GeoLLM assistant: grounds an LLM on a GeoDataFrame schema, asks it for a JSON plan composed only of
/// whitelisted operations (filter, sort, head, select, groupby, buffer, area, length, centroid, to_crs, h3_bin, within_distance, simplify, unique,
/// dissolve, bbox, nearest_to, count) and executes that plan deterministically.
/// </summary>
public sealed class GeoAssistant(ILlmClient llm)
{
    public const string SystemPrompt = """
        You are GeoLLM, a geospatial data assistant for the GeoPandas.Net library.
        You receive a dataset schema and a user question. Respond ONLY with a JSON object:
        {"steps":[{"op":"...", ...args}], "explanation":"one or two sentences for the user, in the user's language"}
        Allowed ops (apply in order to the current table):
        - {"op":"filter","column":C,"cmp":"==|!=|>|>=|<|<=|contains|startswith","value":V}
        - {"op":"sort","column":C,"ascending":true|false}
        - {"op":"head","n":N}
        - {"op":"select","columns":[C,...]}
        - {"op":"area","as":"area_m2"}            (planar area; reprojects to UTM first when CRS is geographic)
        - {"op":"length","as":"length_m"}          (same rules as area)
        - {"op":"centroid"}
        - {"op":"buffer","distance":D}             (metres; reprojects to UTM when geographic)
        - {"op":"to_crs","crs":"EPSG:xxxx"}
        - {"op":"bbox","minx":..,"miny":..,"maxx":..,"maxy":..}  (in the dataset CRS)
        - {"op":"nearest_to","x":X,"y":Y,"k":K}    (k nearest features to a point in the dataset CRS)
        - {"op":"dissolve","by":C}
        - {"op":"groupby","by":C,"agg":[{"column":C2,"func":"sum|mean|min|max|count|median"}]}
        - {"op":"count"}
        - {"op":"h3_bin","res":0-15,"value":C?}   (H3 hexagon binning: count, and sum/mean of C per hexagon; WGS 84 output)
        - {"op":"within_distance","x":X,"y":Y,"distance":D}   (features within D of a point, dataset CRS units, with a distance column)
        - {"op":"simplify","tolerance":T}         (Douglas-Peucker, dataset CRS units)
        - {"op":"unique","column":C}               (distinct values of C with their counts)
        Use only column names from the schema. Never invent columns. Prefer few steps.
        """;

    public ILlmClient Llm { get; } = llm;

    /// <summary>Compact, token-efficient schema description used to ground the model.</summary>
    public static string DescribeSchema(GeoDataFrame gdf, int sampleValues = 3)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"rows: {gdf.RowCount}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"crs: {gdf.Crs?.ToString() ?? "unknown"} ({(gdf.IsGeographic ? "geographic lon/lat degrees" : "projected")})");
        var b = gdf.TotalBounds;
        if (!b.IsNull) sb.AppendLine(CultureInfo.InvariantCulture, $"bounds: minx={b.MinX:G8} miny={b.MinY:G8} maxx={b.MaxX:G8} maxy={b.MaxY:G8}");
        var types = gdf.Geometry.GeomType.NonNull().Distinct().Take(5);
        sb.AppendLine($"geometry types: {string.Join(", ", types)}");
        sb.AppendLine("columns:");
        foreach (var c in gdf.Columns)
        {
            if (c is GeoSeries) continue;
            string type = c.DataType == typeof(double) ? "float" : c.DataType == typeof(long) ? "int" : c.DataType == typeof(bool) ? "bool" : c.DataType == typeof(DateTime) ? "datetime" : "string";
            var samples = Enumerable.Range(0, c.Length).Where(i => !c.IsNull(i)).Select(c.Format).Distinct().Take(sampleValues);
            string extra = c.IsNumeric ? string.Create(CultureInfo.InvariantCulture, $" min={c.Min():G6} max={c.Max():G6}") : "";
            sb.AppendLine($"  - {c.Name} ({type}){extra} e.g. {string.Join(" | ", samples)}");
        }
        return sb.ToString();
    }

    /// <summary>Asks the model for a plan and executes it.</summary>
    public async Task<GeoAnswer> AskAsync(GeoDataFrame gdf, string question, CancellationToken ct = default)
    {
        var prompt = $"Dataset schema:\n{DescribeSchema(gdf)}\nQuestion: {question}";
        var raw = await Llm.CompleteAsync(SystemPrompt, prompt, ct).ConfigureAwait(false);
        var plan = GeoQueryPlan.Parse(raw);
        var result = Execute(gdf, plan);
        return new GeoAnswer(question, plan, result, plan.Explanation);
    }

    /// <summary>Natural-language summary of a dataset.</summary>
    public Task<string> SummarizeAsync(GeoDataFrame gdf, string language = "English", CancellationToken ct = default) =>
        Llm.CompleteAsync("You are a concise GIS analyst. Answer in plain text, max 6 sentences.",
            $"Summarize this geospatial dataset for a decision maker in {language}. Mention extent, notable attributes and possible analyses.\n{DescribeSchema(gdf, 5)}", ct);

    /// <summary>Executes a plan deterministically (whitelisted operations only).</summary>
    public static DataFrame Execute(GeoDataFrame source, GeoQueryPlan plan)
    {
        DataFrame current = source;
        foreach (var step in plan.Steps)
        {
            var a = step.Args;
            string S(string k) => a.TryGetValue(k, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText() : throw new FormatException($"'{step.Op}' needs '{k}'.");
            double D(string k) => a.TryGetValue(k, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.Parse(v.GetString()!, CultureInfo.InvariantCulture) : throw new FormatException($"'{step.Op}' needs '{k}'.");
            GeoDataFrame G() => current as GeoDataFrame ?? throw new InvalidOperationException($"'{step.Op}' needs geometry, but the table has none at this point.");

            current = step.Op.ToLowerInvariant() switch
            {
                "filter" => Filter(current, S("column"), a.TryGetValue("cmp", out var cmp) ? cmp.GetString() ?? "==" : "==", a["value"]),
                "sort" => current.SortBy(S("column"), !a.TryGetValue("ascending", out var asc) || asc.ValueKind != JsonValueKind.False),
                "head" => current.Head((int)D("n")),
                "select" => current is GeoDataFrame g0 ? g0.Select(a["columns"].EnumerateArray().Select(x => x.GetString()!).Where(current.HasColumn).ToArray())
                                                       : current.Select(a["columns"].EnumerateArray().Select(x => x.GetString()!).ToArray()),
                "area" => Metric(G(), g => g.Area(), a.TryGetValue("as", out var an) ? an.GetString()! : "area_m2"),
                "length" => Metric(G(), g => g.GeomLength(), a.TryGetValue("as", out var ln) ? ln.GetString()! : "length_m"),
                "centroid" => G().Centroid(),
                "buffer" => Metric2(G(), g => g.Buffer(D("distance"))),
                "to_crs" => G().ToCrs(S("crs")),
                "bbox" => G().Cx(D("minx"), D("miny"), D("maxx"), D("maxy")),
                "nearest_to" => Nearest(G(), D("x"), D("y"), a.TryGetValue("k", out var k) ? k.GetInt32() : 1),
                "dissolve" => G().Dissolve(S("by"), AggFunc.Sum),
                "groupby" => current.GroupBy(S("by")).Agg(a["agg"].EnumerateArray().Select(x =>
                    (x.GetProperty("column").GetString()!, ParseAgg(x.GetProperty("func").GetString()!), $"{x.GetProperty("column").GetString()}_{x.GetProperty("func").GetString()}")).ToArray()),
                "count" => new DataFrame(Column.Create("count", new[] { (long)current.RowCount })),
                "h3_bin" => H3Frame.Bin(G(), (int)D("res"), a.TryGetValue("value", out var hv) ? hv.GetString() : null),
                "within_distance" => WithinDistance(G(), D("x"), D("y"), D("distance")),
                "simplify" => Metric2(G(), g => g.Simplify(D("tolerance"))),
                "unique" => Unique(current, S("column")),
                _ => throw new InvalidOperationException($"Operation '{step.Op}' is not allowed.")
            };
        }
        return current;
    }

    private static AggFunc ParseAgg(string f) => f.ToLowerInvariant() switch
    {
        "sum" => AggFunc.Sum, "mean" or "avg" => AggFunc.Mean, "min" => AggFunc.Min, "max" => AggFunc.Max,
        "count" => AggFunc.Count, "median" => AggFunc.Median, _ => throw new FormatException($"Unknown aggregation '{f}'.")
    };

    private static DataFrame Filter(DataFrame df, string column, string cmp, JsonElement value)
    {
        var c = df[column];
        var mask = new bool[df.RowCount];
        if (c.IsNumeric && value.ValueKind == JsonValueKind.Number)
        {
            double v = value.GetDouble();
            for (int i = 0; i < mask.Length; i++)
            {
                double x = c.GetDouble(i);
                mask[i] = !double.IsNaN(x) && cmp switch
                {
                    "==" => x == v, "!=" => x != v, ">" => x > v, ">=" => x >= v, "<" => x < v, "<=" => x <= v, _ => false
                };
            }
        }
        else
        {
            string v = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
            for (int i = 0; i < mask.Length; i++)
            {
                var x = c.GetValue(i)?.ToString();
                if (x is null) continue;
                mask[i] = cmp switch
                {
                    "==" => string.Equals(x, v, StringComparison.OrdinalIgnoreCase),
                    "!=" => !string.Equals(x, v, StringComparison.OrdinalIgnoreCase),
                    "contains" => x.Contains(v, StringComparison.OrdinalIgnoreCase),
                    "startswith" => x.StartsWith(v, StringComparison.OrdinalIgnoreCase),
                    _ => string.CompareOrdinal(x, v) switch { var r => cmp switch { ">" => r > 0, ">=" => r >= 0, "<" => r < 0, "<=" => r <= 0, _ => false } }
                };
            }
        }
        return df.Filter(mask);
    }

    private static GeoDataFrame Metric(GeoDataFrame gdf, Func<GeoDataFrame, Column<double>> f, string name)
    {
        var projected = gdf.IsGeographic ? gdf.ToCrs(gdf.EstimateUtmCrs()) : gdf;
        return gdf.WithColumn(f(projected).Rename(name));
    }

    private static GeoDataFrame Metric2(GeoDataFrame gdf, Func<GeoDataFrame, GeoDataFrame> f)
    {
        if (!gdf.IsGeographic) return f(gdf);
        var crs = gdf.Crs!;
        return f(gdf.ToCrs(gdf.EstimateUtmCrs())).ToCrs(crs);
    }

    private static GeoDataFrame WithinDistance(GeoDataFrame gdf, double x, double y, double distance)
    {
        var p = Geometry.Geo.Point(x, y);
        var rows = Enumerable.Range(0, gdf.RowCount).Select(i => (i, d: gdf.Geometry[i]?.Distance(p) ?? double.PositiveInfinity))
            .Where(t => t.d <= distance).OrderBy(t => t.d).ToList();
        return gdf.Take(rows.Select(t => t.i).ToArray()).WithColumn(Column.Create("distance", rows.Select(t => t.d).ToArray()));
    }

    private static DataFrame Unique(DataFrame df, string column)
    {
        var groups = Enumerable.Range(0, df.RowCount).GroupBy(i => Convert.ToString(df[column].GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "")
            .OrderByDescending(g => g.Count()).ToList();
        return new DataFrame(Column.Create(column, groups.Select(g => g.Key).ToArray()), Column.Create("count", groups.Select(g => (long)g.Count()).ToArray()));
    }

    private static GeoDataFrame Nearest(GeoDataFrame gdf, double x, double y, int k)
    {
        var p = Geometry.Geo.Point(x, y);
        var res = gdf.SIndex.Nearest(x, y, k, i => gdf.Geometry[i]!.Distance(p));
        var picked = gdf.Take(res.Select(r => r.Item).ToArray());
        return picked.WithColumn(Column.Create("distance", res.Select(r => r.Distance).ToArray()));
    }
}
