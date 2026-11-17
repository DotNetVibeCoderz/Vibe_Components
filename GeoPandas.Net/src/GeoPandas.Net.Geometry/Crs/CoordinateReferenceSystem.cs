using System.Globalization;
using System.Text;

namespace GeoPandas.Net.Geometry.Crs;

public enum CrsKind { Geographic, Projected }

/// <summary>
/// Coordinate Reference System. Create with <see cref="FromEpsg"/>, <see cref="Parse"/> (accepts "EPSG:4326",
/// PROJ.4 strings, OGC WKT1, ESRI .prj WKT and WKT2) or the static well-known instances.
/// Geographic CRSs use longitude/latitude in degrees (x = lon, y = lat, like GeoPandas "always_xy").
/// </summary>
public sealed class CoordinateReferenceSystem : IEquatable<CoordinateReferenceSystem>
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal CoordinateReferenceSystem(string name, CrsKind kind, Datum datum, Projection? projection, int? epsg,
        double toMeter = 1.0, string? unitName = null)
    {
        Name = name;
        Kind = kind;
        Datum = datum;
        Projection = projection;
        Epsg = epsg;
        ToMeter = toMeter;
        UnitName = unitName ?? (kind == CrsKind.Geographic ? "degree" : toMeter == 1.0 ? "metre" : "unit");
    }

    public string Name { get; }
    public CrsKind Kind { get; }
    public Datum Datum { get; }
    public Ellipsoid Ellipsoid => Datum.Ellipsoid;
    /// <summary>Projection, or null for geographic CRSs.</summary>
    public Projection? Projection { get; }
    /// <summary>EPSG code when known.</summary>
    public int? Epsg { get; }
    /// <summary>Linear unit conversion factor to metres (projected CRSs).</summary>
    public double ToMeter { get; }
    public string UnitName { get; }

    public bool IsGeographic => Kind == CrsKind.Geographic;
    public bool IsProjected => Kind == CrsKind.Projected;

    /// <summary>Authority string such as "EPSG:4326", or null.</summary>
    public string? Authority => Epsg is { } c ? $"EPSG:{c}" : null;

    public static CoordinateReferenceSystem Wgs84 => CrsRegistry.Get(4326);
    public static CoordinateReferenceSystem WebMercator => CrsRegistry.Get(3857);

    public static CoordinateReferenceSystem FromEpsg(int code) => CrsRegistry.Get(code);
    public static CoordinateReferenceSystem Parse(string definition) => CrsParser.Parse(definition);
    public static bool TryParse(string definition, out CoordinateReferenceSystem? crs)
    {
        try { crs = Parse(definition); return true; }
        catch (Exception) { crs = null; return false; }
    }

    /// <summary>UTM zone for WGS 84 (EPSG:326zz north / 327zz south).</summary>
    public static CoordinateReferenceSystem Utm(int zone, bool south) => FromEpsg((south ? 32700 : 32600) + zone);

    /// <summary>Best UTM CRS for a lon/lat location (GeoPandas <c>estimate_utm_crs</c>).</summary>
    public static CoordinateReferenceSystem EstimateUtm(double lon, double lat)
    {
        int zone = Math.Clamp((int)Math.Floor((lon + 180) / 6) + 1, 1, 60);
        return Utm(zone, lat < 0);
    }

    /// <summary>PROJ.4 representation.</summary>
    public string ToProj4()
    {
        var sb = new StringBuilder();
        if (Projection is null) sb.Append("+proj=longlat");
        else
        {
            var p = Projection.Parameters;
            if (Projection is TransverseMercator && p.UtmZone is { } z)
                sb.Append($"+proj=utm +zone={z}").Append(p.UtmSouth ? " +south" : "");
            else if (Projection is global::GeoPandas.Net.Geometry.Crs.WebMercator)
                sb.Append(Inv, $"+proj=merc +a={Ellipsoid.A} +b={Ellipsoid.A} +lat_ts=0 +lon_0={p.Lon0} +x_0={p.FalseEasting} +y_0={p.FalseNorthing} +k=1 +nadgrids=@null +wktext");
            else
            {
                sb.Append("+proj=").Append(Projection.ProjName);
                sb.Append(Inv, $" +lat_0={p.Lat0} +lon_0={p.Lon0}");
                if (!double.IsNaN(p.Lat1)) sb.Append(Inv, $" +lat_1={p.Lat1}");
                if (!double.IsNaN(p.Lat2)) sb.Append(Inv, $" +lat_2={p.Lat2}");
                if (!double.IsNaN(p.LatTs)) sb.Append(Inv, $" +lat_ts={p.LatTs}");
                if (Projection is TransverseMercator or Mercator || p.K0 != 1) sb.Append(Inv, $" +k={p.K0}");
                sb.Append(Inv, $" +x_0={p.FalseEasting} +y_0={p.FalseNorthing}");
            }
        }
        if (Projection is not global::GeoPandas.Net.Geometry.Crs.WebMercator)
        {
            if (Ellipsoid == Ellipsoid.Wgs84 && Datum.IsWgs84Compatible) sb.Append(" +datum=WGS84");
            else
            {
                sb.Append(Inv, $" +a={Ellipsoid.A}");
                if (Ellipsoid.IsSphere) sb.Append(Inv, $" +b={Ellipsoid.A}");
                else sb.Append(Inv, $" +rf={Ellipsoid.InverseFlattening}");
                if (Datum.ToWgs84 is { } t) sb.Append(" +towgs84=").Append(string.Join(",", t.Select(v => v.ToString(Inv))));
            }
        }
        if (IsProjected) sb.Append(ToMeter == 1 ? " +units=m" : string.Create(Inv, $" +to_meter={ToMeter}"));
        sb.Append(" +no_defs");
        return sb.ToString();
    }

    /// <summary>OGC WKT1 representation (GDAL-compatible, usable as an ESRI .prj).</summary>
    public string ToWkt()
    {
        string auth(int? code) => code is { } c ? $",AUTHORITY[\"EPSG\",\"{c}\"]" : "";
        var e = Ellipsoid;
        var geog = new StringBuilder();
        geog.Append(Inv, $"GEOGCS[\"{(IsGeographic ? Name : Datum.Name)}\",DATUM[\"{Datum.Name}\",SPHEROID[\"{e.Name}\",{e.A},{e.InverseFlattening}]");
        if (Datum.ToWgs84 is { } t) geog.Append(",TOWGS84[").Append(string.Join(",", t.Select(v => v.ToString(Inv)))).Append(']');
        geog.Append("],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]");
        if (IsGeographic) { geog.Append(auth(Epsg)).Append(']'); return geog.ToString(); }
        geog.Append(']');

        var p = Projection!.Parameters;
        var sb = new StringBuilder();
        sb.Append($"PROJCS[\"{Name}\",").Append(geog).Append($",PROJECTION[\"{Projection.WktName}\"]");
        void Param(string n, double v) => sb.Append(Inv, $",PARAMETER[\"{n}\",{v}]");
        switch (Projection)
        {
            case LambertConformalConic or AlbersEqualArea:
                if (Projection is AlbersEqualArea || !double.IsNaN(p.Lat2))
                {
                    Param("standard_parallel_1", p.Lat1);
                    Param("standard_parallel_2", double.IsNaN(p.Lat2) ? p.Lat1 : p.Lat2);
                    Param("latitude_of_origin", p.Lat0);
                    Param("central_meridian", p.Lon0);
                }
                else
                {
                    Param("latitude_of_origin", double.IsNaN(p.Lat1) ? p.Lat0 : p.Lat1);
                    Param("central_meridian", p.Lon0);
                    Param("scale_factor", p.K0);
                }
                break;
            case Mercator when !double.IsNaN(p.LatTs):
                Param("standard_parallel_1", p.LatTs);
                Param("central_meridian", p.Lon0);
                break;
            default:
                Param("latitude_of_origin", p.Lat0);
                Param("central_meridian", p.Lon0);
                Param("scale_factor", p.K0);
                break;
        }
        Param("false_easting", p.FalseEasting);
        Param("false_northing", p.FalseNorthing);
        sb.Append(Inv, $",UNIT[\"{(ToMeter == 1 ? "metre" : UnitName)}\",{ToMeter}]");
        sb.Append(auth(Epsg)).Append(']');
        return sb.ToString();
    }

    public bool Equals(CoordinateReferenceSystem? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Epsg is not null && other.Epsg is not null) return Epsg == other.Epsg;
        return ToProj4() == other.ToProj4();
    }

    public override bool Equals(object? obj) => Equals(obj as CoordinateReferenceSystem);
    public override int GetHashCode() => Epsg ?? ToProj4().GetHashCode();
    public static bool operator ==(CoordinateReferenceSystem? a, CoordinateReferenceSystem? b) => a?.Equals(b) ?? b is null;
    public static bool operator !=(CoordinateReferenceSystem? a, CoordinateReferenceSystem? b) => !(a == b);

    public override string ToString() => Authority is { } a ? $"{a} ({Name})" : Name;
}
