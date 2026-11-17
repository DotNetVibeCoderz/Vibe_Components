using GeoPandas.Net.Datasets;
using NetTopologySuite.Geometries;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Tests;

public class GeoDataFrameTests
{
    private static readonly GeoDataFrame Districts = SampleData.Districts();
    private static readonly GeoDataFrame Pois = SampleData.PointsOfInterest(300);

    [Fact]
    public void SampleData_IsValid()
    {
        Assert.True(Districts.RowCount >= 25);
        Assert.All(Districts.Geometry.IsValid, Assert.True);
        Assert.Equal(32748, Districts.Crs!.Epsg);
        // districts tile the city without overlaps
        double sum = Districts.Area().Sum();
        Assert.Equal(SampleData.CityBoundary().Area, sum, 0);
    }

    [Fact]
    public void SJoin_Within_MatchesBruteForce()
    {
        var joined = Pois.SJoin(Districts, SpatialPredicate.Within);
        int expected = 0;
        for (int i = 0; i < Pois.RowCount; i++)
            for (int j = 0; j < Districts.RowCount; j++)
                if (Pois.Geometry[i]!.Within(Districts.Geometry[j]!)) expected++;
        Assert.Equal(expected, joined.RowCount);
        Assert.True(joined.HasColumn("index_right"));
        Assert.True(joined.HasColumn("name_left") && joined.HasColumn("name_right"));
    }

    [Fact]
    public void SJoin_Left_KeepsUnmatched_And_Right_Inverts()
    {
        var outside = GeoDataFrame.FromGeometries([Geo.Point(0, 0), Pois.Geometry[0]], Districts.Crs, Column.Create("id", new[] { 1L, 2L }));
        var left = outside.SJoin(Districts, SpatialPredicate.Within, JoinHow.Left);
        Assert.Equal(2, left.RowCount);
        Assert.True(left["index_right"].IsNull(0));
        var right = Districts.SJoin(outside, SpatialPredicate.Contains, JoinHow.Right);
        Assert.Equal(2, right.RowCount);
    }

    [Fact]
    public void SJoinNearest_MatchesBruteForce()
    {
        var hospitals = Pois.Filter<string?>("category", c => c == "hospital");
        var schools = Pois.Filter<string?>("category", c => c == "school");
        var near = schools.SJoinNearest(hospitals, distanceColumn: "dist_m");
        Assert.Equal(schools.RowCount, near.RowCount);
        for (int i = 0; i < schools.RowCount; i++)
        {
            double best = Enumerable.Range(0, hospitals.RowCount).Min(j => schools.Geometry[i]!.Distance(hospitals.Geometry[j]!));
            Assert.Equal(best, near["dist_m"].GetDouble(i), 6);
        }
    }

    [Fact]
    public void SJoinNearest_Polygons_IsExact()
    {
        var a = GeoDataFrame.FromGeometries([Geo.Box(0, 0, 100, 10)], null);
        var b = GeoDataFrame.FromGeometries([Geo.Point(50, 15), Geo.Point(-30, 5), Geo.Point(98, 30)], null, Column.Create("id", new[] { 1L, 2L, 3L }));
        var near = a.SJoinNearest(b);
        Assert.Equal(1L, near["id"].GetValue(0));
        Assert.Equal(5.0, near["distance"].GetDouble(0), 9);
    }

    [Fact]
    public void Overlay_Intersection_And_Union_PreserveArea()
    {
        var circle = GeoDataFrame.FromGeometries([Geo.Circle(SampleData.CenterX, SampleData.CenterY, 3000)], Districts.Crs, Column.Create("zone", new[] { "core" }));
        var inter = Districts.Overlay(circle, OverlayHow.Intersection);
        Assert.Equal(circle.Area().Sum(), inter.Area().Sum(), 0);
        Assert.True(inter.HasColumn("zone") && inter.HasColumn("name"));

        var diff = Districts.Overlay(circle, OverlayHow.Difference);
        Assert.Equal(Districts.Area().Sum() - circle.Area().Sum(), diff.Area().Sum(), 0);

        var union = Districts.Overlay(circle, OverlayHow.Union);
        Assert.Equal(Districts.Area().Sum(), union.Area().Sum(), 0); // circle is inside the city
    }

    [Fact]
    public void Dissolve_ByRegion()
    {
        var regions = Districts.Dissolve("region", AggFunc.Sum);
        Assert.InRange(regions.RowCount, 2, 5);
        Assert.Equal(Districts["population"].Sum(), regions["population"].Sum());
        Assert.Equal(Districts.Area().Sum(), regions.Area().Sum(), 0);
    }

    [Fact]
    public void Clip_And_Cx()
    {
        var box = Geo.Box(SampleData.CenterX - 2000, SampleData.CenterY - 2000, SampleData.CenterX + 2000, SampleData.CenterY + 2000);
        var clipped = Districts.Clip(box);
        Assert.Equal(box.Area, clipped.Area().Sum(), 0);
        var cx = Pois.Cx(SampleData.CenterX - 2000, SampleData.CenterY - 2000, SampleData.CenterX + 2000, SampleData.CenterY + 2000);
        Assert.Equal(Pois.Geometry.Intersects(box).Count(b => b), cx.RowCount);
    }

    [Fact]
    public void Explode_SplitsMultiParts()
    {
        var mp = Geo.MultiPolygon(Geo.Box(0, 0, 1, 1), Geo.Box(2, 2, 3, 3));
        var gdf = GeoDataFrame.FromGeometries([mp, Geo.Point(5, 5)], null, Column.Create("id", new[] { 1L, 2L }));
        var ex = gdf.Explode();
        Assert.Equal(3, ex.RowCount);
        Assert.Equal(new object?[] { 1L, 1L, 2L }, ex["id"].AsObjects().ToArray());
    }

    [Fact]
    public void ToCrs_RoundTrip()
    {
        var wgs = Districts.ToCrs(4326);
        Assert.True(wgs.IsGeographic);
        var back = wgs.ToCrs(32748);
        for (int i = 0; i < Districts.RowCount; i++)
            Assert.True(Districts.Geometry[i]!.EqualsExact(back.Geometry[i]!, 1e-3));
    }

    [Fact]
    public void GeodesicArea_MatchesProjectedArea()
    {
        var wgs = Districts.ToCrs(4326);
        double geodesic = wgs.Geometry.GeodesicArea().Sum();
        double planar = Districts.Area().Sum();
        Assert.InRange(geodesic / planar, 0.995, 1.005); // UTM scale distortion is tiny near the CM
    }

    [Fact]
    public void FromXY_And_Bounds()
    {
        var df = new DataFrame(Column.Create("lon", new[] { 106.8, 107.6 }), Column.Create("lat", new[] { -6.2, -6.9 }));
        var gdf = GeoDataFrame.FromXY(df, "lon", "lat", CoordinateReferenceSystem.Wgs84);
        Assert.Equal(106.8, gdf.TotalBounds.MinX);
        Assert.Equal(-6.2, gdf.TotalBounds.MaxY);
        Assert.Equal(107.6, gdf.Geometry.X[1]);
    }

    [Fact]
    public void CrsMismatch_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => Pois.SJoin(Districts.ToCrs(4326)));
    }

    [Fact]
    public void Plot_RendersSvg()
    {
        var svg = Districts.Plot(new Plotting.LayerStyle { Column = "population", ColorMap = Plotting.ColorMaps.YlOrRd }, "Population").ToSvg(800, 600);
        Assert.StartsWith("<svg", svg);
        Assert.Contains("Population", svg);
        Assert.True(svg.Split("<path").Length > Districts.RowCount);
    }

    [Fact]
    public void Classifier_Breaks()
    {
        var v = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        Assert.Equal(new[] { 20.8, 40.6, 60.4, 80.2, 100 }, Plotting.Classifier.Breaks(v, 5, Plotting.ClassificationScheme.EqualInterval).Select(x => Math.Round(x, 6)));
        var jenks = Plotting.Classifier.Breaks([1, 2, 3, 50, 51, 52, 100, 101, 102], 3, Plotting.ClassificationScheme.NaturalBreaks);
        Assert.Equal(new[] { 3.0, 52, 102 }, jenks);
    }
}
