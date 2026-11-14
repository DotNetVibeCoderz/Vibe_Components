using Electrical.Core.Network;
using Electrical.SCADA;
using Electrical.StateEstimation;

namespace Electrical.DigitalTwin;

/// <summary>Standard deviations given to measurements mapped from SCADA tags.</summary>
/// <param name="VoltagePu">σ of voltage magnitudes [pu].</param>
/// <param name="PowerMw">σ of active/reactive flows and injections [MW / Mvar].</param>
/// <param name="AngleDeg">σ of voltage angles [degrees] (only used when <paramref name="IncludeAngles"/> is true).</param>
/// <param name="IncludeAngles">Map BUSn.VA tags as PMU angle measurements (off: conventional SCADA has no angles).</param>
public sealed record TagMeasurementSigmas(double VoltagePu = 0.002, double PowerMw = 0.1, double AngleDeg = 0.05, bool IncludeAngles = false);

/// <summary>A parsed SCADA point name.</summary>
/// <param name="Kind">"VM", "VA", "KV", "PF", "QF", "PT", "QT", "LOAD", "CLOSED", "PI", "QI", "P", "Q".</param>
/// <param name="Element">"BUS", "BR" or "GEN".</param>
/// <param name="Id">Bus id, branch index or generator index.</param>
public readonly record struct TagPoint(string Element, int Id, string Kind);

/// <summary>
/// Maps RTU/SCADA tag names (the <see cref="RtuSimulator"/> naming: <c>BUS{id}.VM</c>, <c>BUS{id}.VA</c>, <c>BUS{id}.PI</c>/<c>.QI</c> (net injection),
/// <c>BR{k}.PF/.QF/.PT/.QT</c>, <c>BR{k}.CLOSED</c>) to state-estimation <see cref="Measurement"/> objects.
/// Tags with a quality other than Good are skipped; flows of open branches are ignored.
/// </summary>
public static class TagMeasurementMapper
{
    /// <summary>Parses a tag name (after removing <paramref name="prefix"/>); false for unrelated tags.</summary>
    public static bool TryParse(string name, out TagPoint point, string prefix = "")
    {
        point = default;
        if (prefix.Length > 0)
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            name = name[prefix.Length..];
        }
        int dot = name.IndexOf('.');
        if (dot <= 0) return false;
        string head = name[..dot], kind = name[(dot + 1)..].ToUpperInvariant();
        string element = head.StartsWith("BUS", StringComparison.OrdinalIgnoreCase) ? "BUS"
            : head.StartsWith("BR", StringComparison.OrdinalIgnoreCase) ? "BR"
            : head.StartsWith("GEN", StringComparison.OrdinalIgnoreCase) ? "GEN" : "";
        if (element.Length == 0 || !int.TryParse(head.AsSpan(element.Length), out int id)) return false;
        point = new TagPoint(element, id, kind);
        return true;
    }

    /// <summary>Converts the Good-quality tags of a database to measurements for <paramref name="net"/>.</summary>
    /// <param name="branchClosed">Receives breaker statuses found in BRk.CLOSED tags (index → closed).</param>
    public static List<Measurement> ToMeasurements(TagDatabase tags, PowerNetwork net, TagMeasurementSigmas? sigmas = null, string prefix = "",
        IDictionary<int, bool>? branchClosed = null)
    {
        var s = sigmas ?? new TagMeasurementSigmas();
        var list = new List<Measurement>();
        var all = tags.All;
        var status = new Dictionary<int, bool>();
        foreach (var t in all)
            if (t.Quality == TagQuality.Good && TryParse(t.Name, out var p, prefix) && p is { Element: "BR", Kind: "CLOSED" } && p.Id < net.Branches.Count)
                status[p.Id] = t.Value >= 0.5;
        if (branchClosed is not null) foreach (var kv in status) branchClosed[kv.Key] = kv.Value;
        bool Closed(int k) => status.TryGetValue(k, out var c) ? c : net.Branches[k].InService;

        foreach (var t in all)
        {
            if (t.Quality != TagQuality.Good || double.IsNaN(t.Value) || double.IsInfinity(t.Value)) continue;
            if (!TryParse(t.Name, out var p, prefix)) continue;
            switch (p.Element)
            {
                case "BUS" when net.ContainsBus(p.Id):
                    switch (p.Kind)
                    {
                        case "VM": list.Add(new Measurement(MeasurementType.VoltageMagnitude, p.Id, t.Value, s.VoltagePu)); break;
                        case "VA" when s.IncludeAngles: list.Add(new Measurement(MeasurementType.VoltageAngle, p.Id, t.Value, s.AngleDeg)); break;
                        case "PI": list.Add(new Measurement(MeasurementType.ActiveInjection, p.Id, t.Value, s.PowerMw)); break;
                        case "QI": list.Add(new Measurement(MeasurementType.ReactiveInjection, p.Id, t.Value, s.PowerMw)); break;
                    }
                    break;
                case "BR" when p.Id >= 0 && p.Id < net.Branches.Count && Closed(p.Id):
                    switch (p.Kind)
                    {
                        case "PF": list.Add(new Measurement(MeasurementType.ActiveFlow, p.Id, t.Value, s.PowerMw, BranchEnd.From)); break;
                        case "QF": list.Add(new Measurement(MeasurementType.ReactiveFlow, p.Id, t.Value, s.PowerMw, BranchEnd.From)); break;
                        case "PT": list.Add(new Measurement(MeasurementType.ActiveFlow, p.Id, t.Value, s.PowerMw, BranchEnd.To)); break;
                        case "QT": list.Add(new Measurement(MeasurementType.ReactiveFlow, p.Id, t.Value, s.PowerMw, BranchEnd.To)); break;
                    }
                    break;
            }
        }
        return list;
    }
}
