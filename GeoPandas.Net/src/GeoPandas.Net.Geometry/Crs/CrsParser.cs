using System.Globalization;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>Parses CRS definitions: "EPSG:xxxx", bare codes, URNs, PROJ.4 strings, OGC WKT1, ESRI WKT and WKT2.</summary>
public static class CrsParser
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static CoordinateReferenceSystem Parse(string definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition);
        var s = definition.Trim();

        if (TryParseAuthorityCode(s, out int code)) return CrsRegistry.Get(code);
        if (s.StartsWith('{')) return ProjJson.Parse(s);
        if (s.StartsWith('+') || s.Contains("+proj=", StringComparison.OrdinalIgnoreCase)) return FromProj4(s);
        if (s.Contains('[')) return FromWkt(s);

        var lower = s.ToLowerInvariant();
        return lower switch
        {
            "wgs84" or "wgs 84" or "crs84" or "ogc:crs84" or "urn:ogc:def:crs:ogc:1.3:crs84" or "latlong" or "lonlat" => CrsRegistry.Get(4326),
            "webmercator" or "pseudo-mercator" or "google" => CrsRegistry.Get(3857),
            _ => throw new FormatException($"Unrecognized CRS definition '{definition}'.")
        };
    }

    private static bool TryParseAuthorityCode(string s, out int code)
    {
        code = 0;
        if (int.TryParse(s, NumberStyles.Integer, Inv, out code)) return true;
        var lower = s.ToLowerInvariant();
        if (lower.StartsWith("epsg:")) return int.TryParse(s.AsSpan(5), NumberStyles.Integer, Inv, out code);
        // urn:ogc:def:crs:EPSG::4326 or http://www.opengis.net/def/crs/EPSG/0/4326
        if (lower.Contains("epsg"))
        {
            int i = s.Length - 1;
            while (i >= 0 && char.IsDigit(s[i])) i--;
            if (i < s.Length - 1 && (s[i] == ':' || s[i] == '/'))
                return int.TryParse(s.AsSpan(i + 1), NumberStyles.Integer, Inv, out code);
        }
        return false;
    }

    // ------------------------------------------------------------------ PROJ.4

    public static CoordinateReferenceSystem FromProj4(string proj4)
    {
        var p = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in proj4.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.TrimStart('+');
            int eq = t.IndexOf('=');
            if (eq < 0) p[t] = null; else p[t[..eq]] = t[(eq + 1)..];
        }

        if (p.TryGetValue("init", out var init) && init is not null && TryParseAuthorityCode(init, out int ic))
            return CrsRegistry.Get(ic);

        string proj = p.GetValueOrDefault("proj") ?? throw new FormatException("PROJ string has no +proj.");
        double D(string key, double def = 0) => p.TryGetValue(key, out var v) && v is not null ? double.Parse(v, Inv) : def;

        var ellipsoid = ParseProj4Ellipsoid(p);
        double[]? towgs = p.TryGetValue("towgs84", out var tw) && tw is not null
            ? PadTo7(tw.Split(',').Select(x => double.Parse(x, Inv)).ToArray())
            : null;
        string datumName = "Unknown";
        if (p.TryGetValue("datum", out var dn) && dn is not null)
        {
            datumName = dn;
            switch (dn.ToUpperInvariant())
            {
                case "WGS84": ellipsoid = Ellipsoid.Wgs84; datumName = "WGS_1984"; break;
                case "NAD83": ellipsoid = Ellipsoid.Grs80; towgs ??= new double[7]; break;
                case "NAD27": ellipsoid = Ellipsoid.Clarke1866; towgs ??= [-8, 160, 176, 0, 0, 0, 0]; break;
                case "OSGB36": ellipsoid = Ellipsoid.Airy1830; towgs ??= [446.448, -125.157, 542.06, 0.15, 0.247, 0.842, -20.489]; break;
            }
        }
        else if (ellipsoid == Ellipsoid.Wgs84) datumName = "WGS_1984";
        var datum = new Datum(datumName, ellipsoid, towgs);
        if (p.TryGetValue("nadgrids", out var ng) && ng is not null && GridCatalog.Resolve(ng) is { } grid)
            datum = new Datum(datumName == "Unknown" ? grid.FromDatum ?? "Unknown" : datumName, ellipsoid, null) { GridShift = grid };

        if (proj is "longlat" or "latlong" or "lonlat" or "latlon")
        {
            var code = datum.Ellipsoid == Ellipsoid.Wgs84 && datum.IsWgs84Compatible ? 4326 : (int?)null;
            return WithGeoidGrid(code is { } c ? CrsRegistry.Get(c) : CrsRegistry.Geographic("Custom geographic", datum, null), p);
        }

        double toMeter = p.TryGetValue("to_meter", out var tm) && tm is not null ? double.Parse(tm, Inv) : UnitToMeter(p.GetValueOrDefault("units"));

        if (proj == "utm")
        {
            int zone = (int)D("zone");
            bool south = p.ContainsKey("south");
            if (ellipsoid == Ellipsoid.Wgs84 && datum.IsWgs84Compatible) return WithGeoidGrid(CrsRegistry.Get((south ? 32700 : 32600) + zone), p);
            return WithGeoidGrid(CrsRegistry.Projected($"UTM zone {zone}{(south ? 'S' : 'N')}", datum,
                new TransverseMercator(ellipsoid, ProjectionParameters.Utm(zone, south)), null, toMeter), p);
        }

        bool sphere = ellipsoid.IsSphere || (p.ContainsKey("nadgrids") && proj == "merc" && ellipsoid.A == 6378137 && ellipsoid.IsSphere);
        if (proj == "merc" && sphere && ellipsoid.A == 6378137 && D("x_0") == 0 && D("y_0") == 0 && D("lon_0") == 0)
            return CrsRegistry.Get(3857);

        var pp = new ProjectionParameters
        {
            Lat0 = D("lat_0"),
            Lon0 = p.ContainsKey("lonc") ? D("lonc") : D("lon_0"),
            K0 = p.ContainsKey("k_0") ? D("k_0", 1) : D("k", 1),
            Azimuth = D("alpha", double.NaN),
            RectifiedGridAngle = D("gamma", double.NaN),
            NoUOffset = p.ContainsKey("no_uoff") || p.ContainsKey("no_off"),
            FalseEasting = D("x_0") * toMeter,
            FalseNorthing = D("y_0") * toMeter,
            Lat1 = D("lat_1", double.NaN),
            Lat2 = D("lat_2", double.NaN),
            LatTs = D("lat_ts", double.NaN)
        };
        if (proj == "ups") pp = pp with { Lat0 = p.ContainsKey("south") ? -90 : 90, K0 = 0.994, FalseEasting = 2_000_000, FalseNorthing = 2_000_000 };
        if (proj == "stere" && Math.Abs(Math.Abs(pp.Lat0) - 90) > 1e-9)
            throw new NotSupportedException("Only polar +proj=stere (lat_0=±90) is supported; use +proj=sterea for the oblique (EPSG 9809) form.");
        var projection = Projection.Create(proj, ellipsoid, pp);
        return WithGeoidGrid(CrsRegistry.Projected($"Custom {proj}", datum, projection, null, toMeter), p);
    }

    private static CoordinateReferenceSystem WithGeoidGrid(CoordinateReferenceSystem crs, Dictionary<string, string?> p)
    {
        if (!p.TryGetValue("geoidgrids", out var g) || g is null) return crs;
        foreach (var raw in g.Split(','))
        {
            var name = raw.TrimStart('@');
            if (name.Equals("egm96_coarse", StringComparison.OrdinalIgnoreCase)) return crs.WithGeoid(GeoidModel.Egm96Coarse);
            if (GridCatalog.Locate(name) is { } path) return crs.WithGeoid(GeoidModel.Load(path));
            if (!raw.StartsWith('@')) throw new FileNotFoundException($"Geoid grid '{name}' not found (set GEOPANDAS_NET_GRIDS or PROJ_DATA).");
        }
        return crs;
    }

    private static Ellipsoid ParseProj4Ellipsoid(Dictionary<string, string?> p)
    {
        if (p.TryGetValue("a", out var a) && a is not null)
        {
            double av = double.Parse(a, Inv);
            if (p.TryGetValue("rf", out var rf) && rf is not null) return new Ellipsoid("Custom", av, double.Parse(rf, Inv));
            if (p.TryGetValue("b", out var b) && b is not null) return Ellipsoid.FromAxes("Custom", av, double.Parse(b, Inv));
            if (p.TryGetValue("f", out var f) && f is not null) return new Ellipsoid("Custom", av, 1 / double.Parse(f, Inv));
            return new Ellipsoid("Sphere", av, 0);
        }
        if (p.TryGetValue("R", out var r) && r is not null) return new Ellipsoid("Sphere", double.Parse(r, Inv), 0);
        if (p.TryGetValue("ellps", out var en) && en is not null)
            return Ellipsoid.FromName(en) ?? throw new FormatException($"Unknown ellipsoid '{en}'.");
        return Ellipsoid.Wgs84;
    }

    private static double UnitToMeter(string? unit) => unit?.ToLowerInvariant() switch
    {
        null or "m" => 1.0,
        "km" => 1000.0,
        "ft" => 0.3048,
        "us-ft" => 1200.0 / 3937.0,
        _ => 1.0
    };

    private static double[] PadTo7(double[] v) => v.Length >= 7 ? v[..7] : [.. v, .. new double[7 - v.Length]];

    // ------------------------------------------------------------------ WKT

    /// <summary>Parses OGC WKT1, ESRI WKT (.prj) or WKT2.</summary>
    public static CoordinateReferenceSystem FromWkt(string wkt)
    {
        var root = WktNode.Parse(wkt);
        string kw = root.Keyword.ToUpperInvariant();

        // Prefer the authority code when present and known.
        var id = root.Children.LastOrDefault(c => c.Keyword is "AUTHORITY" or "ID");
        if (id is not null && id.Values.Count >= 2 && id.Values[0].Equals("EPSG", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(id.Values[1], NumberStyles.Integer, Inv, out int code) && CrsRegistry.TryGet(code, out var known))
            return known!;

        if (kw is "GEOGCS" or "GEOGCRS" or "GEODCRS" or "GEOGRAPHICCRS")
            return CrsRegistry.Geographic(root.Values.FirstOrDefault() ?? "Geographic", ParseDatum(root), null);

        if (kw is not ("PROJCS" or "PROJCRS" or "PROJECTEDCRS"))
            throw new FormatException($"Unsupported WKT root '{root.Keyword}'.");

        var name = root.Values.FirstOrDefault() ?? "Projected";
        var geog = root.Find("GEOGCS") ?? root.Find("BASEGEOGCRS") ?? root.Find("BASEGEODCRS") ?? root;
        var datum = ParseDatum(geog);

        // ESRI Web Mercator variants
        string method = (root.Find("PROJECTION")?.Values.FirstOrDefault() ?? root.Find("CONVERSION")?.Find("METHOD")?.Values.FirstOrDefault() ?? "").Trim();
        string m = Ellipsoid.Normalize(method);

        var prms = new Dictionary<string, double>();
        foreach (var node in (root.Find("CONVERSION") ?? root).Children.Where(c => c.Keyword == "PARAMETER"))
            if (node.Values.Count >= 2 && double.TryParse(node.Values[1], NumberStyles.Float, Inv, out var v))
                prms[Ellipsoid.Normalize(node.Values[0])] = v;

        double Get(double def, params string[] keys)
        {
            foreach (var k in keys) if (prms.TryGetValue(k, out var v)) return v;
            return def;
        }

        double toMeter = 1.0;
        string? unitName = null;
        var unit = root.Children.LastOrDefault(c => c.Keyword is "UNIT" or "LENGTHUNIT");
        if (unit is not null && unit.Values.Count >= 2 && double.TryParse(unit.Values[1], NumberStyles.Float, Inv, out var um))
        {
            toMeter = um;
            unitName = unit.Values[0];
        }

        var pp = new ProjectionParameters
        {
            Lat0 = Get(0, "latitudeoforigin", "latitudeofnaturalorigin", "latitudeoffalseorigin", "latitudeofcenter"),
            Lon0 = Get(0, "centralmeridian", "longitudeofnaturalorigin", "longitudeoffalseorigin", "longitudeofcenter", "longitudeoforigin"),
            K0 = Get(1, "scalefactor", "scalefactoratnaturalorigin"),
            FalseEasting = Get(0, "falseeasting", "eastingatfalseorigin") * toMeter,
            FalseNorthing = Get(0, "falsenorthing", "northingatfalseorigin") * toMeter,
            Lat1 = Get(double.NaN, "standardparallel1", "latitudeof1ststandardparallel"),
            Lat2 = Get(double.NaN, "standardparallel2", "latitudeof2ndstandardparallel"),
            Azimuth = Get(double.NaN, "azimuth", "azimuthofinitialline", "azimuthatprojectioncentre"),
            RectifiedGridAngle = Get(double.NaN, "rectifiedgridangle", "anglefromrectifiedtoskewgrid"),
        };
        if (!prms.ContainsKey("scalefactor") && !prms.ContainsKey("scalefactoratnaturalorigin"))
            pp = pp with { K0 = Get(1, "scalefactoroninitialline", "scalefactoratprojectioncentre") };
        if (prms.ContainsKey("latitudeofprojectioncentre") || prms.ContainsKey("longitudeofprojectioncentre"))
            pp = pp with { Lat0 = Get(pp.Lat0, "latitudeofprojectioncentre"), Lon0 = Get(pp.Lon0, "longitudeofprojectioncentre") };
        if (prms.ContainsKey("eastingatprojectioncentre"))
            pp = pp with { FalseEasting = Get(0, "eastingatprojectioncentre") * toMeter, FalseNorthing = Get(0, "northingatprojectioncentre") * toMeter };

        Projection projection;
        if (m.Contains("auxiliarysphere") || m.Contains("pseudomercator") || m.Contains("popularvisualisation"))
            return CrsRegistry.Get(3857);
        if (m.Contains("transversemercator") || m == "gausskruger")
            projection = new TransverseMercator(datum.Ellipsoid, pp);
        else if (m.Contains("lambertconformalconic") || m.Contains("lambertconicconformal"))
        {
            if (m.Contains("1sp") || double.IsNaN(pp.Lat1))
                pp = pp with { Lat1 = pp.Lat0, Lat2 = double.NaN };
            projection = new LambertConformalConic(datum.Ellipsoid, pp);
        }
        else if (m.Contains("albers"))
            projection = new AlbersEqualArea(datum.Ellipsoid, pp);
        else if (m.Contains("lambertazimuthalequalarea"))
            projection = new LambertAzimuthalEqualArea(datum.Ellipsoid, pp);
        else if (m.Contains("polarstereographic") || m.Contains("stereographicnorthpole") || m.Contains("stereographicsouthpole"))
        {
            double latOrigin = Get(double.NaN, "latitudeoforigin", "latitudeofnaturalorigin");
            double sp = Get(double.NaN, "latitudeofstandardparallel", "standardparallel1");
            bool south = m.Contains("southpole") || (!double.IsNaN(sp) ? sp < 0 : latOrigin < 0);
            if (!double.IsNaN(sp) || (!double.IsNaN(latOrigin) && Math.Abs(Math.Abs(latOrigin) - 90) > 1e-9) || m.Contains("variantb"))
                pp = pp with { Lat0 = south ? -90 : 90, LatTs = double.IsNaN(sp) ? latOrigin : sp, Lon0 = Get(pp.Lon0, "longitudeoforigin", "centralmeridian", "straightverticallongitudefrompole") };
            else pp = pp with { Lat0 = south ? -90 : 90 };
            projection = new PolarStereographic(datum.Ellipsoid, pp);
        }
        else if (m.Contains("obliquestereographic") || m.Contains("doublestereographic"))
            projection = new ObliqueStereographic(datum.Ellipsoid, pp);
        else if (m.Contains("hotine") || m.Contains("obliquemercator") || m.Contains("rectifiedskeworthomorphic"))
        {
            bool variantB = m.Contains("azimuthcenter") || m.Contains("variantb") || prms.ContainsKey("eastingatprojectioncentre") || m == "obliquemercator";
            projection = new HotineObliqueMercator(datum.Ellipsoid, pp, variantB);
        }
        else if (m.Contains("cassini"))
            projection = new CassiniSoldner(datum.Ellipsoid, pp);
        else if (m.Contains("equirectangular") || m.Contains("equidistantcylindrical") || m.Contains("platecarree"))
            projection = new EquidistantCylindrical(datum.Ellipsoid, pp with { LatTs = double.IsNaN(pp.Lat1) ? 0 : pp.Lat1 });
        else if (m.Contains("cylindricalequalarea"))
            projection = new CylindricalEqualArea(datum.Ellipsoid, pp with { LatTs = double.IsNaN(pp.Lat1) ? 0 : pp.Lat1 });
        else if (m.Contains("sinusoidal"))
            projection = new Sinusoidal(datum.Ellipsoid, pp);
        else if (m.Contains("mollweide"))
            projection = new Mollweide(datum.Ellipsoid, pp);
        else if (m.Contains("robinson"))
            projection = new Robinson(datum.Ellipsoid, pp);
        else if (m.Contains("equalearth"))
            projection = new EqualEarth(datum.Ellipsoid, pp);
        else if (m.Contains("mercator"))
        {
            if (!double.IsNaN(pp.Lat1)) pp = pp with { LatTs = pp.Lat1, Lat1 = double.NaN };
            projection = new Mercator(datum.Ellipsoid, pp);
        }
        else throw new NotSupportedException($"WKT projection method '{method}' is not supported yet.");

        // Detect UTM to keep nice names / codes.
        if (projection is TransverseMercator && pp.K0 == 0.9996 && pp.FalseEasting == 500000 && pp.Lat0 == 0
            && (pp.FalseNorthing is 0 or 10000000) && Math.Abs((pp.Lon0 + 183) / 6 - Math.Round((pp.Lon0 + 183) / 6)) < 1e-9)
        {
            int zone = (int)Math.Round((pp.Lon0 + 183) / 6);
            bool south = pp.FalseNorthing == 10000000;
            if (datum.Ellipsoid == Ellipsoid.Wgs84 && datum.IsWgs84Compatible && toMeter == 1)
                return CrsRegistry.Get((south ? 32700 : 32600) + zone);
            projection = new TransverseMercator(datum.Ellipsoid, pp with { UtmZone = zone, UtmSouth = south });
        }

        return CrsRegistry.Projected(name, datum, projection, null, toMeter, unitName);
    }

    private static Datum ParseDatum(WktNode geog)
    {
        var datumNode = geog.Find("DATUM") ?? geog.Find("ENSEMBLE");
        var sph = datumNode?.Find("SPHEROID") ?? datumNode?.Find("ELLIPSOID") ?? geog.Find("SPHEROID") ?? geog.Find("ELLIPSOID");
        Ellipsoid ellipsoid = Ellipsoid.Wgs84;
        if (sph is not null && sph.Values.Count >= 3)
        {
            double a = double.Parse(sph.Values[1], Inv), rf = double.Parse(sph.Values[2], Inv);
            var known = Ellipsoid.FromName(sph.Values[0]);
            ellipsoid = known is not null && Math.Abs(known.A - a) < 1e-3 ? known : new Ellipsoid(sph.Values[0], a, rf);
        }
        double[]? towgs = null;
        var tw = datumNode?.Find("TOWGS84") ?? geog.Find("TOWGS84");
        if (tw is not null) towgs = PadTo7(tw.Values.Select(v => double.Parse(v, Inv)).ToArray());
        var dname = datumNode?.Values.FirstOrDefault() ?? "Unknown";
        var nd = Ellipsoid.Normalize(dname);
        if (towgs is null && ellipsoid != Ellipsoid.Wgs84)
        {
            if (nd.Contains("northamerican1983") || nd.Contains("etrs") || nd.Contains("europeanterrestrial") || nd.Contains("gda") || nd.Contains("rgf93"))
                towgs = new double[7];
            else if (nd.Contains("northamerican1927")) towgs = [-8, 160, 176, 0, 0, 0, 0];
            else if (nd.Contains("osgb")) towgs = [446.448, -125.157, 542.06, 0.15, 0.247, 0.842, -20.489];
            else if (nd.Contains("europeandatum1950")) towgs = [-87, -98, -121, 0, 0, 0, 0];
        }
        return new Datum(dname, ellipsoid, towgs);
    }

    /// <summary>Minimal WKT tree: KEYWORD[value, value, CHILD[...], ...].</summary>
    internal sealed class WktNode
    {
        public string Keyword { get; init; } = "";
        public List<string> Values { get; } = [];
        public List<WktNode> Children { get; } = [];

        public WktNode? Find(string keyword)
        {
            foreach (var c in Children) if (c.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase)) return c;
            foreach (var c in Children) { var f = c.Find(keyword); if (f is not null) return f; }
            return null;
        }

        public static WktNode Parse(string text)
        {
            int pos = 0;
            var node = ParseNode(text, ref pos);
            return node;
        }

        private static WktNode ParseNode(string s, ref int pos)
        {
            SkipWs(s, ref pos);
            int start = pos;
            while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
            var node = new WktNode { Keyword = s[start..pos].ToUpperInvariant() };
            SkipWs(s, ref pos);
            if (pos >= s.Length || (s[pos] != '[' && s[pos] != '(')) return node;
            char close = s[pos] == '[' ? ']' : ')';
            pos++;
            while (pos < s.Length)
            {
                SkipWs(s, ref pos);
                if (s[pos] == close) { pos++; break; }
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '"')
                {
                    int e = pos + 1;
                    var sb = new System.Text.StringBuilder();
                    while (e < s.Length)
                    {
                        if (s[e] == '"') { if (e + 1 < s.Length && s[e + 1] == '"') { sb.Append('"'); e += 2; continue; } break; }
                        sb.Append(s[e++]);
                    }
                    node.Values.Add(sb.ToString());
                    pos = e + 1;
                }
                else if (char.IsDigit(s[pos]) || s[pos] is '-' or '+' or '.')
                {
                    int e = pos;
                    while (e < s.Length && (char.IsDigit(s[e]) || s[e] is '-' or '+' or '.' or 'e' or 'E')) e++;
                    node.Values.Add(s[pos..e]);
                    pos = e;
                }
                else
                {
                    var child = ParseNode(s, ref pos);
                    if (child.Values.Count == 0 && child.Children.Count == 0) node.Values.Add(child.Keyword); // enum literal e.g. EAST
                    else node.Children.Add(child);
                }
            }
            return node;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
        }
    }
}
