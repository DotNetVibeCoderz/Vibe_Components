using System.Diagnostics;
using GeoPandas.Net.AI;
using GeoPandas.Net.Analytics;
using GeoPandas.Net.CAD;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Network;
using GeoPandas.Net.Plotting;
using GeoPandas.Net.Raster;
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
        new("stats", new("Spatial statistics", "Statistik spasial"), "∴"),
        new("network", new("Networks", "Jaringan"), "⋈"),
        new("raster", new("Raster & terrain", "Raster & terrain"), "▦"),
        new("civil", new("Civil engineering", "Teknik sipil"), "≋"),
        new("rs", new("Remote sensing", "Penginderaan jauh"), "◎"),
        new("cad", new("CAD integration", "Integrasi CAD"), "⌗"),
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
}
