using System.Text.Json;
using System.Text.Json.Nodes;
using GeoPandas.Net.Arrow;
using GeoPandas.Net.Geometry.Native;
using GeoPandas.Net.PointCloud;
using GeoPandas.Net.Raster;
using GeoPandas.Net.RemoteSensing;
using GeoPandas.Net.Survey;
using NetTopologySuite.Geometries;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.Tests;

/// <summary>Phase 3 (projections, EPSG, grids, geoids, surveying, GNSS) and the v0.3 format follow-ups. References come from PROJ 9.8 / pyproj.</summary>
public sealed class Phase3Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gpdnet-p3-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);
    private string P(string n) => Path.Combine(_dir, n);

    // ------------------------------------------------------------------ projections & EPSG

    public static IEnumerable<object[]> ProjectionCases() =>
        JsonNode.Parse(File.ReadAllText(Data("projections_reference.json")))!.AsArray()
            .SelectMany(n => new[] { "wkt", "projjson", "proj4" }.Where(f => (n![f]?.GetValue<string>() ?? "").Length > 0).Select(f => new object[] { n!["code"]!.GetValue<string>(), f }));

    [Theory]
    [MemberData(nameof(ProjectionCases))]
    public void Projections_MatchProj(string code, string form)
    {
        var node = JsonNode.Parse(File.ReadAllText(Data("projections_reference.json")))!.AsArray().First(n => n!["code"]!.GetValue<string>() == code)!;
        string text = node[form]!.GetValue<string>();
        // strip the authority so the definition itself is parsed (not the EPSG registry)
        text = text.Replace("AUTHORITY[\"EPSG\"", "AUTHORITY[\"X\"").Replace("\"authority\": \"EPSG\"", "\"authority\": \"X\"").Replace("\"authority\":\"EPSG\"", "\"authority\":\"X\"");
        var crs = form == "projjson" ? ProjJson.Parse(text) : CoordinateReferenceSystem.Parse(text);
        double[] A(string k) => node[k]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        var lon = A("lon"); var lat = A("lat"); var xs = A("x"); var ys = A("y");
        for (int i = 0; i < lon.Length; i++)
        {
            crs.Projection!.Forward(lon[i] * Math.PI / 180, lat[i] * Math.PI / 180, out var x, out var y);
            Assert.Equal(xs[i], x / crs.ToMeter, 4);
            Assert.Equal(ys[i], y / crs.ToMeter, 4);
            crs.Projection.Inverse(xs[i] * crs.ToMeter, ys[i] * crs.ToMeter, out var lo, out var la);
            Assert.Equal(0, ((lo * 180 / Math.PI - lon[i]) % 360 + 540) % 360 - 180, 7);
            Assert.Equal(lat[i], la * 180 / Math.PI, 7);
        }
    }

    [Fact]
    public void EpsgDataset_MatchesProj_ForAllSampledCodes()
    {
        var items = JsonNode.Parse(File.ReadAllText(Data("epsg_reference.json")))!.AsArray();
        var worst = new List<(int Code, double Err)>();
        foreach (var it in items)
        {
            int code = it![0]!.GetValue<int>();
            var crs = CoordinateReferenceSystem.FromEpsg(code);
            for (int k = 0; k < 2; k++)
            {
                double lon = it[1]![k]!.GetValue<double>(), lat = it[2]![k]!.GetValue<double>(), ex = it[3]![k]!.GetValue<double>(), ey = it[4]![k]!.GetValue<double>();
                if (double.IsInfinity(ex)) continue;
                crs.Projection!.Forward(lon * Math.PI / 180, lat * Math.PI / 180, out var x, out var y);
                double err = Math.Max(Math.Abs(x / crs.ToMeter - ex), Math.Abs(y / crs.ToMeter - ey)) * crs.ToMeter;
                if (err > 0.01) worst.Add((code, err));
            }
        }
        Assert.True(items.Count > 700);
        Assert.Empty(worst); // all within 1 cm (Palestine Cassini is the only one between 1 mm and 1 cm)
    }

    [Fact]
    public void EpsgDataset_SearchAreaAndCount()
    {
        Assert.True(EpsgDatabase.Count > 5500);
        Assert.Contains("EPSG", EpsgDatabase.Version);
        var tm3 = CrsRegistry.Search("Indonesia TM-3");
        Assert.True(tm3.Count >= 16);
        var here = CrsRegistry.AtLocation(106.8, -6.2);
        Assert.Contains(here, e => e.Code == 32748);
        Assert.Contains(here, e => e.Code == 23834);
        var rd = CoordinateReferenceSystem.FromEpsg(28992);
        Assert.IsType<ObliqueStereographic>(rd.Projection);
        Assert.NotNull(rd.AreaOfUse);
        Assert.Equal("Amersfoort / RD New", rd.Name);
    }

    // ------------------------------------------------------------------ NTv2 & geoids

    private static ShiftSubgrid MakeSubgrid(string name, string? parent, double w, double e, double s, double n, double inc, double amp)
    {
        int rows = (int)Math.Round((n - s) / inc) + 1, cols = (int)Math.Round((e - w) / inc) + 1;
        var lat = new float[rows * cols]; var lon = new float[rows * cols];
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            {
                double x = w + c * inc, y = s + r * inc;
                lat[r * cols + c] = (float)(amp * Math.Sin(x * 0.7) + 0.3 * y);
                lon[r * cols + c] = (float)(-amp * Math.Cos(y * 0.9) + 0.05 * x);
            }
        return new ShiftSubgrid(name, parent, s, n, w, e, inc, inc, lat, lon);
    }

    [Fact]
    public void Ntv2_MatchesProjHgridshift()
    {
        var g = new HorizontalGridShift("test", [MakeSubgrid("PARENT", null, 100, 110, -10, 0, 0.5, 3), MakeSubgrid("CHILD", "PARENT", 104, 106, -7, -5, 0.1, 3.4)], "TESTF", "TESTT");
        g.WriteNtv2(P("test.gsb"));
        var back = HorizontalGridShift.LoadNtv2(P("test.gsb"));
        Assert.Equal(2, back.Subgrids.Count);
        var r = JsonNode.Parse(File.ReadAllText(Data("grids_reference.json")))!["ntv2"]!;
        double[] A(string k) => r[k]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
        var lon = A("lon"); var lat = A("lat");
        for (int i = 0; i < lon.Length; i++)
        {
            double a = lon[i] * Math.PI / 180, b = lat[i] * Math.PI / 180;
            back.Forward(ref a, ref b);
            Assert.Equal(A("fwd_lon")[i], a * 180 / Math.PI, 1e-9); // ~0.1 mm
            Assert.Equal(A("fwd_lat")[i], b * 180 / Math.PI, 1e-9);
            a = lon[i] * Math.PI / 180; b = lat[i] * Math.PI / 180;
            back.Inverse(ref a, ref b);
            Assert.Equal(A("inv_lon")[i], a * 180 / Math.PI, 1e-9);
            Assert.Equal(A("inv_lat")[i], b * 180 / Math.PI, 1e-9);
        }
        // as a datum (+nadgrids) in a CRS transformation
        var src = CoordinateReferenceSystem.Parse($"+proj=longlat +ellps=bessel +nadgrids={P("test.gsb")}");
        var (x, y) = CoordinateTransformer.Create(src, CoordinateReferenceSystem.FromEpsg(32748)).Transform(105.3, -6.2);
        Assert.Equal(533270.6846490311, x, 3);
        Assert.Equal(9314516.687171366, y, 3);
    }

    [Fact]
    public void Geoids_GtxIsgAndEgm96_MatchProj()
    {
        int cols = 41, rows = 31; var v = new float[cols * rows];
        for (int rr = 0; rr < rows; rr++) for (int c = 0; c < cols; c++) v[rr * cols + c] = (float)(20 + 5 * Math.Sin(c * 0.3) + 3 * Math.Cos(rr * 0.2));
        var g = new GeoidModel("test", 100, -10, 0.25, 0.25, cols, rows, v);
        g.WriteGtx(P("test.gtx")); g.WriteIsg(P("test.isg"));
        var gtx = GeoidModel.Load(P("test.gtx")); var isg = GeoidModel.Load(P("test.isg"));
        var r = JsonNode.Parse(File.ReadAllText(Data("grids_reference.json")))!;
        var lon = r["gtx"]!["lon"]!.AsArray(); var lat = r["gtx"]!["lat"]!.AsArray(); var n = r["gtx"]!["n"]!.AsArray();
        for (int i = 0; i < lon.Count; i++)
        {
            Assert.Equal(n[i]!.GetValue<double>(), gtx.Undulation(lon[i]!.GetValue<double>(), lat[i]!.GetValue<double>()), 6);
            Assert.Equal(n[i]!.GetValue<double>(), isg.Undulation(lon[i]!.GetValue<double>(), lat[i]!.GetValue<double>()), 3);
        }
        foreach (var p in r["egm96"]!.AsArray())
            Assert.InRange(GeoidModel.Egm96Coarse.Undulation(p!["lon"]!.GetValue<double>(), p["lat"]!.GetValue<double>()) - p["n"]!.GetValue<double>(), -1.5, 1.5);
        // compound CRS: orthometric UTM heights → WGS 84 ellipsoidal (pyproj: 121.79141665033191)
        var crs = CoordinateReferenceSystem.Parse($"+proj=utm +zone=48 +south +datum=WGS84 +geoidgrids={P("test.gtx")}");
        double x = 700000, y = 9300000, z = 100;
        CoordinateTransformer.Create(crs, CoordinateReferenceSystem.Wgs84).Transform(ref x, ref y, ref z);
        Assert.Equal(121.79141665033191, z, 6);
    }

    // ------------------------------------------------------------------ surveying

    [Fact]
    public void Traverse_ClosedLoop_BowditchClosesExactly()
    {
        // 100 m square, clockwise; small angle and distance errors
        var legs = new List<TraverseLeg>
        {
            new("B", 100.02, AngleRight: 90.0 + 5.0 / 3600),
            new("C", 99.98, AngleRight: 270.0 - 4.0 / 3600),
            new("D", 100.03, AngleRight: 270.0 + 3.0 / 3600),
            new("A", 99.99, AngleRight: 270.0 - 2.0 / 3600),
        };
        var t = Traverse.Compute("A", 1000, 1000, backsightAzimuth: 270, legs, closingAngleRight: 270.0, adjustment: TraverseAdjustment.Bowditch);
        Assert.Equal(4, t.Stations.RowCount);
        Assert.True(t.PrecisionRatio > 3000);
        Assert.Equal(1000, t.Stations["e"].GetDouble(0), 9);
        // adjusted legs sum to zero (the loop closes)
        Assert.Equal(0, t.Legs["d_e"].ToDoubleArray().Zip(t.Legs["corr_e"].ToDoubleArray(), (a, b) => a + b).Sum(), 9);
        Assert.Equal(0, t.Legs["d_n"].ToDoubleArray().Zip(t.Legs["corr_n"].ToDoubleArray(), (a, b) => a + b).Sum(), 9);
        Assert.True(Math.Abs(t.AngularMisclosureSeconds) < 30);
        Assert.Equal(4, t.LegLines().RowCount);
    }

    [Fact]
    public void Cogo_IntersectionsAndResection_RecoverTruth()
    {
        var p = Cogo.BearingBearing(0, 0, 45, 100, 0, 315)!.Value;
        Assert.Equal(50, p.E, 9); Assert.Equal(50, p.N, 9);
        var dd = Cogo.DistanceDistance(0, 0, 5, 8, 0, 5);
        Assert.Equal(2, dd.Count);
        Assert.Contains(dd, s => Math.Abs(s.E - 4) < 1e-9 && Math.Abs(Math.Abs(s.N) - 3) < 1e-9);
        // resection: station at (37, 52), three known points
        (double E, double N) st = (37, 52), a = (0, 200), b = (180, 140), c = (90, -60);
        double Dir((double E, double N) q) => Angles.Inverse(st.E, st.N, q.E, q.N).Azimuth - 17.5; // arbitrary zero
        var r = Cogo.Resection(a, b, c, Dir(a), Dir(b), Dir(c));
        Assert.Equal(37, r.E, 6); Assert.Equal(52, r.N, 6);
        Assert.Equal("123°45'06.7\"", Angles.ToDms(Angles.ParseDms("123 45 6.7")));
    }

    private static (AdjustmentNetwork Net, Dictionary<string, (double E, double N)> Truth) TrilaterationNetwork(double noise, int seed)
    {
        var truth = new Dictionary<string, (double, double)> { ["A"] = (1000, 1000), ["B"] = (1600, 1050), ["C"] = (1550, 1520), ["D"] = (980, 1480), ["P"] = (1300, 1250), ["Q"] = (1250, 1400) };
        var rng = new Random(seed);
        double G() => noise * Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var net = new AdjustmentNetwork();
        net.AddFixed("A", 1000, 1000).AddFixed("B", 1600, 1050).AddFixed("C", 1550, 1520).AddFixed("D", 980, 1480);
        net.AddStation("P", 1290, 1260).AddStation("Q", 1260, 1390);
        foreach (var (f, t) in new[] { ("A", "P"), ("B", "P"), ("C", "P"), ("D", "P"), ("A", "Q"), ("C", "Q"), ("D", "Q"), ("P", "Q") })
        {
            var (az, d) = Angles.Inverse(truth[f].Item1, truth[f].Item2, truth[t].Item1, truth[t].Item2);
            net.Distance(f, t, d + G() * 0.003, 0.003);
            net.Azimuth(f, t, az + G() * 2 / 3600, 2);
        }
        return (net, truth);
    }

    [Fact]
    public void LeastSquares2D_RecoversTruth_AndPassesChiSquare()
    {
        var (exact, truth) = TrilaterationNetwork(0, 1);
        var r0 = exact.Adjust2D();
        foreach (var id in new[] { "P", "Q" })
        {
            int i = Enumerable.Range(0, r0.Stations.RowCount).First(k => (string)r0.Stations["id"].GetValue(k)! == id);
            Assert.Equal(truth[id].E, r0.Stations["e"].GetDouble(i), 6);
            Assert.Equal(truth[id].N, r0.Stations["n"].GetDouble(i), 6);
        }
        var (noisy, _) = TrilaterationNetwork(1, 7);
        var r = noisy.Adjust2D();
        Assert.Equal(16 - 4, r.DegreesOfFreedom);
        Assert.True(r.GlobalTestPassed, r.ToString());
        Assert.InRange(r.VarianceFactor, 0.2, 3);
        Assert.True(r.Stations["ellipse_a"].Max() > 0);
        Assert.Equal(6, r.ErrorEllipses().RowCount);

        // a gross error (0.5 m) on one distance is flagged by data snooping
        var (bad, _) = TrilaterationNetwork(1, 7);
        bad.Distance("B", "Q", Angles.Inverse(1600, 1050, 1250, 1400).Distance + 0.5, 0.003);
        var rb = bad.Adjust2D();
        Assert.False(rb.GlobalTestPassed);
        Assert.NotEmpty(rb.Outliers);
    }

    [Fact]
    public void Leveling_LoopAdjustment()
    {
        var net = new AdjustmentNetwork().AddBenchmark("BM1", 100.000).AddHeightPoint("X").AddHeightPoint("Y");
        net.HeightDifference("BM1", "X", 5.012, AdjustmentNetwork.LevelingSigma(3, 1.0));
        net.HeightDifference("X", "Y", -2.003, AdjustmentNetwork.LevelingSigma(3, 1.0));
        net.HeightDifference("Y", "BM1", -3.003, AdjustmentNetwork.LevelingSigma(3, 1.0));
        var r = net.AdjustLeveling();
        // misclosure +6 mm distributed equally (equal weights): each dh corrected by −2 mm
        var h = Enumerable.Range(0, r.Stations.RowCount).ToDictionary(i => (string)r.Stations["id"].GetValue(i)!, i => r.Stations["h"].GetDouble(i));
        Assert.Equal(105.010, h["X"], 6);
        Assert.Equal(103.005, h["Y"], 6);
        Assert.Equal(1, r.DegreesOfFreedom);
    }

    // ------------------------------------------------------------------ GNSS

    [Fact]
    public void Gnss_SinglePointPositioning_RealRinex()
    {
        var obs = RinexObservation.Read(Data("14601736.18o"));
        var nav = RinexNavigation.Read(Data("14601736.18n"));
        Assert.Equal(3, obs.Epochs.Count);
        Assert.NotNull(nav.IonoAlpha);
        var eph = nav.Ephemerides[0];
        var (x, y, z, _) = Gnss.SatellitePosition(eph, eph.Toe);
        Assert.InRange(Math.Sqrt(x * x + y * y + z * z), 26.4e6, 26.7e6); // GPS orbit radius
        var sol = Gnss.SinglePointPositioning(obs, nav);
        Assert.Equal(3, sol.RowCount);
        var ap = obs.ApproxPosition!.Value;
        for (int i = 0; i < sol.RowCount; i++)
        {
            double d = Math.Sqrt(Math.Pow(sol["x"].GetDouble(i) - ap.X, 2) + Math.Pow(sol["y"].GetDouble(i) - ap.Y, 2) + Math.Pow(sol["z"].GetDouble(i) - ap.Z, 2));
            Assert.True(d < 10, $"epoch {i}: {d:F2} m from the receiver's position");
        }
        Assert.Equal(4326, sol.Crs!.Epsg);
        Assert.InRange(Gnss.Klobuchar(nav.IonoAlpha!, nav.IonoBeta!, -33.8, 151.1, 45, 30, 22000), 0.5, 30);
    }

    // ------------------------------------------------------------------ v0.3 format follow-ups

    [Theory]
    [InlineData("curv_nc4.nc")]
    [InlineData("curv_cdf2.nc")]
    public void NetCdf_WindowsAndCurvilinear(string file)
    {
        using var nc = NetCdfDataset.Open(Data(file));
        var w = nc.ReadWindow("sst", [1, 5, 10], [1, 4, 4]);
        Assert.Equal(16, w.Length);
        Assert.Equal(13.5, w[0], 4); Assert.Equal(15.3, w[15], 4);
        Assert.NotNull(nc.CurvilinearCoordinates("sst"));
        var pts = nc.ToPoints("sst", 2);
        Assert.Equal(600, pts.RowCount);
        int k = Enumerable.Range(0, pts.RowCount).First(i => pts["row"].GetDouble(i) == 7 && pts["col"].GetDouble(i) == 12);
        Assert.Equal(24.7, pts["sst"].GetDouble(k), 4);
        Assert.Equal(106.99, pts.Geometry[k]!.Coordinate.X, 6);
        Assert.Equal(-7.06, pts.Geometry[k]!.Coordinate.Y, 6);
        var grid = nc.Regrid("sst", 0.05, null, 2);
        Assert.Equal(24.7, grid.Sample(106.99, -7.06), 3);
    }

    [Theory]
    [InlineData("native_point.parquet", 4)]
    [InlineData("native_polygon.parquet", 3)]
    [InlineData("native_multipolygon.parquet", 3)]
    public void GeoParquet_NativeGeoArrow_ReadsGeoPandas_AndRoundTrips(string file, int rows)
    {
        var info = GeoParquet.ReadInfo(Data(file));
        Assert.NotEqual("WKB", info.Encoding, StringComparer.OrdinalIgnoreCase);
        var g = GeoParquet.Read(Data(file));
        Assert.Equal(rows, g.RowCount);
        Assert.Contains(g.Geometry, x => x is null);
        GeoParquet.Write(g, P("n.parquet"), new GeoParquetWriteOptions { Encoding = GeoParquetEncoding.Native, RowGroupSize = 2 });
        var back = GeoParquet.Read(P("n.parquet"));
        Assert.Equal(info.Encoding, GeoParquet.ReadInfo(P("n.parquet")).Encoding);
        Assert.True(g.Geometry.Zip(back.Geometry).All(p => p.First is null ? p.Second is null : p.First.EqualsExact(p.Second)));
        if (file.Contains("polygon"))
            Assert.Contains(g.Geometry, x => x is Polygon { NumInteriorRings: 1 } || x is MultiPolygon mp && mp.Geometries.Any(q => ((Polygon)q).NumInteriorRings == 1));
    }

    [Fact]
    public void Laz14_AndCopc_WithNativeCodec()
    {
        if (!NativeKernels.SupportsLaz) return; // managed fallback run: laszip.net covers formats 0–5 only
        var l14 = Las.Read(Data("1_4_w_evlr.laz"));
        Assert.Equal(1000, l14.Count);
        Assert.Equal(6, l14.Source!.PointFormat);
        Assert.Equal(1694510.387, l14.X[0], 3);
        Assert.Equal(5597520.533, l14.Z.Sum(), 2);

        var full = Las.Read(Data("simple.copc.laz"));
        Assert.Equal(1065, full.Count);
        var info = Copc.ReadInfo(Data("simple.copc.laz"));
        Assert.Equal(65, info.Nodes.Count);
        var bb = full.Envelope2D;
        var box = new Envelope(bb.MinX, bb.MinX + bb.Width / 2, bb.MinY, bb.MinY + bb.Height / 2);
        var q = Copc.Read(Data("simple.copc.laz"), box);
        Assert.Equal(Enumerable.Range(0, full.Count).Count(i => box.Contains(full.X[i], full.Y[i])), q.Count);
        Assert.True(Copc.Read(Data("simple.copc.laz"), maxDepth: 0).Count < full.Count);
    }

    [Fact]
    public void Sentinel2Safe_ReflectanceMaskAndNdvi()
    {
        var s2 = Sentinel2Product.Open(Directory.GetDirectories(Data(""), "*.SAFE")[0]);
        Assert.True(s2.IsLevel2A);
        Assert.Equal("05.10", s2.ProcessingBaseline);
        Assert.Equal(32748, s2.Crs!.Epsg);
        var red = s2.Reflectance("B04", 10);
        Assert.Equal((1000 + 2000 + 60 + 5 - 1000) / 10000.0, red[0, 1, 2], 5);
        Assert.True(float.IsNaN(red[0, 0, 0]));
        Assert.Equal(699960, red.Transform.OriginX);
        var mask = s2.ClearMask();
        Assert.Equal(0, mask[0, 0, 0]);
        Assert.Equal(1, mask[0, 15, 15]);
        Assert.Equal(0.2898, s2.Ndvi()[0, 5, 5], 4);
    }

    [Fact]
    public void LandsatC2_ScalingQaAndNdvi_AndStac()
    {
        var dem = Datasets.SampleData.Dem(30, 20);
        float[] Fill(Func<int, float> f) => Enumerable.Range(0, 600).Select(f).ToArray();
        void Tif(string name, float[] v) => GeoTiff.Write(new RasterGrid(30, 20, [v], dem.Transform, dem.Crs), P(name));
        Tif("LC09_L2SP_122064_20240110_20240111_02_T1_SR_B4.TIF", Fill(i => 10000 + i));
        Tif("LC09_L2SP_122064_20240110_20240111_02_T1_SR_B5.TIF", Fill(i => 20000 + i));
        Tif("LC09_L2SP_122064_20240110_20240111_02_T1_ST_B10.TIF", Fill(_ => 44000));
        Tif("LC09_L2SP_122064_20240110_20240111_02_T1_QA_PIXEL.TIF", Fill(i => i == 5 ? (1 << 3) : 21824));
        File.WriteAllText(P("LC09_L2SP_122064_20240110_20240111_02_T1_MTL.txt"), """
            GROUP = LANDSAT_METADATA_FILE
              GROUP = PRODUCT_CONTENTS
                LANDSAT_PRODUCT_ID = "LC09_L2SP_122064_20240110_20240111_02_T1"
                PROCESSING_LEVEL = "L2SP"
                FILE_NAME_BAND_4 = "LC09_L2SP_122064_20240110_20240111_02_T1_SR_B4.TIF"
                FILE_NAME_BAND_5 = "LC09_L2SP_122064_20240110_20240111_02_T1_SR_B5.TIF"
                FILE_NAME_BAND_ST_B10 = "LC09_L2SP_122064_20240110_20240111_02_T1_ST_B10.TIF"
                FILE_NAME_QUALITY_L1_PIXEL = "LC09_L2SP_122064_20240110_20240111_02_T1_QA_PIXEL.TIF"
              END_GROUP = PRODUCT_CONTENTS
              GROUP = IMAGE_ATTRIBUTES
                SPACECRAFT_ID = "LANDSAT_9"
                SENSOR_ID = "OLI_TIRS"
                DATE_ACQUIRED = 2024-01-10
                SCENE_CENTER_TIME = "02:59:41.1234560Z"
                CLOUD_COVER = 12.34
                SUN_ELEVATION = 60.1
              END_GROUP = IMAGE_ATTRIBUTES
              GROUP = LEVEL2_SURFACE_REFLECTANCE_PARAMETERS
                REFLECTANCE_MULT_BAND_4 = 2.75E-05
                REFLECTANCE_ADD_BAND_4 = -0.200000
                REFLECTANCE_MULT_BAND_5 = 2.75E-05
                REFLECTANCE_ADD_BAND_5 = -0.200000
              END_GROUP = LEVEL2_SURFACE_REFLECTANCE_PARAMETERS
              GROUP = LEVEL2_SURFACE_TEMPERATURE_PARAMETERS
                TEMPERATURE_MULT_BAND_ST_B10 = 0.00341802
                TEMPERATURE_ADD_BAND_ST_B10 = 149.000000
              END_GROUP = LEVEL2_SURFACE_TEMPERATURE_PARAMETERS
            END_GROUP = LANDSAT_METADATA_FILE
            END
            """);
        var scene = LandsatScene.Open(_dir);
        Assert.True(scene.IsOli && scene.IsLevel2);
        Assert.Equal(new DateTime(2024, 1, 10, 2, 59, 41, DateTimeKind.Utc), scene.Acquired);
        Assert.Equal(10000 * 2.75e-5 - 0.2, scene.Reflectance(4)[0, 0, 0], 5);
        Assert.Equal(44000 * 0.00341802 + 149, scene.SurfaceTemperature()[0, 0, 0], 2);
        var ndvi = scene.Ndvi();
        Assert.True(float.IsNaN(ndvi[0, 0, 5]));            // cloud bit set
        double nir = 20000 * 2.75e-5 - 0.2, red = 10000 * 2.75e-5 - 0.2;
        Assert.Equal((nir - red) / (nir + red), ndvi[0, 0, 0], 4);

        // STAC item over the same files
        File.WriteAllText(P("item.json"), """
            {"type":"Feature","stac_version":"1.0.0","id":"LC09_test","collection":"landsat-c2-l2",
             "geometry":{"type":"Polygon","coordinates":[[[106,-7],[107,-7],[107,-6],[106,-6],[106,-7]]]},
             "properties":{"datetime":"2024-01-10T02:59:41Z","eo:cloud_cover":12.34,"proj:epsg":32748},
             "assets":{"red":{"href":"LC09_L2SP_122064_20240110_20240111_02_T1_SR_B4.TIF","type":"image/tiff; application=geotiff","roles":["data"]}}}
            """);
        var item = StacItem.Load(P("item.json"));
        Assert.Equal(32748, item.Epsg);
        Assert.Equal(12.34, item.CloudCover);
        Assert.Equal(1.0, item.Footprint!.Area, 9);
        Assert.Equal(10000, item.ReadAsset("red")[0, 0, 0]);
    }

    [Fact]
    public void ModisGridAndViirsHdfEos5()
    {
        Assert.Equal((28, 9), ModisGrid.TileFor(106.8, -6.2));
        var t = ModisGrid.TileTransform(28, 9, 1200);
        Assert.Equal(926.625433, t.PixelWidth, 5);
        var v = ModisGrid.ReadHdfEos5(Data("viirs_hdfeos5_subset.h5"), "SurfReflect_I1_1");
        Assert.True(float.IsNaN(v[0, 0, 0]));
        Assert.Equal(0.005, v[0, 0, 1], 6);
        Assert.Equal(143 * 50 * 1e-4, v[0, 11, 11], 5);
        Assert.Equal(11119505.196667, v.Transform.OriginX, 3);
        Assert.Equal("sinu", v.Crs!.Projection!.ProjName);
    }
}
