using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Plotting;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate.Polygon;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Scene;

/// <summary>Builds meshes from rasters, polygons, lines and point clouds.</summary>
public static class MeshBuilders
{
    /// <summary>
    /// A terrain mesh from a DEM: one vertex per <paramref name="step"/>-th cell centre, two triangles per quad (quads touching no-data are
    /// skipped), vertex colours from a colour map over the elevation range, smooth normals.
    /// </summary>
    public static Mesh Terrain(RasterGrid dem, LocalFrame frame, int step = 1, ColorMap? colors = null, double zScale = 1, int band = 0)
    {
        var to = frame.From(dem.Crs);
        int cols = (dem.Width - 1) / step + 1, rows = (dem.Height - 1) / step + 1;
        var stats = dem.Statistics(band);
        var cmap = colors ?? ColorMaps.Terrain;
        var mesh = new Mesh { Name = "terrain", Material = new SceneMaterial("terrain", Roughness: 1f), Colors = [] };
        var index = new int[rows * cols];
        var data = dem.Bands[band];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int rr = Math.Min(r * step, dem.Height - 1), cc = Math.Min(c * step, dem.Width - 1);
                float z = data[rr * dem.Width + cc];
                if (dem.IsNoData(z)) { index[r * cols + c] = -1; continue; }
                var (x, y) = dem.Transform.PixelToWorld(cc + 0.5, rr + 0.5);
                index[r * cols + c] = mesh.Positions.Count;
                mesh.Positions.Add(to(x, y, z * zScale));
                var col = cmap.Sample(stats.Max > stats.Min ? (z - stats.Min) / (stats.Max - stats.Min) : 0.5);
                mesh.Colors.Add((col.R, col.G, col.B, 255));
            }
        for (int r = 0; r + 1 < rows; r++)
            for (int c = 0; c + 1 < cols; c++)
            {
                int a = index[r * cols + c], b = index[r * cols + c + 1], d = index[(r + 1) * cols + c], e = index[(r + 1) * cols + c + 1];
                if (a < 0 || b < 0 || d < 0 || e < 0) continue;
                // rows run south from the top: (a, d, b) and (b, d, e) are counter-clockwise seen from above
                mesh.Indices.AddRange([(uint)a, (uint)d, (uint)b, (uint)b, (uint)d, (uint)e]);
            }
        return mesh.ComputeNormals();
    }

    /// <summary>
    /// Extruded (LOD1) buildings: roofs and floors triangulated with constrained Delaunay (holes respected), vertical walls with flat normals.
    /// Each feature gets its row index as feature id, and the attribute columns become the feature table.
    /// </summary>
    public static Mesh Extrude(GeoDataFrame polygons, LocalFrame frame, string heightColumn, string? baseColumn = null,
        (byte R, byte G, byte B)? color = null, ColorMap? heightColors = null)
    {
        var to = frame.From(polygons.Crs);
        var mesh = new Mesh { Name = "buildings", Material = new SceneMaterial("buildings", 0.85f, 0.83f, 0.78f), Normals = [], Colors = [], FeatureIds = [] };
        var heights = polygons[heightColumn];
        double hMax = Enumerable.Range(0, polygons.RowCount).Select(i => heights.IsNull(i) ? 0 : heights.GetDouble(i)).DefaultIfEmpty(1).Max();
        for (int i = 0; i < polygons.RowCount; i++)
        {
            var g = polygons.Geometry[i];
            if (g is null || g.IsEmpty || heights.IsNull(i)) continue;
            double h = heights.GetDouble(i), z0 = baseColumn is null || polygons[baseColumn].IsNull(i) ? 0 : polygons[baseColumn].GetDouble(i);
            if (!(h > 0)) continue;
            var c = heightColors is not null ? heightColors.Sample(h / Math.Max(hMax, 1e-9)) : default;
            var rgba = heightColors is not null ? (c.R, c.G, c.B, (byte)255) : color is { } cc ? (cc.R, cc.G, cc.B, (byte)255) : ((byte)217, (byte)212, (byte)199, (byte)255);
            foreach (var poly in g is MultiPolygon mp ? mp.Geometries.Cast<Polygon>() : g is Polygon p ? [p] : [])
                AddPrism(mesh, poly, z0, z0 + h, to, (uint)i, rgba);
        }
        var attrs = polygons.Attributes();
        mesh.FeatureTable = attrs;
        return mesh;
    }

    private static void AddPrism(Mesh mesh, Polygon poly, double z0, double z1, Func<double, double, double, (double E, double N, double U)> to, uint id,
        (byte, byte, byte, byte) rgba)
    {
        // roof and floor
        var tris = ConstrainedDelaunayTriangulator.Triangulate(poly);
        for (int ti = 0; ti < tris.NumGeometries; ti++)
        {
            if (tris.GetGeometryN(ti) is not Polygon t) continue;
            var cs = t.ExteriorRing.Coordinates;
            // counter-clockwise (in projected x/y) for the roof, reversed for the floor
            bool ccw = (cs[1].X - cs[0].X) * (cs[2].Y - cs[0].Y) - (cs[1].Y - cs[0].Y) * (cs[2].X - cs[0].X) > 0;
            int[] order = ccw ? [0, 1, 2] : [0, 2, 1];
            uint b = (uint)mesh.Positions.Count;
            foreach (var k in order) Vertex(mesh, to(cs[k].X, cs[k].Y, z1), (0f, 0f, 1f), id, rgba);
            mesh.Indices.AddRange([b, b + 1, b + 2]);
            b = (uint)mesh.Positions.Count;
            foreach (var k in order.Reverse()) Vertex(mesh, to(cs[k].X, cs[k].Y, z0), (0f, 0f, -1f), id, rgba);
            mesh.Indices.AddRange([b, b + 1, b + 2]);
        }
        // walls: outward for the shell (counter-clockwise ring), inward-facing rings for holes
        foreach (var (ring, isHole) in new[] { (poly.ExteriorRing, false) }.Concat(poly.InteriorRings.Select(r => (r, true))))
        {
            var cs = ring.Coordinates;
            bool ringCcw = NetTopologySuite.Algorithm.Orientation.IsCCW(cs);
            bool outward = ringCcw ^ isHole;
            for (int k = 0; k + 1 < cs.Length; k++)
            {
                var a = cs[k]; var bpt = cs[k + 1];
                if (a.Equals2D(bpt)) continue;
                var (p0, p1) = outward ? (a, bpt) : (bpt, a);
                var v0 = to(p0.X, p0.Y, z0); var v1 = to(p1.X, p1.Y, z0); var v2 = to(p1.X, p1.Y, z1); var v3 = to(p0.X, p0.Y, z1);
                double ex = v1.E - v0.E, ny = v1.N - v0.N, l = Math.Sqrt(ex * ex + ny * ny);
                var n = l > 0 ? ((float)(ny / l), (float)(-ex / l), 0f) : (0f, 0f, 1f);
                uint b = (uint)mesh.Positions.Count;
                Vertex(mesh, v0, n, id, rgba); Vertex(mesh, v1, n, id, rgba); Vertex(mesh, v2, n, id, rgba); Vertex(mesh, v3, n, id, rgba);
                mesh.Indices.AddRange([b, b + 1, b + 2, b, b + 2, b + 3]);
            }
        }
    }

    private static void Vertex(Mesh m, (double E, double N, double U) p, (float, float, float) n, uint id, (byte, byte, byte, byte) rgba)
    {
        m.Positions.Add(p); m.Normals!.Add(n); m.FeatureIds!.Add(id); m.Colors!.Add(rgba);
    }

    /// <summary>A point mesh (glTF POINTS) from XYZ coordinates with optional RGB colours.</summary>
    public static Mesh Points(IReadOnlyList<(double X, double Y, double Z)> points, CoordinateReferenceSystem? crs, LocalFrame frame,
        IReadOnlyList<(byte R, byte G, byte B)>? colors = null)
    {
        var to = frame.From(crs);
        var mesh = new Mesh { Name = "points", Mode = PrimitiveMode.Points, Material = new SceneMaterial("points", Unlit: true), Colors = [] };
        for (int i = 0; i < points.Count; i++)
        {
            mesh.Positions.Add(to(points[i].X, points[i].Y, points[i].Z));
            var c = colors is null ? ((byte)255, (byte)255, (byte)255) : colors[i];
            mesh.Colors.Add((c.Item1, c.Item2, c.Item3, 255));
        }
        return mesh;
    }

    /// <summary>Lines (glTF LINES) from line geometries; Z from the coordinates or <paramref name="zColumn"/>.</summary>
    public static Mesh Lines(GeoDataFrame lines, LocalFrame frame, string? zColumn = null, (byte R, byte G, byte B)? color = null)
    {
        var to = frame.From(lines.Crs);
        var mesh = new Mesh { Name = "lines", Mode = PrimitiveMode.Lines, Material = new SceneMaterial("lines", Unlit: true), Colors = [], FeatureIds = [] };
        var rgba = color is { } c ? (c.R, c.G, c.B, (byte)255) : ((byte)176, (byte)48, (byte)106, (byte)255);
        for (int i = 0; i < lines.RowCount; i++)
        {
            var g = lines.Geometry[i];
            if (g is null) continue;
            double zc = zColumn is null || lines[zColumn].IsNull(i) ? 0 : lines[zColumn].GetDouble(i);
            foreach (var ls in g is MultiLineString ml ? ml.Geometries.Cast<LineString>() : g is LineString l ? [l] : [])
            {
                var cs = ls.Coordinates;
                uint b = (uint)mesh.Positions.Count;
                foreach (var p in cs)
                {
                    mesh.Positions.Add(to(p.X, p.Y, double.IsNaN(p.Z) ? zc : p.Z));
                    mesh.Colors.Add(rgba); mesh.FeatureIds.Add((uint)i);
                }
                for (int k = 0; k + 1 < cs.Length; k++) mesh.Indices.AddRange([b + (uint)k, b + (uint)k + 1]);
            }
        }
        mesh.FeatureTable = lines.Attributes();
        return mesh;
    }
}
