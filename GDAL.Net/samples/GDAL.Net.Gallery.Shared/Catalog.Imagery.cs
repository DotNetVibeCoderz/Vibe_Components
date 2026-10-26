using GDAL.Net.Processing;
using GDAL.Net.Rendering;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net.Gallery;

public static partial class Catalog
{
    private static IEnumerable<GallerySample> RemoteSensingSamples()
    {
        yield return new GallerySample(
            "composites",
            SampleCategory.RemoteSensing,
            new("True & false color composites", "Komposit warna asli & semu"),
            new("Combine Sentinel-2-like bands into natural color (B4-B3-B2), color infrared (B8-B4-B3) or agriculture views with per-band contrast stretching.",
                "Gabungkan band mirip Sentinel-2 menjadi warna alami (B4-B3-B2), inframerah (B8-B4-B3), atau tampilan pertanian dengan contrast stretch per band."),
            """
            using var scene = Gdal.OpenRaster("scene_bandung.tif");   // B2, B3, B4, B8
            var falseColor = new RenderOptions
            {
                Bands = [4, 3, 2],                    // NIR, Red, Green → R, G, B
                Stretch = StretchMode.Percentile,
            };
            RgbaImage img = RasterRenderer.Render(scene, falseColor, 800, 600);
            """,
            (ctx, args) =>
            {
                var scene = ctx.OpenScene();
                int[] bands = args.Get("composite") switch
                {
                    "Color infrared (8-4-3)" => [4, 3, 2],
                    "Vegetation (8-3-2)" => [4, 2, 1],
                    _ => [3, 2, 1],
                };
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(scene, new RenderOptions { Bands = bands, Stretch = Enum.Parse<StretchMode>(args.Get("stretch")) }, args.Get("composite")));
                result.Metrics.Add(new(new("Bands", "Band"), string.Join(" · ", bands.Select(b => scene.GetBand(b).Description))));
                result.Metrics.Add(new(new("Pixel size", "Ukuran piksel"), F(scene.GeoTransform.PixelWidth), "m"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("composite", new("Composite", "Komposit"), "Color infrared (8-4-3)", "True color (4-3-2)", "Color infrared (8-4-3)", "Vegetation (8-3-2)"),
                SampleParameter.Choice("stretch", new("Stretch", "Stretch"), "Percentile", "Percentile", "MinMax", "StdDev"),
            ],
            Equivalent = "RasterRenderer RGB",
        };

        yield return new GallerySample(
            "ndvi",
            SampleCategory.RemoteSensing,
            new("NDVI, NDWI & SAVI", "NDVI, NDWI & SAVI"),
            new("Compute vegetation and water indices band by band with the Rust/SIMD kernels, streamed in row blocks so memory stays flat for any image size.",
                "Hitung indeks vegetasi dan air per band dengan kernel Rust/SIMD, diproses per blok baris sehingga memori tetap kecil untuk ukuran citra apa pun."),
            """
            using var scene = Gdal.OpenRaster("scene_bandung.tif");
            using var ndvi = SpectralIndices.Ndvi(nir: scene.GetBand(4), red: scene.GetBand(3));
            using var ndwi = SpectralIndices.Ndwi(green: scene.GetBand(2), nir: scene.GetBand(4));
            using var savi = SpectralIndices.Savi(scene.GetBand(4), scene.GetBand(3), l: 0.5f);
            """,
            (ctx, args) =>
            {
                using var scene = ctx.OpenScene();
                var index = args.Get("index");
                var ds = index switch
                {
                    "NDWI" => SpectralIndices.Ndwi(scene.GetBand(2), scene.GetBand(4)),
                    "SAVI" => SpectralIndices.Savi(scene.GetBand(4), scene.GetBand(3)),
                    _ => SpectralIndices.Ndvi(scene.GetBand(4), scene.GetBand(3)),
                };
                var band = ds.GetBand(1);
                var stats = band.ComputeStatistics();
                var ramp = index == "NDWI" ? ColorRamp.Water : ColorRamp.Ndvi;
                var result = new SampleResult
                {
                    Crs = "EPSG:32748",
                    Histogram = band.GetHistogram(60, -1, 1),
                    HistogramTitle = new($"{index} distribution", $"Sebaran {index}"),
                };
                result.Rasters.Add(new RasterLayer(ds, new RenderOptions { Ramp = ramp, Stretch = StretchMode.Custom, Min = -1, Max = 1 }, index, -1, 1));
                var values = band.ReadAll<float>();
                var dense = values.Count(v => v > 0.5f) * 100.0 / values.Length;
                result.Metrics.Add(new(new("Mean", "Rata-rata"), F(stats.Mean, "0.000")));
                result.Metrics.Add(new(new(index == "NDWI" ? "Water (> 0)" : "Dense vegetation (> 0.5)", index == "NDWI" ? "Air (> 0)" : "Vegetasi rapat (> 0.5)"),
                    F(index == "NDWI" ? values.Count(v => v > 0) * 100.0 / values.Length : dense, "0.0"), "%"));
                result.Metrics.Add(new(new("Kernel backend", "Backend kernel"), Kernels.Backend));
                return result;
            })
        {
            Parameters = [SampleParameter.Choice("index", new("Index", "Indeks"), "NDVI", "NDVI", "NDWI", "SAVI")],
            Equivalent = "gdal raster calc",
        };

        yield return new GallerySample(
            "band-math",
            SampleCategory.RemoteSensing,
            new("Band math calculator", "Kalkulator band math"),
            new("Type any expression over named bands (B1…B4, BLUE, GREEN, RED, NIR) with operators, comparisons, conditionals and functions. Evaluated with vectorized TensorPrimitives.",
                "Ketik ekspresi apa pun atas band bernama (B1…B4, BLUE, GREEN, RED, NIR) dengan operator, perbandingan, kondisi, dan fungsi. Dievaluasi dengan TensorPrimitives tervektorisasi."),
            """
            var aliases = new Dictionary<string, int> { ["BLUE"] = 1, ["GREEN"] = 2, ["RED"] = 3, ["NIR"] = 4 };
            using var evi = BandMath.Calculate(scene,
                "2.5 * (NIR - RED) / (NIR + 6 * RED - 7.5 * BLUE + 10000)", aliases);

            // Conditionals and functions are supported too
            using var mask = BandMath.Calculate(scene, "NIR > 2500 && RED < 1000 ? 1 : 0", aliases);
            """,
            (ctx, args) =>
            {
                using var scene = ctx.OpenScene();
                var aliases = new Dictionary<string, int> { ["BLUE"] = 1, ["GREEN"] = 2, ["RED"] = 3, ["NIR"] = 4 };
                var ds = BandMath.Calculate(scene, args.Get("expression"), aliases);
                var stats = ds.GetBand(1).ComputeStatistics();
                var result = new SampleResult { Crs = "EPSG:32748", Histogram = ds.GetBand(1).GetHistogram(48), HistogramTitle = new("Result distribution", "Sebaran hasil") };
                result.Rasters.Add(new RasterLayer(ds, new RenderOptions { Ramp = Ramp(args.Get("ramp")), Stretch = StretchMode.Percentile }, "Result", stats.Minimum, stats.Maximum));
                result.Metrics.Add(new(new("Range", "Rentang"), $"{F(stats.Minimum, "0.###")} – {F(stats.Maximum, "0.###")}"));
                result.Metrics.Add(new(new("Mean", "Rata-rata"), F(stats.Mean, "0.###")));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.TextBox("expression", new("Expression", "Ekspresi"), "2.5 * (NIR - RED) / (NIR + 6 * RED - 7.5 * BLUE + 10000)", multiline: true) with { Hint = new("e.g. NIR > 2500 && RED < 1000 ? 1 : 0", "mis. NIR > 2500 && RED < 1000 ? 1 : 0") },
                SampleParameter.Choice("ramp", new("Color ramp", "Color ramp"), "NDVI", RampNames),
            ],
            Equivalent = "gdal_calc / gdal raster calc",
        };

        yield return new GallerySample(
            "land-cover",
            SampleCategory.RemoteSensing,
            new("Land cover polygons", "Poligon tutupan lahan"),
            new("A classified scene, cleaned with a sieve filter and polygonized into a GeoPackage — area per class summarized with SQL.",
                "Citra terklasifikasi, dibersihkan dengan sieve filter lalu dipoligonkan ke GeoPackage — luas per kelas diringkas dengan SQL."),
            """
            RasterAlgorithms.Sieve(classes, classes, threshold: 12, eightConnected: true);
            var layer = gpkg.CreateLayer("land_cover", utm48s, GeometryType.Polygon);
            layer.CreateField("class", FieldType.Integer);
            RasterAlgorithms.Polygonize(classes, layer, valueFieldIndex: 0, eightConnected: true);

            using var summary = gpkg.ExecuteSql(
                "SELECT name, COUNT(*) AS parcels, ROUND(SUM(area_ha)) AS hectares FROM land_cover GROUP BY name ORDER BY hectares DESC",
                dialect: "SQLITE");
            """,
            (ctx, args) =>
            {
                using var ds = ctx.OpenLandCover();
                var data = VectorData.Load(ds.GetLayer(0));
                var result = new SampleResult { Crs = "EPSG:32748" };
                if (args.Flag("relief")) result.Rasters.Add(new RasterLayer(ctx.OpenDem(), new RenderOptions { Ramp = ColorRamp.Grayscale, Hillshade = true }, "Relief"));
                result.Vectors.Add(new StyledVector(data, LandCoverStyle, "Land cover"));
                using var sql = ds.ExecuteSql("SELECT name, COUNT(*) AS parcels, ROUND(SUM(area_ha)) AS hectares FROM land_cover GROUP BY name ORDER BY hectares DESC", dialect: "SQLITE")!;
                var rows = sql.ReadRecords(includeGeometry: false);
                result.Table = Table(["Class", "Parcels", "Hectares"], rows.Select(r => new[] { TableData.Format(r.Attributes["name"]), TableData.Format(r.Attributes["parcels"]), TableData.Format(r.Attributes["hectares"]) }));
                result.Metrics.Add(new(new("Polygons", "Poligon"), data.Features.Count.ToString()));
                result.Metrics.Add(new(new("Classes", "Kelas"), rows.Count.ToString()));
                return result;
            })
        {
            Parameters = [SampleParameter.Toggle("relief", new("Show relief", "Tampilkan relief"), true)],
            Equivalent = "gdal_sieve + gdal_polygonize",
        };
    }

    private static IEnumerable<GallerySample> ProjectionSamples()
    {
        yield return new GallerySample(
            "reproject-raster",
            SampleCategory.Projection,
            new("Reproject a raster", "Reproyeksi raster"),
            new("Warp the DEM from UTM 48S to geographic WGS 84, Web Mercator or the Indonesian DGN95 datum with any resampling method.",
                "Warp DEM dari UTM 48S ke geografis WGS 84, Web Mercator, atau datum Indonesia DGN95 dengan metode resampling apa pun."),
            """
            using var wgs84 = GdalUtils.Warp(dem, "dem_wgs84.tif", new WarpOptions
            {
                TargetSrs = "EPSG:4326",
                Resampling = ResampleAlgorithm.Bilinear,
                Multithread = true,
            });
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var target = args.Get("target").Split(' ')[0];
                var warped = GdalUtils.Warp(dem, "", new WarpOptions { Format = "MEM", TargetSrs = target, Resampling = Enum.Parse<ResampleAlgorithm>(args.Get("resampling")), DestinationNoData = -9999 });
                var srs = warped.SpatialReference!;
                var result = new SampleResult { Crs = target, Output = srs.ToWkt(WktFormat.Wkt2_2019, multiline: true), OutputLanguage = "wkt" };
                result.Owned.Add(srs);
                result.Rasters.Add(new RasterLayer(warped, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, target));
                result.Metrics.Add(new(new("Target CRS", "CRS tujuan"), srs.ToString()));
                result.Metrics.Add(new(new("Output size", "Ukuran keluaran"), $"{warped.RasterWidth} × {warped.RasterHeight}", "px"));
                result.Metrics.Add(new(new("Pixel size", "Ukuran piksel"), F(warped.GeoTransform.PixelWidth, srs.IsGeographic ? "0.000000" : "0.00"), srs.IsGeographic ? "°" : "m"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("target", new("Target CRS", "CRS tujuan"), "EPSG:4326 (WGS 84)", "EPSG:4326 (WGS 84)", "EPSG:3857 (Web Mercator)", "EPSG:4755 (DGN95)", "EPSG:23830 (DGN95 / Indonesia TM-3 zone 48.2)"),
                SampleParameter.Choice("resampling", new("Resampling", "Resampling"), "Bilinear", "NearestNeighbour", "Bilinear", "Cubic", "Lanczos"),
            ],
            Equivalent = "gdalwarp -t_srs",
        };

        yield return new GallerySample(
            "transform-coordinates",
            SampleCategory.Projection,
            new("Transform coordinates", "Transformasi koordinat"),
            new("Convert a longitude/latitude to several CRSs at once (UTM, Web Mercator, DGN95, TM-3) with PROJ, and inspect the CRS as WKT2, PROJJSON or ESRI WKT.",
                "Konversi bujur/lintang ke beberapa CRS sekaligus (UTM, Web Mercator, DGN95, TM-3) dengan PROJ, dan lihat CRS sebagai WKT2, PROJJSON, atau ESRI WKT."),
            """
            using var wgs84 = SpatialReference.Wgs84;
            using var utm = SpatialReference.UtmFor(longitude: 107.6191, latitude: -6.9175);   // EPSG:32748
            using var ct = CoordinateTransformation.Create(wgs84, utm);
            var (x, y) = ct.Transform(107.6191, -6.9175);

            string wkt2 = utm.ToWkt(WktFormat.Wkt2_2019, multiline: true);
            string projJson = utm.ToProjJson();
            """,
            (ctx, args) =>
            {
                var lon = args.Number("lon", 107.6191);
                var lat = args.Number("lat", -6.9175);
                using var wgs = SpatialReference.Wgs84;
                using var utm = SpatialReference.UtmFor(lon, lat);
                var targets = new (string Label, SpatialReference Srs)[]
                {
                    ($"UTM {utm.Name.Split(' ').Last()}", utm.Clone()),
                    ("Web Mercator", SpatialReference.WebMercator),
                    ("DGN95", SpatialReference.FromEpsg(4755)),
                    ("DGN95 / TM-3 48.2", SpatialReference.FromEpsg(23830)),
                    ("WGS 84 geocentric (ECEF)", SpatialReference.FromEpsg(4978)),
                };
                var rows = new List<string[]>();
                foreach (var (label, srs) in targets)
                {
                    using (srs)
                    {
                        try
                        {
                            using var ct = CoordinateTransformation.Create(wgs, srs);
                            var (x, y, z) = ct.Transform(lon, lat, 0);
                            var unit = srs.IsGeographic ? "°" : "m";
                            rows.Add([label, $"EPSG:{srs.Epsg}", F(x, srs.IsGeographic ? "0.000000" : "#,0.00"), F(y, srs.IsGeographic ? "0.000000" : "#,0.00"), srs.IsGeocentric ? F(z, "#,0.00") : "—", unit]);
                        }
                        catch (GdalException e)
                        {
                            rows.Add([label, $"EPSG:{srs.Epsg}", e.Message, "", "", ""]);
                        }
                    }
                }
                var format = args.Get("format");
                var result = new SampleResult
                {
                    Table = Table(["CRS", "Code", "X / Lon", "Y / Lat", "Z", "Unit"], rows),
                    Output = format switch
                    {
                        "PROJJSON" => utm.ToProjJson(),
                        "ESRI WKT" => utm.ToEsriWkt(),
                        "PROJ string" => utm.ToProj4(),
                        "WKT1" => utm.ToPrettyWkt(),
                        _ => utm.ToWkt(WktFormat.Wkt2_2019, multiline: true),
                    },
                    OutputLanguage = format == "PROJJSON" ? "json" : "wkt",
                };
                result.Metrics.Add(new(new("Best UTM zone", "Zona UTM terbaik"), utm.ToString()));
                if (utm.AreaOfUse is { } area) result.Metrics.Add(new(new("Area of use", "Wilayah penggunaan"), area.Name));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Number("lon", new("Longitude", "Bujur"), 107.6191, -180, 180, 0.0001),
                SampleParameter.Number("lat", new("Latitude", "Lintang"), -6.9175, -85, 85, 0.0001),
                SampleParameter.Choice("format", new("CRS format", "Format CRS"), "WKT2", "WKT2", "WKT1", "PROJJSON", "ESRI WKT", "PROJ string"),
            ],
            Equivalent = "gdalsrsinfo / cs2cs",
        };

        yield return new GallerySample(
            "crs-search",
            SampleCategory.Projection,
            new("Search the EPSG database", "Cari di database EPSG"),
            new("Full-text search over thousands of CRSs in the PROJ database, or list every projected CRS valid at a location.",
                "Pencarian teks penuh pada ribuan CRS di database PROJ, atau daftar semua CRS terproyeksi yang berlaku di suatu lokasi."),
            """
            IEnumerable<CrsInfo> hits = CrsDatabase.Search("Indonesia TM-3");
            IEnumerable<CrsInfo> here = CrsDatabase.At(longitude: 107.6, latitude: -6.9);
            foreach (var crs in hits) Console.WriteLine($"{crs.Id}  {crs.Name}  ({crs.AreaName})");
            """,
            (ctx, args) =>
            {
                var query = args.Get("query");
                var hits = (string.IsNullOrWhiteSpace(query) ? CrsDatabase.At(107.6, -6.9) : CrsDatabase.Search(query)).Take(200).ToList();
                var result = new SampleResult
                {
                    Table = Table(["Code", "Name", "Type", "Area of use"], hits.Select(h => new[] { h.Id, h.Name, h.Type.ToString(), h.AreaName ?? "" })),
                };
                result.Metrics.Add(new(new("Matches", "Hasil"), hits.Count.ToString()));
                result.Metrics.Add(new(new("CRSs in database", "CRS di database"), CrsDatabase.GetAll().Count.ToString("#,0")));
                return result;
            })
        {
            Parameters = [SampleParameter.TextBox("query", new("Search", "Cari"), "Indonesia") with { Hint = new("Empty: projected CRSs valid in Bandung", "Kosong: CRS terproyeksi yang berlaku di Bandung") }],
            Equivalent = "projinfo --searchpaths",
        };
    }
}
