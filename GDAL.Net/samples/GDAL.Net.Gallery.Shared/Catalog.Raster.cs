using System.Diagnostics;
using GDAL.Net.Rendering;
using GDAL.Net.Vector;

namespace GDAL.Net.Gallery;

public static partial class Catalog
{
    private static IEnumerable<GallerySample> RasterSamples()
    {
        yield return new GallerySample(
            "raster-info",
            SampleCategory.Raster,
            new("Inspect a raster", "Periksa raster"),
            new("Open a Cloud Optimized GeoTIFF and read its size, georeferencing, CRS, bands, statistics and metadata — the gdalinfo report as JSON.",
                "Buka Cloud Optimized GeoTIFF lalu baca ukuran, georeferensi, CRS, band, statistik, dan metadatanya — laporan gdalinfo dalam JSON."),
            """
            using var dem = Gdal.OpenRaster("dem_bandung.tif");
            Console.WriteLine($"{dem.RasterWidth} x {dem.RasterHeight}, {dem.RasterCount} band");
            Console.WriteLine(dem.SpatialReference);          // WGS 84 / UTM zone 48S (EPSG:32748)
            Console.WriteLine(dem.GeoTransform);              // origin + 30 m pixels
            var band = dem.GetBand(1);
            var stats = band.ComputeStatistics();
            Console.WriteLine($"{stats.Minimum:0} – {stats.Maximum:0} m");

            // Full gdalinfo report, as JSON
            string json = GdalUtils.Info(dem, "-json", "-stats");
            """,
            (ctx, args) =>
            {
                var dem = ctx.OpenDem();
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "DEM"));
                var band = dem.GetBand(1);
                var stats = band.ComputeStatistics();
                result.Output = GdalUtils.Info(dem, "-json", "-stats");
                result.OutputLanguage = "json";
                result.Metrics.Add(new(new("Size", "Ukuran"), $"{dem.RasterWidth} × {dem.RasterHeight}", "px"));
                result.Metrics.Add(new(new("Pixel size", "Ukuran piksel"), F(dem.GeoTransform.PixelWidth), "m"));
                result.Metrics.Add(new(new("CRS", "CRS"), dem.SpatialReference?.ToString() ?? "—"));
                result.Metrics.Add(new(new("Elevation", "Elevasi"), $"{F(stats.Minimum, "0")} – {F(stats.Maximum, "0")}", "m"));
                result.Metrics.Add(new(new("Overviews", "Overview"), band.OverviewCount.ToString()));
                result.Metrics.Add(new(new("Layout", "Tata letak"), dem.GetMetadataItem("LAYOUT", "IMAGE_STRUCTURE") ?? "GTiff"));
                return result;
            })
        { Equivalent = "gdalinfo -json -stats" };

        yield return new GallerySample(
            "raster-styling",
            SampleCategory.Raster,
            new("Color ramps & stretching", "Color ramp & stretching"),
            new("Render a DEM with any color ramp, contrast stretch and on-the-fly hillshading. Only the visible window is read, at screen resolution, using overviews.",
                "Render DEM dengan color ramp, contrast stretch, dan hillshade langsung. Hanya jendela yang terlihat yang dibaca, pada resolusi layar, memakai overview."),
            """
            using var dem = Gdal.OpenRaster("dem_bandung.tif");
            var options = new RenderOptions
            {
                Ramp = ColorRamp.Terrain,
                Stretch = StretchMode.Percentile,   // 2 % – 98 %
                Hillshade = true,
                HillshadeStrength = 0.6,
            };
            RgbaImage image = RasterRenderer.Render(dem, options, 1024, 768);
            image.SavePng("dem.png");
            """,
            (ctx, args) =>
            {
                var dem = ctx.OpenDem();
                var stretch = Enum.Parse<StretchMode>(args.Get("stretch"));
                var options = new RenderOptions
                {
                    Ramp = Ramp(args.Get("ramp")),
                    Stretch = stretch,
                    Hillshade = args.Flag("hillshade"),
                    HillshadeStrength = args.Number("strength", 0.6),
                };
                var range = RasterRenderer.ResolveStretch(dem, dem.GetBand(1), options);
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(dem, options, "DEM", range.Min, range.Max));
                result.Metrics.Add(new(new("Stretch range", "Rentang stretch"), $"{F(range.Min, "0")} – {F(range.Max, "0")}", "m"));
                result.Metrics.Add(new(new("Kernel backend", "Backend kernel"), Processing.Kernels.Backend));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("ramp", new("Color ramp", "Color ramp"), "Terrain", RampNames),
                SampleParameter.Choice("stretch", new("Stretch", "Stretch"), "Percentile", "Percentile", "MinMax", "StdDev"),
                SampleParameter.Toggle("hillshade", new("Hillshade", "Hillshade"), true),
                SampleParameter.Number("strength", new("Shade strength", "Kekuatan bayangan"), 0.6, 0, 1, 0.05),
            ],
            Equivalent = "RasterRenderer (Rust kernels)",
        };

        yield return new GallerySample(
            "histogram",
            SampleCategory.Raster,
            new("Statistics & histogram", "Statistik & histogram"),
            new("Compute exact band statistics and a histogram with GDAL, then compare with the Rust-accelerated kernels on the same pixels.",
                "Hitung statistik band dan histogram secara eksak dengan GDAL, lalu bandingkan dengan kernel Rust pada piksel yang sama."),
            """
            var band = dem.GetBand(1);
            BandStatistics stats = band.ComputeStatistics();
            Histogram histogram = band.GetHistogram(buckets: 64);

            // Same thing on an array, with the Rust kernels (managed SIMD fallback)
            float[] pixels = band.ReadAll<float>();
            var fast = Kernels.Statistics(pixels, noData: -9999);
            ulong[] counts = Kernels.Histogram(pixels, fast.Minimum, fast.Maximum, 64);
            """,
            (ctx, args) =>
            {
                var dem = ctx.OpenDem();
                var band = dem.GetBand(1);
                var buckets = (int)args.Number("buckets", 64);
                var stats = band.ComputeStatistics();
                var hist = band.GetHistogram(buckets);
                var pixels = band.ReadAll<float>();
                var sw = Stopwatch.StartNew();
                var fast = Processing.Kernels.Statistics(pixels, -9999);
                var t = sw.Elapsed;
                var result = new SampleResult { Crs = "EPSG:32748", Histogram = hist, HistogramTitle = new("Elevation distribution (m)", "Sebaran elevasi (m)") };
                result.Rasters.Add(new RasterLayer(dem, new RenderOptions { Ramp = ColorRamp.Viridis, Stretch = StretchMode.MinMax }, "DEM", stats.Minimum, stats.Maximum));
                result.Metrics.Add(new(new("Minimum", "Minimum"), F(stats.Minimum, "0.0"), "m"));
                result.Metrics.Add(new(new("Maximum", "Maksimum"), F(stats.Maximum, "0.0"), "m"));
                result.Metrics.Add(new(new("Mean", "Rata-rata"), F(stats.Mean, "0.0"), "m"));
                result.Metrics.Add(new(new("Std. deviation", "Simpangan baku"), F(stats.StdDev, "0.0"), "m"));
                result.Metrics.Add(new(new("Kernel time", "Waktu kernel"), F(t.TotalMilliseconds, "0.00"), "ms"));
                result.Metrics.Add(new(new("Pixels", "Piksel"), F(fast.ValidCount, "#,0")));
                result.Note = new($"Kernels: {Processing.Kernels.Backend}", $"Kernel: {Processing.Kernels.Backend}");
                return result;
            })
        {
            Parameters = [SampleParameter.Number("buckets", new("Buckets", "Jumlah kelas"), 64, 8, 256, 8)],
            Equivalent = "gdalinfo -hist",
        };

        yield return new GallerySample(
            "cog",
            SampleCategory.Raster,
            new("Convert to Cloud Optimized GeoTIFF", "Konversi ke Cloud Optimized GeoTIFF"),
            new("Translate a raster to COG with your choice of compression and compare file size and time. COGs stream efficiently over HTTP and object storage.",
                "Ubah raster menjadi COG dengan kompresi pilihan, lalu bandingkan ukuran file dan waktunya. COG efisien di-stream lewat HTTP dan object storage."),
            """
            using var cog = GdalUtils.Translate(dem, "dem_cog.tif", new TranslateOptions
            {
                Format = "COG",
                CreationOptions = new Dictionary<string, string>
                {
                    ["COMPRESS"] = "ZSTD",
                    ["PREDICTOR"] = "YES",
                    ["OVERVIEWS"] = "AUTO",
                },
            }, progress: new Progress<GdalProgress>(p => Console.Write($"\r{p.Percent:0}%")));
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var compress = args.Get("compress");
                var path = ctx.Scratch($"dem_{compress.ToLowerInvariant()}.tif");
                var co = new Dictionary<string, string> { ["COMPRESS"] = compress, ["OVERVIEWS"] = "AUTO" };
                if (compress is "DEFLATE" or "ZSTD" or "LZW") co["PREDICTOR"] = "YES";
                if (compress is "JPEG")
                {
                    // Lossy codecs need 8-bit data: scale elevations to Byte first.
                    co["QUALITY"] = "85";
                }
                var sw = Stopwatch.StartNew();
                var lossy = compress is "JPEG";
                var cog = GdalUtils.Translate(dem, path, new TranslateOptions
                {
                    Format = "COG",
                    CreationOptions = co,
                    OutputType = lossy ? DataType.Byte : null,
                    ScaleRange = lossy ? (650, 2250, 1, 255) : null,
                });
                var elapsed = sw.Elapsed;
                cog.Dispose();
                var reopened = Gdal.OpenRaster(path);
                var raw = (long)dem.RasterWidth * dem.RasterHeight * 4;
                var size = new FileInfo(path).Length;
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(reopened, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, $"COG ({compress})"));
                result.Metrics.Add(new(new("Compression", "Kompresi"), compress));
                result.Metrics.Add(new(new("File size", "Ukuran file"), FileSize(size)));
                result.Metrics.Add(new(new("Uncompressed", "Tanpa kompresi"), FileSize(raw)));
                result.Metrics.Add(new(new("Ratio", "Rasio"), F((double)raw / size, "0.0") + "×"));
                result.Metrics.Add(new(new("Time", "Waktu"), F(elapsed.TotalMilliseconds, "0"), "ms"));
                result.Output = GdalUtils.Info(reopened);
                return result;
            })
        {
            Parameters = [SampleParameter.Choice("compress", new("Compression", "Kompresi"), "ZSTD", "ZSTD", "DEFLATE", "LZW", "JPEG", "NONE")],
            Equivalent = "gdal_translate -of COG",
        };

        yield return new GallerySample(
            "resampling",
            SampleCategory.Raster,
            new("Resampling methods", "Metode resampling"),
            new("Downsample a raster with nearest, bilinear, cubic, Lanczos, average or mode resampling and see how each one treats the terrain.",
                "Perkecil raster dengan resampling nearest, bilinear, cubic, Lanczos, average, atau mode, lalu lihat pengaruhnya pada terrain."),
            """
            // Read the whole band into a 120 x 90 buffer with the chosen kernel
            float[] small = dem.GetBand(1).ReadResampled<float>(120, 90, ResampleAlgorithm.Lanczos);

            // or write a resampled file
            using var coarse = GdalUtils.Warp(dem, "dem_300m.tif", new WarpOptions
            {
                Resolution = (300, 300),
                Resampling = ResampleAlgorithm.Average,
            });
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var alg = Enum.Parse<ResampleAlgorithm>(args.Get("method"));
                var res = args.Number("resolution", 300);
                var coarse = GdalUtils.Warp(dem, "", new WarpOptions { Format = "MEM", Resolution = (res, res), Resampling = alg });
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(coarse, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true, Resampling = ResampleAlgorithm.NearestNeighbour }, $"{alg} {res} m"));
                result.Metrics.Add(new(new("Output size", "Ukuran keluaran"), $"{coarse.RasterWidth} × {coarse.RasterHeight}", "px"));
                result.Metrics.Add(new(new("Max elevation", "Elevasi maks."), F(coarse.GetBand(1).ComputeMinMax().Max, "0.0"), "m"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("method", new("Method", "Metode"), "Lanczos", "NearestNeighbour", "Bilinear", "Cubic", "CubicSpline", "Lanczos", "Average", "Mode"),
                SampleParameter.Number("resolution", new("Resolution (m)", "Resolusi (m)"), 300, 60, 1200, 30),
            ],
            Equivalent = "gdalwarp -tr -r",
        };
    }

    private static IEnumerable<GallerySample> TerrainSamples()
    {
        yield return new GallerySample(
            "hillshade",
            SampleCategory.Terrain,
            new("Hillshade", "Hillshade"),
            new("Shade terrain from a light source with gdaldem: classic, combined, multidirectional or Igor's method, with adjustable sun position and vertical exaggeration.",
                "Beri bayangan terrain dari sumber cahaya dengan gdaldem: klasik, combined, multidirectional, atau metode Igor, dengan posisi matahari dan eksagerasi vertikal yang dapat diatur."),
            """
            using var hillshade = GdalUtils.DemProcessing(dem, "hillshade.tif", DemProcessingMode.Hillshade,
                new DemOptions
                {
                    Azimuth = 315,
                    Altitude = 45,
                    ZFactor = 2,
                    HillshadeVariant = "multidirectional",
                });
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var variant = args.Get("variant");
                var opts = new DemOptions
                {
                    Format = "MEM",
                    Azimuth = variant == "multidirectional" ? null : args.Number("azimuth", 315),
                    Altitude = variant == "igor" ? null : args.Number("altitude", 45),
                    ZFactor = args.Number("z", 1.5),
                    HillshadeVariant = variant == "standard" ? null : variant,
                };
                var hs = GdalUtils.DemProcessing(dem, "", DemProcessingMode.Hillshade, opts);
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(hs, new RenderOptions { Ramp = ColorRamp.Grayscale, Stretch = StretchMode.None }, "Hillshade"));
                result.Metrics.Add(new(new("Variant", "Varian"), variant));
                result.Metrics.Add(new(new("Data type", "Tipe data"), hs.GetBand(1).DataType.ToString()));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("variant", new("Method", "Metode"), "standard", "standard", "combined", "multidirectional", "igor"),
                SampleParameter.Number("azimuth", new("Sun azimuth (°)", "Azimut matahari (°)"), 315, 0, 360, 5),
                SampleParameter.Number("altitude", new("Sun altitude (°)", "Ketinggian matahari (°)"), 45, 5, 90, 5),
                SampleParameter.Number("z", new("Vertical exaggeration", "Eksagerasi vertikal"), 1.5, 0.5, 5, 0.5),
            ],
            Equivalent = "gdaldem hillshade",
        };

        yield return new GallerySample(
            "slope-aspect",
            SampleCategory.Terrain,
            new("Slope, aspect & roughness", "Kemiringan, aspek & kekasaran"),
            new("Derive slope (degrees or percent), aspect, terrain ruggedness (TRI), topographic position (TPI) or roughness from the DEM.",
                "Turunkan kemiringan (derajat atau persen), aspek, terrain ruggedness (TRI), topographic position (TPI), atau kekasaran dari DEM."),
            """
            using var slope = GdalUtils.DemProcessing(dem, "slope.tif", DemProcessingMode.Slope,
                new DemOptions { SlopePercent = false, Algorithm = "Horn" });
            using var aspect = GdalUtils.DemProcessing(dem, "aspect.tif", DemProcessingMode.Aspect, new DemOptions());
            using var tri = GdalUtils.DemProcessing(dem, "tri.tif", DemProcessingMode.TerrainRuggednessIndex, new DemOptions());
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var mode = Enum.Parse<DemProcessingMode>(args.Get("product"));
                var output = GdalUtils.DemProcessing(dem, "", mode, new DemOptions { Format = "MEM", SlopePercent = args.Flag("percent") });
                var stats = output.GetBand(1).ComputeStatistics();
                var ramp = mode switch
                {
                    DemProcessingMode.Aspect => ColorRamp.Turbo,
                    DemProcessingMode.Slope => ColorRamp.Magma,
                    _ => ColorRamp.Inferno,
                };
                var result = new SampleResult { Crs = "EPSG:32748", Histogram = output.GetBand(1).GetHistogram(48), HistogramTitle = new(mode.ToString(), mode.ToString()) };
                result.Rasters.Add(new RasterLayer(output, new RenderOptions { Ramp = ramp, Stretch = StretchMode.Percentile }, mode.ToString(), stats.Minimum, stats.Maximum));
                result.Metrics.Add(new(new("Mean", "Rata-rata"), F(stats.Mean, "0.00")));
                result.Metrics.Add(new(new("Maximum", "Maksimum"), F(stats.Maximum, "0.00")));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("product", new("Product", "Produk"), "Slope", "Slope", "Aspect", "TerrainRuggednessIndex", "TopographicPositionIndex", "Roughness"),
                SampleParameter.Toggle("percent", new("Slope in percent", "Kemiringan dalam persen"), false),
            ],
            Equivalent = "gdaldem slope | aspect | TRI | TPI | roughness",
        };

        yield return new GallerySample(
            "color-relief",
            SampleCategory.Terrain,
            new("Color relief", "Relief berwarna"),
            new("Build a gdaldem color file straight from a ColorRamp and produce an RGBA color relief map with transparent nodata.",
                "Buat file warna gdaldem langsung dari ColorRamp dan hasilkan peta relief RGBA dengan nodata transparan."),
            """
            File.WriteAllText("ramp.txt", ColorRamp.Terrain.ToGdalColorFile(min: 650, max: 2200));
            using var relief = GdalUtils.DemProcessing(dem, "relief.tif", DemProcessingMode.ColorRelief,
                new DemOptions { Alpha = true }, colorFile: "ramp.txt");
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var (min, max) = dem.GetBand(1).ComputeMinMax();
                var colorFile = ctx.Scratch("ramp.txt");
                File.WriteAllText(colorFile, Ramp(args.Get("ramp")).ToGdalColorFile(min, max, 24));
                var relief = GdalUtils.DemProcessing(dem, "", DemProcessingMode.ColorRelief, new DemOptions { Format = "MEM", Alpha = true }, colorFile);
                var result = new SampleResult { Crs = "EPSG:32748", Output = File.ReadAllText(colorFile) };
                result.Rasters.Add(new RasterLayer(relief, new RenderOptions { Mode = RenderMode.Rgb, Stretch = StretchMode.None }, "Color relief"));
                result.Metrics.Add(new(new("Bands", "Band"), relief.RasterCount.ToString(), "RGBA"));
                return result;
            })
        {
            Parameters = [SampleParameter.Choice("ramp", new("Color ramp", "Color ramp"), "Terrain", RampNames)],
            Equivalent = "gdaldem color-relief",
        };

        yield return new GallerySample(
            "contours",
            SampleCategory.Terrain,
            new("Contour lines", "Garis kontur"),
            new("Generate contour lines (or elevation band polygons) at any interval and overlay them on the shaded relief.",
                "Hasilkan garis kontur (atau poligon kelas elevasi) dengan interval apa pun dan tampilkan di atas relief berbayang."),
            """
            using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 50);
            Layer lines = contours.GetLayer(0);       // field "elev"

            // Elevation bands as polygons instead of lines
            using var bands = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval: 250, polygons: true);
            """,
            (ctx, args) =>
            {
                var dem = ctx.OpenDem();
                var interval = args.Number("interval", 100);
                var polygons = args.Flag("polygons");
                using var contours = RasterAlgorithms.GenerateContours(dem.GetBand(1), interval, polygons);
                var layer = contours.GetLayer(0);
                var data = VectorData.Load(layer);
                var result = new SampleResult { Crs = "EPSG:32748", Table = TableData.From(data, 200) };
                result.Rasters.Add(new RasterLayer(dem, new RenderOptions { Ramp = ColorRamp.Grayscale, Hillshade = true, Opacity = 0.9 }, "DEM"));
                var style = polygons
                    ? new VectorStyle
                    {
                        Stroke = new Rgba(255, 255, 255, 120),
                        StrokeWidth = 0.6,
                        FillSelector = f => ColorRamp.Terrain.Evaluate(((f["elev_min"] as double? ?? 600) - 600) / 1700) with { A = 170 },
                    }
                    : ContourStyle;
                result.Vectors.Add(new StyledVector(data, style, polygons ? "Elevation bands" : "Contours"));
                result.Metrics.Add(new(new("Features", "Fitur"), data.Features.Count.ToString()));
                result.Metrics.Add(new(new("Interval", "Interval"), F(interval, "0"), "m"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Number("interval", new("Interval (m)", "Interval (m)"), 100, 20, 500, 10),
                SampleParameter.Toggle("polygons", new("Elevation bands (polygons)", "Kelas elevasi (poligon)"), false),
            ],
            Equivalent = "gdal_contour",
        };

        yield return new GallerySample(
            "viewshed",
            SampleCategory.Terrain,
            new("Viewshed from the summit", "Viewshed dari puncak"),
            new("Which parts of the basin can an observer on the volcano rim see? Viewshed accounts for terrain occlusion and earth curvature.",
                "Bagian cekungan mana yang terlihat oleh pengamat di bibir kawah? Viewshed memperhitungkan halangan terrain dan kelengkungan bumi."),
            """
            using var visible = RasterAlgorithms.Viewshed(dem.GetBand(1),
                observerX: 787_500, observerY: 9_251_500,   // UTM 48S
                observerHeight: 2, maxDistance: 20_000);
            // 255 = visible, 0 = hidden
            """,
            (ctx, args) =>
            {
                var dem = ctx.OpenDem();
                var (ox, oy) = (args.Get("observer") == "City center" ? SampleContext.BandungUtm : SampleContext.VolcanoUtm);
                if (args.Get("observer") == "Volcano rim") oy -= 1_100;
                var vs = RasterAlgorithms.Viewshed(dem.GetBand(1), ox, oy, args.Number("height", 2), maxDistance: args.Number("distance", 20) * 1000,
                    visibleValue: 255, invisibleValue: 0, outOfRangeValue: 0, noDataValue: 0);
                vs.GetBand(1).NoDataValue = 0;
                var data = vs.GetBand(1).ReadAll<byte>();
                var visiblePct = data.Count(v => v == 255) * 100.0 / data.Length;
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(dem, new RenderOptions { Ramp = ColorRamp.Grayscale, Hillshade = true }, "DEM"));
                result.Rasters.Add(new RasterLayer(vs, new RenderOptions { Ramp = new ColorRamp("Visible", (0, new Rgba(42, 157, 143, 0)), (1, new Rgba(242, 201, 76, 170))), Stretch = StretchMode.Custom, Min = 0, Max = 255 }, "Viewshed"));
                result.Vectors.Add(Marker("Observer", ox, oy, new Rgba(200, 85, 61), 8, "Observer"));
                result.Metrics.Add(new(new("Visible area", "Area terlihat"), F(visiblePct, "0.0"), "%"));
                result.Metrics.Add(new(new("Observer", "Pengamat"), $"{F(ox, "0")} E, {F(oy, "0")} N"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("observer", new("Observer", "Pengamat"), "Volcano rim", "Volcano rim", "Summit", "City center"),
                SampleParameter.Number("height", new("Observer height (m)", "Tinggi pengamat (m)"), 2, 0, 200, 1),
                SampleParameter.Number("distance", new("Max distance (km)", "Jarak maks. (km)"), 20, 2, 40, 1),
            ],
            Equivalent = "gdal_viewshed",
        };
    }
}
