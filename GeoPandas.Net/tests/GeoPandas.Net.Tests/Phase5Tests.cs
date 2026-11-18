using System.Text.Json.Nodes;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Index;

namespace GeoPandas.Net.Tests;

/// <summary>Phase 5 (v0.5): spatial indexes (H3 vs Uber's h3 4.5, S2 vs s2sphere), scene export, live layers, AI, GPU.</summary>
public sealed class Phase5Tests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);
    private static readonly Lazy<JsonNode> H3Ref = new(() => JsonNode.Parse(File.ReadAllText(Data("h3_reference.json")))!);
    private static ulong P(JsonNode n) => H3.Parse(n.GetValue<string>());
    private static IEnumerable<string> Strs(JsonNode n) => n.AsArray().Select(x => x!.GetValue<string>());

    [Fact]
    public void H3_IndexingCentresAndBoundariesMatchReferenceLibrary()
    {
        foreach (var p in H3Ref.Value["points"]!.AsArray())
        {
            var c = H3.LatLngToCell(p!["lat"]!.GetValue<double>(), p["lng"]!.GetValue<double>(), p["res"]!.GetValue<int>());
            Assert.Equal(p["cell"]!.GetValue<string>(), H3.ToString(c));
            Assert.True(H3.IsValid(c));
            var (lat, lng) = H3.CellToLatLng(c);
            Assert.Equal(p["center"]![0]!.GetValue<double>(), lat, 10);
            Assert.Equal(p["center"]![1]!.GetValue<double>(), lng, 10);
            var b = H3.CellToBoundary(c);
            var rb = p["boundary"]!.AsArray();
            Assert.Equal(rb.Count, b.Count);
            for (int i = 0; i < b.Count; i++)
            {
                Assert.Equal(rb[i]![0]!.GetValue<double>(), b[i].Lat, 10);
                Assert.Equal(rb[i]![1]!.GetValue<double>(), b[i].Lng, 10);
            }
        }
    }

    [Fact]
    public void H3_PentagonsNeighboursAndAreas()
    {
        foreach (var p in H3Ref.Value["pentagons"]!.AsArray())
        {
            var c = P(p!["cell"]!);
            Assert.True(H3.IsPentagon(c));
            Assert.Equal(H3.GetResolution(c) % 2 == 1 ? 10 : 5, H3.CellToBoundary(c).Count);   // class III pentagons gain 5 edge-crossing vertices
            Assert.Equal(Strs(p["neighbors"]!).Order(), H3.Neighbors(c).Select(H3.ToString).Order());
            Assert.Equal(p["area"]!.GetValue<double>(), H3.CellAreaKm2(c), p["area"]!.GetValue<double>() * 1e-12);
        }
        foreach (var a in H3Ref.Value["areas"]!.AsArray())
            Assert.Equal(a!["km2"]!.GetValue<double>(), H3.CellAreaKm2(P(a["cell"]!)), a["km2"]!.GetValue<double>() * 1e-12);
    }

    [Fact]
    public void H3_GridDisksRingsHierarchyAndCompaction()
    {
        foreach (var d in H3Ref.Value["disks"]!.AsArray())
        {
            var o = P(d!["origin"]!);
            int k = d["k"]!.GetValue<int>();
            Assert.Equal(Strs(d["cells"]!).Order(), H3.GridDisk(o, k).Select(H3.ToString).Order());
            if (d["ring"] is JsonArray ring) Assert.Equal(Strs(ring).Order(), H3.GridRing(o, k).Select(H3.ToString).Order());
        }
        foreach (var h in H3Ref.Value["hierarchy"]!.AsArray())
        {
            var c = P(h!["cell"]!);
            int res = H3.GetResolution(c);
            foreach (var (pr, expected) in h["parents"]!.AsObject()) Assert.Equal(expected!.GetValue<string>(), H3.ToString(H3.CellToParent(c, int.Parse(pr))));
            Assert.Equal(Strs(h["children"]!).Order(), H3.CellToChildren(c, res + 2).Select(H3.ToString).Order());
            Assert.Equal(h["center_child"]!.GetValue<string>(), H3.ToString(H3.CellToCenterChild(c, res + 3)));
        }
        var comp = H3Ref.Value["compact"]!;
        var cells = comp["cells"]!.AsArray().Select(x => P(x!)).ToList();
        var compacted = H3.Compact(cells);
        Assert.Equal(Strs(comp["compacted"]!).Order(), compacted.Select(H3.ToString).Order());
        Assert.Equal(cells.Order(), H3.Uncompact(compacted, 8).Order());
    }

    [Fact]
    public void H3_PolygonFillMatchesCentreContainment()
    {
        foreach (var pf in H3Ref.Value["polyfill"]!.AsArray())
        {
            var ring = pf!["ring"]!.AsArray().Select(p => (p!.AsArray()[1]!.GetValue<double>(), p.AsArray()[0]!.GetValue<double>())).ToList();
            ring.Add(ring[0]);
            var mine = H3.PolygonToCells(Geo.Polygon(ring.ToArray()), pf["res"]!.GetValue<int>()).Select(H3.ToString).ToHashSet();
            Assert.True(mine.SetEquals(Strs(pf["cells"]!)));
        }
    }

    [Fact]
    public void H3_FrameHelpers_BinPointsIntoHexagons()
    {
        var pts = GeoDataFrame.FromGeometries(Enumerable.Range(0, 200).Select(i => (NetTopologySuite.Geometries.Geometry?)Geo.Point(106.8 + (i % 20) * 0.002, -6.2 + (i / 20) * 0.002)),
            Geometry.Crs.CoordinateReferenceSystem.Wgs84, Column.Create("v", Enumerable.Range(0, 200).Select(i => (double)i).ToArray()));
        var bins = H3Frame.Bin(pts, 8, "v");
        Assert.Equal(200, bins["count"].ToDoubleArray().Sum());
        Assert.Equal(Enumerable.Range(0, 200).Sum(), bins["v_sum"].ToDoubleArray().Sum(), 6);
        Assert.All(Enumerable.Range(0, bins.RowCount), i => Assert.InRange(bins["area_km2"].GetDouble(i), 0.6, 0.85));   // res 8 ≈ 0.74 km²
        var indexed = H3Frame.IndexPoints(pts, 9);
        Assert.Equal(H3.ToString(H3.LatLngToCell(-6.2, 106.8, 9)), indexed["h3"].GetValue(0));
    }
    [Fact]
    public void S2_CellIdsTokensGeometryAndNeighboursMatchS2sphere()
    {
        var r = JsonNode.Parse(File.ReadAllText(Data("s2_reference.json")))!;
        foreach (var p in r["points"]!.AsArray())
        {
            double lat = p!["lat"]!.GetValue<double>(), lng = p["lng"]!.GetValue<double>();
            int level = p["level"]!.GetValue<int>();
            Assert.Equal(p["leaf"]!.GetValue<string>(), S2.FromLatLng(lat, lng).ToString());
            var id = S2.FromLatLng(lat, lng, level);
            Assert.Equal(p["id"]!.GetValue<string>(), id.ToString());
            Assert.Equal(level, S2.Level(id));
            Assert.True(S2.IsValid(id));
            Assert.Equal(p["token"]!.GetValue<string>(), S2.ToToken(id));
            Assert.Equal(id, S2.FromToken(p["token"]!.GetValue<string>()));
            var (clat, clng) = S2.ToLatLng(id);
            Assert.Equal(p["center"]![0]!.GetValue<double>(), clat, 10);
            Assert.Equal(p["center"]![1]!.GetValue<double>(), clng, 10);
            var v = S2.Vertices(id);
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal(p["vertices"]![k]![0]!.GetValue<double>(), v[k].Lat, 10);
                Assert.Equal(p["vertices"]![k]![1]!.GetValue<double>(), v[k].Lng, 10);
            }
            Assert.Equal(Strs(p["neighbors"]!), S2.EdgeNeighbors(id).Select(S2.ToToken));
            if (level < 30)
            {
                Assert.Equal(Strs(p["children"]!), S2.Children(id).Select(S2.ToToken));
                Assert.All(S2.Children(id), c => Assert.Equal(id, S2.Parent(c, level)));
            }
            // s2sphere documents exact_area as accurate to 6 digits (leaf cells are ~1e-18 sr)
            double area = p["area"]!.GetValue<double>();
            Assert.True(Math.Abs(S2.ExactAreaSr(id) / area - 1) < 1e-6);
        }
    }

    [Fact]
    public void S2_PolygonFillAndCover()
    {
        var poly = Geo.Box(106.7, -6.35, 106.95, -6.10);
        var inside = S2.PolygonToCells(poly, 13);
        var cover = S2.Cover(poly, 13);
        Assert.NotEmpty(inside);
        Assert.True(inside.All(c => cover.Contains(c)));          // centre-contained cells are part of the cover
        Assert.All(cover, c => Assert.True(S2.ToPolygon(c).Intersects(poly)));
        double coverArea = cover.Sum(c => S2.AreaKm2(c)), polyKm2 = 0.25 * 0.25 * 111.32 * 111.32 * Math.Cos(6.2 * Math.PI / 180);
        Assert.InRange(coverArea / polyKm2, 1.0, 1.25);
        Assert.InRange(inside.Sum(c => S2.AreaKm2(c)) / polyKm2, 0.9, 1.1);
    }
}
