using System.Globalization;

namespace Electrical.Gallery.Shared;

// ----------------------------------------------------------------------------------------------------------------------------
// Declarative demos: a scenario declares its controls (Params) and returns KPIs + content blocks. Both apps render these with one
// generic view (web: Components/Demos/InteractiveView.razor, desktop: Views.Interactive), so a new demo needs no UI code at all.
// ----------------------------------------------------------------------------------------------------------------------------

/// <summary>Table cell. <see cref="Tone"/> is "ok", "warn", "alarm" or "na" to render a status chip; numeric cells are right-aligned.</summary>
public readonly record struct Cell(string Text, string? Tone = null, bool Numeric = false)
{
    public static implicit operator Cell(string text) => new(text);
    public static Cell Num(double v, string fmt = "0.00") => new(double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—", null, true);
    public static Cell Chip(string text, string tone) => new(text, tone);
}

/// <summary>Content block of a declarative demo. <see cref="Wide"/> blocks span the full width; others pair up in two columns.</summary>
public abstract record Block(L Title, L? Meta, bool Wide);

/// <summary>An Electrical.Visualization / GIS SVG.</summary>
public sealed record ChartBlock(L Title, string Svg, L? Meta = null, bool Wide = false) : Block(Title, Meta, Wide);

/// <summary>Data table.</summary>
public sealed record TableBlock(L Title, L[] Headers, IReadOnlyList<Cell[]> Rows, L? Meta = null, bool Wide = false) : Block(Title, Meta, Wide);

/// <summary>Code or data listing ("csharp", "xml", "json", "text").</summary>
public sealed record ListingBlock(L Title, string Text, string Language = "text", L? Meta = null, bool Wide = false) : Block(Title, Meta, Wide);

/// <summary>Explanatory note (full width).</summary>
public sealed record NoteBlock(L Text) : Block(new("", ""), null, true);

public sealed record InteractiveResult(IReadOnlyList<Kpi> Kpis, IReadOnlyList<Block> Blocks);

/// <summary>Control declared by a scenario.</summary>
public abstract record Param(string Key, L Label);

/// <summary>Segmented choice.</summary>
public sealed record ChoiceParam(string Key, L Label, IReadOnlyList<(string Value, L Text)> Options, string Default) : Param(Key, Label);

/// <summary>Slider.</summary>
public sealed record RangeParam(string Key, L Label, double Min, double Max, double Step, double Default, string Format = "0.##", string Unit = "") : Param(Key, Label);

/// <summary>Current control values (strings; numbers in invariant culture).</summary>
public sealed class ParamValues
{
    private readonly Dictionary<string, string> _v;
    public ParamValues(IEnumerable<Param> ps) => _v = ps.ToDictionary(p => p.Key, p => p switch
    {
        ChoiceParam c => c.Default,
        RangeParam r => r.Default.ToString(CultureInfo.InvariantCulture),
        _ => "",
    });
    public string this[string key] { get => _v[key]; set => _v[key] = value; }
    public double D(string key) => double.Parse(_v[key], CultureInfo.InvariantCulture);
    public int I(string key) => (int)Math.Round(D(key));
    public string Get(string key, string fallback) => _v.TryGetValue(key, out var x) ? x : fallback;
    public bool Has(string key) => _v.ContainsKey(key);
    public ParamValues Clone() => new(new Dictionary<string, string>(_v));
    private ParamValues(Dictionary<string, string> v) => _v = new(v);
}

/// <summary>A demo implemented declaratively.</summary>
public interface IInteractiveScenario
{
    string Id { get; }
    IReadOnlyList<Param> Params(ParamValues current);
    InteractiveResult Run(ParamValues values, string lang);
}

/// <summary>Registry of declarative demos (keyed by catalog id).</summary>
public static class InteractiveScenarios
{
    public static readonly IReadOnlyDictionary<string, IInteractiveScenario> All = new IInteractiveScenario[]
    {
        new ShortCircuitDemo(), new ProtectionDemo(), new StabilityDemo(), new HarmonicDemo(), new CircuitDemo(),
        new RenewableDemo(), new SmartGridDemo(), new OptimizationDemo(), new StateEstimationDemo(), new ForecastingDemo(), new SimulationDemo(), new VoltageControlDemo(), new DigitalTwinDemo(), new InteropDemo(), new DistributionDemo(),
        new AcOpfDemo(), new ScucDemo(), new MarketDemo(),
    }.ToDictionary(s => s.Id);

    public static IInteractiveScenario? Find(string id) => All.TryGetValue(id, out var s) ? s : null;

    /// <summary>Default values for a scenario (params can depend on values, e.g. a "view" switch, so iterate to a fixed point).</summary>
    public static ParamValues Defaults(IInteractiveScenario s)
    {
        var v = new ParamValues(s.Params(new ParamValues(Array.Empty<Param>())));
        return Sync(s, v);
    }

    /// <summary>Adds defaults for params that appeared after a value change (keeps existing values).</summary>
    public static ParamValues Sync(IInteractiveScenario s, ParamValues v)
    {
        foreach (var p in s.Params(v))
        {
            if (!v.Has(p.Key))
                v[p.Key] = p switch { ChoiceParam c => c.Default, RangeParam r => r.Default.ToString(CultureInfo.InvariantCulture), _ => "" };
        }
        return v;
    }
}

internal static class Lx
{
    /// <summary>Shorthand for identical EN/ID text (symbols, units, proper names).</summary>
    public static L Same(string s) => new(s, s);
    public static string Pick(string lang, string en, string id) => lang == "id" ? id : en;
}
