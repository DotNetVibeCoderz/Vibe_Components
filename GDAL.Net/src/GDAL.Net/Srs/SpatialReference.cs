using System.Text;
using GDAL.Net.Native;

namespace GDAL.Net.Srs;

/// <summary>WKT flavours supported by <see cref="SpatialReference.ToWkt(WktFormat, bool)"/>.</summary>
public enum WktFormat
{
    /// <summary>GDAL's default (WKT1 when possible).</summary>
    Default,
    Wkt1,
    Wkt1Esri,
    Wkt2_2015,
    Wkt2_2019,
}

/// <summary>
/// A coordinate reference system (OGRSpatialReference). Accepts EPSG codes, WKT1/WKT2, PROJJSON, PROJ strings,
/// ESRI WKT, URNs and well-known names. GDAL.Net uses longitude/easting-first axis order by default.
/// </summary>
public sealed unsafe class SpatialReference : GdalObject, IEquatable<SpatialReference>
{
    private SpatialReference(nint handle, AxisMappingStrategy axis = AxisMappingStrategy.TraditionalGisOrder)
        : base(new GdalSafeHandle(handle, &Release))
    {
        if (axis != AxisMappingStrategy.Custom)
        {
            GdalNative.OSRSetAxisMappingStrategy(handle, (int)axis);
        }
    }

    private static void Release(nint h) => GdalNative.OSRRelease(h);

    /// <summary>Creates an empty spatial reference.</summary>
    public SpatialReference() : this(GdalNative.OSRNewSpatialReference(null)) { }

    internal static SpatialReference CloneFrom(nint borrowed)
    {
        var clone = GdalNative.OSRClone(borrowed);
        return new SpatialReference(clone, AxisMappingStrategy.Custom);
    }

    // ------------------------------------------------------------------ factories

    /// <summary>From an EPSG code (e.g. 4326, 3857, 32748).</summary>
    public static SpatialReference FromEpsg(int code)
    {
        var srs = new SpatialReference();
        GdalNative.CheckOgr(GdalNative.OSRImportFromEPSG(srs.Handle, code), $"Import EPSG:{code}");
        srs.AxisMappingStrategy = AxisMappingStrategy.TraditionalGisOrder;
        return srs;
    }

    /// <summary>From WKT (WKT1 or WKT2).</summary>
    public static SpatialReference FromWkt(string wkt)
    {
        var srs = new SpatialReference();
        var bytes = Utf8.Encode(wkt);
        fixed (byte* p = bytes)
        {
            var ptr = p;
            GdalNative.CheckOgr(GdalNative.OSRImportFromWkt(srs.Handle, &ptr), "Import WKT");
        }
        srs.AxisMappingStrategy = AxisMappingStrategy.TraditionalGisOrder;
        return srs;
    }

    /// <summary>From a PROJ string, e.g. "+proj=utm +zone=48 +south +datum=WGS84".</summary>
    public static SpatialReference FromProj4(string proj)
    {
        var srs = new SpatialReference();
        GdalNative.CheckOgr(GdalNative.OSRImportFromProj4(srs.Handle, proj), "Import PROJ string");
        srs.AxisMappingStrategy = AxisMappingStrategy.TraditionalGisOrder;
        return srs;
    }

    /// <summary>From ESRI .prj WKT content.</summary>
    public static SpatialReference FromEsri(string esriWkt)
    {
        var srs = new SpatialReference();
        using var lines = new NativeStringList(esriWkt.Split('\n'));
        GdalNative.CheckOgr(GdalNative.OSRImportFromESRI(srs.Handle, lines.Pointer), "Import ESRI WKT");
        srs.AxisMappingStrategy = AxisMappingStrategy.TraditionalGisOrder;
        return srs;
    }

    /// <summary>
    /// From any user input understood by GDAL: "EPSG:4326", "WGS84", "urn:ogc:def:crs:EPSG::32748", WKT, PROJJSON, PROJ strings, ...
    /// </summary>
    public static SpatialReference FromUserInput(string definition)
    {
        var srs = new SpatialReference();
        GdalNative.CheckOgr(GdalNative.OSRSetFromUserInput(srs.Handle, definition), $"Parse CRS '{Truncate(definition)}'");
        srs.AxisMappingStrategy = AxisMappingStrategy.TraditionalGisOrder;
        return srs;
    }

    /// <summary>From PROJJSON.</summary>
    public static SpatialReference FromProjJson(string projJson) => FromUserInput(projJson);

    /// <summary>WGS 84 geographic (EPSG:4326, lon/lat order).</summary>
    public static SpatialReference Wgs84 => FromEpsg(4326);

    /// <summary>Web Mercator (EPSG:3857).</summary>
    public static SpatialReference WebMercator => FromEpsg(3857);

    /// <summary>WGS 84 / UTM zone (EPSG:326xx north, 327xx south).</summary>
    public static SpatialReference Utm(int zone, bool north) => FromEpsg((north ? 32600 : 32700) + zone);

    /// <summary>Best UTM zone for a longitude/latitude.</summary>
    public static SpatialReference UtmFor(double longitude, double latitude)
        => Utm(Math.Clamp((int)Math.Floor((longitude + 180) / 6) + 1, 1, 60), latitude >= 0);

    private static string Truncate(string s) => s.Length <= 60 ? s : s[..60] + "…";

    // ------------------------------------------------------------------ export

    /// <summary>Exports to WKT.</summary>
    public string ToWkt(WktFormat format = WktFormat.Default, bool multiline = false)
    {
        using var opts = new NativeStringList(null);
        if (format != WktFormat.Default)
        {
            opts.Add("FORMAT=" + format switch
            {
                WktFormat.Wkt1 => "WKT1",
                WktFormat.Wkt1Esri => "WKT1_ESRI",
                WktFormat.Wkt2_2015 => "WKT2_2015",
                _ => "WKT2_2019",
            });
        }
        opts.Add("MULTILINE=" + (multiline ? "YES" : "NO"));
        nint result;
        GdalNative.CheckOgr(GdalNative.OSRExportToWktEx(Handle, &result, opts.Pointer), "Export WKT");
        return Interop.TakeString(result) ?? string.Empty;
    }

    /// <summary>Exports to PROJJSON.</summary>
    public string ToProjJson(bool multiline = true)
    {
        using var opts = new NativeStringList([multiline ? "MULTILINE=YES" : "MULTILINE=NO"]);
        nint result;
        GdalNative.CheckOgr(GdalNative.OSRExportToPROJJSON(Handle, &result, opts.Pointer), "Export PROJJSON");
        return Interop.TakeString(result) ?? string.Empty;
    }

    /// <summary>Exports to a PROJ string (may lose information).</summary>
    public string ToProj4()
    {
        nint result;
        GdalNative.CheckOgr(GdalNative.OSRExportToProj4(Handle, &result), "Export PROJ string");
        return Interop.TakeString(result)?.Trim() ?? string.Empty;
    }

    /// <summary>Exports to ESRI WKT (.prj content).</summary>
    public string ToEsriWkt() => ToWkt(WktFormat.Wkt1Esri);

    /// <summary>Pretty printed WKT1.</summary>
    public string ToPrettyWkt(bool simplify = false)
    {
        nint result;
        GdalNative.CheckOgr(GdalNative.OSRExportToPrettyWkt(Handle, &result, simplify ? 1 : 0), "Export pretty WKT");
        return Interop.TakeString(result) ?? string.Empty;
    }

    // ------------------------------------------------------------------ properties

    public string Name => Interop.PtrToString(GdalNative.OSRGetName(Handle)) ?? string.Empty;

    public string? AuthorityName => Interop.PtrToString(GdalNative.OSRGetAuthorityName(Handle, null));

    public string? AuthorityCode => Interop.PtrToString(GdalNative.OSRGetAuthorityCode(Handle, null));

    /// <summary>EPSG code when known (tries auto-identification when not explicit).</summary>
    public int? Epsg
    {
        get
        {
            if (string.Equals(AuthorityName, "EPSG", StringComparison.OrdinalIgnoreCase) && int.TryParse(AuthorityCode, out var code)) return code;
            using var clone = Clone();
            if (GdalNative.OSRAutoIdentifyEPSG(clone.Handle) == 0
                && string.Equals(clone.AuthorityName, "EPSG", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(clone.AuthorityCode, out code))
            {
                return code;
            }
            GdalNative.CPLErrorReset();
            return null;
        }
    }

    public bool IsGeographic => GdalNative.OSRIsGeographic(Handle) != 0;
    public bool IsProjected => GdalNative.OSRIsProjected(Handle) != 0;
    public bool IsGeocentric => GdalNative.OSRIsGeocentric(Handle) != 0;
    public bool IsCompound => GdalNative.OSRIsCompound(Handle) != 0;

    /// <summary>Linear unit (name, meters per unit).</summary>
    public (string Name, double ToMeters) LinearUnits
    {
        get
        {
            nint name;
            var v = GdalNative.OSRGetLinearUnits(Handle, &name);
            return (Interop.PtrToString(name) ?? string.Empty, v);
        }
    }

    /// <summary>Angular unit (name, radians per unit).</summary>
    public (string Name, double ToRadians) AngularUnits
    {
        get
        {
            nint name;
            var v = GdalNative.OSRGetAngularUnits(Handle, &name);
            return (Interop.PtrToString(name) ?? string.Empty, v);
        }
    }

    /// <summary>UTM zone (positive north / negative south), or 0.</summary>
    public int UtmZone
    {
        get
        {
            int north;
            var zone = GdalNative.OSRGetUTMZone(Handle, &north);
            return north != 0 ? zone : -zone;
        }
    }

    /// <summary>Area of use in WGS84 degrees, or null.</summary>
    public (Envelope Bounds, string Name)? AreaOfUse
    {
        get
        {
            double w, s, e, n;
            nint name;
            if (GdalNative.OSRGetAreaOfUse(Handle, &w, &s, &e, &n, &name) == 0) return null;
            return (new Envelope(w, s, e, n), Interop.PtrToString(name) ?? string.Empty);
        }
    }

    public AxisMappingStrategy AxisMappingStrategy
    {
        get => (AxisMappingStrategy)GdalNative.OSRGetAxisMappingStrategy(Handle);
        set => GdalNative.OSRSetAxisMappingStrategy(Handle, (int)value);
    }

    /// <summary>Validates the definition.</summary>
    public bool Validate() => GdalNative.OSRValidate(Handle) == 0;

    public SpatialReference Clone() => new(GdalNative.OSRClone(Handle), AxisMappingStrategy.Custom);

    /// <summary>True when both describe the same CRS.</summary>
    public bool IsSame(SpatialReference other) => GdalNative.OSRIsSame(Handle, other.Handle) != 0;

    public bool Equals(SpatialReference? other) => other is not null && IsSame(other);
    public override bool Equals(object? obj) => obj is SpatialReference s && Equals(s);
    public override int GetHashCode() => Name.GetHashCode(StringComparison.Ordinal);

    public override string ToString()
    {
        var sb = new StringBuilder(Name);
        if (AuthorityName is { } a && AuthorityCode is { } c) sb.Append(" (").Append(a).Append(':').Append(c).Append(')');
        return sb.ToString();
    }
}
