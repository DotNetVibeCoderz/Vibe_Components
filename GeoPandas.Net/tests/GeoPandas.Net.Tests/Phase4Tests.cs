using System.Text.Json.Nodes;
using GeoPandas.Net.Analytics;
using GeoPandas.Net.Civil;
using GeoPandas.Net.Data;
using GeoPandas.Net.Network.Utility;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Tests;

/// <summary>Phase 4 (v0.4): spatial regression, GWR, universal kriging (vs PySAL spreg / mgwr, PyKrige), utility networks (vs EPANET 2.2).</summary>
public sealed class Phase4Tests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);
    private static readonly Lazy<JsonNode> Stats = new(() => JsonNode.Parse(File.ReadAllText(Data("stats_reference.json")))!);
    private static double[] A(JsonNode n) => n.AsArray().Select(v => v!.GetValue<double>()).ToArray();
    private static void Near(double[] expected, double[] actual, double tol)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.True(Math.Abs(expected[i] - actual[i]) <= tol, $"[{i}] expected {expected[i]}, actual {actual[i]}");
    }

    private static (GeoDataFrame Grid, SpatialWeights W) Grid()
    {
        var grid = GeoJson.ReadFile(Data("stats_grid.geojson"));
        return (grid, SpatialWeights.Queen(grid).RowStandardize());
    }

    [Fact]
    public void Ols_WithLagrangeMultipliers_MatchesSpreg()
    {
        var (grid, w) = Grid();
        var r = Stats.Value["ols"]!;
        Assert.Equal(Stats.Value["neighbors"]!.AsArray().Select(v => v!.GetValue<int>()), w.Neighbors.Select(n => n.Length));
        var ols = SpatialRegression.Ols(grid, "y", ["x1", "x2"], w);
        Near(A(r["betas"]!), ols.Betas, 1e-9);
        Near(A(r["se"]!), ols.StdErrors, 1e-9);
        Near(A(r["t"]!), ols.Statistics, 1e-7);
        Assert.Equal(r["r2"]!.GetValue<double>(), ols.R2, 10);
        Assert.Equal(r["logll"]!.GetValue<double>(), ols.LogLikelihood, 8);
        Assert.Equal(r["aic"]!.GetValue<double>(), ols.Aic, 8);
        foreach (var (name, key) in new[] { ("Lagrange Multiplier (lag)", "lm_lag"), ("Robust LM (lag)", "rlm_lag"), ("Lagrange Multiplier (error)", "lm_error"),
                     ("Robust LM (error)", "rlm_error"), ("Lagrange Multiplier (SARMA)", "lm_sarma") })
        {
            var t = ols.Diagnostics.Single(d => d.Name == name);
            var e = A(r[key]!);
            Assert.Equal(e[0], t.Statistic, 6);
            Assert.True(Math.Abs(e[1] - t.PValue) <= 1e-9 * Math.Max(1, e[1]) || Math.Abs(Math.Log(e[1] / t.PValue)) < 1e-6, $"{key} p {t.PValue} vs {e[1]}");
        }
        Assert.Equal(5, ols.Coefficients().ColumnCount);
    }

    [Theory]
    [InlineData("lag")]
    [InlineData("error")]
    public void MaximumLikelihoodSpatialModels_MatchSpreg(string kind)
    {
        var (grid, w) = Grid();
        var r = Stats.Value[kind]!;
        var res = kind == "lag" ? SpatialRegression.SpatialLag(grid, "y", ["x1", "x2"], w) : SpatialRegression.SpatialError(grid, "y", ["x1", "x2"], w);
        Near(A(r["betas"]!), res.Betas, 1e-6);
        Near(A(r["se"]!), res.StdErrors, 1e-6);
        Near(A(r["z"]!), res.Statistics, 1e-4);
        Assert.Equal(r["logll"]!.GetValue<double>(), res.LogLikelihood, 6);
        Assert.Equal(r["aic"]!.GetValue<double>(), res.Aic, 6);
        Assert.Equal(r["pr2"]!.GetValue<double>(), res.R2, 8);
        Assert.True(Math.Abs(r["sig2"]!.GetValue<double>() - res.Sigma2) < 1e-6);   // λ is optimised to ±1e-7, as spreg
        Assert.InRange(res.Rho!.Value, 0.3, 0.9);    // the simulated process has ρ = 0.55
    }

    [Fact]
    public void Gwr_SelectsBandwidthAndMatchesMgwr()
    {
        var pts = GeoJson.ReadFile(Data("stats_gwr.geojson"));
        var g = Stats.Value["gwr"]!;
        var res = Gwr.Fit(pts, "y", ["x1", "x2"]);
        Assert.Equal(g["bw"]!.GetValue<double>(), res.Bandwidth);
        Assert.Equal(g["aicc"]!.GetValue<double>(), res.Aicc, 8);
        Assert.Equal(g["r2"]!.GetValue<double>(), res.R2, 10);
        Assert.Equal(g["adj_r2"]!.GetValue<double>(), res.AdjustedR2, 10);
        Assert.Equal(g["trS"]!.GetValue<double>(), res.EffectiveParameters, 9);
        Assert.Equal(g["sigma2"]!.GetValue<double>(), res.Sigma2, 10);
        var rows = g["rows"]!.AsArray().Select(v => v!.GetValue<int>()).ToArray();
        for (int t = 0; t < rows.Length; t++)
        {
            Near(A(g["params"]![t]!), [res.Params[rows[t], 0], res.Params[rows[t], 1], res.Params[rows[t], 2]], 1e-10);
            Near(A(g["bse"]![t]!), [res.StdErrors[rows[t], 0], res.StdErrors[rows[t], 1], res.StdErrors[rows[t], 2]], 1e-10);
            Assert.Equal(g["local_r2"]![t]!.GetValue<double>(), res.LocalR2[rows[t]], 10);
        }
        var withCols = res.AddTo(pts);
        Assert.Contains(withCols.Columns, c => c.Name == "b_x1");
        // fixed Gaussian kernel: the golden-section search lands on mgwr's bandwidth
        var fixedRes = Gwr.Fit(pts, "y", ["x1", "x2"], kernel: GwrKernel.Gaussian, fixedBandwidth: true);
        Assert.Equal(g["fixed_bw"]!.GetValue<double>(), fixedRes.Bandwidth, 1);
        Assert.Equal(g["fixed_aicc"]!.GetValue<double>(), fixedRes.Aicc, 4);
    }

    [Fact]
    public void UniversalKriging_MatchesPyKrige()
    {
        var u = Stats.Value["uk"]!;
        var vg = new Variogram(VariogramModel.Spherical, u["nugget"]!.GetValue<double>(), u["nugget"]!.GetValue<double>() + u["psill"]!.GetValue<double>(), u["range"]!.GetValue<double>());
        var (z, v) = Interpolation.UniversalKriging(A(u["sx"]!), A(u["sy"]!), A(u["sz"]!), A(u["qx"]!), A(u["qy"]!), vg);
        Near(A(u["z"]!), z, 1e-9);
        Near(A(u["var"]!), v, 1e-9);
        // a neighbourhood holding every sample reproduces the global system; a smaller one still gives finite estimates
        var (zAll, _) = Interpolation.UniversalKriging(A(u["sx"]!), A(u["sy"]!), A(u["sz"]!), A(u["qx"]!), A(u["qy"]!), vg, neighbors: 120);
        Near(A(u["z"]!), zAll, 1e-9);
        var (zl, vl) = Interpolation.UniversalKriging(A(u["sx"]!), A(u["sy"]!), A(u["sz"]!), A(u["qx"]!), A(u["qy"]!), vg, neighbors: 30);
        Assert.All(zl.Concat(vl), x => Assert.True(double.IsFinite(x)));
    }

    // ------------------------------------------------------------------ utility networks

    public static IEnumerable<object[]> EpanetCases() => [["Net1", "epanet_net1.inp"], ["Net3", "epanet_net3.inp"], ["Net1_SI_DW", "epanet_net1_si_dw.inp"]];

    [Theory]
    [MemberData(nameof(EpanetCases))]
    public void Hydraulics_MatchEpanet22(string key, string file)
    {
        var net = WaterNetwork.ReadInp(Data(file));
        var res = Hydraulics.Solve(net, new HydraulicOptions { Accuracy = 1e-10 });
        var e = JsonNode.Parse(File.ReadAllText(Data("epanet_reference.json")))![key]!;
        for (int i = 0; i < net.Nodes.Count; i++)
        {
            Assert.True(Math.Abs(res.Head[i] - e["head"]![net.Nodes[i].Id]!.GetValue<double>()) < 1e-4, $"head {net.Nodes[i].Id}");
            Assert.True(Math.Abs(res.Demand[i] - e["demand"]![net.Nodes[i].Id]!.GetValue<double>()) < 1e-6, $"demand {net.Nodes[i].Id}");
        }
        for (int k = 0; k < net.Links.Count; k++)
        {
            Assert.True(Math.Abs(res.Flow[k] - e["flow"]![net.Links[k].Id]!.GetValue<double>()) < 1e-6, $"flow {net.Links[k].Id}");
            Assert.Equal(e["status"]![net.Links[k].Id]!.GetValue<int>() != 0, res.LinkOpen[k]);
        }
        // .inp round trip gives the same solution
        var back = Hydraulics.Solve(WaterNetwork.ParseInp(net.ToInp()), new HydraulicOptions { Accuracy = 1e-10 });
        Assert.Equal(res.Head, back.Head);
        // GIS views
        Assert.Equal(net.Links.Count, res.LinksFrame().RowCount);
        Assert.Contains(res.NodesFrame().Columns, c => c.Name == "pressure");
    }

    [Fact]
    public void Hydraulics_ClosingAPipeDisconnectsTheBranch()
    {
        var net = WaterNetwork.ReadInp(Data("epanet_net1.inp"));
        var res = Hydraulics.Solve(net, new HydraulicOptions { ClosedLinks = ["31", "122"] });
        var i32 = net.Nodes.FindIndex(n => n.Id == "32");
        Assert.True(res.Disconnected[i32]);
        Assert.True(double.IsNaN(res.Head[i32]));
        Assert.Equal(1, res.Disconnected.Count(d => d));
    }

    [Fact]
    public void Topology_FindsEveryPlantedError()
    {
        LineString L(double x0, double y0, double x1, double y1) => Geometry.Geo.LineString((x0, y0), (x1, y1));
        var lines = GeoDataFrame.FromGeometries(
        [
            L(0, 0, 10, 0), L(10, 0, 20, 0), L(20.4, 0, 30, 0), L(15, -5, 15, 5), L(10, 0, 0, 0), L(100, 100, 110, 100), L(50, 50, 50, 50.0001)
        ], null);
        var v = NetworkTopology.Validate(lines, tolerance: 0.001, snapDistance: 1);
        int Count(TopologyRule r) => Enumerable.Range(0, v.RowCount).Count(i => (string)v["rule"].GetValue(i)! == r.ToString());
        Assert.Equal(1, Count(TopologyRule.Duplicate));
        Assert.Equal(1, Count(TopologyRule.CrossingWithoutNode));
        Assert.Equal(2, Count(TopologyRule.Undershoot));
        Assert.Equal(1, Count(TopologyRule.ZeroLength));
        Assert.Equal(4, Count(TopologyRule.Disconnected));
        Assert.Equal(5, Count(TopologyRule.Dangle));
        var clean = NetworkTopology.Validate(GeoDataFrame.FromGeometries([L(0, 0, 10, 0), L(10, 0, 20, 0), L(10, 0, 10, 10)], null),
            rules: TopologyRule.All & ~TopologyRule.Dangle);
        Assert.Equal(0, clean.RowCount);
    }

    private static WaterNetwork Ladder()
    {
        var net = new WaterNetwork { Units = FlowUnits.LPS };
        net.Nodes.Add(new WaterNode { Id = "R", Kind = WaterNodeKind.Reservoir, Elevation = 50, X = 0, Y = 0 });
        foreach (var (id, x, y) in new[] { ("A", 100.0, 0.0), ("B", 200.0, 0.0), ("C", 300.0, 0.0), ("D", 100.0, -100.0), ("E", 300.0, -100.0) })
        {
            var n = new WaterNode { Id = id, Kind = WaterNodeKind.Junction, X = x, Y = y };
            n.Demands.Add((0.001, null));
            net.Nodes.Add(n);
        }
        foreach (var (id, a, b) in new[] { ("P1", "R", "A"), ("P2", "A", "B"), ("P3", "B", "C"), ("P4", "A", "D"), ("P5", "C", "E"), ("P6", "D", "E") })
            net.Links.Add(new WaterLink { Id = id, Kind = WaterLinkKind.Pipe, From = a, To = b, Length = 100, Diameter = 0.15, Roughness = 120 });
        return net;
    }

    [Fact]
    public void ValveIsolation_SegmentsAndUnintendedIsolation()
    {
        var net = Ladder();
        var iso = new ValveIsolation(net, [new("V1", "P2", "A"), new("V2", "P2", "B"), new("V3", "P4", "A"), new("V4", "P3", "C")]);
        Assert.Equal(4, iso.Segments.Count);

        var p2 = iso.Isolate("P2");
        Assert.Equal(["V1", "V2"], p2.ValvesToClose.Order());
        Assert.Equal(["P2"], p2.IsolatedPipes);
        Assert.Empty(p2.UnintendedSegments);
        Assert.Equal(0, p2.AffectedDemand);

        var p4 = iso.Isolate("P4");
        Assert.Equal(["V3", "V4"], p4.ValvesToClose.Order());
        Assert.Equal(["C", "D", "E"], p4.IsolatedNodes.Order());
        Assert.Equal(0.003, p4.AffectedDemand, 12);

        // breaking the source main cuts off everything downstream
        var p1 = iso.Isolate("P1");
        Assert.Equal(3, p1.UnintendedSegments.Count);
        Assert.Equal(0.005, p1.AffectedDemand, 12);
        Assert.Equal(4, iso.SegmentsFrame().RowCount);

        // valve points snap to the nearest pipe end
        var pts = GeoDataFrame.FromGeometries([Geometry.Geo.Point(105, 0.5), Geometry.Geo.Point(299, -2)], null);
        var snapped = ValveIsolation.SnapValves(net, pts);
        Assert.Equal(new IsolationValve("V1", "P2", "A"), snapped[0]);
        Assert.Equal("C", snapped[1].Node);
    }

    [Fact]
    public void WaterNetwork_FromGis_SolvesALoop()
    {
        var pipes = GeoDataFrame.FromGeometries([
            Geometry.Geo.LineString((0, 0), (100, 0)), Geometry.Geo.LineString((100, 0), (100, 100)), Geometry.Geo.LineString((100, 100), (0, 100)),
            Geometry.Geo.LineString((0, 100), (0.005, 0.004))], null,
            Column.Create("diameter", new[] { 0.2, 0.15, 0.15, 0.2 }), Column.Create("roughness", new[] { 130.0, 130, 130, 130 }));
        var res = GeoDataFrame.FromGeometries([Geometry.Geo.Point(0, 0)], null, Column.Create("head", new[] { 60.0 }));
        var junctions = GeoDataFrame.FromGeometries([Geometry.Geo.Point(100, 100)], null, Column.Create("elevation", new[] { 10.0 }), Column.Create("demand", new[] { 0.02 }));
        var net = WaterNetwork.FromGeoDataFrames(pipes, junctions, res);
        Assert.Equal(4, net.Nodes.Count);       // the 4th pipe's end snaps onto the reservoir
        var h = Hydraulics.Solve(net);
        // continuity: the reservoir supplies the 20 L/s demand
        Assert.Equal(-0.02, h.Demand[net.Nodes.FindIndex(n => n.Kind == WaterNodeKind.Reservoir)], 6);
        Assert.All(h.Pressure.Where((_, i) => net.Nodes[i].Kind == WaterNodeKind.Junction), p => Assert.InRange(p, 0, 60));
    }

    // ------------------------------------------------------------------ civil

    [Theory]
    [InlineData(300.0, 60.0, 60.0, 59.9400277713684, 1.9985718830375276)]
    [InlineData(300.0, 60.0, 25.0, 24.99924649006708, 0.14467281120526992)]
    [InlineData(150.0, 120.0, 120.0, 118.09416982397066, 15.818071170155896)]
    [InlineData(50.0, 80.0, 80.0, 75.02948262568233, 20.377734261068365)]
    public void Clothoid_MatchesScipyFresnel(double r, double ls, double s, double x, double y)
    {
        var (cx, cy) = HorizontalAlignment.Clothoid(s, r, ls);
        Assert.Equal(x, cx, 9);
        Assert.Equal(y, cy, 9);
    }

    [Fact]
    public void HorizontalAlignment_SpiralCurveSpiralClosesOnTheForwardTangent()
    {
        (double X, double Y)[] pis = [(0, 0), (400, 0), (700, 300), (1200, 300)];
        var h = new HorizontalAlignment(pis, [250, 400], [60, 0], startStation: 1000);
        Assert.Equal(2, h.Curves.Count);
        foreach (var (c, pi, next) in new[] { (h.Curves[0], pis[1], pis[2]), (h.Curves[1], pis[2], pis[3]) })
        {
            // the ST lies on the forward tangent, Ts from the PI, heading along it
            var (x, y, az) = h.PointAt(c.StationSt);
            double azOut = Math.Atan2(next.X - pi.X, next.Y - pi.Y);
            Assert.Equal(pi.X + c.TangentLength * Math.Sin(azOut), x, 6);
            Assert.Equal(pi.Y + c.TangentLength * Math.Cos(azOut), y, 6);
            Assert.Equal(0, HorizontalAlignment.NormalizeAngle(az - azOut), 9);
        }
        // simple curve: T = R tan(Δ/2), L = RΔ
        var simple = h.Curves[1];
        Assert.Equal(400 * Math.Tan(Math.Abs(simple.DeflectionDeg) * Math.PI / 360), simple.TangentLength, 9);
        Assert.Equal(400 * Math.Abs(simple.DeflectionDeg) * Math.PI / 180, simple.CurveLength, 9);
        Assert.Equal(45, simple.DeflectionDeg, 9);           // azimuth 45° → 90°: a right turn
        // stationing and the inverse
        Assert.Equal(1000, h.StartStation);
        foreach (var (sta, off) in new[] { (1100.0, 0.0), (1420.0, 7.5), (1555.5, -12.0), (1900.0, 3.0) })
        {
            var (x, y, _) = h.PointAt(sta, off);
            var (s2, o2) = h.StationOffset(x, y);
            Assert.Equal(sta, s2, 6);
            Assert.Equal(off, o2, 6);
        }
        Assert.Equal(h.Length, h.ToLineString(0.5).Length, 1);
        Assert.Equal("1+200.00", HorizontalAlignment.FormatStation(1200));
        Assert.Equal(7, h.ElementsFrame().RowCount);   // T, S, C, S, T, C, T
    }

    [Fact]
    public void VerticalAlignment_TextbookCrestCurve()
    {
        // g1 = +3 %, g2 = -2 %, L = 200 m, PVI 1+000 at 100 m
        var v = new VerticalAlignment([new Pvi(0, 70), new Pvi(1000, 100, 200), new Pvi(2000, 80)]);
        Assert.Equal(97, v.ElevationAt(900), 9);
        Assert.Equal(98.1875, v.ElevationAt(950), 9);
        Assert.Equal(98.8, v.ElevationAt(1020), 9);      // high point at x = g1·L/(g1 - g2) = 120 m
        Assert.Equal(0, v.GradeAt(1020), 12);
        var t = v.CurveTable();
        Assert.Equal(40, t["k"].GetDouble(0), 9);        // K = L / |A| = 200 / 5
        Assert.Equal("crest", t["type"].GetValue(0));
        Assert.Equal(1020, t["turning_station"].GetDouble(0), 9);
    }

    private static Raster.Raster FlatGround(double z, double size = 800, double cell = 1)
    {
        int n = (int)(size / cell);
        var band = Enumerable.Repeat((float)z, n * n).ToArray();
        return new Raster.Raster(n, n, [band], new Raster.GeoTransform(-size / 2, cell, 0, size / 2, 0, -cell));
    }

    [Theory]
    [InlineData(103.0)]   // fill 3 m
    [InlineData(97.0)]    // cut 3 m
    public void Corridor_FlatGround_TrapezoidalPrism(double profileZ)
    {
        var h = new HorizontalAlignment([(-250.0, 0.0), (250.0, 0.0)], []);
        var v = new VerticalAlignment([new Pvi(0, profileZ), new Pvi(500, profileZ)]);
        var template = new CorridorTemplate { Left = [new(3.5, 0)], Right = [new(3.5, 0)], CutSlope = 2, FillSlope = 2 };
        var corridor = new Corridor(h, v, template, FlatGround(100));
        var sec = corridor.Section(250);
        // top width 7, height 3, side slopes 1V:2H, bottom 19, area (7 + 19) / 2 · 3 = 39
        Assert.Equal(39, profileZ > 100 ? sec.FillArea : sec.CutArea, 6);
        Assert.Equal(0, profileZ > 100 ? sec.CutArea : sec.FillArea, 9);
        Assert.Equal(-9.5, sec.LeftCatch, 6);
        Assert.Equal(9.5, sec.RightCatch, 6);
        var vol = corridor.Volumes(25);
        Assert.Equal(39 * 500, profileZ > 100 ? vol["cum_fill"].GetDouble(vol.RowCount - 1) : vol["cum_cut"].GetDouble(vol.RowCount - 1), 3);
        // grid method on the rasterised design surface agrees with the prism
        var design = corridor.DesignSurface(25);
        var ew = Earthwork.Volume(FlatGround(100), design);
        Assert.True(Math.Abs((profileZ > 100 ? ew.Fill : ew.Cut) - 39 * 500) < 0.01 * 39 * 500);   // cell discretisation at the catch lines
        Assert.Equal(19 * 500, corridor.Footprint(25).Area, 0);
        Assert.Equal(5, corridor.FeatureLines(50).RowCount);
    }

    [Fact]
    public void Earthwork_BalancedPadOnAPlane()
    {
        int n = 200;
        var band = new float[n * n];
        for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) band[r * n + c] = (float)(50 + 0.05 * (c + 0.5));   // z = 50 + 0.05·x
        var ground = new Raster.Raster(n, n, [band], new Raster.GeoTransform(0, 1, 0, n, 0, -1));
        var pad = Geometry.Geo.Box(40, 40, 120, 120);
        double z = Earthwork.BalancedPadElevation(ground, pad);
        Assert.Equal(50 + 0.05 * 80, z, 4);          // mean of the plane over x in [40, 120]
        var r1 = Earthwork.LevelPad(ground, pad, z);
        Assert.Equal(r1.Cut, r1.Fill, 1);
        Assert.Equal(0.5 * 40 * 2 * 80, r1.Cut, 0);   // a wedge: half of 40 m run, 2 m rise, 80 m wide
    }

    [Fact]
    public void Subdivision_EqualAreasAndTargetLots()
    {
        var rect = Geometry.Geo.Box(0, 0, 100, 40);
        var lots = Subdivision.Split(rect, 4);
        Assert.Equal(4, lots.RowCount);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(1000, lots["area"].GetDouble(i), 6));
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(25, lots["frontage_m"].GetDouble(i), 6));
        var irregular = (Polygon)Geometry.Geo.FromWkt("POLYGON ((0 0, 120 10, 110 70, 30 90, -10 50, 0 0))");
        var parts = Subdivision.Split(irregular, 5, frontageAzimuthDeg: 30);
        Assert.All(Enumerable.Range(0, 5), i => Assert.Equal(irregular.Area / 5, parts["area"].GetDouble(i), 4));
        Assert.Equal(irregular.Area, parts.Geometry.Sum(g => g!.Area), 6);
        var byArea = Subdivision.SplitByArea(rect, 900);   // 4 lots of 900 and a 400 remainder merged into the last lot
        Assert.Equal(4, byArea.RowCount);
        Assert.Equal(1300, byArea["area"].GetDouble(3), 6);
    }
}
