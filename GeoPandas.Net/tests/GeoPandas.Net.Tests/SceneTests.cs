using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using GeoPandas.Net.Cesium;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Scene;
using GeoPandas.Net.Survey;

namespace GeoPandas.Net.Tests;

/// <summary>glTF / 3D Tiles export. Output was also checked with Khronos gltf-validator and CesiumGS 3d-tiles-validator (0 errors, 0 warnings)
/// and its georeferencing with pyproj (vertices within 0.06 mm of their source coordinates); see Progress.md.</summary>
public sealed class SceneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-scene-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static GeoDataFrame Buildings()
    {
        var crs = CoordinateReferenceSystem.FromEpsg(32748);
        var a = Geo.FromWkt("POLYGON ((700000 9300000, 700030 9300000, 700030 9300020, 700000 9300020, 700000 9300000), (700010 9300005, 700020 9300005, 700020 9300015, 700010 9300015, 700010 9300005))");
        var b = Geo.Box(700050, 9300010, 700062, 9300040);
        return GeoDataFrame.FromGeometries([a, b], crs, Column.Create("name", new[] { "Courtyard", "Tower" }), Column.Create("height", new[] { 9.0, 45.5 }),
            Column.Create("floors", new long[] { 3, 13 }));
    }

    private static (JsonNode Json, byte[] Bin) ReadGlb(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        int jl = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        var json = JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jl))!;
        int bl = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(20 + jl));
        return (json, glb.AsSpan(28 + jl, bl).ToArray());
    }

    [Fact]
    public void Extrusion_VolumeNormalsAndWindings()
    {
        var b = Buildings();
        var frame = LocalFrame.AtCentre(b.TotalBounds, b.Crs);
        var mesh = MeshBuilders.Extrude(b, frame, "height");
        // closed, outward-wound solids: volume = footprint × height (true metres vs UTM grid metres: scale factor² ≈ 0.9996–1.0004)
        double expected = 500 * 9.0 + 360 * 45.5;
        Assert.InRange(mesh.Volume() / expected, 0.999, 1.001);
        Assert.Equal(mesh.VertexCount, mesh.Normals!.Count);
        Assert.Equal([0u, 1u], mesh.FeatureIds!.Distinct().Order());
    }

    [Fact]
    public void Glb_StructureAccessorsAndFeatureMetadata()
    {
        var b = Buildings();
        var frame = LocalFrame.AtCentre(b.TotalBounds, b.Crs);
        var (json, bin) = ReadGlb(Gltf.ToGlb(MeshBuilders.Extrude(b, frame, "height")));
        Assert.Equal("2.0", (string)json["asset"]!["version"]!);
        var prim = json["meshes"]![0]!["primitives"]![0]!;
        var posAcc = json["accessors"]![(int)prim["attributes"]!["POSITION"]!]!;
        Assert.Equal(3, posAcc["min"]!.AsArray().Count);
        Assert.Equal(5125, (int)json["accessors"]![(int)prim["indices"]!]!["componentType"]!);
        Assert.Contains("EXT_mesh_features", json["extensionsUsed"]!.AsArray().Select(x => (string)x!));
        Assert.Equal(2, (int)prim["extensions"]!["EXT_mesh_features"]!["featureIds"]![0]!["featureCount"]!);
        // decode the string property "name" from the property table
        var meta = json["extensions"]!["EXT_structural_metadata"]!;
        var nameProp = meta["propertyTables"]![0]!["properties"]!["name"]!;
        var views = json["bufferViews"]!.AsArray();
        var values = views[(int)nameProp["values"]!]!; var offsets = views[(int)nameProp["stringOffsets"]!]!;
        var vb = bin.AsSpan((int)values["byteOffset"]!, (int)values["byteLength"]!).ToArray(); var ob = bin.AsSpan((int)offsets["byteOffset"]!, (int)offsets["byteLength"]!).ToArray();
        var names = Enumerable.Range(0, 2).Select(i => Encoding.UTF8.GetString(vb, (int)BinaryPrimitives.ReadUInt32LittleEndian(ob.AsSpan(i * 4)), (int)BinaryPrimitives.ReadUInt32LittleEndian(ob.AsSpan((i + 1) * 4)) - (int)BinaryPrimitives.ReadUInt32LittleEndian(ob.AsSpan(i * 4)))).ToArray();
        Assert.Equal(["Courtyard", "Tower"], names);
        var heightValues = views[(int)meta["propertyTables"]![0]!["properties"]!["height"]!["values"]!]!;
        Assert.Equal(45.5, BinaryPrimitives.ReadDoubleLittleEndian(bin.AsSpan((int)heightValues["byteOffset"]! + 8)));
        Assert.Equal(0, (int)heightValues["byteOffset"]! % 8);
    }

    [Fact]
    public void Tileset_PlacesVerticesOnTheEllipsoid()
    {
        var b = Buildings();
        var path = Tiles3D.WriteBuildings(b, _dir, options: new TilesetOptions { MaxFeaturesPerTile = 1 });
        var tileset = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("1.1", (string)tileset["asset"]!["version"]!);
        var root = tileset["root"]!;
        Assert.Equal("ADD", (string)root["refine"]!);
        var m = root["transform"]!.AsArray().Select(v => (double)v!).ToArray();
        var leaves = root["children"]!.AsArray();
        Assert.Equal(2, leaves.Count);
        // the tower's roof corner (700062, 9300040, 45.5) must come back from glTF (y-up) → ENU → ECEF → geodetic
        var tower = leaves.Select(l => File.ReadAllBytes(Path.Combine(_dir, (string)l!["content"]!["uri"]!))).Select(ReadGlb)
            .First(g => (double)g.Json["accessors"]![(int)g.Json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!]!["max"]![1]! > 40);   // y-up: max height
        var acc = tower.Json["accessors"]![(int)tower.Json["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"]!]!;
        var view = tower.Json["bufferViews"]![(int)acc["bufferView"]!]!;
        var (lonT, latT) = CoordinateTransformer.Create(b.Crs!, CoordinateReferenceSystem.Wgs84).Transform(700062, 9300040);
        double best = double.MaxValue;
        for (int i = 0; i < (int)acc["count"]!; i++)
        {
            int o = (int)view["byteOffset"]! + i * 12;
            double x = BinaryPrimitives.ReadSingleLittleEndian(tower.Bin.AsSpan(o)), y = BinaryPrimitives.ReadSingleLittleEndian(tower.Bin.AsSpan(o + 4)), z = BinaryPrimitives.ReadSingleLittleEndian(tower.Bin.AsSpan(o + 8));
            double e = x, n = -z, u = y;
            double X = m[0] * e + m[4] * n + m[8] * u + m[12], Y = m[1] * e + m[5] * n + m[9] * u + m[13], Z = m[2] * e + m[6] * n + m[10] * u + m[14];
            var (lon, lat, h) = Gnss.EcefToGeodetic(X, Y, Z);
            if (Math.Abs(h - 45.5) > 0.01) continue;
            best = Math.Min(best, Math.Sqrt(Math.Pow((lon - lonT) * 111320 * Math.Cos(lat * Math.PI / 180), 2) + Math.Pow((lat - latT) * 110574, 2)));
        }
        Assert.True(best < 0.005, $"nearest roof vertex {best} m from the source corner");
    }

    [Fact]
    public void TerrainTileset_HasReplacementPyramid()
    {
        var dem = Datasets.SampleData.Dem(80, 60);
        var path = Tiles3D.WriteTerrain(dem, _dir, chunk: 32, coarseStep: 4);
        var root = JsonNode.Parse(File.ReadAllText(path))!["root"]!;
        Assert.Equal("REPLACE", (string)root["refine"]!);
        Assert.Equal(6, root["children"]!.AsArray().Count);           // ⌈79/32⌉ × ⌈59/32⌉
        var mesh = MeshBuilders.Terrain(dem, LocalFrame.AtCentre(dem.Bounds, dem.Crs));
        Assert.Equal(80 * 60, mesh.VertexCount);
        Assert.Equal(79 * 59 * 2, mesh.TriangleCount);
        Assert.True(mesh.Normals!.All(n => n.Z > 0));                   // counter-clockwise from above
    }

    [Fact]
    public void CesiumAdapter_BuildsOptionsInWgs84()
    {
        var b = Buildings();
        var ds = CesiumExtensions.ToDataSourceOptions(b, "blocks", fill: "#b0306a");
        var fc = JsonNode.Parse(ds.Data!)!;
        Assert.Equal(2, fc["features"]!.AsArray().Count);
        double lon = (double)fc["features"]![1]!["geometry"]!["coordinates"]![0]![0]![0]!;
        Assert.InRange(lon, 106.8, 106.9);                              // UTM 48S → degrees
        var rect = CesiumExtensions.ToRectangle(b);
        Assert.True(rect.West < rect.East && rect.South < rect.North && rect.South < -6);
        var pts = GeoDataFrame.FromGeometries([Geo.Point(700000, 9300000), Geo.Point(700100, 9300100)], b.Crs, Column.Create("z", new[] { 12.0, 30.0 }));
        var pc = CesiumExtensions.ToPointCollectionOptions(pts, heightColumn: "z", colorColumn: "z");
        Assert.Equal(30.0, pc.Positions[1].Height);
        Assert.Equal(2, pc.Colors!.Count);
        var style = CesiumExtensions.ColorByProperty("height", [(10, "#fee08b"), (40, "#d73027")]);
        Assert.Equal("${height} >= 40", style.ColorConditions![0][0]);
    }
}
