using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Network.Utility;

/// <summary>EPANET flow units. US units use feet and inches; SI units use metres and millimetres.</summary>
public enum FlowUnits { CFS, GPM, MGD, IMGD, AFD, LPS, LPM, MLD, CMH, CMD }

public enum HeadlossFormula { HazenWilliams, DarcyWeisbach, ChezyManning }

public enum WaterNodeKind { Junction, Reservoir, Tank }

public enum WaterLinkKind { Pipe, Pump, Valve }

public enum LinkStatus { Open, Closed }

/// <summary>A junction, reservoir or tank. All quantities are SI (m, m³/s).</summary>
public sealed class WaterNode
{
    public required string Id { get; init; }
    public WaterNodeKind Kind { get; init; }
    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    /// <summary>Elevation (junction, tank) or total head (reservoir), m.</summary>
    public double Elevation { get; set; }
    /// <summary>Demand categories: base demand (m³/s) and pattern id.</summary>
    public List<(double BaseDemand, string? Pattern)> Demands { get; } = [];
    public string? HeadPattern { get; set; }
    public double InitialLevel { get; set; }
    public double MinLevel { get; set; }
    public double MaxLevel { get; set; }
    public double TankDiameter { get; set; }
    public double MinVolume { get; set; }
    public override string ToString() => $"{Kind} {Id}";
}

/// <summary>A pipe, pump or valve. All quantities are SI.</summary>
public sealed class WaterLink
{
    public required string Id { get; init; }
    public WaterLinkKind Kind { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public double Length { get; set; }
    /// <summary>Diameter in metres.</summary>
    public double Diameter { get; set; }
    /// <summary>Hazen-Williams C, Darcy-Weisbach roughness (m) or Manning n.</summary>
    public double Roughness { get; set; }
    public double MinorLoss { get; set; }
    public LinkStatus Status { get; set; } = LinkStatus.Open;
    /// <summary>Pipe with a check valve (flow only From → To).</summary>
    public bool CheckValve { get; set; }
    public string? PumpCurve { get; set; }
    /// <summary>Constant pump power in kW (when no curve).</summary>
    public double? PumpPower { get; set; }
    public double Speed { get; set; } = 1;
    /// <summary>PRV, PSV, PBV, FCV, TCV or GPV.</summary>
    public string? ValveType { get; set; }
    public double ValveSetting { get; set; }
    public List<(double X, double Y)> Vertices { get; } = [];
    public override string ToString() => $"{Kind} {Id} {From}→{To}";
}

/// <summary>A simple control: <c>LINK id OPEN|CLOSED|setting IF NODE id ABOVE|BELOW value</c> or <c>… AT TIME t</c>.</summary>
public sealed record SimpleControl(string Link, string Action, string? Node, bool Above, double Value, double? AtTimeHours, string Text);

/// <summary>
/// A pressurised water distribution network (EPANET data model): junctions, reservoirs, tanks, pipes, pumps and valves, with patterns,
/// curves and simple controls. Reads and writes EPANET 2.x <c>.inp</c> files, converts to GeoDataFrames and solves steady-state
/// hydraulics (<see cref="Hydraulics"/>).
/// </summary>
public sealed class WaterNetwork
{
    public FlowUnits Units { get; set; } = FlowUnits.LPS;
    public HeadlossFormula Headloss { get; set; } = HeadlossFormula.HazenWilliams;
    public double DemandMultiplier { get; set; } = 1;
    public string? DefaultPattern { get; set; }
    public int Trials { get; set; } = 40;
    public double Accuracy { get; set; } = 0.001;
    public string Title { get; set; } = "";
    public CoordinateReferenceSystem? Crs { get; set; }

    public List<WaterNode> Nodes { get; } = [];
    public List<WaterLink> Links { get; } = [];
    public Dictionary<string, double[]> Patterns { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Curves (pump head, efficiency, volume) in SI: flow m³/s and head m for pump curves.</summary>
    public Dictionary<string, List<(double X, double Y)>> Curves { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SimpleControl> Controls { get; } = [];
    /// <summary>Sections this reader keeps verbatim but does not interpret (rules, energy, quality…), for round-tripping.</summary>
    public Dictionary<string, List<string>> OtherSections { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsUsUnits => Units is FlowUnits.CFS or FlowUnits.GPM or FlowUnits.MGD or FlowUnits.IMGD or FlowUnits.AFD;
    /// <summary>m³/s per flow unit (EPANET conversion factors).</summary>
    public double FlowFactor => Units switch
    {
        FlowUnits.CFS => 0.028316846592, FlowUnits.GPM => 0.028316846592 / 448.831, FlowUnits.MGD => 0.028316846592 / 0.64632,
        FlowUnits.IMGD => 0.028316846592 / 0.5382, FlowUnits.AFD => 0.028316846592 / 1.9837, FlowUnits.LPS => 0.001,
        FlowUnits.LPM => 0.001 / 60, FlowUnits.MLD => 1.0 / 86.4, FlowUnits.CMH => 1.0 / 3600, _ => 1.0 / 86400,
    };
    /// <summary>Metres per length/head unit (ft or m).</summary>
    public double LengthFactor => IsUsUnits ? 0.3048 : 1;
    /// <summary>Metres per diameter unit (in or mm).</summary>
    public double DiameterFactor => IsUsUnits ? 0.0254 : 0.001;
    /// <summary>Metres per Darcy-Weisbach roughness unit (millifeet or mm).</summary>
    public double RoughnessFactor => Headloss == HeadlossFormula.DarcyWeisbach ? (IsUsUnits ? 0.0003048 : 0.001) : 1;

    public WaterNode Node(string id) => _nodeIndex is { } ix && ix.TryGetValue(id, out var i) ? Nodes[i] : Nodes.First(n => n.Id == id);
    public WaterLink Link(string id) => Links.First(l => l.Id == id);
    private Dictionary<string, int>? _nodeIndex;
    internal Dictionary<string, int> NodeIndex() => _nodeIndex = Nodes.Select((n, i) => (n.Id, i)).ToDictionary(t => t.Id, t => t.i, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pattern multiplier at a time step (0 = first value), 1 when the pattern is missing.</summary>
    public double PatternValue(string? pattern, int step = 0)
    {
        var id = pattern ?? DefaultPattern;
        if (id is null || !Patterns.TryGetValue(id, out var p) || p.Length == 0) return 1;
        return p[step % p.Length];
    }

    /// <summary>Total junction demand (m³/s) at a pattern step.</summary>
    public double DemandAt(WaterNode n, int step = 0) => n.Demands.Sum(d => d.BaseDemand * PatternValue(d.Pattern, step)) * DemandMultiplier;

    // ------------------------------------------------------------------ EPANET .inp reader

    public static WaterNetwork ReadInp(string path, CoordinateReferenceSystem? crs = null) => ParseInp(File.ReadAllText(path), crs);

    public static WaterNetwork ParseInp(string text, CoordinateReferenceSystem? crs = null)
    {
        var net = new WaterNetwork { Crs = crs };
        var sections = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        var raw = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string current = "";
        foreach (var line0 in text.Split('\n'))
        {
            var line = line0.TrimEnd('\r');
            var trimmed = line.Trim();
            if (trimmed.StartsWith('['))
            {
                current = trimmed.Trim('[', ']').ToUpperInvariant();
                if (current == "END") break;
                continue;
            }
            int sc = line.IndexOf(';');
            var content = (sc >= 0 ? line[..sc] : line).Trim();
            if (content.Length == 0) continue;
            if (!sections.TryGetValue(current, out var list)) sections[current] = list = [];
            list.Add(content.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
            if (!raw.TryGetValue(current, out var rl)) raw[current] = rl = [];
            rl.Add(content);
        }
        IEnumerable<string[]> S(string name) => sections.TryGetValue(name, out var l) ? l : [];
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        // options first: units decide every conversion
        foreach (var t in S("OPTIONS"))
        {
            string key = t[0].ToUpperInvariant();
            if (key == "UNITS") net.Units = Enum.Parse<FlowUnits>(t[1], true);
            else if (key == "HEADLOSS") net.Headloss = t[1].ToUpperInvariant() switch { "D-W" => HeadlossFormula.DarcyWeisbach, "C-M" => HeadlossFormula.ChezyManning, _ => HeadlossFormula.HazenWilliams };
            else if (key == "PATTERN") net.DefaultPattern = t[1];
            else if (key == "DEMAND" && t.Length > 2 && t[1].Equals("MULTIPLIER", StringComparison.OrdinalIgnoreCase)) net.DemandMultiplier = D(t[2]);
            else if (key == "TRIALS") net.Trials = (int)D(t[1]);
            else if (key == "ACCURACY") net.Accuracy = D(t[1]);
        }
        net.Title = string.Join("\n", raw.GetValueOrDefault("TITLE") ?? []);
        double qf = net.FlowFactor, lf = net.LengthFactor, df = net.DiameterFactor;

        foreach (var t in S("JUNCTIONS"))
        {
            var n = new WaterNode { Id = t[0], Kind = WaterNodeKind.Junction, Elevation = D(t[1]) * lf };
            if (t.Length > 2) n.Demands.Add((D(t[2]) * qf, t.Length > 3 ? t[3] : null));
            net.Nodes.Add(n);
        }
        foreach (var t in S("RESERVOIRS"))
            net.Nodes.Add(new WaterNode { Id = t[0], Kind = WaterNodeKind.Reservoir, Elevation = D(t[1]) * lf, HeadPattern = t.Length > 2 ? t[2] : null });
        foreach (var t in S("TANKS"))
            net.Nodes.Add(new WaterNode
            {
                Id = t[0], Kind = WaterNodeKind.Tank, Elevation = D(t[1]) * lf, InitialLevel = D(t[2]) * lf, MinLevel = D(t[3]) * lf,
                MaxLevel = D(t[4]) * lf, TankDiameter = D(t[5]) * lf, MinVolume = t.Length > 6 ? D(t[6]) * lf * lf * lf : 0
            });
        var ix = net.NodeIndex();
        // [DEMANDS] replaces the junction's demand from [JUNCTIONS] (EPANET 2.2 semantics)
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in S("DEMANDS"))
        {
            var n = net.Nodes[ix[t[0]]];
            if (replaced.Add(n.Id)) n.Demands.Clear();
            n.Demands.Add((D(t[1]) * qf, t.Length > 2 ? t[2] : null));
        }

        foreach (var t in S("PIPES"))
        {
            var l = new WaterLink
            {
                Id = t[0], Kind = WaterLinkKind.Pipe, From = t[1], To = t[2], Length = D(t[3]) * lf, Diameter = D(t[4]) * df,
                Roughness = D(t[5]) * net.RoughnessFactor, MinorLoss = t.Length > 6 ? D(t[6]) : 0
            };
            if (t.Length > 7)
            {
                if (t[7].Equals("CLOSED", StringComparison.OrdinalIgnoreCase)) l.Status = LinkStatus.Closed;
                else if (t[7].Equals("CV", StringComparison.OrdinalIgnoreCase)) l.CheckValve = true;
            }
            net.Links.Add(l);
        }
        foreach (var t in S("PUMPS"))
        {
            var l = new WaterLink { Id = t[0], Kind = WaterLinkKind.Pump, From = t[1], To = t[2] };
            for (int i = 3; i + 1 < t.Length; i += 2)
                switch (t[i].ToUpperInvariant())
                {
                    case "HEAD": l.PumpCurve = t[i + 1]; break;
                    case "POWER": l.PumpPower = D(t[i + 1]) * (net.IsUsUnits ? 0.745699872 : 1); break;
                    case "SPEED": l.Speed = D(t[i + 1]); break;
                }
            net.Links.Add(l);
        }
        foreach (var t in S("VALVES"))
        {
            string type = t[4].ToUpperInvariant();
            double setting = type switch
            {
                "PRV" or "PSV" or "PBV" => D(t[5]) * lf,      // pressure (head) settings
                "FCV" => D(t[5]) * qf,
                "TCV" => D(t[5]),
                _ => 0
            };
            net.Links.Add(new WaterLink
            {
                Id = t[0], Kind = WaterLinkKind.Valve, From = t[1], To = t[2], Diameter = D(t[3]) * df, ValveType = type, ValveSetting = setting,
                MinorLoss = t.Length > 6 ? D(t[6]) : 0, PumpCurve = type == "GPV" ? t[5] : null
            });
        }
        var linkIx = net.Links.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        foreach (var t in S("STATUS"))
        {
            var l = net.Links[linkIx[t[0]]];
            var v = t[1].ToUpperInvariant();
            if (v == "CLOSED") l.Status = LinkStatus.Closed;
            else if (v == "OPEN") l.Status = LinkStatus.Open;
            else if (l.Kind == WaterLinkKind.Pump) l.Speed = D(t[1]);
        }
        foreach (var t in S("PATTERNS"))
        {
            var vals = t.Skip(1).Select(D).ToArray();
            net.Patterns[t[0]] = net.Patterns.TryGetValue(t[0], out var p) ? [.. p, .. vals] : vals;
        }
        if (net.DefaultPattern is null && net.Patterns.ContainsKey("1")) net.DefaultPattern = "1";
        // curves: pump head curves to SI now (other curve types keep their numbers)
        var pumpCurves = net.Links.Where(l => l.Kind == WaterLinkKind.Pump && l.PumpCurve is not null).Select(l => l.PumpCurve!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in S("CURVES"))
        {
            if (!net.Curves.TryGetValue(t[0], out var c)) net.Curves[t[0]] = c = [];
            double x = D(t[1]), y = D(t[2]);
            c.Add(pumpCurves.Contains(t[0]) ? (x * qf, y * lf) : (x, y));
        }
        foreach (var t in S("COORDINATES"))
            if (ix.TryGetValue(t[0], out var i)) { net.Nodes[i].X = D(t[1]); net.Nodes[i].Y = D(t[2]); }
        foreach (var t in S("VERTICES"))
            if (linkIx.TryGetValue(t[0], out var i)) net.Links[i].Vertices.Add((D(t[1]), D(t[2])));
        foreach (var line in raw.GetValueOrDefault("CONTROLS") ?? [])
            if (ParseControl(line, net) is { } c) net.Controls.Add(c);
        foreach (var (k, v) in raw)
            if (k is not ("TITLE" or "OPTIONS" or "JUNCTIONS" or "RESERVOIRS" or "TANKS" or "DEMANDS" or "PIPES" or "PUMPS" or "VALVES" or "STATUS"
                or "PATTERNS" or "CURVES" or "COORDINATES" or "VERTICES" or "CONTROLS"))
                net.OtherSections[k] = v;
        return net;
    }

    private static SimpleControl? ParseControl(string line, WaterNetwork net)
    {
        var t = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (t.Length < 6 || !t[0].Equals("LINK", StringComparison.OrdinalIgnoreCase)) return null;
        string link = t[1], action = t[2].ToUpperInvariant();
        if (t[3].Equals("IF", StringComparison.OrdinalIgnoreCase) && t.Length >= 8 && t[4].Equals("NODE", StringComparison.OrdinalIgnoreCase))
        {
            // level (tanks) or pressure (junctions), in length units
            double v = double.Parse(t[7], CultureInfo.InvariantCulture);
            return new SimpleControl(link, action, t[5], t[6].Equals("ABOVE", StringComparison.OrdinalIgnoreCase), v * net.LengthFactor, null, line);
        }
        if (t[3].Equals("AT", StringComparison.OrdinalIgnoreCase) && t[4].Equals("TIME", StringComparison.OrdinalIgnoreCase))
        {
            var s = t[5];
            double hours = s.Contains(':') ? TimeSpan.ParseExact(s.Length <= 5 ? s + ":00" : s, @"h\:mm\:ss", CultureInfo.InvariantCulture).TotalHours
                : double.Parse(s, CultureInfo.InvariantCulture);
            return new SimpleControl(link, action, null, false, 0, hours, line);
        }
        return new SimpleControl(link, action, null, false, 0, null, line);
    }

    // ------------------------------------------------------------------ EPANET .inp writer

    public void WriteInp(string path) => File.WriteAllText(path, ToInp());

    public string ToInp()
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        double qf = FlowFactor, lf = LengthFactor, df = DiameterFactor;
        string F(double v) => v.ToString("G10", ci);
        sb.AppendLine("[TITLE]").AppendLine(Title.Length > 0 ? Title : "GeoPandas.Net water network").AppendLine();
        sb.AppendLine("[JUNCTIONS]");
        foreach (var n in Nodes.Where(n => n.Kind == WaterNodeKind.Junction))
        {
            var first = n.Demands.Count > 0 ? n.Demands[0] : (0, null);
            sb.AppendLine(ci, $" {n.Id}\t{F(n.Elevation / lf)}\t{F(first.Item1 / qf)}\t{first.Item2 ?? ""}");
        }
        sb.AppendLine().AppendLine("[RESERVOIRS]");
        foreach (var n in Nodes.Where(n => n.Kind == WaterNodeKind.Reservoir)) sb.AppendLine(ci, $" {n.Id}\t{F(n.Elevation / lf)}\t{n.HeadPattern ?? ""}");
        sb.AppendLine().AppendLine("[TANKS]");
        foreach (var n in Nodes.Where(n => n.Kind == WaterNodeKind.Tank))
            sb.AppendLine(ci, $" {n.Id}\t{F(n.Elevation / lf)}\t{F(n.InitialLevel / lf)}\t{F(n.MinLevel / lf)}\t{F(n.MaxLevel / lf)}\t{F(n.TankDiameter / lf)}\t{F(n.MinVolume / (lf * lf * lf))}");
        sb.AppendLine().AppendLine("[PIPES]");
        foreach (var l in Links.Where(l => l.Kind == WaterLinkKind.Pipe))
            sb.AppendLine(ci, $" {l.Id}\t{l.From}\t{l.To}\t{F(l.Length / lf)}\t{F(l.Diameter / df)}\t{F(l.Roughness / RoughnessFactor)}\t{F(l.MinorLoss)}\t{(l.CheckValve ? "CV" : l.Status == LinkStatus.Closed ? "Closed" : "Open")}");
        sb.AppendLine().AppendLine("[PUMPS]");
        foreach (var l in Links.Where(l => l.Kind == WaterLinkKind.Pump))
        {
            var p = l.PumpCurve is not null ? $"HEAD {l.PumpCurve}" : $"POWER {F(l.PumpPower!.Value / (IsUsUnits ? 0.745699872 : 1))}";
            if (l.Speed != 1) p += $" SPEED {F(l.Speed)}";
            sb.AppendLine(ci, $" {l.Id}\t{l.From}\t{l.To}\t{p}");
        }
        sb.AppendLine().AppendLine("[VALVES]");
        foreach (var l in Links.Where(l => l.Kind == WaterLinkKind.Valve))
        {
            string setting = l.ValveType switch
            {
                "PRV" or "PSV" or "PBV" => F(l.ValveSetting / lf),
                "FCV" => F(l.ValveSetting / qf),
                "GPV" => l.PumpCurve ?? "",
                _ => F(l.ValveSetting)
            };
            sb.AppendLine(ci, $" {l.Id}\t{l.From}\t{l.To}\t{F(l.Diameter / df)}\t{l.ValveType}\t{setting}\t{F(l.MinorLoss)}");
        }
        sb.AppendLine().AppendLine("[DEMANDS]");
        foreach (var n in Nodes.Where(n => n.Demands.Count > 1))
            foreach (var (b, p) in n.Demands) sb.AppendLine(ci, $" {n.Id}\t{F(b / qf)}\t{p ?? ""}");
        sb.AppendLine().AppendLine("[STATUS]");
        foreach (var l in Links.Where(l => l.Kind != WaterLinkKind.Pipe && l.Status == LinkStatus.Closed)) sb.AppendLine(ci, $" {l.Id}\tClosed");
        sb.AppendLine().AppendLine("[PATTERNS]");
        foreach (var (id, vals) in Patterns)
            for (int i = 0; i < vals.Length; i += 6) sb.AppendLine(ci, $" {id}\t{string.Join("\t", vals.Skip(i).Take(6).Select(F))}");
        sb.AppendLine().AppendLine("[CURVES]");
        var pumpCurves = Links.Where(l => l.Kind == WaterLinkKind.Pump && l.PumpCurve is not null).Select(l => l.PumpCurve!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, pts) in Curves)
            foreach (var (x, y) in pts)
            {
                bool pump = pumpCurves.Contains(id);
                sb.Append(' ').Append(id).Append('\t').Append(F(pump ? x / qf : x)).Append('\t').AppendLine(F(pump ? y / lf : y));
            }
        sb.AppendLine().AppendLine("[CONTROLS]");
        foreach (var c in Controls) sb.AppendLine(c.Text);
        foreach (var (k, v) in OtherSections)
        {
            sb.AppendLine().AppendLine(ci, $"[{k}]");
            foreach (var line in v) sb.AppendLine(line);
        }
        sb.AppendLine().AppendLine("[OPTIONS]");
        sb.AppendLine(ci, $" Units\t{Units}");
        sb.AppendLine(ci, $" Headloss\t{Headloss switch { HeadlossFormula.DarcyWeisbach => "D-W", HeadlossFormula.ChezyManning => "C-M", _ => "H-W" }}");
        if (DefaultPattern is not null) sb.AppendLine(ci, $" Pattern\t{DefaultPattern}");
        sb.AppendLine(ci, $" Demand Multiplier\t{F(DemandMultiplier)}");
        sb.AppendLine(ci, $" Trials\t{Trials}");
        sb.AppendLine(ci, $" Accuracy\t{F(Accuracy)}");
        sb.AppendLine().AppendLine("[COORDINATES]");
        foreach (var n in Nodes.Where(n => !double.IsNaN(n.X))) sb.AppendLine(ci, $" {n.Id}\t{F(n.X)}\t{F(n.Y)}");
        sb.AppendLine().AppendLine("[VERTICES]");
        foreach (var l in Links) foreach (var (x, y) in l.Vertices) sb.AppendLine(ci, $" {l.Id}\t{F(x)}\t{F(y)}");
        sb.AppendLine().AppendLine("[END]");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ GIS conversion

    /// <summary>Nodes as points: id, kind, elevation (m), demand (m³/s at the first pattern step).</summary>
    public GeoDataFrame NodesFrame() => GeoDataFrame.FromGeometries(
        Nodes.Select(n => double.IsNaN(n.X) ? null : (NetTopologySuite.Geometries.Geometry?)Geo.Point(n.X, n.Y)), Crs,
        Column.Create("id", Nodes.Select(n => n.Id).ToArray()),
        Column.Create("kind", Nodes.Select(n => n.Kind.ToString().ToLowerInvariant()).ToArray()),
        Column.Create("elevation", Nodes.Select(n => n.Elevation).ToArray()),
        Column.Create("demand", Nodes.Select(n => n.Kind == WaterNodeKind.Junction ? DemandAt(n) : 0).ToArray()));

    /// <summary>Links as line strings (with vertices): id, kind, from, to, length (m), diameter (m), roughness, status.</summary>
    public GeoDataFrame LinksFrame()
    {
        var ix = NodeIndex();
        var geoms = Links.Select(l =>
        {
            var a = Nodes[ix[l.From]]; var b = Nodes[ix[l.To]];
            if (double.IsNaN(a.X) || double.IsNaN(b.X)) return (NetTopologySuite.Geometries.Geometry?)null;
            var pts = new List<Coordinate> { new(a.X, a.Y) };
            pts.AddRange(l.Vertices.Select(v => new Coordinate(v.X, v.Y)));
            pts.Add(new Coordinate(b.X, b.Y));
            return Geo.LineString(pts);
        });
        return GeoDataFrame.FromGeometries(geoms, Crs,
            Column.Create("id", Links.Select(l => l.Id).ToArray()),
            Column.Create("kind", Links.Select(l => l.Kind.ToString().ToLowerInvariant()).ToArray()),
            Column.Create("from", Links.Select(l => l.From).ToArray()),
            Column.Create("to", Links.Select(l => l.To).ToArray()),
            Column.Create("length", Links.Select(l => l.Length).ToArray()),
            Column.Create("diameter", Links.Select(l => l.Diameter).ToArray()),
            Column.Create("roughness", Links.Select(l => l.Roughness).ToArray()),
            Column.Create("status", Links.Select(l => l.CheckValve ? "cv" : l.Status.ToString().ToLowerInvariant()).ToArray()));
    }

    /// <summary>
    /// Builds a network from GIS layers: pipes (line strings) whose end points are snapped to junctions within <paramref name="snapTolerance"/>
    /// (new junctions are created where none exists), plus optional source points (reservoirs, with a <c>head</c> column).
    /// Pipe columns: <c>diameter</c> (m), <c>roughness</c> (C), optional <c>id</c>, <c>status</c>; junction columns: <c>elevation</c>, <c>demand</c> (m³/s).
    /// </summary>
    public static WaterNetwork FromGeoDataFrames(GeoDataFrame pipes, GeoDataFrame? junctions = null, GeoDataFrame? reservoirs = null, double snapTolerance = 0.01)
    {
        var net = new WaterNetwork { Crs = pipes.Crs, Units = FlowUnits.LPS };
        var nodes = new List<WaterNode>();
        var tree = new Dictionary<(long, long), int>();
        (long, long) Key(double x, double y) => ((long)Math.Round(x / snapTolerance), (long)Math.Round(y / snapTolerance));
        int Find(double x, double y)
        {
            var (kx, ky) = Key(x, y);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                    if (tree.TryGetValue((kx + dx, ky + dy), out var i) && Math.Abs(nodes[i].X - x) <= snapTolerance && Math.Abs(nodes[i].Y - y) <= snapTolerance) return i;
            return -1;
        }
        int Add(WaterNode n) { nodes.Add(n); tree[Key(n.X, n.Y)] = nodes.Count - 1; return nodes.Count - 1; }
        string Str(DataFrame df, string col, int i, string def) => df.Columns.Any(c => c.Name == col) && df[col].GetValue(i) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture)! : def;
        double Num(DataFrame df, string col, int i, double def) => df.Columns.Any(c => c.Name == col) && !df[col].IsNull(i) ? df[col].GetDouble(i) : def;
        if (reservoirs is not null)
            for (int i = 0; i < reservoirs.RowCount; i++)
            {
                var p = reservoirs.Geometry[i]!.Coordinate;
                Add(new WaterNode { Id = Str(reservoirs, "id", i, $"R{i + 1}"), Kind = WaterNodeKind.Reservoir, X = p.X, Y = p.Y, Elevation = Num(reservoirs, "head", i, 0) });
            }
        if (junctions is not null)
            for (int i = 0; i < junctions.RowCount; i++)
            {
                var p = junctions.Geometry[i]!.Coordinate;
                if (Find(p.X, p.Y) >= 0) continue;
                var n = new WaterNode { Id = Str(junctions, "id", i, $"J{i + 1}"), Kind = WaterNodeKind.Junction, X = p.X, Y = p.Y, Elevation = Num(junctions, "elevation", i, 0) };
                double d = Num(junctions, "demand", i, 0);
                if (d != 0) n.Demands.Add((d, null));
                Add(n);
            }
        int auto = 0;
        for (int i = 0; i < pipes.RowCount; i++)
        {
            if (pipes.Geometry[i] is not LineString ls || ls.NumPoints < 2) continue;
            int Ends(Coordinate c)
            {
                int f = Find(c.X, c.Y);
                return f >= 0 ? f : Add(new WaterNode { Id = $"N{++auto}", Kind = WaterNodeKind.Junction, X = c.X, Y = c.Y });
            }
            int a = Ends(ls.StartPoint.Coordinate), b = Ends(ls.EndPoint.Coordinate);
            var link = new WaterLink
            {
                Id = Str(pipes, "id", i, $"P{i + 1}"), Kind = WaterLinkKind.Pipe, From = nodes[a].Id, To = nodes[b].Id, Length = ls.Length,
                Diameter = Num(pipes, "diameter", i, 0.1), Roughness = Num(pipes, "roughness", i, 100), MinorLoss = Num(pipes, "minor_loss", i, 0),
                Status = Str(pipes, "status", i, "open").Equals("closed", StringComparison.OrdinalIgnoreCase) ? LinkStatus.Closed : LinkStatus.Open
            };
            for (int k = 1; k < ls.NumPoints - 1; k++) link.Vertices.Add((ls.GetCoordinateN(k).X, ls.GetCoordinateN(k).Y));
            net.Links.Add(link);
        }
        net.Nodes.AddRange(nodes);
        return net;
    }

    public override string ToString() =>
        $"WaterNetwork: {Nodes.Count(n => n.Kind == WaterNodeKind.Junction)} junctions, {Nodes.Count(n => n.Kind == WaterNodeKind.Reservoir)} reservoirs, " +
        $"{Nodes.Count(n => n.Kind == WaterNodeKind.Tank)} tanks, {Links.Count(l => l.Kind == WaterLinkKind.Pipe)} pipes, {Links.Count(l => l.Kind == WaterLinkKind.Pump)} pumps, " +
        $"{Links.Count(l => l.Kind == WaterLinkKind.Valve)} valves ({Units}, {Headloss})";
}
