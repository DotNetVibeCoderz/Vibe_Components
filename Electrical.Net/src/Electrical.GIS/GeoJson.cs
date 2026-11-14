using System.Globalization;
using System.Text;
using System.Text.Json;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.GIS;

/// <summary>Options for <see cref="GeoJsonExporter"/>.</summary>
public sealed record GeoJsonOptions
{
    /// <summary>Also write generators and loads as Point features at their bus (kind "generator" / "load"). Default true.</summary>
    public bool IncludeInjections { get; init; } = true;
    /// <summary>Write electrical parameters (r, x, b in pu, ratings, taps) so the file can be imported back as a network. Default true.</summary>
    public bool IncludeElectricalData { get; init; } = true;
    /// <summary>Write features of buses without location with a null geometry (otherwise they are skipped). Default true.</summary>
    public bool IncludeUnlocated { get; init; } = true;
    /// <summary>Routed branch paths (branch index → vertices); straight lines between the buses are used otherwise.</summary>
    public IReadOnlyDictionary<int, GeoPoint[]>? BranchPaths { get; init; }
    /// <summary>Indented output. Default false.</summary>
    public bool Indented { get; init; }
}

/// <summary>
/// Writes a <see cref="PowerNetwork"/> as an RFC 7946 GeoJSON FeatureCollection: buses as Points (properties: kind, id, name, baseKv, type,
/// loadMw, genMw …), branches as LineStrings (kind, index, from, to, branchKind, lengthKm, rateA …), optionally generators/loads, and —
/// when a <see cref="PowerFlowResult"/> is given — result properties: bus <c>vm</c> [pu], <c>vaDeg</c>, <c>kv</c>; branch <c>pFromMw</c>,
/// <c>qFromMvar</c>, <c>pToMw</c>, <c>lossMw</c>, <c>loading</c> [% of RateA]. Coordinates are [longitude, latitude].
/// </summary>
public static class GeoJsonExporter
{
    /// <summary>Exports to a GeoJSON string.</summary>
    public static string Export(PowerNetwork net, PowerFlowResult? result = null, GeoJsonOptions? options = null)
    {
        using var ms = new MemoryStream();
        Write(net, ms, result, options);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Writes GeoJSON (UTF-8) to a stream.</summary>
    public static void Write(PowerNetwork net, Stream stream, PowerFlowResult? result = null, GeoJsonOptions? options = null)
    {
        options ??= new GeoJsonOptions();
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = options.Indented });
        Write(net, w, result, options);
    }

    /// <summary>Writes the FeatureCollection with an existing writer.</summary>
    public static void Write(PowerNetwork net, Utf8JsonWriter w, PowerFlowResult? result = null, GeoJsonOptions? options = null)
    {
        options ??= new GeoJsonOptions();
        bool el = options.IncludeElectricalData;
        w.WriteStartObject();
        w.WriteString("type", "FeatureCollection");
        w.WriteString("name", net.Name);
        WriteNum(w, "baseMva", net.BaseMva);
        WriteNum(w, "frequencyHz", net.FrequencyHz);
        var bounds = GeoBounds.Of(net);
        if (!bounds.IsEmpty)
        {
            w.WriteStartArray("bbox");
            foreach (var v in bounds.ToGeoJsonBBox()) w.WriteNumberValue(v);
            w.WriteEndArray();
        }
        w.WriteStartArray("features");

        var busRes = result?.Buses.ToDictionary(b => b.BusId);
        foreach (var bus in net.Buses)
        {
            if (bus.Location is null && !options.IncludeUnlocated) continue;
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteString("id", "bus-" + bus.Id.ToString(CultureInfo.InvariantCulture));
            WritePoint(w, bus.Location);
            w.WriteStartObject("properties");
            w.WriteString("kind", "bus");
            WriteNum(w, "id", bus.Id);
            WriteName(w, bus.Name);
            WriteNum(w, "baseKv", bus.BaseKv);
            w.WriteString("type", bus.Type.ToString());
            WriteNum(w, "loadMw", net.Loads.Where(l => l.Bus == bus.Id && l.InService).Sum(l => l.P));
            WriteNum(w, "genMw", net.Generators.Where(g => g.Bus == bus.Id && g.InService).Sum(g => g.P));
            if (el)
            {
                WriteNum(w, "vmin", bus.Vmin); WriteNum(w, "vmax", bus.Vmax);
                WriteNum(w, "gs", bus.Gs); WriteNum(w, "bs", bus.Bs);
                WriteNum(w, "area", bus.Area); WriteNum(w, "zone", bus.Zone);
            }
            if (busRes is not null && busRes.TryGetValue(bus.Id, out var br))
            {
                WriteNum(w, "vm", br.Vm);
                WriteNum(w, "vaDeg", br.VaDeg);
                WriteNum(w, "kv", br.Vm * bus.BaseKv);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        for (int k = 0; k < net.Branches.Count; k++)
        {
            var b = net.Branches[k];
            GeoPoint[]? path = options.BranchPaths is not null && options.BranchPaths.TryGetValue(k, out var p) && p.Length >= 2 ? p
                : net.GetBus(b.From).Location is GeoPoint a && net.GetBus(b.To).Location is GeoPoint c ? [a, c] : null;
            if (path is null && !options.IncludeUnlocated) continue;
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteString("id", "branch-" + k.ToString(CultureInfo.InvariantCulture));
            if (path is null) w.WriteNull("geometry");
            else
            {
                w.WriteStartObject("geometry");
                w.WriteString("type", "LineString");
                w.WriteStartArray("coordinates");
                foreach (var v in path) WriteCoord(w, v);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteStartObject("properties");
            w.WriteString("kind", "branch");
            WriteNum(w, "index", k);
            WriteNum(w, "from", b.From);
            WriteNum(w, "to", b.To);
            WriteName(w, b.Name);
            w.WriteString("branchKind", b.IsTransformer ? nameof(BranchKind.Transformer) : b.Kind.ToString());
            w.WriteBoolean("inService", b.InService);
            WriteNum(w, "lengthKm", b.LengthKm);
            WriteNum(w, "rateA", b.RateA);
            if (el)
            {
                WriteNum(w, "r", b.R); WriteNum(w, "x", b.X); WriteNum(w, "b", b.B);
                WriteNum(w, "tap", b.Tap); WriteNum(w, "shiftDeg", b.ShiftDeg);
                w.WriteString("connection", b.Connection.ToString());
            }
            if (result is not null && k < result.Branches.Count)
            {
                var r = result.Branches[k];
                WriteNum(w, "pFromMw", r.PFrom); WriteNum(w, "qFromMvar", r.QFrom);
                WriteNum(w, "pToMw", r.PTo); WriteNum(w, "qToMvar", r.QTo);
                WriteNum(w, "lossMw", r.PLoss);
                WriteNum(w, "loading", r.LoadingPercent);
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }

        if (options.IncludeInjections)
        {
            for (int i = 0; i < net.Generators.Count; i++)
            {
                var g = net.Generators[i];
                var loc = net.GetBus(g.Bus).Location;
                if (loc is null && !options.IncludeUnlocated) continue;
                w.WriteStartObject();
                w.WriteString("type", "Feature");
                w.WriteString("id", "gen-" + i.ToString(CultureInfo.InvariantCulture));
                WritePoint(w, loc);
                w.WriteStartObject("properties");
                w.WriteString("kind", "generator");
                WriteNum(w, "bus", g.Bus);
                WriteName(w, g.Name);
                w.WriteString("genKind", g.Kind.ToString());
                WriteNum(w, "pMw", g.P); WriteNum(w, "qMvar", g.Q);
                WriteNum(w, "pmax", g.Pmax); WriteNum(w, "pmin", g.Pmin);
                WriteNum(w, "qmax", g.Qmax); WriteNum(w, "qmin", g.Qmin);
                WriteNum(w, "vset", g.Vset); WriteNum(w, "mbase", g.MBase);
                w.WriteBoolean("inService", g.InService);
                if (result is not null && i < result.Generators.Count) { WriteNum(w, "pResultMw", result.Generators[i].P); WriteNum(w, "qResultMvar", result.Generators[i].Q); }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            for (int i = 0; i < net.Loads.Count; i++)
            {
                var l = net.Loads[i];
                var loc = net.GetBus(l.Bus).Location;
                if (loc is null && !options.IncludeUnlocated) continue;
                w.WriteStartObject();
                w.WriteString("type", "Feature");
                w.WriteString("id", "load-" + i.ToString(CultureInfo.InvariantCulture));
                WritePoint(w, loc);
                w.WriteStartObject("properties");
                w.WriteString("kind", "load");
                WriteNum(w, "bus", l.Bus);
                WriteName(w, l.Name);
                WriteNum(w, "pMw", l.P); WriteNum(w, "qMvar", l.Q);
                w.WriteBoolean("inService", l.InService);
                if (l.Category is not null) w.WriteString("category", l.Category);
                w.WriteEndObject();
                w.WriteEndObject();
            }
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
    }

    /// <summary>A GeoJSON Feature with a Polygon geometry (ring closed automatically), e.g. an outage area.</summary>
    public static string Polygon(IReadOnlyList<GeoPoint> ring, IReadOnlyDictionary<string, string>? properties = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "Feature");
            w.WriteStartObject("geometry");
            w.WriteString("type", "Polygon");
            w.WriteStartArray("coordinates");
            w.WriteStartArray();
            foreach (var p in ring) WriteCoord(w, p);
            if (ring.Count > 0 && ring[0] != ring[^1]) WriteCoord(w, ring[0]);
            w.WriteEndArray();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("properties");
            if (properties is not null) foreach (var kv in properties) w.WriteString(kv.Key, kv.Value);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteNum(Utf8JsonWriter w, string name, double v)
    {
        if (double.IsFinite(v)) w.WriteNumber(name, v); else w.WriteNull(name);
    }

    private static void WriteName(Utf8JsonWriter w, string? name)
    {
        if (name is null) w.WriteNull("name"); else w.WriteString("name", name);
    }

    private static void WritePoint(Utf8JsonWriter w, GeoPoint? p)
    {
        if (p is not GeoPoint g) { w.WriteNull("geometry"); return; }
        w.WriteStartObject("geometry");
        w.WriteString("type", "Point");
        w.WritePropertyName("coordinates");
        WriteCoord(w, g);
        w.WriteEndObject();
    }

    private static void WriteCoord(Utf8JsonWriter w, GeoPoint p)
    {
        w.WriteStartArray();
        w.WriteNumberValue(p.Longitude);
        w.WriteNumberValue(p.Latitude);
        w.WriteEndArray();
    }
}

/// <summary>Result of <see cref="GeoJsonImporter.Import(string, double)"/>.</summary>
public sealed record GeoJsonImportResult(PowerNetwork Network, IReadOnlyDictionary<int, GeoPoint[]> BranchPaths, IReadOnlyList<string> Warnings);

/// <summary>
/// Reads a GeoJSON FeatureCollection back into a <see cref="PowerNetwork"/>. Point features with <c>kind</c> "bus" (or without kind)
/// become buses; LineString / MultiLineString features with <c>kind</c> "branch" (or without kind) become branches — terminal buses come
/// from the <c>from</c>/<c>to</c> properties or, if missing, by snapping the line end points to the nearest bus within
/// <c>snapToleranceKm</c>. Missing electrical data defaults to a 0.01 + j0.1 pu line; lengths come from <c>lengthKm</c> or the geometry.
/// </summary>
public static class GeoJsonImporter
{
    /// <summary>Parses a GeoJSON string.</summary>
    public static GeoJsonImportResult Import(string json, double snapToleranceKm = 0.05)
    {
        using var doc = JsonDocument.Parse(json);
        return Import(doc.RootElement, snapToleranceKm);
    }

    /// <summary>Parses an already loaded FeatureCollection.</summary>
    public static GeoJsonImportResult Import(JsonElement root, double snapToleranceKm = 0.05)
    {
        var warnings = new List<string>();
        var net = new PowerNetwork
        {
            Name = Str(root, "name") ?? "geojson",
            BaseMva = Num(root, "baseMva") ?? 100,
            FrequencyHz = Num(root, "frequencyHz") ?? 50,
        };
        var paths = new Dictionary<int, GeoPoint[]>();
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
            return new GeoJsonImportResult(net, paths, ["No 'features' array."]);

        var lines = new List<(JsonElement Props, GeoPoint[] Path)>();
        var gens = new List<(JsonElement Props, GeoPoint? Loc)>();
        var loads = new List<(JsonElement Props, GeoPoint? Loc)>();
        int autoId = 0;
        foreach (var f in features.EnumerateArray())
        {
            var props = f.TryGetProperty("properties", out var pp) && pp.ValueKind == JsonValueKind.Object ? pp : default;
            string? kind = props.ValueKind == JsonValueKind.Object ? Str(props, "kind") : null;
            var geom = f.TryGetProperty("geometry", out var gg) && gg.ValueKind == JsonValueKind.Object ? gg : default;
            string? gtype = geom.ValueKind == JsonValueKind.Object ? Str(geom, "type") : null;
            GeoPoint? point = gtype == "Point" ? Coord(geom.GetProperty("coordinates")) : null;
            switch (kind)
            {
                case "generator": gens.Add((props, point)); continue;
                case "load": loads.Add((props, point)); continue;
            }
            if (kind == "bus" || (kind is null && gtype == "Point"))
            {
                int id = props.ValueKind == JsonValueKind.Object && Num(props, "id") is double d ? (int)d : ++autoId + 100000;
                var bus = new Bus
                {
                    Id = id, Location = point, Name = props.ValueKind == JsonValueKind.Object ? Str(props, "name") : null,
                };
                if (props.ValueKind == JsonValueKind.Object)
                {
                    bus.BaseKv = Num(props, "baseKv") ?? 1;
                    if (Enum.TryParse<BusType>(Str(props, "type"), out var bt)) bus.Type = bt;
                    bus.Vmin = Num(props, "vmin") ?? bus.Vmin; bus.Vmax = Num(props, "vmax") ?? bus.Vmax;
                    bus.Gs = Num(props, "gs") ?? 0; bus.Bs = Num(props, "bs") ?? 0;
                    bus.Area = (int)(Num(props, "area") ?? 1); bus.Zone = (int)(Num(props, "zone") ?? 1);
                    if (Num(props, "vm") is double vm) bus.Vm = vm;
                    if (Num(props, "vaDeg") is double va) bus.VaDeg = va;
                }
                if (net.ContainsBus(bus.Id)) { warnings.Add($"Duplicate bus id {bus.Id}; feature skipped."); continue; }
                net.Buses.Add(bus);
            }
            else if (kind == "branch" || (kind is null && gtype is "LineString" or "MultiLineString"))
            {
                GeoPoint[] path = gtype switch
                {
                    "LineString" => geom.GetProperty("coordinates").EnumerateArray().Select(Coord).ToArray(),
                    "MultiLineString" => geom.GetProperty("coordinates").EnumerateArray().SelectMany(l => l.EnumerateArray()).Select(Coord).ToArray(),
                    _ => [],
                };
                lines.Add((props, path));
            }
        }

        BusSpatialIndex? index = null;
        foreach (var (props, path) in lines)
        {
            bool hasProps = props.ValueKind == JsonValueKind.Object;
            int? from = hasProps && Num(props, "from") is double f ? (int)f : null;
            int? to = hasProps && Num(props, "to") is double t ? (int)t : null;
            if ((from is null || to is null) && path.Length >= 2)
            {
                index ??= new BusSpatialIndex(net);
                from ??= index.Nearest(path[0]) is { } h1 && h1.DistanceKm <= snapToleranceKm ? h1.BusId : null;
                to ??= index.Nearest(path[^1]) is { } h2 && h2.DistanceKm <= snapToleranceKm ? h2.BusId : null;
            }
            if (from is not int fb || to is not int tb || !net.ContainsBus(fb) || !net.ContainsBus(tb) || fb == tb)
            {
                warnings.Add("Line feature could not be attached to two distinct buses; skipped.");
                continue;
            }
            var br = new Branch { From = fb, To = tb, R = 0.01, X = 0.1 };
            if (hasProps)
            {
                br.Name = Str(props, "name");
                br.R = Num(props, "r") ?? br.R; br.X = Num(props, "x") ?? br.X; br.B = Num(props, "b") ?? 0;
                br.RateA = Num(props, "rateA") ?? 0; br.Tap = Num(props, "tap") ?? 0; br.ShiftDeg = Num(props, "shiftDeg") ?? 0;
                br.LengthKm = Num(props, "lengthKm") ?? 0;
                br.InService = Bool(props, "inService") ?? true;
                if (Enum.TryParse<BranchKind>(Str(props, "branchKind"), out var bk)) br.Kind = bk;
                if (Enum.TryParse<WindingConnection>(Str(props, "connection"), out var wc)) br.Connection = wc;
            }
            if (br.LengthKm <= 0 && path.Length >= 2 && !br.IsTransformer) br.LengthKm = Geo.PathLengthKm(path);
            if (path.Length > 2) paths[net.Branches.Count] = path;
            net.Branches.Add(br);
        }

        foreach (var (props, _) in gens)
        {
            if (Num(props, "bus") is not double b || !net.ContainsBus((int)b)) { warnings.Add("Generator without a known bus; skipped."); continue; }
            var g = new Generator
            {
                Bus = (int)b, Name = Str(props, "name"), P = Num(props, "pMw") ?? 0, Q = Num(props, "qMvar") ?? 0,
                Pmax = Num(props, "pmax") ?? 9999, Pmin = Num(props, "pmin") ?? 0, Qmax = Num(props, "qmax") ?? 9999, Qmin = Num(props, "qmin") ?? -9999,
                Vset = Num(props, "vset") ?? 1, MBase = Num(props, "mbase") ?? net.BaseMva, InService = Bool(props, "inService") ?? true,
            };
            if (Enum.TryParse<GeneratorKind>(Str(props, "genKind"), out var gk)) g.Kind = gk;
            net.Generators.Add(g);
        }
        foreach (var (props, _) in loads)
        {
            if (Num(props, "bus") is not double b || !net.ContainsBus((int)b)) { warnings.Add("Load without a known bus; skipped."); continue; }
            net.Loads.Add(new Load
            {
                Bus = (int)b, Name = Str(props, "name"), P = Num(props, "pMw") ?? 0, Q = Num(props, "qMvar") ?? 0,
                InService = Bool(props, "inService") ?? true, Category = Str(props, "category"),
            });
        }
        return new GeoJsonImportResult(net, paths, warnings);
    }

    /// <summary>Copies bus locations from a GeoJSON file into an existing network (matched by the <c>id</c> property). Returns the number of buses updated.</summary>
    public static int ApplyLocations(PowerNetwork net, string json)
    {
        var r = Import(json);
        int n = 0;
        foreach (var b in r.Network.Buses)
            if (b.Location is not null && net.ContainsBus(b.Id)) { net.GetBus(b.Id).Location = b.Location; n++; }
        return n;
    }

    private static GeoPoint Coord(JsonElement c) => new(c[1].GetDouble(), c[0].GetDouble());

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
