using System.Numerics;
using Electrical.Core.Network;
using Electrical.Visualization;

namespace Electrical.DigitalTwin.ThreeD;

/// <summary>Dimensions and switches of <see cref="GridSceneBuilder.Build"/> (all lengths in metres).</summary>
public sealed record GridSceneOptions
{
    /// <summary>Scene metres per single-line-diagram unit when no bus locations are available.</summary>
    public float LayoutScale { get; init; } = 0.35f;
    /// <summary>Scene metres per kilometre when all buses have <see cref="Bus.Location"/> (geographic layout).</summary>
    public float MetresPerKm { get; init; } = 120f;
    /// <summary>Force the single-line layout even when locations exist.</summary>
    public bool IgnoreLocations { get; init; }
    public float BusbarHeight { get; init; } = 7f;
    public float MinBusbarLength { get; init; } = 16f;
    public float TowerHeight { get; init; } = 16f;
    /// <summary>Maximum span between towers.</summary>
    public float SpanLength { get; init; } = 45f;
    /// <summary>Mid-span conductor sag.</summary>
    public float Sag { get; init; } = 1.6f;
    /// <summary>Horizontal phase spacing of overhead conductors.</summary>
    public float PhaseSpacing { get; init; } = 2.2f;
    public float ConductorRadius { get; init; } = 0.12f;
    public float CableRadius { get; init; } = 0.35f;
    public bool ShowGenerators { get; init; } = true;
    public bool ShowLoads { get; init; } = true;
    public bool ShowGround { get; init; } = true;
    /// <summary>Label busbars (name and, after live data, voltage).</summary>
    public bool Labels { get; init; } = true;
    /// <summary>Label lines, cables and transformers (name and loading).</summary>
    public bool BranchLabels { get; init; }
    /// <summary>Saturation span of the bus voltage ramp [pu].</summary>
    public double VoltageSpanPu { get; init; } = 0.1;
    /// <summary>Reference for unrated branches [MVA]; null = the largest flow of the applied result.</summary>
    public double? UnratedReferenceMva { get; init; }
    public double AlarmFlashHz { get; init; } = 1.5;
    /// <summary>Buses drawn as substations (with a gantry); null = slack and generator buses and transformer terminals.</summary>
    public IReadOnlyCollection<int>? SubstationBuses { get; init; }
}

/// <summary>
/// Procedural 3D scene from a <see cref="PowerNetwork"/>: busbars on post insulators at every bus, a breaker and a disconnector bay at the
/// from end of every branch, transformers (tank, bushings, radiators) at transformer branches, overhead lines with towers every
/// <see cref="GridSceneOptions.SpanLength"/> and three catenary conductors, underground cables with terminations, gantries at
/// substation buses, and generator / load symbols. Bus positions come from <see cref="Bus.Location"/> (equirectangular projection)
/// when every bus has one, otherwise from the automatic single-line layout <see cref="SldLayout"/> (layers become depth).
/// </summary>
public static class GridSceneBuilder
{
    private static readonly Rgba Steel = Rgba.Parse("#9aa3ad");
    private static readonly Rgba Aluminium = Rgba.Parse("#c9ced6");
    private static readonly Rgba TankGrey = Rgba.Parse("#7d8b99");
    private static readonly Rgba CableBlack = Rgba.Parse("#2b2f36");
    private static readonly Rgba GroundGreen = Rgba.Parse("#5b7553");
    private static readonly Rgba GenBlue = Rgba.Parse("#3a6ea5");
    private static readonly Rgba LoadAmber = Rgba.Parse("#d08c2b");
    private static readonly Rgba Red = Rgba.Parse("#e5484d");

    /// <summary>Number of intermediate towers of a line between the busbar centres <paramref name="a"/> and <paramref name="b"/> (spans ≤ <see cref="GridSceneOptions.SpanLength"/>).</summary>
    public static int TowerCount(Vector3 a, Vector3 b, GridSceneOptions o) =>
        Math.Max(0, (int)Math.Ceiling(Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z)) / o.SpanLength) - 1);

    public static GridScene3D Build(PowerNetwork network, GridSceneOptions? options = null)
    {
        var o = options ?? new GridSceneOptions();
        var (pos, barLength) = Layout(network, o);
        var scene = new GridScene3D(network, o, pos);
        var subs = o.SubstationBuses?.ToHashSet() ?? DefaultSubstations(network);

        if (o.ShowGround)
        {
            Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
            foreach (var p in pos.Values) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            var pad = new Vector3(25, 0, 25);
            scene.Add(new SceneObject("Ground", GridElementKind.Ground, new MeshData().AddGround(lo - pad, hi + pad, -0.02f), GroundGreen, 0, 0.95f));
        }

        // Busbars: a horizontal tube on two post insulators.
        foreach (var bus in network.Buses)
        {
            var c = pos[bus.Id];
            float half = barLength[bus.Id] / 2;
            var m = new MeshData();
            Vector3 l = c + new Vector3(-half, o.BusbarHeight, 0), r = c + new Vector3(half, o.BusbarHeight, 0);
            m.AddCylinder(l, r, 0.18f, 0.18f, 10);
            foreach (var x in new[] { -half + 1, half - 1 })
            {
                m.AddBox(c + new Vector3(x, 0.4f, 0), new Vector3(0.5f, 0.4f, 0.5f));
                m.AddCylinder(c + new Vector3(x, 0.8f, 0), c + new Vector3(x, o.BusbarHeight - 0.1f, 0), 0.22f, 0.16f, 10);
            }
            scene.Add(new SceneObject($"Busbar {bus.Id}", GridElementKind.Busbar, m, Aluminium, 0.3f, 0.4f)
            {
                BusId = bus.Id, Label = o.Labels ? scene.BusName(bus.Id) : null, LabelAnchor = c + new Vector3(0, o.BusbarHeight + 2.5f, 0),
            });
            if (subs.Contains(bus.Id))
            {
                var g = new MeshData();
                float gh = o.BusbarHeight + 5, gx = half + 1.5f;
                g.AddBeam(c + new Vector3(-gx, 0, -2), c + new Vector3(-gx, gh, -2), 0.3f, 0.3f);
                g.AddBeam(c + new Vector3(gx, 0, -2), c + new Vector3(gx, gh, -2), 0.3f, 0.3f);
                g.AddBeam(c + new Vector3(-gx, gh, -2), c + new Vector3(gx, gh, -2), 0.3f, 0.4f);
                scene.Add(new SceneObject($"Gantry {bus.Id}", GridElementKind.Gantry, g, Steel, 0.6f, 0.5f) { BusId = bus.Id });
            }
        }

        // Attachment points along the busbars.
        var attach = Attachments(network, pos, barLength, o);

        for (int k = 0; k < network.Branches.Count; k++)
        {
            var br = network.Branches[k];
            var a = attach[(k, true)];
            var b = attach[(k, false)];
            var dir = new Vector3(b.X - a.X, 0, b.Z - a.Z);
            dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitZ;
            var side = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dir));
            var ground = new Vector3(a.X, 0, a.Z);

            // Switchgear bay at the from end: disconnector then breaker.
            var dPos = ground + dir * 4;
            var bPos = ground + dir * 8;
            scene.Add(new SceneObject($"Disconnector {br.From}-{br.To}#{k}", GridElementKind.Disconnector, Disconnector(dPos, side, br.InService), Steel, 0.7f, 0.4f) { BranchIndex = k });
            scene.Add(new SceneObject($"Breaker {br.From}-{br.To}#{k}", GridElementKind.Breaker, Breaker(bPos), Red, 0.3f, 0.5f) { BranchIndex = k });

            if (br.IsTransformer)
            {
                var mid = (ground + new Vector3(b.X, 0, b.Z)) / 2;
                var m = TransformerMesh(mid, dir, side, a, b);
                scene.Add(new SceneObject($"Transformer {br.From}-{br.To}#{k}", GridElementKind.Transformer, m, TankGrey, 0.4f, 0.6f)
                {
                    BranchIndex = k, Label = o.BranchLabels ? scene.BranchName(k) : null, LabelAnchor = mid + new Vector3(0, 7, 0),
                });
            }
            else if (br.Kind == BranchKind.Cable)
            {
                var m = new MeshData();
                var ga = new Vector3(a.X, 0.3f, a.Z) + dir * 10;
                var gb = new Vector3(b.X, 0.3f, b.Z) - dir * 2;
                m.AddCylinder(a, new Vector3(a.X, 0.3f, a.Z), o.CableRadius * 0.8f, o.CableRadius * 0.8f, 8);
                m.AddTube([new Vector3(a.X, 0.3f, a.Z), ga, gb, new Vector3(b.X, 0.3f, b.Z)], o.CableRadius, 8);
                m.AddCylinder(new Vector3(b.X, 0.3f, b.Z), b, o.CableRadius * 0.8f, o.CableRadius * 0.8f, 8);
                scene.Add(new SceneObject($"Cable {br.From}-{br.To}#{k}", GridElementKind.Cable, m, CableBlack, 0.1f, 0.8f)
                {
                    BranchIndex = k, Label = o.BranchLabels ? scene.BranchName(k) : null, LabelAnchor = (ga + gb) / 2 + new Vector3(0, 2, 0),
                });
            }
            else
            {
                int towers = TowerCount(pos[br.From], pos[br.To], o);
                var support = new List<Vector3> { a };
                for (int t = 1; t <= towers; t++)
                {
                    var p = Vector3.Lerp(new Vector3(a.X, 0, a.Z), new Vector3(b.X, 0, b.Z), (float)t / (towers + 1));
                    scene.Add(new SceneObject($"Tower {br.From}-{br.To}#{k}.{t}", GridElementKind.Tower, Tower(p, side, o), Steel, 0.7f, 0.5f) { BranchIndex = k });
                    support.Add(p + new Vector3(0, o.TowerHeight - 1.2f, 0));
                }
                support.Add(b);
                var m = new MeshData();
                for (int ph = -1; ph <= 1; ph++)
                {
                    var path = new List<Vector3>();
                    for (int s = 0; s < support.Count - 1; s++)
                    {
                        // Phases fan out to the tower crossarm spacing and converge on the busbar ends.
                        float wa = s == 0 ? 0.35f : 1f, wb = s == support.Count - 2 ? 0.35f : 1f;
                        var pa = support[s] + side * (ph * o.PhaseSpacing * wa);
                        var pb = support[s + 1] + side * (ph * o.PhaseSpacing * wb);
                        var pts = Catenary.Points(pa, pb, o.Sag, 12);
                        path.AddRange(s == 0 ? pts : pts.Skip(1));
                    }
                    m.AddTube(path, o.ConductorRadius, 6);
                }
                var midPt = (support[support.Count / 2] + support[(support.Count - 1) / 2]) / 2;
                scene.Add(new SceneObject($"Line {br.From}-{br.To}#{k}", GridElementKind.OverheadLine, m, Aluminium, 0.4f, 0.45f)
                {
                    BranchIndex = k, Label = o.BranchLabels ? scene.BranchName(k) : null, LabelAnchor = midPt + new Vector3(0, 3, 0),
                });
            }
        }

        if (o.ShowGenerators)
            for (int g = 0; g < network.Generators.Count; g++)
            {
                var gen = network.Generators[g];
                var c = pos[gen.Bus] + new Vector3(-barLength[gen.Bus] / 2 + 3, 0, -9);
                var m = new MeshData().AddBox(c + new Vector3(0, 1.5f, 0), new Vector3(2.5f, 1.5f, 1.8f))
                    .AddCylinder(c + new Vector3(-2.5f, 1.6f, 0), c + new Vector3(-4.5f, 1.6f, 0), 1.1f, 1.1f, 14)
                    .AddBeam(c + new Vector3(0, 3, 0), pos[gen.Bus] + new Vector3(-barLength[gen.Bus] / 2 + 3, o.BusbarHeight, 0), 0.08f, 0.08f);
                scene.Add(new SceneObject(gen.Name ?? $"Generator {g}", GridElementKind.Generator, m, GenBlue, 0.5f, 0.4f) { BusId = gen.Bus });
            }

        if (o.ShowLoads)
            for (int l = 0; l < network.Loads.Count; l++)
            {
                var load = network.Loads[l];
                var c = pos[load.Bus] + new Vector3(barLength[load.Bus] / 2 - 3, 0, 7);
                var m = new MeshData()
                    .AddCylinder(c + new Vector3(0, o.BusbarHeight - 0.2f, 0), c + new Vector3(0, 2.2f, 0), 0.15f, 0.15f, 8)
                    .AddCylinder(c + new Vector3(0, 2.2f, 0), c + new Vector3(0, 0.6f, 0), 0.9f, 0f, 14);
                scene.Add(new SceneObject(load.Name ?? $"Load {load.Bus}.{l}", GridElementKind.Load, m, LoadAmber, 0.2f, 0.6f) { BusId = load.Bus });
            }
        return scene;
    }

    private static HashSet<int> DefaultSubstations(PowerNetwork net)
    {
        var s = new HashSet<int>(net.Generators.Select(g => g.Bus));
        if (net.SlackBus is { } sb) s.Add(sb.Id);
        foreach (var br in net.Branches.Where(b => b.IsTransformer)) { s.Add(br.From); s.Add(br.To); }
        return s;
    }

    private static (Dictionary<int, Vector3> Pos, Dictionary<int, float> Length) Layout(PowerNetwork net, GridSceneOptions o)
    {
        var pos = new Dictionary<int, Vector3>();
        var len = new Dictionary<int, float>();
        if (!o.IgnoreLocations && net.Buses.Count > 0 && net.Buses.All(b => b.Location is not null))
        {
            double lat0 = net.Buses.Average(b => b.Location!.Value.Latitude), lon0 = net.Buses.Average(b => b.Location!.Value.Longitude);
            double kx = 111.320 * Math.Cos(lat0 * Math.PI / 180), kz = 110.574;
            foreach (var b in net.Buses)
            {
                var g = b.Location!.Value;
                pos[b.Id] = new Vector3((float)((g.Longitude - lon0) * kx * o.MetresPerKm), 0, (float)(-(g.Latitude - lat0) * kz * o.MetresPerKm));
                len[b.Id] = o.MinBusbarLength;
            }
            return (pos, len);
        }
        var sld = SldLayout.Compute(net);
        float cx = (float)(sld.Width / 2);
        foreach (var b in net.Buses)
        {
            var n = sld.Node(b.Id);
            pos[b.Id] = new Vector3(((float)n.X - cx) * o.LayoutScale, 0, (float)n.Y * o.LayoutScale);
            len[b.Id] = Math.Max(o.MinBusbarLength, (float)n.Width * o.LayoutScale);
        }
        return (pos, len);
    }

    private static Dictionary<(int Branch, bool From), Vector3> Attachments(PowerNetwork net, Dictionary<int, Vector3> pos,
        Dictionary<int, float> len, GridSceneOptions o)
    {
        var result = new Dictionary<(int, bool), Vector3>();
        var ends = new Dictionary<int, List<(int Branch, bool From, float OtherX)>>();
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var br = net.Branches[k];
            (ends.TryGetValue(br.From, out var lf) ? lf : ends[br.From] = []).Add((k, true, pos[br.To].X));
            (ends.TryGetValue(br.To, out var lt) ? lt : ends[br.To] = []).Add((k, false, pos[br.From].X));
        }
        foreach (var (bus, list) in ends)
        {
            var c = pos[bus];
            float half = len[bus] / 2 - 1.5f;
            var ordered = list.OrderBy(e => e.OtherX).ThenBy(e => e.Branch).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                float x = ordered.Count == 1 ? 0 : -half + 2 * half * i / (ordered.Count - 1);
                result[(ordered[i].Branch, ordered[i].From)] = c + new Vector3(x, o.BusbarHeight, 0);
            }
        }
        return result;
    }

    private static MeshData Disconnector(Vector3 p, Vector3 side, bool closed)
    {
        var m = new MeshData();
        var a = p - side * 1.2f; var b = p + side * 1.2f;
        m.AddBox(p + new Vector3(0, 0.3f, 0), new Vector3(1.6f, 0.3f, 0.4f), side);
        m.AddCylinder(a + new Vector3(0, 0.6f, 0), a + new Vector3(0, 3.2f, 0), 0.18f, 0.14f, 8);
        m.AddCylinder(b + new Vector3(0, 0.6f, 0), b + new Vector3(0, 3.2f, 0), 0.18f, 0.14f, 8);
        // Blade: horizontal when closed, raised when open.
        var bladeEnd = closed ? b + new Vector3(0, 3.3f, 0) : a + new Vector3(0, 5.6f, 0) + side * 0.6f;
        m.AddBeam(a + new Vector3(0, 3.3f, 0), bladeEnd, 0.06f, 0.06f);
        return m;
    }

    private static MeshData Breaker(Vector3 p)
    {
        var m = new MeshData();
        m.AddBox(p + new Vector3(0, 0.6f, 0), new Vector3(0.8f, 0.6f, 0.8f));
        m.AddCylinder(p + new Vector3(0, 1.2f, 0), p + new Vector3(0, 3.6f, 0), 0.35f, 0.3f, 12);
        m.AddCylinder(p + new Vector3(0, 3.6f, 0), p + new Vector3(0, 4.0f, 0), 0.45f, 0.45f, 12);
        return m;
    }

    private static MeshData TransformerMesh(Vector3 mid, Vector3 dir, Vector3 side, Vector3 hv, Vector3 lv)
    {
        var m = new MeshData();
        m.AddBox(mid + new Vector3(0, 2.2f, 0), new Vector3(2.4f, 2.2f, 1.8f), dir);
        for (int i = -1; i <= 1; i++)
        {
            var top = mid + new Vector3(0, 4.4f, 0) + side * (i * 0.9f);
            m.AddCylinder(top - dir * 1.2f, top - dir * 1.2f + new Vector3(0, 1.8f, 0), 0.16f, 0.1f, 8);
            m.AddCylinder(top + dir * 1.2f, top + dir * 1.2f + new Vector3(0, 1.2f, 0), 0.13f, 0.08f, 8);
        }
        foreach (float s in new[] { -1f, 1f })
            for (int f = -2; f <= 2; f++)
                m.AddBox(mid + new Vector3(0, 2.0f, 0) + side * (s * 2.3f) + dir * (f * 0.8f), new Vector3(0.05f, 1.6f, 0.35f), dir);
        // Leads from the busbar attachment points to the bushings.
        m.AddTube([hv, mid - dir * 1.2f + new Vector3(0, 6.2f, 0)], 0.07f, 6);
        m.AddTube([mid + dir * 1.2f + new Vector3(0, 5.6f, 0), lv], 0.07f, 6);
        return m;
    }

    private static MeshData Tower(Vector3 p, Vector3 side, GridSceneOptions o)
    {
        var m = new MeshData();
        float h = o.TowerHeight, w = 1.6f;
        var fwd = Vector3.Cross(side, Vector3.UnitY);
        Vector3 top = p + new Vector3(0, h, 0);
        foreach (var (sx, sz) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
        {
            var foot = p + side * (sx * w) + fwd * (sz * w);
            var head = top + side * (sx * 0.4f) + fwd * (sz * 0.4f) - new Vector3(0, 1, 0);
            m.AddBeam(foot, head, 0.12f, 0.12f);
        }
        m.AddBeam(top - new Vector3(0, 1, 0) - side * (o.PhaseSpacing + 0.6f), top - new Vector3(0, 1, 0) + side * (o.PhaseSpacing + 0.6f), 0.15f, 0.2f);
        m.AddBeam(top - new Vector3(0, 1.5f, 0), top + new Vector3(0, 1.2f, 0), 0.1f, 0.1f);
        return m;
    }
}
