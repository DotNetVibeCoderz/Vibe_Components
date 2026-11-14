using System.Text.Json;
using Electrical.Core.Network;
using Electrical.GIS;
using Electrical.PowerFlow;
using static Electrical.Gallery.Shared.Fmt;

namespace Electrical.Gallery.Shared;

public sealed record GisDemoResult(
    int Buses, double LoadScale, bool Continuous, string MapSvg, string GeoJsonSnippet, int GeoJsonFeatures, int GeoJsonBytes,
    double LineKm, BusHit? Nearest, GeoPoint Probe, IReadOnlyList<(int Bus, double Vm)> WeakestBuses, IReadOnlyList<Kpi> Kpis);

/// <summary>Geo-located synthetic 150 kV grid on a Web Mercator map (Electrical.GIS).</summary>
public static class GisScenario
{
    public static readonly int[] Sizes = [25, 49, 100];

    /// <summary>Jakarta (Monas) — the probe point for the nearest-substation search.</summary>
    public static readonly GeoPoint Probe = new(-6.1754, 106.8272);

    public static PowerNetwork Network(int buses)
    {
        var net = TestCases.SyntheticGrid(buses);
        // Deterministic jitter so the grid looks like real substations rather than a lattice.
        var rng = new Random(1150);
        foreach (var b in net.Buses)
            if (b.Location is GeoPoint p)
                b.Location = new GeoPoint(p.Latitude + (rng.NextDouble() - 0.5) * 0.026, p.Longitude + (rng.NextDouble() - 0.5) * 0.026);
        LineLengths.Assign(net, routeFactor: 1.08);
        return net;
    }

    public static GisDemoResult Run(int buses = 49, double loadScale = 1.25, bool continuous = true, string lang = "en")
    {
        // Only loads grow: the extra demand is imported through the slack substation, so loading and voltage drop build up across the map.
        var net = Network(buses);
        foreach (var l in net.Loads) { l.P *= loadScale; l.Q *= loadScale; }
        var pf = PowerFlowSolver.Solve(net);
        string svg = GisSvgRenderer.Render(net, pf, new GisSvgOptions
        {
            Width = 860, Height = 600, ContinuousColors = continuous, BusRadius = buses > 60 ? 4.2 : 5.5, ShowLabels = buses <= 49,
        });

        string json = GeoJsonExporter.Export(net, pf);
        using var parsed = JsonDocument.Parse(json);
        int features = parsed.RootElement.GetProperty("features").GetArrayLength();
        string firstFeature = GalleryJson.Indent(parsed.RootElement.GetProperty("features")[0]);
        string snippet = "{\n  \"type\": \"FeatureCollection\",\n  \"features\": [\n    " + firstFeature.Replace("\n", "\n    ", StringComparison.Ordinal) + ",\n    …\n  ]\n}";

        double km = net.Branches.Sum(b => b.LengthKm);
        var nearest = new BusSpatialIndex(net).Nearest(Probe);
        var weakest = pf.Buses.OrderBy(b => b.Vm).Take(5).Select(b => (b.BusId, b.Vm)).ToList();
        var most = pf.MostLoadedBranch;
        var kpis = new List<Kpi>
        {
            new(new("Substations", "Gardu induk"), net.BusCount.ToString(), null, new($"{net.Branches.Count} lines · 150 kV", $"{net.Branches.Count} saluran · 150 kV")),
            new(new("Route length", "Panjang rute"), F(km, "0"), "km", new("haversine × 1.08 routing", "haversine × 1,08 rute")),
            new(new("Min voltage", "Tegangan min"), F(pf.MinVoltageBus.Vm, "0.000"), "pu", new($"bus {pf.MinVoltageBus.BusId}", $"bus {pf.MinVoltageBus.BusId}"),
                pf.MinVoltageBus.Vm < 0.95 ? "alarm" : pf.MinVoltageBus.Vm < 0.98 ? "warn" : "ok"),
            new(new("Max loading", "Pembebanan maks"), F(most?.LoadingPercent ?? 0, "0"), "%", new($"line {most?.From}–{most?.To}", $"saluran {most?.From}–{most?.To}"),
                most is { LoadingPercent: > 100 } ? "alarm" : most is { LoadingPercent: > 70 } ? "warn" : "ok"),
            new(new("Nearest to Monas", "Terdekat ke Monas"), nearest is { } n ? $"B{n.BusId}" : "—", null, nearest is { } m ? new($"{F(m.DistanceKm, "0.0")} km away", $"berjarak {F(m.DistanceKm, "0.0")} km") : null),
        };
        return new GisDemoResult(buses, loadScale, continuous, svg, snippet, features, json.Length, km, nearest, Probe, weakest, kpis);
    }
}

internal static class GalleryJson
{
    public static string Indent(JsonElement e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true })) e.WriteTo(w);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }
}
