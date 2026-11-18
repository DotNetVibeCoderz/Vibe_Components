using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoPandas.Net.Plotting;
using NetTopologySuite.Geometries;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Scene;

/// <summary>Options of a 3D Tiles export.</summary>
public sealed record TilesetOptions
{
    /// <summary>Maximum features per leaf tile of a feature quadtree.</summary>
    public int MaxFeaturesPerTile { get; init; } = 500;
    /// <summary>Ellipsoidal height of the local frame origin.</summary>
    public double OriginHeight { get; init; }
    /// <summary>Multiplier from tile size (m) to geometric error.</summary>
    public double GeometricErrorFactor { get; init; } = 0.1;
}

/// <summary>
/// OGC 3D Tiles 1.1 tileset writer: glTF (GLB) content placed with an ENU → ECEF root transform, oriented bounding boxes, a spatial quadtree
/// for features (additive refinement) and a two-level terrain pyramid (replacement refinement). Opens in CesiumJS / Cesium.Net, deck.gl,
/// iTowns and the 3d-tiles-validator.
/// </summary>
public static class Tiles3D
{
    /// <summary>Extruded buildings as a quadtree tileset; feature attributes are kept as EXT_structural_metadata.</summary>
    public static string WriteBuildings(GeoDataFrame buildings, string directory, string heightColumn = "height", string? baseColumn = null,
        TilesetOptions? options = null, ColorMap? heightColors = null)
    {
        options ??= new TilesetOptions();
        Directory.CreateDirectory(directory);
        var frame = LocalFrame.AtCentre(buildings.TotalBounds, buildings.Crs, options.OriginHeight);
        var centroids = Enumerable.Range(0, buildings.RowCount).Select(i => buildings.Geometry[i]?.Centroid.Coordinate).ToArray();
        int tileNo = 0;
        JsonObject Node(List<int> rows, Envelope env, int depth)
        {
            if (rows.Count > options.MaxFeaturesPerTile && depth < 12)
            {
                double mx = (env.MinX + env.MaxX) / 2, my = (env.MinY + env.MaxY) / 2;
                var quads = new[] { new Envelope(env.MinX, mx, env.MinY, my), new Envelope(mx, env.MaxX, env.MinY, my), new Envelope(env.MinX, mx, my, env.MaxY), new Envelope(mx, env.MaxX, my, env.MaxY) };
                var children = new JsonArray();
                foreach (var q in quads)
                {
                    var sub = rows.Where(r => centroids[r] is { } c && c.X >= q.MinX && c.X <= q.MaxX && c.Y >= q.MinY && c.Y <= q.MaxY && Quadrant(c, mx, my) == Quadrant(new Coordinate(q.Centre.X, q.Centre.Y), mx, my)).ToList();
                    if (sub.Count > 0) children.Add((JsonNode)Node(sub, q, depth + 1));
                }
                var box = BoxOf(children);
                return new JsonObject { ["boundingVolume"] = new JsonObject { ["box"] = box }, ["geometricError"] = Diagonal(box) * options.GeometricErrorFactor, ["children"] = children };
            }
            var subset = buildings.Take(rows.ToArray());
            var mesh = MeshBuilders.Extrude(subset, frame, heightColumn, baseColumn, heightColors: heightColors);
            string uri = $"tiles/b{tileNo++}.glb";
            Directory.CreateDirectory(Path.Combine(directory, "tiles"));
            Gltf.WriteGlb(Path.Combine(directory, uri), mesh);
            var leafBox = Box(mesh);
            return new JsonObject { ["boundingVolume"] = new JsonObject { ["box"] = leafBox }, ["geometricError"] = 0.0, ["content"] = new JsonObject { ["uri"] = uri } };
        }
        var root = Node(Enumerable.Range(0, buildings.RowCount).Where(i => centroids[i] is not null).ToList(), buildings.TotalBounds, 0);
        return WriteTileset(directory, root, frame, "ADD");
    }

    private static int Quadrant(Coordinate c, double mx, double my) => (c.X >= mx ? 1 : 0) + (c.Y >= my ? 2 : 0);

    /// <summary>
    /// A terrain tileset: a coarse root (every <paramref name="coarseStep"/>-th cell) refined into <paramref name="chunk"/>-cell children at full
    /// resolution (REPLACE refinement).
    /// </summary>
    public static string WriteTerrain(RasterGrid dem, string directory, int chunk = 128, int coarseStep = 8, TilesetOptions? options = null, ColorMap? colors = null)
    {
        options ??= new TilesetOptions();
        Directory.CreateDirectory(Path.Combine(directory, "tiles"));
        var frame = LocalFrame.AtCentre(dem.Bounds, dem.Crs, options.OriginHeight);
        var coarse = MeshBuilders.Terrain(dem, frame, coarseStep, colors);
        Gltf.WriteGlb(Path.Combine(directory, "tiles/terrain_root.glb"), coarse);
        var children = new JsonArray();
        int k = 0;
        for (int r0 = 0; r0 < dem.Height - 1; r0 += chunk)
            for (int c0 = 0; c0 < dem.Width - 1; c0 += chunk)
            {
                int w = Math.Min(chunk + 1, dem.Width - c0), h = Math.Min(chunk + 1, dem.Height - r0);
                if (w < 2 || h < 2) continue;
                var sub = Window(dem, c0, r0, w, h);
                var mesh = MeshBuilders.Terrain(sub, frame, 1, colors);
                if (mesh.Positions.Count == 0) continue;
                string uri = $"tiles/terrain_{k++}.glb";
                Gltf.WriteGlb(Path.Combine(directory, uri), mesh);
                children.Add((JsonNode)new JsonObject { ["boundingVolume"] = new JsonObject { ["box"] = Box(mesh) }, ["geometricError"] = 0.0, ["content"] = new JsonObject { ["uri"] = uri } });
            }
        var rootBox = Box(coarse);
        double cellErr = Math.Max(dem.CellSizeX, dem.CellSizeY) * coarseStep;
        var root = new JsonObject
        {
            ["boundingVolume"] = new JsonObject { ["box"] = BoxOf(children, rootBox) },
            ["geometricError"] = cellErr,
            ["content"] = new JsonObject { ["uri"] = "tiles/terrain_root.glb" },
            ["children"] = children
        };
        return WriteTileset(directory, root, frame, "REPLACE", cellErr * 4);
    }

    /// <summary>Any meshes (already in <paramref name="frame"/>) as a single-tile tileset.</summary>
    public static string WriteMeshes(string directory, LocalFrame frame, params Mesh[] meshes)
    {
        Directory.CreateDirectory(Path.Combine(directory, "tiles"));
        var nodes = new JsonArray();
        for (int i = 0; i < meshes.Length; i++)
        {
            string uri = $"tiles/mesh_{i}.glb";
            Gltf.WriteGlb(Path.Combine(directory, uri), meshes[i]);
            nodes.Add((JsonNode)new JsonObject { ["boundingVolume"] = new JsonObject { ["box"] = Box(meshes[i]) }, ["geometricError"] = 0.0, ["content"] = new JsonObject { ["uri"] = uri } });
        }
        var root = new JsonObject { ["boundingVolume"] = new JsonObject { ["box"] = BoxOf(nodes) }, ["geometricError"] = Diagonal(BoxOf(nodes)) * 0.05, ["children"] = nodes };
        return WriteTileset(directory, root, frame, "ADD");
    }

    private static RasterGrid Window(RasterGrid r, int c0, int r0, int w, int h)
    {
        var band = new float[w * h];
        var src = r.Bands[0];
        for (int y = 0; y < h; y++) Array.Copy(src, (r0 + y) * r.Width + c0, band, y * w, w);
        var (ox, oy) = r.Transform.PixelToWorld(c0, r0);
        var t = new Raster.GeoTransform(ox, r.Transform.PixelWidth, 0, oy, 0, r.Transform.PixelHeight);
        return new RasterGrid(w, h, [band], t, r.Crs, r.NoData);
    }

    private static string WriteTileset(string directory, JsonObject root, LocalFrame frame, string refine, double? rootError = null)
    {
        root["refine"] = refine;
        root["transform"] = new JsonArray(frame.EnuToEcef().Select(v => (JsonNode)v).ToArray());
        double err = rootError ?? Math.Max((double)root["geometricError"]!, Diagonal((JsonArray)root["boundingVolume"]!["box"]!) * 0.1);
        var tileset = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "1.1", ["generator"] = "GeoPandas.Net" },
            ["geometricError"] = err,
            ["root"] = root,
        };
        var path = Path.Combine(directory, "tileset.json");
        File.WriteAllText(path, tileset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    /// <summary>Oriented box (centre + half axes) in the tile frame (ENU, z up).</summary>
    private static JsonArray Box(Mesh m)
    {
        var (a, b, c, d, e, f) = m.Bounds();
        double hz = Math.Max((f - c) / 2, 0.01);
        return new JsonArray((a + d) / 2, (b + e) / 2, (c + f) / 2, Math.Max((d - a) / 2, 0.01), 0.0, 0.0, 0.0, Math.Max((e - b) / 2, 0.01), 0.0, 0.0, 0.0, hz);
    }

    private static JsonArray BoxOf(JsonArray children, JsonArray? extra = null)
    {
        double a = double.MaxValue, b = double.MaxValue, c = double.MaxValue, d = double.MinValue, e = double.MinValue, f = double.MinValue;
        void Add(JsonArray box)
        {
            double cx = (double)box[0]!, cy = (double)box[1]!, cz = (double)box[2]!, hx = (double)box[3]!, hy = (double)box[7]!, hz = (double)box[11]!;
            a = Math.Min(a, cx - hx); b = Math.Min(b, cy - hy); c = Math.Min(c, cz - hz);
            d = Math.Max(d, cx + hx); e = Math.Max(e, cy + hy); f = Math.Max(f, cz + hz);
        }
        foreach (var ch in children) Add((JsonArray)ch!["boundingVolume"]!["box"]!);
        if (extra is not null) Add(extra);
        return new JsonArray((a + d) / 2, (b + e) / 2, (c + f) / 2, (d - a) / 2, 0.0, 0.0, 0.0, (e - b) / 2, 0.0, 0.0, 0.0, (f - c) / 2);
    }

    private static double Diagonal(JsonArray box) => 2 * Math.Sqrt(Math.Pow((double)box[3]!, 2) + Math.Pow((double)box[7]!, 2) + Math.Pow((double)box[11]!, 2));
}
