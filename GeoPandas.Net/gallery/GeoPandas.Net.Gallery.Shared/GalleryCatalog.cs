using System.Diagnostics;
using GeoPandas.Net.AI;
using GeoPandas.Net.Analytics;
using GeoPandas.Net.Arrow;
using GeoPandas.Net.CAD;
using GeoPandas.Net.Data;
using GeoPandas.Net.Data.Versioning;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.PointCloud;
using GeoPandas.Net.Versioning;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Network;
using GeoPandas.Net.Plotting;
using GeoPandas.Net.Raster;
using GeoPandas.Net.Survey;
using NetTopologySuite.Geometries;
using GeoPandas.Net.Geometry.Crs;
using RasterGrid = GeoPandas.Net.Raster.Raster;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Gallery;

public enum Lang { En, Id }

/// <summary>Bilingual text.</summary>
public readonly record struct L10n(string En, string Id)
{
    public string this[Lang l] => l == Lang.Id ? Id : En;
}

public sealed record GalleryCategory(string Key, L10n Name, string Glyph);

/// <summary>Output of running a sample: a map, an optional table and key figures.</summary>
public sealed record SampleResult(MapScene Scene, DataFrame? Table, IReadOnlyList<(L10n Label, string Value)> Figures, TimeSpan Elapsed);

public sealed record GallerySample(string Id, string Category, L10n Title, L10n Summary, string Code, Func<SampleResult> Build)
{
    private SampleResult? _cached;
    private readonly Lock _lock = new();

    /// <summary>Runs the sample (cached) and measures the elapsed time.</summary>
    public SampleResult Run()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;
            var sw = Stopwatch.StartNew();
            var r = Build();
            return _cached = r with { Elapsed = sw.Elapsed };
        }
    }
}

/// <summary>All gallery samples. Both the Blazor and Avalonia galleries render from this catalog.</summary>
public static class GalleryCatalog
{
    public const string Credit = "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil";
    public const string CreditEn = "Made by Gravicode Studios, led by Kang Fadhil";

    public static IReadOnlyList<GalleryCategory> Categories { get; } =
    [
        new("gis", new("GIS & vector", "GIS & vektor"), "◇"),
        new("data", new("Data & formats", "Data & format"), "⧉"),
        new("stats", new("Spatial statistics", "Statistik spasial"), "∴"),
        new("network", new("Networks", "Jaringan"), "⋈"),
        new("raster", new("Raster & terrain", "Raster & terrain"), "▦"),
        new("civil", new("Civil engineering", "Teknik sipil"), "≋"),
        new("rs", new("Remote sensing", "Penginderaan jauh"), "◎"),
        new("cad", new("CAD integration", "Integrasi CAD"), "⌗"),
        new("survey", new("Surveying & geodesy", "Survei & geodesi"), "⌖"),
        new("ai", new("Spatial AI", "AI spasial"), "✶"),
    ];

    // shared, lazily created data
    private static readonly Lazy<GeoDataFrame> s_districts = new(() => SampleData.Districts());
    private static readonly Lazy<GeoDataFrame> s_pois = new(() => SampleData.PointsOfInterest());
    private static readonly Lazy<GeoDataFrame> s_roads = new(() => SampleData.Roads());
    private static readonly Lazy<RasterGrid> s_dem = new(() => SampleData.Dem());
    private static GeoDataFrame Districts => s_districts.Value;
    private static GeoDataFrame Pois => s_pois.Value;
    private static GeoDataFrame Roads => s_roads.Value;
    private static RasterGrid Dem => s_dem.Value;

    private static readonly LayerStyle Base = new() { Fill = "#e3e8df", Stroke = "#9aa79a", StrokeWidth = 0.7, FillOpacity = 1 };
    private static readonly LayerStyle RoadStyle = new() { Fill = null, Stroke = "#8d99a6", StrokeWidth = 0.8 };

    private static MapScene Scene(string? title = null, string? subtitle = null) => new()
    {
        Title = title, Subtitle = subtitle, ShowFrame = true, Padding = 46, Background = "#f7f9f5",
        FontFamily = "'Public Sans', Inter, 'Segoe UI', sans-serif"
    };

    private static (L10n, string) F(string en, string id, string value) => (new L10n(en, id), value);

    public static IReadOnlyList<GallerySample> Samples { get; } =
    [
        // ---------------------------------------------------------------- GIS
        new("choropleth", "gis", new("Population choropleth", "Peta koroplet populasi"),
            new("Classify districts by population density with quantile breaks and a sequential colour map — GeoPandas' plot(column=…) in C#.",
                "Klasifikasi distrik berdasarkan kepadatan penduduk dengan kuantil dan colormap sekuensial — setara plot(column=…) GeoPandas di C#."),
            """
            var districts = SampleData.Districts();
            var withDensity = districts.WithColumn<double>("density",
                r => r.GetDouble("population") / r.GetDouble("area_km2"));

            withDensity.Plot(new LayerStyle
            {
                Column = "density", ColorMap = ColorMaps.YlOrRd,
                Scheme = ClassificationScheme.Quantiles, Classes = 5,
                LegendTitle = "People / km²"
            }).SaveSvg("density.svg");
            """,
            () =>
            {
                var d = Districts.WithColumn<double>("density", r => Math.Round(r.GetDouble("population") / r.GetDouble("area_km2")));
                var scene = Scene().AddLayer(d, new LayerStyle { Column = "density", ColorMap = ColorMaps.YlOrRd, LegendTitle = "People / km²", Stroke = "#ffffff", StrokeWidth = 0.9, FillOpacity = 0.95 });
                var top = d.SortBy("density", false).Head(8).Select("name", "region", "population", "area_km2", "density").Attributes();
                return new(scene, top, [F("Districts", "Distrik", d.RowCount.ToString()), F("Total population", "Total penduduk", $"{d["population"].Sum():N0}"), F("Max density", "Kepadatan maks", $"{d["density"].Max():N0} /km²")], default);
            }),

        new("sjoin", "gis", new("Spatial join: facilities per district", "Spatial join: fasilitas per distrik"),
            new("Join 400 points of interest to the district that contains them using the STR-tree index, then count by district.",
                "Gabungkan 400 titik fasilitas ke distrik yang memuatnya memakai indeks STR-tree, lalu hitung per distrik."),
            """
            var joined = pois.SJoin(districts, SpatialPredicate.Within);
            var perDistrict = joined.GroupBy("name_right")
                                    .Agg(("name_left", AggFunc.Count, "facilities"));
            var map = districts.Merge(perDistrict, "name", "name_right", JoinHow.Left);
            """,
            () =>
            {
                var joined = Pois.SJoin(Districts, SpatialPredicate.Within);
                var counts = joined.GroupBy("name_right").Agg(("name_left", AggFunc.Count, "facilities"));
                var merged = (GeoDataFrame)Districts.Merge(counts, "name", "name_right", JoinHow.Left);
                var scene = Scene().AddLayer(merged, new LayerStyle { Column = "facilities", ColorMap = ColorMaps.YlGnBu, LegendTitle = "Facilities", Stroke = "#ffffff" })
                    .AddLayer(Pois, new LayerStyle { Column = "category", Categorical = true, PointRadius = 2.6, Stroke = "#ffffff", StrokeWidth = 0.5, LegendTitle = "Category", FillOpacity = 1 });
                return new(scene, counts.SortBy("facilities", false).Head(10), [F("Points", "Titik", Pois.RowCount.ToString()), F("Matched", "Cocok", joined.RowCount.ToString())], default);
            }),

        new("nearest", "gis", new("Nearest hospital for every school", "Rumah sakit terdekat tiap sekolah"),
            new("sjoin_nearest finds the closest hospital to each school with exact geometry distance and branch-and-bound search.",
                "sjoin_nearest mencari rumah sakit terdekat untuk tiap sekolah dengan jarak geometri eksak dan pencarian branch-and-bound."),
            """
            var schools   = pois.Filter<string?>("category", c => c == "school");
            var hospitals = pois.Filter<string?>("category", c => c == "hospital");
            var nearest = schools.SJoinNearest(hospitals, distanceColumn: "distance_m");
            Console.WriteLine(nearest["distance_m"].Mean());
            """,
            () =>
            {
                var schools = Pois.Filter<string?>("category", c => c == "school");
                var hosp = Pois.Filter<string?>("category", c => c == "hospital");
                var near = schools.SJoinNearest(hosp, distanceColumn: "distance_m");
                var links = near.Rows.Select(r => (NtsGeometry?)Geo.LineString(
                    (((Point)near.Geometry[r.Index]!).X, ((Point)near.Geometry[r.Index]!).Y),
                    (((Point)hosp.Geometry[(int)(long)r["index_right"]!]!).X, ((Point)hosp.Geometry[(int)(long)r["index_right"]!]!).Y)))
                    .ToArray();
                var scene = Scene().AddLayer(Districts, Base).AddLayer(new GeoSeries(links, Districts.Crs), new LayerStyle { Stroke = "#b0306a", StrokeWidth = 0.9, ShowInLegend = false })
                    .AddLayer(schools, new LayerStyle { Fill = "#2f6f9f", PointRadius = 2.8, Stroke = "#ffffff", LegendTitle = "Schools" }, "School")
                    .AddLayer(hosp, new LayerStyle { Fill = "#b0306a", PointRadius = 5, Stroke = "#ffffff", StrokeWidth = 1.2, LegendTitle = "Hospitals" }, "Hospital");
                var table = near.Select("name_left", "name_right", "distance_m").Attributes().SortBy("distance_m", false).Head(10);
                return new(scene, table, [F("Schools", "Sekolah", schools.RowCount.ToString()), F("Mean distance", "Jarak rata-rata", $"{near["distance_m"].Mean():N0} m"), F("Worst case", "Terjauh", $"{near["distance_m"].Max():N0} m")], default);
            }),

        new("overlay", "gis", new("Transit coverage overlay", "Overlay cakupan transit"),
            new("Buffer transit stops by 600 m, dissolve them, and overlay with districts to measure the share of each district within walking distance.",
                "Buffer halte transit 600 m, dissolve, lalu overlay dengan distrik untuk mengukur porsi wilayah yang terjangkau jalan kaki."),
            """
            var stops = pois.Filter<string?>("category", c => c == "transit");
            var walk  = stops.Buffer(600).Dissolve();
            var covered = districts.Overlay(walk, OverlayHow.Intersection);
            covered = covered.WithColumn(covered.Area().Rename("covered_m2"));
            """,
            () =>
            {
                var stops = Pois.Filter<string?>("category", c => c == "transit");
                var walk = stops.Buffer(600).Select().Dissolve();
                var covered = Districts.Overlay(walk, OverlayHow.Intersection);
                var share = covered.WithColumn(covered.Area().Rename("covered_m2"))
                    .WithColumn<double>("share_pct", r => Math.Round(100 * r.GetDouble("covered_m2") / (r.GetDouble("area_km2") * 1e6), 1));
                var scene = Scene().AddLayer(Districts, Base).AddLayer(share, new LayerStyle { Column = "share_pct", ColorMap = ColorMaps.Blues, LegendTitle = "% covered", Stroke = "#ffffff", StrokeWidth = 0.5 })
                    .AddLayer(stops, new LayerStyle { Fill = "#1b2a33", PointRadius = 2.4, Stroke = "#ffffff", LegendTitle = "Transit stops" }, "Stop");
                double pct = 100 * share.Area().Sum() / Districts.Area().Sum();
                return new(scene, share.Select("name", "share_pct").Attributes().SortBy("share_pct", false).Head(10), [F("Stops", "Halte", stops.RowCount.ToString()), F("City covered", "Kota terjangkau", $"{pct:F1} %")], default);
            }),

        new("dissolve", "gis", new("Dissolve districts into regions", "Dissolve distrik menjadi wilayah"),
            new("Merge district polygons by region and sum their population — the GeoPandas dissolve(by=…, aggfunc='sum').",
                "Gabungkan poligon distrik per wilayah dan jumlahkan populasinya — dissolve(by=…, aggfunc='sum') ala GeoPandas."),
            """
            var regions = districts.Dissolve("region", AggFunc.Sum);
            regions.Plot(new LayerStyle { Column = "region", LabelColumn = "region" });
            """,
            () =>
            {
                var regions = Districts.Dissolve("region", AggFunc.Sum);
                var scene = Scene().AddLayer(regions, new LayerStyle { Column = "region", Categorical = true, LabelColumn = "region", LabelSize = 12, Stroke = "#ffffff", StrokeWidth = 2, LegendTitle = "Region" })
                    .AddLayer(Districts, new LayerStyle { Fill = null, Stroke = "#ffffff", StrokeWidth = 0.4, ShowInLegend = false });
                return new(scene, regions.Select("region", "population", "area_km2").Attributes(), [F("Districts → regions", "Distrik → wilayah", $"{Districts.RowCount} → {regions.RowCount}")], default);
            }),

        new("crs", "gis", new("Reprojection: cities of the world", "Reproyeksi: kota-kota dunia"),
            new("Load WGS 84 points and reproject to Web Mercator with the built-in CRS engine (no PROJ dependency). Symbols sized by population.",
                "Muat titik WGS 84 dan reproyeksi ke Web Mercator dengan engine CRS bawaan (tanpa PROJ). Simbol berukuran sesuai populasi."),
            """
            var cities = SampleData.Cities();            // EPSG:4326
            var merc   = cities.ToCrs("EPSG:3857");      // Web Mercator
            merc.Plot(new LayerStyle { SizeColumn = "pop_mio", LabelColumn = "name" });
            """,
            () =>
            {
                var cities = SampleData.Cities();
                var merc = cities.ToCrs(3857);
                var graticule = new List<NtsGeometry?>();
                for (int lon = -180; lon <= 180; lon += 30) graticule.Add(Geo.LineString(Enumerable.Range(-60, 141).Select(lat => new Coordinate(lon, lat))));
                for (int lat = -60; lat <= 80; lat += 20) graticule.Add(Geo.LineString(Enumerable.Range(-180, 361).Select(lon => new Coordinate(lon, lat))));
                var grid = new GeoSeries(graticule, CoordinateReferenceSystem.Wgs84).ToCrs(3857);
                var scene = Scene();
                scene.AddLayer(grid, new LayerStyle { Stroke = "#c9d3cc", StrokeWidth = 0.6, ShowInLegend = false })
                    .AddLayer(merc, new LayerStyle { Fill = "#b0306a", SizeColumn = "pop_mio", MaxPointRadius = 16, FillOpacity = 0.7, Stroke = "#ffffff", LabelColumn = "name", LabelSize = 8.5 });
                scene.ShowScaleBar = false;
                return new(scene, merc.WithColumn(merc.Geometry.X.Rename("x_3857")).WithColumn(merc.Geometry.Y.Rename("y_3857")).Attributes().Head(10),
                    [F("CRS", "CRS", "EPSG:4326 → EPSG:3857"), F("Cities", "Kota", cities.RowCount.ToString())], default);
            }),

        // ---------------------------------------------------------------- statistics
        new("hotspots", "stats", new("Hot spots of household income", "Hot spot pendapatan rumah tangga"),
            new("Getis-Ord Gi* with queen contiguity weights flags statistically significant clusters of high and low income. Global Moran's I is reported too.",
                "Getis-Ord Gi* dengan bobot queen contiguity menandai klaster pendapatan tinggi/rendah yang signifikan. Moran's I global juga dihitung."),
            """
            var w = SpatialWeights.Queen(districts);
            var moran = Autocorrelation.MoransI(districts["income_mio_idr"].ToDoubleArray(), w.RowStandardize());
            var hot = Autocorrelation.HotSpots(districts, "income_mio_idr", w, significance: 0.1);
            """,
            () =>
            {
                var w = SpatialWeights.Queen(Districts);
                var mi = Autocorrelation.MoransI(Districts["income_mio_idr"].ToDoubleArray(), w.RowStandardize(), 499);
                var hot = Autocorrelation.HotSpots(Districts, "income_mio_idr", w, 0.1);
                var scene = Scene().AddLayer(hot, new LayerStyle { Column = "gi_z", ColorMap = ColorMaps.RdBu.Reversed(), Scheme = ClassificationScheme.EqualInterval, Classes = 7, LegendTitle = "Gi* z-score", Stroke = "#ffffff" });
                return new(scene, hot.Select("name", "income_mio_idr", "gi_z", "gi_p", "hotspot").Attributes().SortBy("gi_z", false).Head(10),
                    [F("Moran's I", "Moran's I", $"{mi.Value:F3}"), F("p (permutation)", "p (permutasi)", $"{mi.PValueSimulated:F3}"), F("z-score", "skor z", $"{mi.ZScore:F2}")], default);
            }),

        new("dbscan", "stats", new("DBSCAN activity hubs", "Pusat aktivitas dengan DBSCAN"),
            new("Density-based clustering on a KD-tree discovers commercial hubs without choosing k; isolated points become noise.",
                "Klasterisasi berbasis kepadatan dengan KD-tree menemukan pusat komersial tanpa menentukan k; titik terisolasi menjadi noise."),
            """
            var hubs = Clustering.Dbscan(pois, eps: 300, minPoints: 4);   // adds a "cluster" column (-1 = noise)
            """,
            () =>
            {
                var shops = Pois;
                var cl = Clustering.Dbscan(shops, 300, 4).WithColumn<string>("cluster_label", r => (long)r["cluster"]! < 0 ? "noise" : $"hub {(long)r["cluster"]! + 1}");
                var hulls = cl.Filter<long>("cluster", c => c >= 0).Dissolve("cluster_label").ConvexHull().Buffer(120);
                var scene = Scene().AddLayer(Districts, Base)
                    .AddLayer(hulls, new LayerStyle { Column = "cluster_label", Categorical = true, FillOpacity = 0.18, Stroke = "#1b2a33", StrokeWidth = 0.8, DashArray = [4, 3], ShowInLegend = false })
                    .AddLayer(cl, new LayerStyle { Column = "cluster_label", Categorical = true, PointRadius = 3, Stroke = "#ffffff", StrokeWidth = 0.5, LegendTitle = "Cluster", FillOpacity = 1 });
                int k = hulls.RowCount;
                return new(scene, cl.GroupBy("cluster_label").Size().SortBy("size", false), [F("Points", "Titik", shops.RowCount.ToString()), F("Hubs found", "Pusat ditemukan", k.ToString())], default);
            }),

        new("kriging", "stats", new("Kriging an elevation surface", "Kriging permukaan elevasi"),
            new("Sample 150 survey points from the DEM, fit a spherical variogram and predict a continuous surface with ordinary kriging.",
                "Ambil 150 titik survei dari DEM, fit variogram spherical, lalu prediksi permukaan kontinu dengan ordinary kriging."),
            """
            var vg = Variogram.Fit(xs, ys, zs, VariogramModel.Spherical);
            var (z, variance) = Interpolation.OrdinaryKriging(xs, ys, zs, gridX, gridY, vg);
            """,
            () =>
            {
                var rng = new Random(11);
                var dem = Dem;
                var b = dem.Bounds;
                int n = 150;
                var xs = Enumerable.Range(0, n).Select(_ => b.MinX + rng.NextDouble() * b.Width).ToArray();
                var ys = Enumerable.Range(0, n).Select(_ => b.MinY + rng.NextDouble() * b.Height).ToArray();
                var zs = xs.Select((x, i) => (double)dem.Sample(x, ys[i])).ToArray();
                int gw = 96, gh = 75;
                double cell = b.Width / gw;
                var gx = new double[gw * gh]; var gy = new double[gw * gh];
                for (int r = 0; r < gh; r++) for (int c = 0; c < gw; c++) { gx[r * gw + c] = b.MinX + (c + 0.5) * cell; gy[r * gw + c] = b.MaxY - (r + 0.5) * cell; }
                var vg = Variogram.Fit(xs, ys, zs);
                var (pred, _) = Interpolation.OrdinaryKriging(xs, ys, zs, gx, gy, vg, 12);
                var surface = new RasterGrid(gw, gh, [pred.Select(v => (float)v).ToArray()], GeoTransform.NorthUp(b.MinX, b.MaxY, cell), dem.Crs);
                var pts = GeoDataFrame.FromGeometries(xs.Select((x, i) => (NtsGeometry?)Geo.Point(x, ys[i])), dem.Crs, Column.Create("z", zs));
                var scene = Scene().AddRaster(surface, ColorMaps.Terrain, legendTitle: "Elevation (m)").AddLayer(pts, new LayerStyle { Fill = "#1b2a33", PointRadius = 2, Stroke = "#ffffff", StrokeWidth = 0.6, ShowInLegend = false });
                var tbl = new DataFrame(Column.Create("parameter", new[] { "model", "nugget", "sill", "range (m)" }), Column.Create("value", new[] { vg.Model.ToString(), $"{vg.Nugget:F1}", $"{vg.Sill:F1}", $"{vg.Range:F0}" }));
                return new(scene, tbl, [F("Samples", "Sampel", n.ToString()), F("Grid", "Grid", $"{gw}×{gh}")], default);
            }),

        // ---------------------------------------------------------------- network
        new("route", "network", new("Fastest route across the city", "Rute tercepat melintasi kota"),
            new("Build a routable graph from road lines (travel time, one-way streets) and compare Dijkstra, A* and Contraction Hierarchies.",
                "Bangun graf dari garis jalan (waktu tempuh, jalan satu arah) dan bandingkan Dijkstra, A* dan Contraction Hierarchies."),
            """
            var graph = NetworkGraph.FromLines(roads, costColumn: "travel_time_s", onewayColumn: "oneway");
            var route = graph.ShortestPath(Geo.Point(695500, 9306500), Geo.Point(709000, 9317000), PathAlgorithm.AStar);
            var ch = graph.BuildContractionHierarchy();      // preprocess once
            var fast = ch.Route(route.Nodes[0], route.Nodes[^1]);
            """,
            () =>
            {
                var g = NetworkGraph.FromLines(Roads, "travel_time_s", "oneway");
                var r = g.ShortestPath(Geo.Point(SampleData.CenterX - 6500, SampleData.CenterY - 5000), Geo.Point(SampleData.CenterX + 7000, SampleData.CenterY + 5000), PathAlgorithm.AStar);
                var sw = Stopwatch.StartNew();
                var ch = g.BuildContractionHierarchy();
                var build = sw.Elapsed; sw.Restart();
                var chRoute = ch.Route(r.Nodes[0], r.Nodes[^1]);
                var q = sw.Elapsed;
                var scene = Scene().AddLayer(Districts, Base).AddLayer(Roads, new LayerStyle { Column = "highway", Categorical = true, StrokeWidth = 1, LegendTitle = "Road class" })
                    .AddLayer(new GeoSeries([r.Geometry], Roads.Crs), new LayerStyle { Stroke = "#b0306a", StrokeWidth = 4, ShowInLegend = false })
                    .AddLayer(new GeoSeries([g.NodePoint(r.Nodes[0]), g.NodePoint(r.Nodes[^1])], Roads.Crs), new LayerStyle { Fill = "#b0306a", PointRadius = 6, Stroke = "#ffffff", StrokeWidth = 2, ShowInLegend = false });
                var table = new DataFrame(Column.Create("algorithm", new[] { "A*", "Contraction Hierarchies" }), Column.Create("cost_s", new[] { Math.Round(r.Cost, 1), Math.Round(chRoute.Cost, 1) }), Column.Create("edges", new[] { (long)r.Edges.Length, chRoute.Edges.Length }));
                return new(scene, table, [F("Graph", "Graf", $"{g.NodeCount} nodes / {g.Edges.Count} edges"), F("Travel time", "Waktu tempuh", $"{r.Cost / 60:F1} min"), F("Distance", "Jarak", $"{r.Length / 1000:F2} km"), F("CH shortcuts", "Shortcut CH", ch.ShortcutCount.ToString())], default);
            }),

        new("isochrone", "network", new("10-minute isochrone from a hospital", "Isokron 10 menit dari rumah sakit"),
            new("Single-source Dijkstra bounded by travel time produces the reachable road set; buffering it yields the service area polygon.",
                "Dijkstra satu sumber yang dibatasi waktu tempuh menghasilkan ruas jalan terjangkau; buffer menjadi poligon area layanan."),
            """
            var source = graph.NearestNode(hospital.X, hospital.Y);
            var (edges, area) = graph.ServiceArea(source, maxCost: 600, bufferDistance: 150);
            """,
            () =>
            {
                var g = NetworkGraph.FromLines(Roads, "travel_time_s");
                var h = (Point)Pois.Filter<string?>("category", c => c == "hospital").Geometry[0]!;
                var src = g.NearestNode(h.X, h.Y);
                var scene = Scene().AddLayer(Districts, Base);
                var rows = new List<(string, double)>();
                foreach (var (mins, color) in new[] { (10, "#c7e9b4"), (6, "#41b6c4"), (3, "#225ea8") })
                {
                    var (edges, area) = g.ServiceArea(src, mins * 60, 160);
                    scene.AddLayer(new GeoSeries([area], Roads.Crs), new LayerStyle { Fill = color, FillOpacity = 0.75, Stroke = null, LegendTitle = $"≤ {mins} min" }, $"{mins} min");
                    rows.Add(($"≤ {mins} min", Math.Round(area.Area / 1e6, 2)));
                }
                scene.AddLayer(Roads, RoadStyle with { ShowInLegend = false }).AddLayer(new GeoSeries([h], Roads.Crs), new LayerStyle { Fill = "#b0306a", PointRadius = 7, Stroke = "#ffffff", StrokeWidth = 2, ShowInLegend = false });
                var table = new DataFrame(Column.Create("band", rows.Select(r => r.Item1).ToArray()), Column.Create("area_km2", rows.Select(r => r.Item2).ToArray()));
                return new(scene, table, [F("Reachable in 10 min", "Terjangkau 10 menit", $"{rows[0].Item2} km²")], default);
            }),

        new("utility", "network", new("Water network isolation trace", "Penelusuran isolasi jaringan air"),
            new("Trace downstream from the treatment plant through open valves only. Pipes cut off by closed valves are highlighted for the outage report.",
                "Telusuri hilir dari instalasi pengolahan hanya melalui katup terbuka. Pipa yang terputus oleh katup tertutup disorot untuk laporan gangguan."),
            """
            var net = NetworkGraph.FromLines(pipes, directed: true);
            var served = net.Trace(plant, e => pipes["valve"].GetValue(e.SourceRow) is "open", downstreamOnly: true);
            """,
            () =>
            {
                var pipes = SampleData.WaterNetwork();
                var g = NetworkGraph.FromLines(pipes, directed: true);
                int plant = g.NearestNode(SampleData.CenterX - 6500, SampleData.CenterY - 4000);
                var served = g.Trace(plant, e => (string?)pipes["valve"].GetValue(e.SourceRow) == "open", downstreamOnly: true).Edges.ToHashSet();
                var status = pipes.WithColumn<string>("status", r => (string?)r["valve"] == "closed" ? "closed valve" : served.Contains(r.Index) ? "served" : "outage");
                var scene = Scene().AddLayer(Districts, Base with { Fill = "#eef2ec" })
                    .AddLayer(status, new LayerStyle { Column = "status", Categorical = true, StrokeWidth = 2.2, LegendTitle = "Pipe status" })
                    .AddLayer(new GeoSeries([g.NodePoint(plant)], pipes.Crs), new LayerStyle { Fill = "#1b2a33", PointRadius = 7, Stroke = "#ffffff", StrokeWidth = 2, ShowInLegend = false });
                return new(scene, status.GroupBy("status").Agg(("pipe_id", AggFunc.Count, "pipes")),
                    [F("Pipes", "Pipa", pipes.RowCount.ToString()), F("Without water", "Tanpa air", status.Filter<string>("status", s => s == "outage").RowCount.ToString())], default);
            }),

        // ---------------------------------------------------------------- raster
        new("hillshade", "raster", new("Hillshade & contours", "Hillshade & kontur"),
            new("Horn slope/aspect kernels produce an analytical hillshade; marching squares draws 50 m contours over a hypsometric tint.",
                "Kernel Horn slope/aspect menghasilkan hillshade analitis; marching squares menggambar kontur 50 m di atas pewarnaan hipsometrik."),
            """
            var dem = GeoTiff.Read("dem.tif");
            var shade = Terrain.Hillshade(dem, azimuth: 315, altitude: 40);
            var contours = dem.Contours(interval: 50);
            new MapScene().AddRaster(dem, ColorMaps.Terrain).AddHillshade(shade)
                          .AddLayer(contours, new LayerStyle { Stroke = "#9a6b3f" });
            """,
            () =>
            {
                var shade = Terrain.Hillshade(Dem, 315, 40);
                var contours = Dem.Contours(50);
                var slope = Terrain.Slope(Dem);
                var scene = Scene().AddRaster(Dem, ColorMaps.Terrain, legendTitle: "Elevation (m)").AddHillshade(shade, 0.5)
                    .AddLayer(contours, new LayerStyle { Fill = null, Stroke = "#9a6b3f", StrokeWidth = 0.7, ShowInLegend = false });
                var st = Dem.Statistics(); var ss = slope.Statistics();
                return new(scene, contours.Attributes().Head(10), [F("DEM", "DEM", $"{Dem.Width}×{Dem.Height} @ {Dem.CellSizeX} m"), F("Relief", "Relief", $"{st.Min:F0}–{st.Max:F0} m"), F("Mean slope", "Lereng rata-rata", $"{ss.Mean:F1}°")], default);
            }),

        new("viewshed", "raster", new("Viewshed of a telecom tower", "Viewshed menara telekomunikasi"),
            new("Line-of-sight analysis from a 40 m tower on the highest hill shows which cells can see the antenna — useful for smart-city and telecom planning.",
                "Analisis garis pandang dari menara 40 m di bukit tertinggi menunjukkan sel yang dapat melihat antena — untuk perencanaan smart city dan telekomunikasi."),
            """
            var visible = Terrain.Viewshed(dem, tower.X, tower.Y, observerHeight: 40, maxDistance: 12_000);
            """,
            () =>
            {
                var dem = Dem;
                var st = dem.Statistics();
                int idx = Array.IndexOf(dem.Bands[0], (float)st.Max);
                var (tx, ty) = dem.Transform.PixelToWorld(idx % dem.Width + 0.5, idx / dem.Width + 0.5);
                var vs = Terrain.Viewshed(dem, tx, ty, 40, 0, 12000);
                var mask = vs.Map(v => v == 1 ? 1 : float.NaN);
                var scene = Scene().AddHillshade(Terrain.Hillshade(dem), 0.9)
                    .AddRaster(mask, new ColorMap("visible", "#b0306a", "#b0306a"), opacity: 0.45)
                    .AddLayer(new GeoSeries([Geo.Point(tx, ty)], dem.Crs), new LayerStyle { Fill = "#1b2a33", PointRadius = 7, Stroke = "#ffffff", StrokeWidth = 2, ShowInLegend = false });
                double pct = 100.0 * vs.Bands[0].Count(v => v == 1) / vs.Bands[0].Length;
                return new(scene, null, [F("Tower height", "Tinggi menara", "40 m"), F("Visible area", "Area terlihat", $"{pct:F1} %")], default);
            }),

        // ---------------------------------------------------------------- civil
        new("watershed", "civil", new("Watershed & stream network", "Daerah aliran sungai & jaringan sungai"),
            new("Priority-flood sink filling, D8 flow direction and flow accumulation extract streams and the catchment of the main outlet.",
                "Pengisian sink priority-flood, arah aliran D8 dan akumulasi aliran mengekstrak sungai serta DAS dari outlet utama."),
            """
            var filled = Hydrology.FillSinks(dem);
            var dir    = Hydrology.FlowDirection(filled);
            var acc    = Hydrology.FlowAccumulation(dir);
            var basin  = Hydrology.Watershed(dir, outletX, outletY);
            var streams = Hydrology.Streams(acc, threshold: 400);
            """,
            () =>
            {
                var dir = Hydrology.FlowDirection(Hydrology.FillSinks(Dem));
                var acc = Hydrology.FlowAccumulation(dir);
                var (_, max, _, _, _) = acc.Statistics();
                int outlet = Array.IndexOf(acc.Bands[0], (float)max);
                var (ox, oy) = acc.Transform.PixelToWorld(outlet % acc.Width + 0.5, outlet / acc.Width + 0.5);
                var basin = Hydrology.Watershed(dir, ox, oy);
                var streams = Hydrology.Streams(acc, 400).Map(v => v == 1 ? 1 : float.NaN);
                var scene = Scene().AddHillshade(Terrain.Hillshade(Dem), 0.85)
                    .AddRaster(basin.Map(v => v == 1 ? 1 : float.NaN), new ColorMap("basin", "#9a6b3f", "#9a6b3f"), opacity: 0.25)
                    .AddRaster(streams, new ColorMap("stream", "#2f6f9f", "#2f6f9f"))
                    .AddLayer(new GeoSeries([Geo.Point(ox, oy)], Dem.Crs), new LayerStyle { Fill = "#b0306a", PointRadius = 6, Stroke = "#ffffff", StrokeWidth = 2, ShowInLegend = false });
                double basinKm2 = basin.Bands[0].Count(v => v == 1) * Dem.CellSizeX * Dem.CellSizeY / 1e6;
                return new(scene, null, [F("Catchment", "Luas DAS", $"{basinKm2:F1} km²"), F("Max accumulation", "Akumulasi maks", $"{max:N0} cells")], default);
            }),

        // ---------------------------------------------------------------- remote sensing
        new("ndvi", "rs", new("NDVI & urban expansion", "NDVI & ekspansi perkotaan"),
            new("Compute NDVI from red/NIR bands for two dates and detect vegetation loss with an Otsu threshold on the difference image.",
                "Hitung NDVI dari band red/NIR untuk dua waktu dan deteksi hilangnya vegetasi dengan ambang Otsu pada citra selisih."),
            """
            var ndvi2020 = SpectralIndex.Ndvi(SpectralIndex.Band(img2020, 2), SpectralIndex.Band(img2020, 1));
            var ndvi2025 = SpectralIndex.Ndvi(SpectralIndex.Band(img2025, 2), SpectralIndex.Band(img2025, 1));
            var (diff, change) = SpectralIndex.ChangeDetection(ndvi2020, ndvi2025);
            """,
            () =>
            {
                var a = SampleData.Multispectral(); var b = SampleData.Multispectral(urbanGrowth: 0.1);
                var n1 = SpectralIndex.Ndvi(SpectralIndex.Band(a, 2), SpectralIndex.Band(a, 1));
                var n2 = SpectralIndex.Ndvi(SpectralIndex.Band(b, 2), SpectralIndex.Band(b, 1));
                var (_, change) = SpectralIndex.ChangeDetection(n1, n2);
                var loss = change.Map(v => v < 0 ? 1 : float.NaN);
                var scene = Scene().AddRaster(n2, ColorMaps.Ndvi, legendTitle: "NDVI", min: -0.2, max: 0.8)
                    .AddRaster(loss, new ColorMap("loss", "#b0306a", "#b0306a"), opacity: 0.85)
                    .AddLayer(Districts, new LayerStyle { Fill = null, Stroke = "#ffffff", StrokeWidth = 0.5, ShowInLegend = false });
                double lossKm2 = change.Bands[0].Count(v => v < 0) * a.CellSizeX * a.CellSizeY / 1e6;
                return new(scene, null, [F("Mean NDVI (before)", "NDVI rata-rata (awal)", $"{n1.Statistics().Mean:F3}"), F("Mean NDVI (after)", "NDVI rata-rata (akhir)", $"{n2.Statistics().Mean:F3}"), F("Vegetation lost", "Vegetasi hilang", $"{lossKm2:F1} km²")], default);
            }),

        // ---------------------------------------------------------------- CAD
        new("cad", "cad", new("CAD site plan placed on the map", "Site plan CAD ditempatkan di peta"),
            new("Read a DXF site plan (parcels, buildings, trees, labels), georeference it with two control points and overlay it on city roads.",
                "Baca site plan DXF (kavling, bangunan, pohon, label), georeferensi dengan dua titik kontrol dan overlay di atas jalan kota."),
            """
            var georef = CadGeoreference.FromControlPoints(cad1, world1, cad2, world2);
            var plan = Dxf.Read("siteplan.dxf", new DxfReadOptions { Georeference = georef, Crs = SampleData.Crs });
            plan.ToFile("siteplan.gpkg");          // CAD → GIS
            """,
            () =>
            {
                double ox = SampleData.CenterX + 300, oy = SampleData.CenterY - 200;
                var georef = CadGeoreference.FromControlPoints((0, 0), (ox, oy), (100, 0), (ox + 92.4, oy + 38.3));
                var plan = Dxf.Parse(SampleData.SitePlanDxf(), new DxfReadOptions { Georeference = georef, Crs = SampleData.Crs });
                var env = plan.TotalBounds; env.ExpandBy(140);
                var scene = Scene();
                scene.Extent = env;
                scene.AddLayer(Roads, RoadStyle with { StrokeWidth = 3, ShowInLegend = false })
                    .AddLayer(plan.Filter<string?>("entity", e => e != "TEXT"), new LayerStyle { Column = "layer", Categorical = true, FillOpacity = 0.55, Stroke = "#1b2a33", StrokeWidth = 0.8, LegendTitle = "DXF layer" })
                    .AddLayer(plan.Filter<string?>("entity", e => e == "TEXT"), new LayerStyle { Fill = null, Stroke = null, PointRadius = 0, LabelColumn = "text", LabelSize = 9, ShowInLegend = false });
                return new(scene, plan.GroupBy("layer").Size(), [F("Entities", "Entitas", plan.RowCount.ToString()), F("Rotation", "Rotasi", $"{georef.RotationDegrees:F1}°"), F("Scale", "Skala", $"{georef.Scale:F3}")], default);
            }),

        // ---------------------------------------------------------------- Data & formats (Phase 2)
        new("flatgeobuf", "data", new("FlatGeobuf: Hilbert R-tree bbox read", "FlatGeobuf: baca bbox via Hilbert R-tree"),
            new("Write 1 500 points to FlatGeobuf with a packed Hilbert R-tree, then read only one window. The blue line follows the row order in the file — the Hilbert curve that keeps neighbours together on disk.",
                "Tulis 1 500 titik ke FlatGeobuf dengan packed Hilbert R-tree, lalu baca satu jendela saja. Garis biru mengikuti urutan baris di file — kurva Hilbert yang menjaga tetangga tetap berdekatan di disk."),
            """
            pois.ToFile("pois.fgb");                                 // packed Hilbert R-tree
            var window = new Envelope(700_500, 703_500, 9_309_500, 9_312_500);
            var hits = FlatGeobuf.Read("pois.fgb", bbox: window);   // memory-mapped: index nodes + matching features only
            var info = FlatGeobuf.ReadInfo("pois.fgb");             // header: count, CRS, index node size
            """,
            () =>
            {
                var pts = SampleData.PointsOfInterest(1500, 21);
                var path = TempFile("pois.fgb");
                FlatGeobuf.Write(pts, path);
                var ordered = FlatGeobuf.Read(path);
                var c = ordered.TotalBounds.Centre;
                var window = new Envelope(c.X - 1500, c.X + 1500, c.Y - 1500, c.Y + 1500);
                var sw = Stopwatch.StartNew();
                var hits = FlatGeobuf.Read(path, window);
                var ms = sw.Elapsed.TotalMilliseconds;
                var curve = Geo.LineString(ordered.Geometry.Select(g => g!.Coordinate));
                var scene = Scene().AddLayer(Districts, Base)
                    .AddLayer(new GeoSeries([curve], pts.Crs), new LayerStyle { Fill = null, Stroke = "#2f6f9f", StrokeWidth = 0.6, LegendTitle = "File order (Hilbert)" }, "Hilbert order")
                    .AddLayer(ordered, new LayerStyle { Fill = "#9aa79a", PointRadius = 1.6, Stroke = null, ShowInLegend = false })
                    .AddLayer(new GeoSeries([Geo.Box(window.MinX, window.MinY, window.MaxX, window.MaxY)], pts.Crs), new LayerStyle { Fill = null, Stroke = "#b0306a", StrokeWidth = 2, DashArray = [6, 4], LegendTitle = "Query window" }, "Window")
                    .AddLayer(hits, new LayerStyle { Fill = "#b0306a", PointRadius = 3, Stroke = "#ffffff", StrokeWidth = 0.6, LegendTitle = "Features read" }, "Read");
                var info = FlatGeobuf.ReadInfo(path);
                var table = new DataFrame(
                    Column.Create("property", new[] { "features", "geometry type", "CRS", "index node size", "header bytes", "index bytes", "file bytes" }),
                    Column.Create("value", new[] { info.FeatureCount.ToString(), info.GeometryType, info.Crs?.ToString() ?? "-", info.IndexNodeSize.ToString(), info.HeaderSize.ToString("N0"), info.IndexSize.ToString("N0"), new FileInfo(path).Length.ToString("N0") }));
                return new(scene, table, [F("Read / total", "Dibaca / total", $"{hits.RowCount} / {info.FeatureCount}"), F("Bbox read", "Baca bbox", $"{ms:F2} ms")], default);
            }),

        new("lazy", "data", new("Lazy GeoParquet query with pushdown", "Kueri GeoParquet lazy dengan pushdown"),
            new("A LazyGeoDataFrame plan over GeoParquet: the bbox prunes row groups through the bbox covering column, only the selected columns are decoded and the attribute filter runs inside the reader.",
                "Rencana LazyGeoDataFrame atas GeoParquet: bbox memangkas row group lewat kolom bbox covering, hanya kolom terpilih yang didekode dan filter atribut dijalankan di dalam reader."),
            """
            roads.ToFile("roads.parquet");                    // GeoParquet 1.1, bbox covering, row groups
            var plan = Gpd.Scan("roads.parquet")
                .FilterBbox(window)
                .Where("highway", CompareOp.Ne, "residential")
                .Select("name", "highway");
            Console.WriteLine(plan.Explain());                 // what was pushed into the reader
            var mainRoads = plan.Collect();
            """,
            () =>
            {
                var path = TempFile("roads.parquet");
                GeoParquet.Write(Roads, path, new GeoParquetWriteOptions { RowGroupSize = 24 });
                var b = Roads.TotalBounds;
                var window = new Envelope(b.MinX + b.Width * 0.15, b.MinX + b.Width * 0.7, b.MinY + b.Height * 0.2, b.MinY + b.Height * 0.85);
                var counter = new CountingSource(GeoParquet.ScanSource(path));
                var plan = LazyGeoDataFrame.From(counter).FilterBbox(window).Where("highway", CompareOp.Ne, "residential").Select("name", "highway");
                var res = plan.Collect();
                var scene = Scene().AddLayer(Districts, Base).AddLayer(Roads, RoadStyle with { Stroke = "#c3cbc2", ShowInLegend = false })
                    .AddLayer(new GeoSeries([Geo.Box(window.MinX, window.MinY, window.MaxX, window.MaxY)], Roads.Crs), new LayerStyle { Fill = null, Stroke = "#b0306a", StrokeWidth = 2, DashArray = [6, 4], LegendTitle = "FilterBbox" }, "Window")
                    .AddLayer(res, new LayerStyle { Column = "highway", Categorical = true, StrokeWidth = 2.2, LegendTitle = "Result (highway)" });
                var lines = plan.Explain().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var table = new DataFrame(Column.Create("plan", lines));
                var info = GeoParquet.ReadInfo(path);
                return new(scene, table, [F("Row groups", "Row group", info.RowGroups.ToString()), F("Rows decoded", "Baris didekode", $"{counter.RowsRead} / {info.Rows}"), F("Rows returned", "Baris hasil", res.RowCount.ToString())], default);
            }),

        new("versioning", "data", new("Dataset versioning: what changed?", "Versi dataset: apa yang berubah?"),
            new("Commit two versions of the district layer to a content-addressed repository and diff them by key: added, removed and modified districts, with the columns that changed.",
                "Commit dua versi layer distrik ke repositori berbasis konten lalu bandingkan per kunci: distrik yang ditambah, dihapus dan diubah, lengkap dengan kolom yang berubah."),
            """
            var repo = DatasetRepository.Open("districts.repo", key: "district_id");
            repo.Commit(districts, "Initial import");
            repo.Commit(edited, "2026 boundary review");
            var diff = repo.Diff("HEAD~1", "HEAD");        // +1 added, -1 removed, ~3 modified
            diff.ToChangeFrame().Plot(new LayerStyle { Column = "_change", Categorical = true });
            """,
            () =>
            {
                var dir = TempDir("districts.repo");
                var repo = DatasetRepository.Open(dir, key: "district_id");
                var v1 = Districts;
                repo.Commit(v1, "Initial import", "gallery");
                int i4 = Enumerable.Range(0, v1.RowCount).First(i => v1["district_id"].GetDouble(i) == 4);
                int i5 = Enumerable.Range(0, v1.RowCount).First(i => v1["district_id"].GetDouble(i) == 5);
                var merged = v1.Geometry[i4]!.Union(v1.Geometry[i5]!);
                var keep = Enumerable.Range(0, v1.RowCount).Where(i => i != i5).ToArray();
                var edited = v1.Take(keep);
                edited = edited.WithGeometry(new GeoSeries(keep.Select(i => i == i4 ? merged : v1.Geometry[i]), v1.Crs))
                    .WithColumn<long>("population", r => r.Get<long>("district_id") is 9 or 17 ? (long)(r.Get<long>("population") * 1.18) : r.Get<long>("population"));
                var added = v1.Take([0]).WithColumn(Column.Create("district_id", new long[] { 33 })).WithColumn(Column.Create("name", new string?[] { "Nusantara Baru" }))
                    .WithGeometry(new GeoSeries([v1.Geometry[0]!.Buffer(-250)], v1.Crs));
                edited = GeoDataFrame.Concat(edited, added);
                repo.Commit(edited, "2026 boundary review", "gallery");
                var diff = repo.Diff("HEAD~1", "HEAD");
                var change = (GeoDataFrame)diff.ToChangeFrame();
                GeoDataFrame Kind(string k) => change.Filter<string?>("_change", c => c == k);
                var scene = Scene().AddLayer(edited, Base)
                    .AddLayer(Kind("modified"), new LayerStyle { Fill = "#9a6b3f", FillOpacity = 0.7, Stroke = "#1b2a33", StrokeWidth = 1, LabelColumn = "name", LegendTitle = "Modified" }, "Modified")
                    .AddLayer(Kind("added"), new LayerStyle { Fill = "#2f6f9f", FillOpacity = 0.8, Stroke = "#1b2a33", StrokeWidth = 1, LabelColumn = "name", LegendTitle = "Added" }, "Added")
                    .AddLayer(Kind("removed"), new LayerStyle { Fill = "#b0306a", FillOpacity = 0.55, Stroke = "#b0306a", StrokeWidth = 2, DashArray = [5, 3], LabelColumn = "name", LegendTitle = "Removed (old outline)" }, "Removed");
                var log = repo.Log();
                var table = new DataFrame(
                    Column.Create("commit", log.Select(c => c.ShortId).ToArray()), Column.Create("message", log.Select(c => c.Message).ToArray()),
                    Column.Create("rows", log.Select(c => (long)c.Rows).ToArray()), Column.Create("summary", log.Select(c => c.Summary ?? "initial").ToArray()));
                return new(scene, table, [F("Diff", "Selisih", diff.Summary), F("Changed columns", "Kolom berubah", string.Join(", ", diff.Modified.SelectMany(m => m.ChangedColumns).Distinct()))], default);
            }),

        new("lidar", "rs", new("LiDAR: canopy & building heights", "LiDAR: tinggi kanopi & bangunan"),
            new("Read a classified LAS tile, grid a DSM (highest return) and a DTM (ground class), and subtract them into a normalised height model (nDSM).",
                "Baca tile LAS terklasifikasi, buat DSM (pantulan tertinggi) dan DTM (kelas ground), lalu kurangkan menjadi model tinggi ternormalisasi (nDSM)."),
            """
            var cloud = Las.Read("tile.las");                       // memory-mapped; LAS formats 0–10, LAZ 0–5
            var dsm = cloud.Dsm(cellSize: 2);
            var dtm = cloud.Dtm(cellSize: 2);                        // ground = class 2, gaps filled
            var ndsm = Raster.Combine(dsm, dtm, (s, t) => s - t);    // height above ground
            var buildings = cloud.ByClass(LasClass.Building);
            """,
            () =>
            {
                var path = TempFile("tile.las");
                Las.Write(SampleData.LidarTile(density: 3), path);
                var cloud = Las.Read(path);
                var dsm = cloud.Dsm(2);
                var dtm = cloud.Dtm(2);
                var ndsm = RasterGrid.Combine(dsm, dtm, (s, t) => float.IsNaN(s) || float.IsNaN(t) ? float.NaN : Math.Max(0, s - t));
                var shade = Terrain.Hillshade(dsm, 315, 45);
                var scene = Scene().AddRaster(ndsm, ColorMaps.Viridis, legendTitle: "Height above ground (m)").AddHillshade(shade, 0.35);
                var counts = cloud.ClassCounts();
                var table = new DataFrame(Column.Create("class", counts.Select(c => (long)c.Class).ToArray()), Column.Create("name", counts.Select(c => c.Name).ToArray()),
                    Column.Create("points", counts.Select(c => (long)c.Count).ToArray()));
                var info = cloud.Source!;
                return new(scene, table, [F("Points", "Titik", cloud.Count.ToString("N0")), F("LAS", "LAS", $"{info.Version} format {info.PointFormat}"), F("Tallest", "Tertinggi", $"{ndsm.Statistics().Max:F1} m")], default);
            }),

        new("netcdf", "rs", new("NetCDF climate grid (CF conventions)", "Grid iklim NetCDF (konvensi CF)"),
            new("Write a 3-hourly 2 m temperature series as CF-1.8 NetCDF, reopen it, decode the time axis and map one step over the archipelago with its main cities.",
                "Tulis seri suhu 2 m per 3 jam sebagai NetCDF CF-1.8, buka kembali, dekode sumbu waktu dan petakan satu langkah di atas kepulauan beserta kota-kota utamanya."),
            """
            using var nc = NetCdfDataset.Open("t2m.nc");            // CDF-1/2/5 and NetCDF-4 (HDF5)
            var times = nc.Times();                                  // "seconds since …" → DateTime
            var step = nc.ToRaster("t2m", 2).Map(k => k - 273.15f);  // north-up WGS 84 grid, Kelvin → °C
            """,
            () =>
            {
                var (grid, times) = SampleData.ClimateGrid(6);
                var path = TempFile("t2m.nc");
                NetCdf.WriteClassic(grid, path, "t2m", times, "K", "2 m air temperature");
                using var nc = NetCdfDataset.Open(path);
                var t = nc.Times()!;
                var step = nc.ToRaster("t2m", 2).Map(k => k - 273.15f);
                var cities = SampleData.Cities().Cx(95, -11, 141, 6);
                var scene = Scene().AddRaster(step, ColorMaps.Spectral.Reversed(), legendTitle: $"°C at {t[2]:HH:mm} UTC")
                    .AddLayer(cities, new LayerStyle { Fill = "#1b2a33", PointRadius = 3.5, Stroke = "#ffffff", StrokeWidth = 1, LabelColumn = "name", ShowInLegend = false });
                var v = nc.Variable("t2m");
                var table = new DataFrame(Column.Create("dimension", v.Dimensions.ToArray()), Column.Create("length", v.Shape.ToArray()));
                var stats = step.Statistics();
                return new(scene, table, [F("Format", "Format", nc.Format), F("Time steps", "Langkah waktu", $"{t.Length} ({t[0]:dd MMM HH:mm} → {t[^1]:HH:mm})"), F("Range", "Rentang", $"{stats.Min:F1} – {stats.Max:F1} °C")], default);
            }),

        // ---------------------------------------------------------------- surveying & geodesy
        new("projections", "survey", new("World map projections", "Proyeksi peta dunia"),
            new("The same graticule and cities in Equal Earth, Robinson and Mollweide, all computed by the built-in projection engine (checked against PROJ to the nanometre).",
                "Graticule dan kota yang sama dalam Equal Earth, Robinson dan Mollweide, semuanya dihitung engine proyeksi bawaan (diverifikasi terhadap PROJ hingga nanometer)."),
            """
            var cities = SampleData.Cities();
            var equalEarth = cities.ToCrs("EPSG:8857");                       // from the embedded EPSG dataset
            var robinson   = cities.ToCrs("+proj=robin +lon_0=0 +datum=WGS84");
            var mollweide  = cities.ToCrs("+proj=moll +datum=WGS84");
            CrsRegistry.AtLocation(106.8, -6.2);                              // CRSs valid in Jakarta
            """,
            () =>
            {
                var cities = SampleData.Cities();
                var defs = new (string Name, CoordinateReferenceSystem Crs, double Dy)[]
                {
                    ("Equal Earth", CoordinateReferenceSystem.FromEpsg(8857), 0),
                    ("Robinson", CoordinateReferenceSystem.Parse("+proj=robin +lon_0=0 +datum=WGS84 +units=m"), -19_500_000),
                    ("Mollweide", CoordinateReferenceSystem.Parse("+proj=moll +lon_0=0 +datum=WGS84 +units=m"), -39_000_000),
                };
                var graticule = new List<NtsGeometry?>();
                for (int lon = -180; lon <= 180; lon += 30) graticule.Add(Geo.LineString(Enumerable.Range(-90, 181).Select(lat => new Coordinate(lon, lat))));
                for (int lat = -90; lat <= 90; lat += 30) graticule.Add(Geo.LineString(Enumerable.Range(-180, 361).Select(lon => new Coordinate(lon, lat))));
                var grat = new GeoSeries(graticule, CoordinateReferenceSystem.Wgs84);
                var scene = Scene(subtitle: "Equal Earth (EPSG:8857) · Robinson · Mollweide");
                var rows = new List<(string, string, double, double)>();
                foreach (var (name, crs, dy) in defs)
                {
                    // project, then stack the three maps vertically in one scene
                    var g = grat.ToCrs(crs).Translate(0, dy);
                    var c = cities.ToCrs(crs);
                    var moved = c.WithGeometry(c.Geometry.Translate(0, dy));
                    var outline = Geo.LineString(Enumerable.Range(-90, 181).Select(lat => new Coordinate(180, lat)).Concat(Enumerable.Range(-90, 181).Reverse().Select(lat => new Coordinate(-180, lat))).Append(new Coordinate(180, -90)));
                    var frame = new GeoSeries([outline], CoordinateReferenceSystem.Wgs84).ToCrs(crs).Translate(0, dy);
                    scene.AddLayer(g, new LayerStyle { Stroke = "#c9d3cc", StrokeWidth = 0.5, ShowInLegend = false })
                        .AddLayer(frame, new LayerStyle { Stroke = "#53625a", StrokeWidth = 1, ShowInLegend = false })
                        .AddLayer(moved, new LayerStyle { Fill = "#b0306a", SizeColumn = "pop_mio", MaxPointRadius = 8, FillOpacity = 0.75, Stroke = "#ffffff" }, name);
                    var label = GeoDataFrame.FromGeometries([Geo.Point(-19_000_000, dy + 7_400_000)], null, Column.Create("name", new[] { name }));
                    scene.AddLayer(label, new LayerStyle { Fill = null, Stroke = null, PointRadius = 0, LabelColumn = "name", LabelSize = 11, ShowInLegend = false });
                    var jk = c.Geometry[0]!.Coordinate;
                    rows.Add((name, crs.Projection!.ProjName, jk.X, jk.Y));
                }
                scene.ShowScaleBar = false;
                var table = new DataFrame(Column.Create("projection", rows.Select(r => r.Item1).ToArray()), Column.Create("proj", rows.Select(r => r.Item2).ToArray()),
                    Column.Create("jakarta_x", rows.Select(r => Math.Round(r.Item3, 2)).ToArray()), Column.Create("jakarta_y", rows.Select(r => Math.Round(r.Item4, 2)).ToArray()));
                var here = CrsRegistry.AtLocation(106.8, -6.2);
                return new(scene, table, [F("EPSG codes", "Kode EPSG", EpsgDatabase.Count.ToString("N0")), F("Valid in Jakarta", "Berlaku di Jakarta", $"{here.Count} projected CRSs")], default);
            }),

        new("geoid", "survey", new("Geoid undulation: GPS height → elevation", "Undulasi geoid: tinggi GPS → elevasi"),
            new("Map the geoid height N over the archipelago and turn GNSS ellipsoidal heights into orthometric heights (H = h − N), the step every GPS survey needs.",
                "Petakan tinggi geoid N di atas kepulauan dan ubah tinggi elipsoid GNSS menjadi tinggi ortometrik (H = h − N), langkah wajib setiap survei GPS."),
            """
            var geoid = GeoidModel.Egm96Coarse;              // or GeoidModel.Load("InaGeoid2020.isg" / "egm2008.gtx" / ".tif")
            double n = geoid.Undulation(106.8456, -6.2088);  // metres above the ellipsoid
            double H = geoid.OrthometricHeight(106.8456, -6.2088, h: 58.4);

            // or as a compound CRS in a transformation
            var crs = CoordinateReferenceSystem.FromEpsg(32748).WithGeoid(geoid);
            """,
            () =>
            {
                var geoid = GeoidModel.Egm96Coarse;
                double w = 94, e = 142, s = -12, n = 8, cell = 0.25;
                int cols = (int)((e - w) / cell), rowsN = (int)((n - s) / cell);
                var band = new float[cols * rowsN];
                for (int r = 0; r < rowsN; r++) for (int c = 0; c < cols; c++)
                        band[r * cols + c] = (float)geoid.Undulation(w + (c + 0.5) * cell, n - (r + 0.5) * cell);
                var ras = new RasterGrid(cols, rowsN, [band], new GeoTransform(w, cell, 0, n, 0, -cell), CoordinateReferenceSystem.Wgs84);
                var cities = SampleData.Cities().Cx(w, s, e, n);
                // a GNSS receiver reads ellipsoidal height h; the map needs orthometric H = h - N
                const double h = 100;
                var names = new List<string>(); var ns = new List<double>(); var os = new List<double>();
                for (int i = 0; i < cities.RowCount; i++)
                {
                    var p = cities.Geometry[i]!.Coordinate;
                    double und = geoid.Undulation(p.X, p.Y);
                    names.Add((string)cities["name"].GetValue(i)!); ns.Add(Math.Round(und, 2)); os.Add(Math.Round(h - und, 2));
                }
                var scene = Scene().AddRaster(ras, ColorMaps.Spectral.Reversed(), legendTitle: "Geoid height N (m)")
                    .AddLayer(cities, new LayerStyle { Fill = "#1b2a33", PointRadius = 3.5, Stroke = "#ffffff", StrokeWidth = 1, LabelColumn = "name", ShowInLegend = false });
                var table = new DataFrame(Column.Create("city", names.ToArray()), Column.Create("N", ns.ToArray()), Column.Create("H_when_h_100m", os.ToArray()));
                var st = ras.Statistics();
                return new(scene, table, [F("Model", "Model", geoid.Name), F("Range", "Rentang", $"{st.Min:F1} … {st.Max:F1} m")], default);
            }),

        new("adjustment", "survey", new("Traverse & least-squares network", "Poligon & jaring kuadrat terkecil"),
            new("Adjust a closed traverse with Bowditch, then a control network of distances and azimuths by least squares; 95 % error ellipses (×2000) and the χ² test show the precision.",
                "Hitung poligon tertutup dengan Bowditch, lalu jaring kontrol jarak dan azimut dengan kuadrat terkecil; elips kesalahan 95 % (×2000) dan uji χ² menunjukkan presisinya."),
            """
            var t = Traverse.Compute("BM1", e0, n0, backsightAzimuth: 270, legs, closingAngleRight: 270);
            Console.WriteLine($"1:{t.PrecisionRatio:F0}");

            var net = new AdjustmentNetwork()
                .AddFixed("BM1", 1000, 1000).AddFixed("BM2", 1600, 1050)
                .AddStation("P1", 1290, 1260)                        // approximate
                .Distance("BM1", "P1", 390.514, sigma: 0.003)
                .Azimuth("BM1", "P1", 50.19444, sigmaArcSec: 2);
            var result = net.Adjust2D();                             // σ̂0², χ², error ellipses, data snooping
            """,
            () =>
            {
                var truth = new Dictionary<string, (double E, double N)>
                {
                    ["BM1"] = (1000, 1000), ["BM2"] = (1600, 1050), ["BM3"] = (1550, 1520), ["BM4"] = (980, 1480),
                    ["P1"] = (1300, 1250), ["P2"] = (1250, 1400), ["P3"] = (1420, 1330),
                };
                var rng = new Random(11);
                double G() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
                var net = new AdjustmentNetwork();
                foreach (var k in new[] { "BM1", "BM2", "BM3", "BM4" }) net.AddFixed(k, truth[k].E, truth[k].N);
                foreach (var k in new[] { "P1", "P2", "P3" }) net.AddStation(k, truth[k].E + 8, truth[k].N - 6);
                var pairs = new[] { ("BM1", "P1"), ("BM2", "P1"), ("BM3", "P1"), ("BM4", "P2"), ("BM3", "P2"), ("P1", "P2"), ("BM2", "P3"), ("BM3", "P3"), ("P1", "P3"), ("P2", "P3") };
                var obsLines = new List<NtsGeometry?>();
                foreach (var (f, t) in pairs)
                {
                    var (az, d) = Angles.Inverse(truth[f].E, truth[f].N, truth[t].E, truth[t].N);
                    net.Distance(f, t, d + 0.003 * G(), 0.003);
                    if (f.StartsWith("BM", StringComparison.Ordinal)) net.Azimuth(f, t, az + 2.0 / 3600 * G(), 2);
                    obsLines.Add(Geo.LineString([new Coordinate(truth[f].E, truth[f].N), new Coordinate(truth[t].E, truth[t].N)]));
                }
                var r = net.Adjust2D();

                // a closed traverse around the network
                var loop = new[] { "BM1", "P1", "P3", "P2", "BM4" };
                var legs = new List<TraverseLeg>();
                double back = Angles.Inverse(truth["BM1"].E, truth["BM1"].N, truth["BM4"].E, truth["BM4"].N).Azimuth;
                for (int i = 0; i < loop.Length; i++)
                {
                    var a = truth[loop[i]]; var b = truth[loop[(i + 1) % loop.Length]];
                    var prev = truth[loop[(i - 1 + loop.Length) % loop.Length]];
                    var (azF, dist) = Angles.Inverse(a.E, a.N, b.E, b.N);
                    double azB = Angles.Inverse(a.E, a.N, prev.E, prev.N).Azimuth;
                    legs.Add(new TraverseLeg(loop[(i + 1) % loop.Length], dist + 0.004 * G(), AngleRight: Angles.Normalize(azF - azB) + 3.0 / 3600 * G()));
                }
                var tr = Traverse.Compute("BM1", truth["BM1"].E, truth["BM1"].N, back, legs, closeOn: truth["BM1"]);

                var scene = Scene(subtitle: "dashed: observations · solid: traverse · pink: 95 % error ellipses ×2000");
                scene.AddLayer(obsLines.OfType<NtsGeometry>(), new LayerStyle { Stroke = "#9aa79a", StrokeWidth = 0.8, DashArray = [4, 3] }, "Observations / Pengukuran")
                    .AddLayer(tr.LegLines(), new LayerStyle { Stroke = "#2f6f8f", StrokeWidth = 1.6 }, "Traverse / Poligon")
                    .AddLayer(r.ErrorEllipses(2000), new LayerStyle { Fill = "#b0306a", FillOpacity = 0.25, Stroke = "#b0306a", StrokeWidth = 1 }, "95 % ellipses ×2000")
                    .AddLayer(r.StationPoints(), new LayerStyle { Fill = "#1b2a33", PointRadius = 4, Stroke = "#ffffff", StrokeWidth = 1, LabelColumn = "id", ShowInLegend = false });
                return new(scene, r.Stations, [
                    F("σ̂0²", "σ̂0²", r.VarianceFactor.ToString("F3")),
                    F("χ² test", "Uji χ²", r.GlobalTestPassed ? "passed / lulus" : "failed / gagal"),
                    F("Traverse", "Poligon", $"1:{tr.PrecisionRatio:F0} ({tr.LinearMisclosure * 1000:F1} mm)")], default);
            }),

        // ---------------------------------------------------------------- AI
        new("geollm", "ai", new("GeoLLM: ask your map", "GeoLLM: tanya peta Anda"),
            new("An LLM turns a natural-language question into a whitelisted query plan that GeoPandas.Net executes — no generated code is ever run. This preview replays a recorded plan offline.",
                "LLM mengubah pertanyaan bahasa alami menjadi rencana kueri ter-whitelist yang dijalankan GeoPandas.Net — tanpa mengeksekusi kode hasil generate. Pratinjau ini memutar ulang rencana yang direkam secara offline."),
            """
            using var llm = OpenAiChatClient.FromEnvironment()!;   // Azure OpenAI, OpenAI, DeepSeek…
            var answer = await new GeoAssistant(llm).AskAsync(districts,
                "Tampilkan 5 distrik terpadat di wilayah Selatan beserta luasnya");
            Console.WriteLine(answer.Explanation);
            """,
            () =>
            {
                // Plan recorded from a real gpt-5-mini (Azure OpenAI) run of the question above.
                var plan = GeoQueryPlan.Parse("""
                    {"steps":[{"op":"filter","column":"region","cmp":"==","value":"Selatan"},
                              {"op":"sort","column":"population","ascending":false},
                              {"op":"area","as":"area_m2"},{"op":"head","n":5}],
                     "explanation":"Menampilkan 5 distrik di wilayah Selatan dengan populasi terbesar dan luasnya."}
                    """);
                var res = (GeoDataFrame)GeoAssistant.Execute(Districts, plan);
                var scene = Scene().AddLayer(Districts, Base).AddLayer(res, new LayerStyle { Fill = "#b0306a", FillOpacity = 0.8, Stroke = "#ffffff", StrokeWidth = 1.2, LabelColumn = "name", LegendTitle = "Answer" }, "Answer");
                var steps = new DataFrame(Column.Create("step", plan.Steps.Select((s, i) => (long)i + 1).ToArray()), Column.Create("operation", plan.Steps.Select(s => s.Op).ToArray()),
                    Column.Create("arguments", plan.Steps.Select(s => string.Join(", ", s.Args.Select(a => $"{a.Key}={a.Value.GetRawText()}"))).ToArray()));
                return new(scene, steps, [F("Model", "Model", "gpt-5-mini (recorded)"), F("Rows returned", "Baris hasil", res.RowCount.ToString())], default);
            }),
    ];

    private static string TempDir(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpd-gallery", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string TempFile(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpd-gallery");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>Wraps a scan source to count the rows the reader actually decoded.</summary>
    private sealed class CountingSource(IGeoScanSource inner) : IGeoScanSource
    {
        public long RowsRead;
        public string Name => inner.Name;
        public CoordinateReferenceSystem? Crs => inner.Crs;
        public IReadOnlyList<string> ColumnNames => inner.ColumnNames;
        public long? RowCountHint => inner.RowCountHint;
        public ScanPushdown Pushdown => inner.Pushdown;
        public IEnumerable<GeoDataFrame> Scan(ScanRequest request)
        {
            foreach (var b in inner.Scan(request)) { RowsRead += b.RowCount; yield return b; }
        }
    }
}
