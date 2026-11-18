using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.BIM;

/// <summary>
/// Writes GIS features to IFC4: building footprints with heights become georeferenced <c>IfcBuilding</c>s with an extruded
/// <c>IfcBuildingElementProxy</c> (LOD1 mass), attributes go to a property set, and an <c>IfcMapConversion</c> + <c>IfcProjectedCRS</c>
/// locate the model in the frame's CRS.
/// </summary>
public static class IfcWriter
{
    /// <summary>Writes buildings to an IFC4 file. Coordinates are written relative to the footprints' centroid (in metres).</summary>
    /// <param name="buildings">Polygon or MultiPolygon footprints in a projected CRS (metres).</param>
    /// <param name="path">Output .ifc path.</param>
    /// <param name="heightColumn">Building height (m).</param>
    /// <param name="baseColumn">Optional ground elevation (m); 0 when absent.</param>
    /// <param name="nameColumn">Optional name column.</param>
    /// <param name="projectName">IfcProject name.</param>
    public static void WriteBuildings(GeoDataFrame buildings, string path, string heightColumn = "height", string? baseColumn = null, string? nameColumn = "name",
        string projectName = "GeoPandas.Net export") => File.WriteAllText(path, BuildingsToIfc(buildings, heightColumn, baseColumn, nameColumn, projectName), Encoding.ASCII);

    public static string BuildingsToIfc(GeoDataFrame buildings, string heightColumn = "height", string? baseColumn = null, string? nameColumn = "name", string projectName = "GeoPandas.Net export")
    {
        var crs = buildings.Crs;
        if (crs is { IsGeographic: true }) throw new ArgumentException("Reproject the buildings to a projected CRS (metres) before writing IFC.");
        var env = buildings.TotalBounds;
        double ox = Math.Round((env.MinX + env.MaxX) / 2), oy = Math.Round((env.MinY + env.MaxY) / 2);
        var w = new Writer();
        var ci = CultureInfo.InvariantCulture;
        string R(double v) => StepFile.Format(v);

        var person = w.Add("IFCPERSON($,$,'GeoPandas.Net',$,$,$,$,$)");
        var org = w.Add("IFCORGANIZATION($,'Gravicode Studios',$,$,$)");
        var po = w.Add($"IFCPERSONANDORGANIZATION({person},{org},$)");
        var app = w.Add($"IFCAPPLICATION({org},'{Version}','GeoPandas.Net','GeoPandas.Net')");
        var history = w.Add($"IFCOWNERHISTORY({po},{app},$,.ADDED.,{DateTimeOffset.UtcNow.ToUnixTimeSeconds()},$,$,{DateTimeOffset.UtcNow.ToUnixTimeSeconds()})");
        var origin = w.Add("IFCCARTESIANPOINT((0.,0.,0.))");
        var wcs = w.Add($"IFCAXIS2PLACEMENT3D({origin},$,$)");
        var north = w.Add("IFCDIRECTION((0.,1.))");
        var context = w.Add($"IFCGEOMETRICREPRESENTATIONCONTEXT($,'Model',3,1.E-05,{wcs},{north})");
        var body = w.Add($"IFCGEOMETRICREPRESENTATIONSUBCONTEXT('Body','Model',*,*,*,*,{context},$,.MODEL_VIEW.,$)");
        var lengthUnit = w.Add("IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)");
        var areaUnit = w.Add("IFCSIUNIT(*,.AREAUNIT.,$,.SQUARE_METRE.)");
        var volUnit = w.Add("IFCSIUNIT(*,.VOLUMEUNIT.,$,.CUBIC_METRE.)");
        var angleUnit = w.Add("IFCSIUNIT(*,.PLANEANGLEUNIT.,$,.RADIAN.)");
        var units = w.Add($"IFCUNITASSIGNMENT(({lengthUnit},{areaUnit},{volUnit},{angleUnit}))");
        var project = w.Add($"IFCPROJECT('{NewGuid()}',{history},{StepFile.Quote(projectName)},$,$,$,$,({context}),{units})");
        if (crs is not null)
        {
            string crsName = crs.Epsg is { } epsg ? $"EPSG:{epsg}" : crs.Name;
            var pcrs = w.Add($"IFCPROJECTEDCRS({StepFile.Quote(crsName)},{StepFile.Quote(crs.Name)},$,$,$,$,{lengthUnit})");
            w.Add($"IFCMAPCONVERSION({context},{pcrs},{R(ox)},{R(oy)},0.,1.,0.,1.)");
        }
        var sitePlacement = w.Add($"IFCLOCALPLACEMENT($,{w.Add($"IFCAXIS2PLACEMENT3D({w.Add("IFCCARTESIANPOINT((0.,0.,0.))")},$,$)")})");
        var site = w.Add($"IFCSITE('{NewGuid()}',{history},'Site',$,$,{sitePlacement},$,$,.ELEMENT.,$,$,$,$,$)");
        w.Add($"IFCRELAGGREGATES('{NewGuid()}',{history},$,$,{project},({site}))");
        var buildingRefs = new List<string>();
        var heightCol = buildings[heightColumn];
        for (int i = 0; i < buildings.RowCount; i++)
        {
            var g = buildings.Geometry[i];
            if (g is null || g.IsEmpty) continue;
            double h = heightCol.GetDouble(i);
            if (!(h > 0)) continue;
            double z0 = baseColumn is null ? 0 : buildings[baseColumn].GetDouble(i);
            string name = nameColumn is not null && buildings.Columns.Any(c => c.Name == nameColumn) && buildings[nameColumn].GetValue(i) is { } nv
                ? Convert.ToString(nv, ci)! : $"Building {i + 1}";
            var bPlacement = w.Add($"IFCLOCALPLACEMENT({sitePlacement},{w.Add($"IFCAXIS2PLACEMENT3D({w.Add($"IFCCARTESIANPOINT((0.,0.,{R(double.IsNaN(z0) ? 0 : z0)}))")},$,$)")})");
            var building = w.Add($"IFCBUILDING('{NewGuid()}',{history},{StepFile.Quote(name)},$,$,{bPlacement},$,$,.ELEMENT.,$,$,$)");
            buildingRefs.Add(building);
            var solids = new List<string>();
            var polys = g switch { Polygon p => [p], MultiPolygon mp => mp.Geometries.Cast<Polygon>().ToList(), _ => new List<Polygon>() };
            foreach (var p in polys)
            {
                string Curve(LineString ring)
                {
                    var pts = ring.Coordinates.Take(ring.NumPoints - 1).Select(c => w.Add($"IFCCARTESIANPOINT(({R(c.X - ox)},{R(c.Y - oy)}))")).ToList();
                    pts.Add(pts[0]);
                    return w.Add($"IFCPOLYLINE(({string.Join(",", pts)}))");
                }
                string profile = p.NumInteriorRings == 0
                    ? w.Add($"IFCARBITRARYCLOSEDPROFILEDEF(.AREA.,$,{Curve(p.ExteriorRing)})")
                    : w.Add($"IFCARBITRARYPROFILEDEFWITHVOIDS(.AREA.,$,{Curve(p.ExteriorRing)},({string.Join(",", p.InteriorRings.Select(Curve))}))");
                var pos = w.Add($"IFCAXIS2PLACEMENT3D({w.Add("IFCCARTESIANPOINT((0.,0.,0.))")},$,$)");
                solids.Add(w.Add($"IFCEXTRUDEDAREASOLID({profile},{pos},{w.Add("IFCDIRECTION((0.,0.,1.))")},{R(h)})"));
            }
            var rep = w.Add($"IFCSHAPEREPRESENTATION({body},'Body','SweptSolid',({string.Join(",", solids)}))");
            var shape = w.Add($"IFCPRODUCTDEFINITIONSHAPE($,$,({rep}))");
            var ePlacement = w.Add($"IFCLOCALPLACEMENT({bPlacement},{w.Add($"IFCAXIS2PLACEMENT3D({w.Add("IFCCARTESIANPOINT((0.,0.,0.))")},$,$)")})");
            var proxy = w.Add($"IFCBUILDINGELEMENTPROXY('{NewGuid()}',{history},{StepFile.Quote(name)},'LOD1 mass',$,{ePlacement},{shape},$,.ELEMENT.)");
            w.Add($"IFCRELCONTAINEDINSPATIALSTRUCTURE('{NewGuid()}',{history},$,$,({proxy}),{building})");
            // attributes → property set
            var props = new List<string>();
            foreach (var c in buildings.Columns)
            {
                if (c.Name == buildings.GeometryColumnName) continue;
                var v = c.GetValue(i);
                string? val = v switch
                {
                    null => null,
                    double d when double.IsNaN(d) => null,
                    double d => $"IFCREAL({R(d)})",
                    float f => $"IFCREAL({R(f)})",
                    long or int => $"IFCINTEGER({Convert.ToInt64(v, ci).ToString(ci)})",
                    bool b => $"IFCBOOLEAN({(b ? ".T." : ".F.")})",
                    _ => $"IFCLABEL({StepFile.Quote(Convert.ToString(v, ci))})"
                };
                if (val is not null) props.Add(w.Add($"IFCPROPERTYSINGLEVALUE({StepFile.Quote(c.Name)},$,{val},$)"));
            }
            if (props.Count > 0)
            {
                var pset = w.Add($"IFCPROPERTYSET('{NewGuid()}',{history},'GPD_Attributes',$,({string.Join(",", props)}))");
                w.Add($"IFCRELDEFINESBYPROPERTIES('{NewGuid()}',{history},$,$,({proxy}),{pset})");
            }
        }
        if (buildingRefs.Count > 0) w.Add($"IFCRELAGGREGATES('{NewGuid()}',{history},$,$,{site},({string.Join(",", buildingRefs)}))");

        var sb = new StringBuilder();
        sb.Append("ISO-10303-21;\nHEADER;\n");
        sb.Append("FILE_DESCRIPTION(('ViewDefinition [ReferenceView]'),'2;1');\n");
        sb.Append(ci, $"FILE_NAME({StepFile.Quote(projectName)},'{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss}',('GeoPandas.Net'),('Gravicode Studios'),'GeoPandas.Net {Version}','GeoPandas.Net','');\n");
        sb.Append("FILE_SCHEMA(('IFC4'));\nENDSEC;\nDATA;\n");
        sb.Append(w.Body);
        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        return sb.ToString();
    }

    private sealed class Writer
    {
        private int _next = 1;
        public StringBuilder Body { get; } = new();
        public string Add(string entity)
        {
            int id = _next++;
            Body.Append('#').Append(id.ToString(CultureInfo.InvariantCulture)).Append('=').Append(entity).Append(";\n");
            return "#" + id.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string Version => typeof(IfcWriter).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const string GuidChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_$";

    /// <summary>A new IFC GlobalId (a GUID compressed to 22 base-64 characters, buildingSMART encoding).</summary>
    public static string NewGuid() => CompressGuid(Guid.NewGuid());

    public static string CompressGuid(Guid g)
    {
        var b = g.ToByteArray();
        // big-endian order of the canonical GUID text
        byte[] n = [b[3], b[2], b[1], b[0], b[5], b[4], b[7], b[6], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]];
        var sb = new StringBuilder(22);
        sb.Append(GuidChars[n[0] >> 6]).Append(GuidChars[n[0] & 63]);
        for (int i = 1; i < 16; i += 3)
        {
            int v = (n[i] << 16) | (n[i + 1] << 8) | n[i + 2];
            sb.Append(GuidChars[(v >> 18) & 63]).Append(GuidChars[(v >> 12) & 63]).Append(GuidChars[(v >> 6) & 63]).Append(GuidChars[v & 63]);
        }
        return sb.ToString();
    }

    public static Guid ExpandGuid(string s)
    {
        if (s.Length != 22) throw new ArgumentException("IFC GlobalIds have 22 characters.");
        var n = new byte[16];
        n[0] = (byte)(GuidChars.IndexOf(s[0]) << 6 | GuidChars.IndexOf(s[1]));
        for (int i = 1, k = 2; i < 16; i += 3, k += 4)
        {
            int v = GuidChars.IndexOf(s[k]) << 18 | GuidChars.IndexOf(s[k + 1]) << 12 | GuidChars.IndexOf(s[k + 2]) << 6 | GuidChars.IndexOf(s[k + 3]);
            n[i] = (byte)(v >> 16); n[i + 1] = (byte)(v >> 8); n[i + 2] = (byte)v;
        }
        return new Guid([n[3], n[2], n[1], n[0], n[5], n[4], n[7], n[6], n[8], n[9], n[10], n[11], n[12], n[13], n[14], n[15]]);
    }
}
