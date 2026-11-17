using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>
/// PROJJSON (PROJ ≥ 6 JSON CRS encoding, schema v0.7) writer and reader. Required by GeoParquet and GeoArrow metadata;
/// readable by pyproj, GDAL, DuckDB and Apache Sedona.
/// </summary>
public static class ProjJson
{
    private const string Schema = "https://proj.org/schemas/v0.7/projjson.schema.json";

    /// <summary>Full PROJJSON (base CRS, datum, conversion, axes) plus the EPSG id when known.</summary>
    public static string Write(CoordinateReferenceSystem crs, bool indented = false) =>
        ToNode(crs).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public static JsonObject ToNode(CoordinateReferenceSystem crs)
    {
        var root = crs.IsGeographic ? Geographic(crs, true) : Projected(crs);
        return root;
    }

    private static JsonObject Id(int code) => new() { ["authority"] = "EPSG", ["code"] = code };

    private static JsonObject EllipsoidNode(Ellipsoid e) => e.IsSphere
        ? new JsonObject { ["name"] = e.Name, ["radius"] = e.A }
        : new JsonObject { ["name"] = e.Name, ["semi_major_axis"] = e.A, ["inverse_flattening"] = e.InverseFlattening };

    private static string DatumName(Datum d) => d.Name.Replace('_', ' ') switch
    {
        "WGS 1984" => "World Geodetic System 1984",
        var n => n
    };

    private static JsonObject Geographic(CoordinateReferenceSystem crs, bool root)
    {
        var o = new JsonObject();
        if (root) o["$schema"] = Schema;
        o["type"] = "GeographicCRS";
        o["name"] = crs.IsGeographic ? crs.Name
            : crs.Datum.Ellipsoid == Ellipsoid.Wgs84 && crs.Datum.IsWgs84Compatible ? "WGS 84" : crs.Datum.Name.Replace('_', ' ');
        o["datum"] = new JsonObject
        {
            ["type"] = "GeodeticReferenceFrame",
            ["name"] = DatumName(crs.Datum),
            ["ellipsoid"] = EllipsoidNode(crs.Datum.Ellipsoid)
        };
        o["coordinate_system"] = new JsonObject
        {
            ["subtype"] = "ellipsoidal",
            ["axis"] = new JsonArray(
                new JsonObject { ["name"] = "Geodetic latitude", ["abbreviation"] = "Lat", ["direction"] = "north", ["unit"] = "degree" },
                new JsonObject { ["name"] = "Geodetic longitude", ["abbreviation"] = "Lon", ["direction"] = "east", ["unit"] = "degree" })
        };
        if (root && crs.Epsg is { } c) o["id"] = Id(c);
        return o;
    }

    private static JsonObject Param(string name, int code, double value, bool angle) => new()
    {
        ["name"] = name, ["value"] = value, ["unit"] = angle ? "degree" : "metre", ["id"] = Id(code)
    };

    private static JsonObject Projected(CoordinateReferenceSystem crs)
    {
        var p = crs.Projection!.Parameters;
        string method; int methodCode;
        var pars = new JsonArray();
        switch (crs.Projection)
        {
            case TransverseMercator:
                method = "Transverse Mercator"; methodCode = 9807;
                pars.Add((JsonNode)Param("Latitude of natural origin", 8801, p.Lat0, true));
                pars.Add((JsonNode)Param("Longitude of natural origin", 8802, p.Lon0, true));
                pars.Add((JsonNode)new JsonObject { ["name"] = "Scale factor at natural origin", ["value"] = p.K0, ["unit"] = "unity", ["id"] = Id(8805) });
                break;
            case WebMercator:
                method = "Popular Visualisation Pseudo Mercator"; methodCode = 1024;
                pars.Add((JsonNode)Param("Latitude of natural origin", 8801, 0, true));
                pars.Add((JsonNode)Param("Longitude of natural origin", 8802, p.Lon0, true));
                break;
            case Mercator when !double.IsNaN(p.LatTs):
                method = "Mercator (variant B)"; methodCode = 9805;
                pars.Add((JsonNode)Param("Latitude of 1st standard parallel", 8823, p.LatTs, true));
                pars.Add((JsonNode)Param("Longitude of natural origin", 8802, p.Lon0, true));
                break;
            case Mercator:
                method = "Mercator (variant A)"; methodCode = 9804;
                pars.Add((JsonNode)Param("Latitude of natural origin", 8801, 0, true));
                pars.Add((JsonNode)Param("Longitude of natural origin", 8802, p.Lon0, true));
                pars.Add((JsonNode)new JsonObject { ["name"] = "Scale factor at natural origin", ["value"] = p.K0, ["unit"] = "unity", ["id"] = Id(8805) });
                break;
            case LambertConformalConic when double.IsNaN(p.Lat2):
                method = "Lambert Conic Conformal (1SP)"; methodCode = 9801;
                pars.Add((JsonNode)Param("Latitude of natural origin", 8801, double.IsNaN(p.Lat1) ? p.Lat0 : p.Lat1, true));
                pars.Add((JsonNode)Param("Longitude of natural origin", 8802, p.Lon0, true));
                pars.Add((JsonNode)new JsonObject { ["name"] = "Scale factor at natural origin", ["value"] = p.K0, ["unit"] = "unity", ["id"] = Id(8805) });
                break;
            case LambertConformalConic or AlbersEqualArea:
                bool lcc = crs.Projection is LambertConformalConic;
                method = lcc ? "Lambert Conic Conformal (2SP)" : "Albers Equal Area"; methodCode = lcc ? 9802 : 9822;
                pars.Add((JsonNode)Param("Latitude of false origin", 8821, p.Lat0, true));
                pars.Add((JsonNode)Param("Longitude of false origin", 8822, p.Lon0, true));
                pars.Add((JsonNode)Param("Latitude of 1st standard parallel", 8823, p.Lat1, true));
                pars.Add((JsonNode)Param("Latitude of 2nd standard parallel", 8824, double.IsNaN(p.Lat2) ? p.Lat1 : p.Lat2, true));
                pars.Add((JsonNode)Param("Easting at false origin", 8826, p.FalseEasting, false));
                pars.Add((JsonNode)Param("Northing at false origin", 8827, p.FalseNorthing, false));
                break;
            default:
                throw new NotSupportedException($"PROJJSON export for {crs.Projection!.GetType().Name} is not implemented.");
        }
        if (methodCode is not (9802 or 9822))
        {
            pars.Add((JsonNode)Param("False easting", 8806, p.FalseEasting, false));
            pars.Add((JsonNode)Param("False northing", 8807, p.FalseNorthing, false));
        }
        JsonNode unit = crs.ToMeter == 1 ? "metre" : new JsonObject { ["type"] = "LinearUnit", ["name"] = crs.UnitName, ["conversion_factor"] = crs.ToMeter };
        var o = new JsonObject
        {
            ["$schema"] = Schema,
            ["type"] = "ProjectedCRS",
            ["name"] = crs.Name,
            ["base_crs"] = Geographic(crs, false),
            ["conversion"] = new JsonObject
            {
                ["name"] = p.UtmZone is { } z ? $"UTM zone {z}{(p.UtmSouth ? 'S' : 'N')}" : crs.Name,
                ["method"] = new JsonObject { ["name"] = method, ["id"] = Id(methodCode) },
                ["parameters"] = pars
            },
            ["coordinate_system"] = new JsonObject
            {
                ["subtype"] = "Cartesian",
                ["axis"] = new JsonArray(
                    new JsonObject { ["name"] = "Easting", ["abbreviation"] = "E", ["direction"] = "east", ["unit"] = unit.DeepClone() },
                    new JsonObject { ["name"] = "Northing", ["abbreviation"] = "N", ["direction"] = "north", ["unit"] = unit.DeepClone() })
            }
        };
        if (crs.Epsg is { } c) o["id"] = Id(c);
        return o;
    }

    // ================================================================== reading

    /// <summary>Parses PROJJSON. Uses the EPSG id when the registry knows it, otherwise rebuilds the CRS from its definition.</summary>
    public static CoordinateReferenceSystem Parse(string json) => Parse(JsonNode.Parse(json) ?? throw new FormatException("Empty PROJJSON."));

    public static bool TryParse(string json, out CoordinateReferenceSystem? crs)
    {
        try { crs = Parse(json); return true; }
        catch (Exception) { crs = null; return false; }
    }

    public static CoordinateReferenceSystem Parse(JsonNode node)
    {
        var type = node["type"]?.GetValue<string>() ?? throw new FormatException("PROJJSON object has no 'type'.");
        if (type == "BoundCRS") return Parse(node["source_crs"]!);
        if (EpsgId(node) is { } code && CrsRegistry.TryGet(code, out var known)) return known!;
        return type switch
        {
            "GeographicCRS" or "GeodeticCRS" => CrsRegistry.Geographic(node["name"]?.GetValue<string>() ?? "Unknown", ReadDatum(node), EpsgId(node)),
            "ProjectedCRS" => ReadProjected(node),
            _ => throw new NotSupportedException($"PROJJSON type '{type}' is not supported.")
        };
    }

    private static int? EpsgId(JsonNode node)
    {
        var id = node["id"] ?? node["ids"]?.AsArray().FirstOrDefault();
        if (id is null) return null;
        if (!string.Equals(id["authority"]?.GetValue<string>(), "EPSG", StringComparison.OrdinalIgnoreCase)) return null;
        var c = id["code"];
        return c is null ? null : c.GetValueKind() == JsonValueKind.Number ? c.GetValue<int>() : int.TryParse(c.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static Datum ReadDatum(JsonNode crs)
    {
        var datum = crs["datum"] ?? crs["datum_ensemble"] ?? throw new FormatException("PROJJSON geodetic CRS has no datum.");
        var e = datum["ellipsoid"] ?? throw new FormatException("PROJJSON datum has no ellipsoid.");
        var name = e["name"]?.GetValue<string>() ?? "Unknown";
        Ellipsoid ell = e["radius"] is { } r
            ? new Ellipsoid(name, Num(r), 0)
            : e["inverse_flattening"] is { } invf ? new Ellipsoid(name, Num(e["semi_major_axis"]!), Num(invf))
            : Ellipsoid.FromAxes(name, Num(e["semi_major_axis"]!), Num(e["semi_minor_axis"]!));
        if (Ellipsoid.FromName(name) is { } std && Math.Abs(std.A - ell.A) < 1e-6 && Math.Abs(std.InverseFlattening - ell.InverseFlattening) < 1e-6) ell = std;
        var dn = datum["name"]?.GetValue<string>() ?? "Unknown";
        return ell == Ellipsoid.Wgs84 && dn.Contains("1984") ? Datum.Wgs84 : new Datum(dn.Replace(' ', '_'), ell);
    }

    private static double Num(JsonNode n) => n is JsonObject o ? Num(o["value"]!) : n.GetValue<double>();

    private static CoordinateReferenceSystem ReadProjected(JsonNode node)
    {
        var baseCrs = node["base_crs"] ?? throw new FormatException("PROJJSON ProjectedCRS has no base_crs.");
        var datum = ReadDatum(baseCrs);
        var conv = node["conversion"] ?? throw new FormatException("PROJJSON ProjectedCRS has no conversion.");
        var method = conv["method"]!;
        int? mcode = EpsgId(method);
        string mname = method["name"]?.GetValue<string>() ?? "";
        var pars = new Dictionary<int, double>();
        foreach (var p in conv["parameters"]?.AsArray() ?? [])
        {
            if (p is null) continue;
            int? code = EpsgId(p) ?? ParamCodeFromName(p["name"]?.GetValue<string>() ?? "");
            if (code is { } c) pars[c] = p["value"]!.GetValue<double>();
        }
        double P(int code, double def = 0) => pars.TryGetValue(code, out var v) ? v : def;
        mcode ??= mname.ToLowerInvariant() switch
        {
            "transverse mercator" => 9807, "popular visualisation pseudo mercator" => 1024, "mercator (variant a)" => 9804,
            "mercator (variant b)" => 9805, "lambert conic conformal (1sp)" => 9801, "lambert conic conformal (2sp)" => 9802,
            "albers equal area" => 9822, _ => null
        };
        var e = datum.Ellipsoid;
        Projection proj = mcode switch
        {
            9807 => new TransverseMercator(e, new ProjectionParameters { Lat0 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) }),
            1024 => new WebMercator(Ellipsoid.Sphere, new ProjectionParameters { Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) }),
            9804 => new Mercator(e, new ProjectionParameters { Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) }),
            9805 => new Mercator(e, new ProjectionParameters { LatTs = P(8823), Lon0 = P(8802), FalseEasting = P(8806), FalseNorthing = P(8807) }),
            9801 => new LambertConformalConic(e, new ProjectionParameters { Lat0 = P(8801), Lat1 = P(8801), Lon0 = P(8802), K0 = P(8805, 1), FalseEasting = P(8806), FalseNorthing = P(8807) }),
            9802 => new LambertConformalConic(e, new ProjectionParameters { Lat0 = P(8821), Lon0 = P(8822), Lat1 = P(8823), Lat2 = P(8824), FalseEasting = P(8826), FalseNorthing = P(8827) }),
            9822 => new AlbersEqualArea(e, new ProjectionParameters { Lat0 = P(8821), Lon0 = P(8822), Lat1 = P(8823), Lat2 = P(8824), FalseEasting = P(8826), FalseNorthing = P(8827) }),
            _ => throw new NotSupportedException($"PROJJSON conversion method '{mname}' is not supported yet.")
        };
        double toMeter = 1;
        var axisUnit = node["coordinate_system"]?["axis"]?.AsArray().FirstOrDefault()?["unit"];
        if (axisUnit is JsonObject u && u["conversion_factor"] is { } cf) toMeter = cf.GetValue<double>();
        return CrsRegistry.Projected(node["name"]?.GetValue<string>() ?? "Unknown", datum, proj, EpsgId(node), toMeter,
            axisUnit is JsonObject u2 ? u2["name"]?.GetValue<string>() : null);
    }

    private static int? ParamCodeFromName(string name) => name.ToLowerInvariant() switch
    {
        "latitude of natural origin" => 8801, "longitude of natural origin" => 8802, "scale factor at natural origin" => 8805,
        "false easting" => 8806, "false northing" => 8807, "latitude of false origin" => 8821, "longitude of false origin" => 8822,
        "latitude of 1st standard parallel" => 8823, "latitude of 2nd standard parallel" => 8824,
        "easting at false origin" => 8826, "northing at false origin" => 8827, _ => null
    };
}

public static class ProjJsonExtensions
{
    /// <summary>PROJJSON representation (used by GeoParquet / GeoArrow).</summary>
    public static string ToProjJson(this CoordinateReferenceSystem crs, bool indented = false) => ProjJson.Write(crs, indented);
}
