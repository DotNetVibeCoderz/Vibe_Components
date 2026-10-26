using GDAL.Net.MultiDimensional;
using GDAL.Net.Rendering;
using GDAL.Net.Srs;
using GDAL.Net.Vector;
using GDAL.Net.Vsi;

namespace GDAL.Net.Gallery;

public static partial class Catalog
{
    private static IEnumerable<GallerySample> MosaicSamples()
    {
        yield return new GallerySample(
            "vrt-mosaic",
            SampleCategory.Mosaic,
            new("Virtual mosaic (VRT)", "Mosaik virtual (VRT)"),
            new("Stitch four DEM tiles into one seamless virtual raster without copying a single pixel, or stack bands from separate files.",
                "Gabungkan empat tile DEM menjadi satu raster virtual tanpa menyalin satu piksel pun, atau tumpuk band dari file terpisah."),
            """
            var tiles = Directory.GetFiles("dem_tiles", "*.tif");
            using var mosaic = GdalUtils.BuildVrt("dem_mosaic.vrt", tiles);

            // Band stacking: one band per input
            using var stack = GdalUtils.BuildVrt("stack.vrt", [red, nir], new BuildVrtOptions { Separate = true });
            """,
            (ctx, args) =>
            {
                ctx.EnsureData();
                var tiles = Directory.GetFiles(ctx.TilesDirectory, "*.tif").OrderBy(f => f).ToArray();
                var selected = args.Get("tiles") switch
                {
                    "Top row" => tiles.Where(t => t.Contains("_r0_")).ToArray(),
                    "Diagonal" => tiles.Where(t => t.Contains("r0_c0") || t.Contains("r1_c1")).ToArray(),
                    _ => tiles,
                };
                var vrtPath = ctx.Scratch("mosaic.vrt");
                var vrt = GdalUtils.BuildVrt(vrtPath, selected, ["-srcnodata", "-9999"]);
                vrt.FlushCache();
                var result = new SampleResult { Crs = "EPSG:32748", Output = File.ReadAllText(vrtPath), OutputLanguage = "xml", Extent = RasterRenderer.FullView(vrt).Union(new Envelope(770_000, 9_229_000, 806_000, 9_256_000)) };
                result.Rasters.Add(new RasterLayer(vrt, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "VRT mosaic"));
                var outlines = new List<Geometry>();
                foreach (var t in selected)
                {
                    using var ds = Gdal.OpenRaster(t);
                    outlines.Add(Geometry.FromEnvelope(ds.Extent));
                }
                result.Vectors.Add(new StyledVector(VectorData.FromGeometries("Tiles", outlines), VectorStyle.Lines(new Rgba(255, 255, 255, 220), 1.5), "Tile footprints"));
                foreach (var g in outlines) g.Dispose();
                result.Metrics.Add(new(new("Tiles", "Tile"), selected.Length.ToString()));
                result.Metrics.Add(new(new("Mosaic size", "Ukuran mosaik"), $"{vrt.RasterWidth} × {vrt.RasterHeight}", "px"));
                result.Metrics.Add(new(new("VRT file", "File VRT"), FileSize(new FileInfo(vrtPath).Length)));
                return result;
            })
        {
            Parameters = [SampleParameter.Choice("tiles", new("Tiles", "Tile"), "All four", "All four", "Top row", "Diagonal")],
            Equivalent = "gdalbuildvrt",
        };

        yield return new GallerySample(
            "clip",
            SampleCategory.Mosaic,
            new("Clip by polygon", "Potong dengan poligon"),
            new("Cut the DEM with a cutline — a circle around the city or the urban land cover polygons — and crop the output to the cut extent.",
                "Potong DEM dengan cutline — lingkaran di sekitar kota atau poligon permukiman — lalu pangkas keluaran ke batas potongan."),
            """
            using var clipped = GdalUtils.Warp(dem, "dem_clip.tif", new WarpOptions
            {
                CutlineFile = "study_area.gpkg",
                CropToCutline = true,
                DestinationNoData = -9999,
            });
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                string cutline;
                var shape = args.Get("shape");
                var result = new SampleResult { Crs = "EPSG:32748" };
                if (shape == "Urban land cover")
                {
                    cutline = ctx.LandCoverPath;
                    var clipped = GdalUtils.Warp(dem, "", new WarpOptions
                    {
                        Format = "MEM",
                        CutlineFile = cutline,
                        CropToCutline = true,
                        DestinationNoData = -9999,
                        ExtraArgs = ["-cwhere", "class = 2"],
                    });
                    result.Rasters.Add(new RasterLayer(clipped, new RenderOptions { Ramp = ColorRamp.Magma, Hillshade = true }, "Clipped"));
                }
                else
                {
                    var (cx, cy) = SampleContext.BandungUtm;
                    using var circle = Geometry.Point(cx, cy).Buffer(args.Number("radius", 8) * 1000, 48);
                    var clipped = GdalUtils.Warp(dem, "", new WarpOptions
                    {
                        Format = "MEM",
                        CutlineWkt = circle.ToWkt(),
                        CutlineSrs = "EPSG:32748",
                        CropToCutline = true,
                        DestinationNoData = -9999,
                    });
                    result.Rasters.Add(new RasterLayer(clipped, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "Clipped"));
                    result.Vectors.Add(new StyledVector(VectorData.FromGeometries("Cutline", [circle]), VectorStyle.Lines(new Rgba(200, 85, 61), 2), "Cutline"));
                }
                var c = result.Rasters[0].Dataset;
                result.Metrics.Add(new(new("Output size", "Ukuran keluaran"), $"{c.RasterWidth} × {c.RasterHeight}", "px"));
                result.Metrics.Add(new(new("Valid pixels", "Piksel valid"), F(c.GetBand(1).ComputeStatistics().Mean, "0") + " m", "mean"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("shape", new("Cutline", "Cutline"), "Circle", "Circle", "Urban land cover"),
                SampleParameter.Number("radius", new("Circle radius (km)", "Radius lingkaran (km)"), 8, 2, 14, 0.5),
            ],
            Equivalent = "gdalwarp -cutline -crop_to_cutline",
        };

        yield return new GallerySample(
            "georeference",
            SampleCategory.Mosaic,
            new("Georeference with GCPs", "Georeferensi dengan GCP"),
            new("Take an image without coordinates (like a scanned map), attach ground control points and rectify it with a polynomial or thin plate spline transform.",
                "Ambil citra tanpa koordinat (seperti peta hasil scan), pasang titik kontrol tanah (GCP), lalu rektifikasi dengan transformasi polinomial atau thin plate spline."),
            """
            using var scan = Gdal.OpenRaster("scanned_map.png", update: true);
            scan.SetGcps(
            [
                new(pixel: 0,   line: 0,   x: 770_000, y: 9_256_000),
                new(pixel: 600, line: 0,   x: 806_000, y: 9_256_000),
                new(pixel: 0,   line: 450, x: 770_000, y: 9_229_000),
                new(pixel: 600, line: 450, x: 806_000, y: 9_229_000),
            ], SpatialReference.FromEpsg(32748));

            using var rectified = GdalUtils.Warp(scan, "rectified.tif",
                new WarpOptions { ThinPlateSpline = true, TargetSrs = "EPSG:32748" });
            """,
            (ctx, args) =>
            {
                using var scene = ctx.OpenScene();
                // Simulate a scanned, slightly rotated image without georeferencing.
                using var raw = GdalUtils.Translate(scene, "", new TranslateOptions { Format = "MEM", Bands = [4, 3, 2], OutputType = DataType.Byte, ScaleRange = (0, 4500, 0, 255) });
                using var scan = Gdal.CreateMemoryRaster(raw.RasterWidth, raw.RasterHeight, 3, DataType.Byte);
                for (var b = 1; b <= 3; b++) scan.GetBand(b).WriteAll<byte>(raw.GetBand(b).ReadAll<byte>());
                var w = scan.RasterWidth;
                var h = scan.RasterHeight;
                var jitter = args.Number("error", 0) * 30;
                var rnd = new Random(5);
                double J() => (rnd.NextDouble() - 0.5) * 2 * jitter;
                var gcps = new List<GroundControlPoint>
                {
                    new(0, 0, 770_000 + J(), 9_256_000 + J(), 0, "NW"),
                    new(w, 0, 806_000 + J(), 9_256_000 + J(), 0, "NE"),
                    new(0, h, 770_000 + J(), 9_229_000 + J(), 0, "SW"),
                    new(w, h, 806_000 + J(), 9_229_000 + J(), 0, "SE"),
                    new(w / 2.0, h / 2.0, 788_000 + J(), 9_242_500 + J(), 0, "C"),
                    new(w / 4.0, h * 0.75, 779_000 + J(), 9_235_750 + J(), 0, "P1"),
                    new(w * 0.8, h * 0.3, 798_800 + J(), 9_247_900 + J(), 0, "P2"),
                    new(w * 0.35, h * 0.15, 782_600 + J(), 9_251_950 + J(), 0, "P3"),
                    new(w * 0.65, h * 0.9, 793_400 + J(), 9_231_700 + J(), 0, "P4"),
                };
                using (var srs = SpatialReference.FromEpsg(32748)) scan.SetGcps(gcps, srs);
                var method = args.Get("method");
                var rectified = GdalUtils.Warp(scan, "", new WarpOptions
                {
                    Format = "MEM",
                    TargetSrs = "EPSG:32748",
                    ThinPlateSpline = method == "Thin plate spline",
                    PolynomialOrder = method switch { "Polynomial 1" => 1, "Polynomial 2" => 2, _ => null },
                    Resampling = ResampleAlgorithm.Bilinear,
                    AddAlpha = true,
                });
                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Rasters.Add(new RasterLayer(rectified, new RenderOptions { Mode = RenderMode.Rgb, Bands = [1, 2, 3], Stretch = StretchMode.None }, "Rectified"));
                var pts = gcps.Select(g => Geometry.Point(g.X, g.Y)).ToList();
                result.Vectors.Add(new StyledVector(VectorData.FromGeometries("GCPs", pts), VectorStyle.Points(new Rgba(242, 201, 76), 6) with { Stroke = new Rgba(18, 38, 58) }, "GCPs"));
                foreach (var p in pts) p.Dispose();
                result.Table = Table(["Id", "Pixel", "Line", "X", "Y"], gcps.Select(g => new[] { g.Id!, F(g.Pixel, "0.#"), F(g.Line, "0.#"), F(g.X, "#,0.0"), F(g.Y, "#,0.0") }));
                result.Metrics.Add(new(new("GCPs", "GCP"), gcps.Count.ToString()));
                result.Metrics.Add(new(new("Method", "Metode"), method));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("method", new("Transform", "Transformasi"), "Polynomial 1", "Polynomial 1", "Polynomial 2", "Thin plate spline"),
                SampleParameter.Number("error", new("GCP error (pixels)", "Galat GCP (piksel)"), 0, 0, 20, 1),
            ],
            Equivalent = "gdal_translate -gcp + gdalwarp -tps",
        };
    }

    private static IEnumerable<GallerySample> CloudSamples()
    {
        yield return new GallerySample(
            "vsi-memory-zip",
            SampleCategory.CloudVfs,
            new("In-memory files & ZIP archives", "File di memori & arsip ZIP"),
            new("Write datasets into /vsimem/ and straight into a ZIP archive, list the archive and read layers back out of it — no temporary files on disk.",
                "Tulis dataset ke /vsimem/ dan langsung ke arsip ZIP, lihat isi arsip, lalu baca kembali layernya — tanpa file sementara di disk."),
            """
            string zip = VirtualFileSystem.NewMemoryPath(".zip");
            using (GdalUtils.VectorTranslate(cities, VirtualFileSystem.Zip(zip, "cities.geojson"), ["-f", "GeoJSON"])) { }
            VirtualFileSystem.CopyFile("dem_bandung.tif", VirtualFileSystem.Zip(zip, "dem.tif"));

            foreach (var entry in VirtualFileSystem.ListDirectory(VirtualFileSystem.Zip(zip)))
                Console.WriteLine(entry);
            byte[] archive = VirtualFileSystem.ReadMemoryFile(zip);   // send it over HTTP, store it, ...
            """,
            (ctx, args) =>
            {
                using var cities = ctx.OpenCities();
                using var dem = ctx.OpenDem();
                var zip = VirtualFileSystem.NewMemoryPath(".zip");
                using (GdalUtils.VectorTranslate(cities, VirtualFileSystem.Zip(zip, "cities.geojson"), ["-f", "GeoJSON"])) { }
                using (GdalUtils.VectorTranslate(cities, VirtualFileSystem.Zip(zip, "cities.csv"), ["-f", "CSV", "-lco", "GEOMETRY=AS_XY"])) { }
                // GeoTIFF needs random access: write it to /vsimem/ first, then stream it into the archive.
                var tif = VirtualFileSystem.NewMemoryPath(".tif");
                using (GdalUtils.Translate(dem, tif, ["-of", "GTiff", "-outsize", "25%", "25%", "-co", "COMPRESS=DEFLATE"])) { }
                VirtualFileSystem.CopyFile(tif, VirtualFileSystem.Zip(zip, "dem_preview.tif"));
                VirtualFileSystem.Delete(tif);
                var entries = VirtualFileSystem.ListDirectory(VirtualFileSystem.Zip(zip));
                var size = VirtualFileSystem.ReadMemoryFile(zip).Length;
                var preview = Gdal.OpenRaster(VirtualFileSystem.Zip(zip, "dem_preview.tif"));
                var raster = GdalUtils.Translate(preview, "", ["-of", "MEM"]);
                preview.Dispose();
                var csv = VirtualFileSystem.ReadAllText(VirtualFileSystem.Zip(zip, "cities.csv"));
                VirtualFileSystem.Delete(zip);
                var result = new SampleResult { Crs = "EPSG:32748", Output = Truncate(csv, 3000), OutputLanguage = "text" };
                result.Rasters.Add(new RasterLayer(raster, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "dem_preview.tif (from ZIP)"));
                result.Table = Table(["Entry"], entries.Select(e => new[] { e }));
                result.Metrics.Add(new(new("Archive size", "Ukuran arsip"), FileSize(size)));
                result.Metrics.Add(new(new("Entries", "Entri"), entries.Count.ToString()));
                result.Metrics.Add(new(new("File systems", "File system"), VirtualFileSystem.Prefixes.Count.ToString()));
                return result;
            })
        { Equivalent = "/vsimem/ + /vsizip/" };

        yield return new GallerySample(
            "vsicurl",
            SampleCategory.CloudVfs,
            new("Stream a remote COG over HTTP", "Stream COG jarak jauh via HTTP"),
            new("Open a Cloud Optimized GeoTIFF directly from a URL with /vsicurl/. Only the header and the blocks/overviews needed for the view are downloaded. The same API works for /vsis3/, /vsiaz/ and /vsigs/.",
                "Buka Cloud Optimized GeoTIFF langsung dari URL dengan /vsicurl/. Hanya header dan blok/overview yang dibutuhkan yang diunduh. API yang sama berlaku untuk /vsis3/, /vsiaz/, dan /vsigs/."),
            """
            Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
            using var remote = Gdal.OpenRaster(VirtualFileSystem.Curl(
                "https://download.osgeo.org/gdal/data/gtiff/small_world.tif"));

            // Cloud buckets: credentials per bucket prefix
            VirtualFileSystem.ConfigureS3(accessKeyId: null, secretAccessKey: null, region: "us-west-2", noSign: true);
            using var sentinel = Gdal.OpenRaster("/vsis3/sentinel-cogs/sentinel-s2-l2a-cogs/48/M/YT/2024/8/S2A_48MYT_20240801_0_L2A/TCI.tif");
            """,
            (ctx, args) =>
            {
                Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
                Gdal.SetConfigOption("GDAL_HTTP_TIMEOUT", "20");
                var url = args.Get("url");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ds = Gdal.OpenRaster(url.StartsWith("/vsi", StringComparison.Ordinal) ? url : VirtualFileSystem.Curl(url));
                var opened = sw.Elapsed;
                var result = new SampleResult { Output = GdalUtils.Info(ds), Crs = ds.SpatialReference?.Epsg is { } e ? $"EPSG:{e}" : null };
                result.Rasters.Add(new RasterLayer(ds, new RenderOptions { Stretch = StretchMode.None }, "Remote raster"));
                result.Metrics.Add(new(new("Open time", "Waktu buka"), F(opened.TotalMilliseconds, "0"), "ms"));
                result.Metrics.Add(new(new("Size", "Ukuran"), $"{ds.RasterWidth} × {ds.RasterHeight} × {ds.RasterCount}"));
                result.Metrics.Add(new(new("Driver", "Driver"), ds.Driver.ShortName));
                return result;
            })
        {
            Parameters = [SampleParameter.TextBox("url", new("URL", "URL"), "https://download.osgeo.org/gdal/data/gtiff/small_world.tif")],
            RequiresNetwork = true,
            Equivalent = "/vsicurl/ /vsis3/ /vsiaz/ /vsigs/",
        };
    }

    private static IEnumerable<GallerySample> MultidimensionalSamples()
    {
        yield return new GallerySample(
            "climate-cube",
            SampleCategory.Multidimensional,
            new("Climate data cube (netCDF)", "Kubus data iklim (netCDF)"),
            new("Explore a time × lat × lon netCDF cube with the multidimensional API: browse variables and attributes, read a monthly slice and view it as a classic raster.",
                "Jelajahi kubus netCDF waktu × lintang × bujur dengan API multidimensi: telusuri variabel dan atribut, baca irisan bulanan, dan tampilkan sebagai raster klasik."),
            """
            using var ds = Gdal.OpenMultidimensional("climate_indonesia.nc");
            using var root = ds.RootGroup!;
            using var temperature = root.OpenArray("temperature");     // time × lat × lon
            Console.WriteLine(string.Join(" × ", temperature.Dimensions.Select(d => $"{d.Name}={d.Size}")));

            // March: one slice [2, 0..69, 0..189]
            var slice = new float[70 * 190];
            temperature.Read<float>(start: [2, 0, 0], count: [1, 70, 190], slice);

            // Or as a classic raster (one band per month) for rendering / warping
            using var classic = temperature.AsClassicDataset(xDimension: 2, yDimension: 1);
            """,
            (ctx, args) =>
            {
                ctx.EnsureData();
                var ds = Gdal.OpenMultidimensional(ctx.ClimatePath);
                var root = ds.RootGroup!;
                var variable = args.Get("variable");
                var arr = root.OpenArray(variable);
                var month = (int)args.Number("month", 1);
                var classic = arr.AsClassicDataset(2, 1);
                // Build a north-up georeferenced single-band raster for the chosen month.
                var band = classic.GetBand(month);
                var data = band.ReadAll<float>();
                var w = classic.RasterWidth;
                var h = classic.RasterHeight;
                var gt = classic.GeoTransform;
                if (gt.PixelHeight > 0)
                {
                    // Flip rows when latitude increases with the row index.
                    var flipped = new float[data.Length];
                    for (var r = 0; r < h; r++) Array.Copy(data, r * w, flipped, (h - 1 - r) * w, w);
                    data = flipped;
                }
                using var wgs = SpatialReference.Wgs84;
                var raster = Processing.RasterFactory.FromArray<float>(data, w, h,
                    GeoTransform.FromOrigin(SampleDataGenerator.ClimateWest, SampleDataGenerator.ClimateNorth, SampleDataGenerator.ClimateStep, SampleDataGenerator.ClimateStep), wgs);
                var stats = raster.GetBand(1).ComputeStatistics();
                var months = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
                var ramp = variable == "rainfall" ? ColorRamp.Ocean.Reversed() : ColorRamp.Turbo;
                var (lo, hi) = variable == "rainfall" ? (0.0, 450.0) : (20.0, 29.0);
                var result = new SampleResult
                {
                    Crs = "EPSG:4326",
                    Output = GdalUtils.MultiDimInfo(ds),
                    OutputLanguage = "json",
                    Histogram = raster.GetBand(1).GetHistogram(40, lo, hi),
                    HistogramTitle = new($"{variable} — {months[month - 1]}", $"{variable} — {months[month - 1]}"),
                };
                result.Rasters.Add(new RasterLayer(raster, new RenderOptions { Ramp = ramp, Stretch = StretchMode.Custom, Min = lo, Max = hi, Resampling = ResampleAlgorithm.Bilinear }, $"{variable} {months[month - 1]}", lo, hi));
                using (var cities = ctx.OpenCities())
                {
                    result.Vectors.Add(new StyledVector(VectorData.Load(cities.GetLayer(0)), VectorStyle.Points(new Rgba(18, 38, 58), 2.5) with { Stroke = new Rgba(255, 255, 255) }, "Cities"));
                }
                result.Table = Table(["Name", "Type / value"],
                    arr.Attributes.Select(a => new[] { a.Name, a.StringValue ?? F(a.NumericValue, "0.###") })
                        .Concat(arr.Dimensions.Select(d => new[] { $"dim:{d.Name}", $"{d.Size} ({d.Type ?? "-"})" })));
                result.Metrics.Add(new(new("Month", "Bulan"), months[month - 1]));
                result.Metrics.Add(new(new("Mean", "Rata-rata"), F(stats.Mean, "0.0"), arr.Unit));
                result.Metrics.Add(new(new("Range", "Rentang"), $"{F(stats.Minimum, "0.0")} – {F(stats.Maximum, "0.0")}", arr.Unit));
                result.Metrics.Add(new(new("Cube", "Kubus"), string.Join(" × ", arr.Dimensions.Select(d => d.Size))));
                classic.Dispose();
                arr.Dispose();
                root.Dispose();
                result.Owned.Add(ds);
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("variable", new("Variable", "Variabel"), "temperature", "temperature", "rainfall"),
                SampleParameter.Number("month", new("Month", "Bulan"), 1, 1, 12, 1),
            ],
            Equivalent = "gdalmdiminfo / gdalmdimtranslate",
        };
    }

    private static IEnumerable<GallerySample> UtilitySamples()
    {
        yield return new GallerySample(
            "gdal-cli",
            SampleCategory.Utilities,
            new("Run the gdal command line", "Jalankan command line gdal"),
            new("Every algorithm of the unified \"gdal\" CLI (GDAL 3.11+) runs in-process: raster info, reproject, calc, tile, vector filter, pipelines…",
                "Semua algoritma CLI \"gdal\" terpadu (GDAL 3.11+) berjalan di dalam proses: raster info, reproject, calc, tile, vector filter, pipeline…"),
            """
            GdalCliResult info = GdalCli.Run("raster", "info", "--format", "text", "dem_bandung.tif");
            Console.WriteLine(info.Output);

            GdalCli.Run("raster", "reproject", "--output-crs", "EPSG:4326", "dem_bandung.tif", "dem_wgs84.tif");
            string usage = GdalCli.GetUsageJson("raster", "tile");
            """,
            (ctx, args) =>
            {
                ctx.EnsureData();
                var line = args.Get("command").Replace("{dem}", ctx.DemPath).Replace("{cities}", ctx.CitiesPath).Replace("{scratch}", ctx.ScratchDirectory);
                var parts = SplitArgs(line);
                if (parts.Count > 0 && parts[0] == "gdal") parts.RemoveAt(0);
                var r = GdalCli.Run([.. parts], null);
                var result = new SampleResult { Output = r.Output ?? "(done — no text output)", OutputLanguage = (r.Output ?? "").TrimStart().StartsWith('{') ? "json" : "text" };
                if (r.Dataset is { RasterCount: > 0 } ds)
                {
                    result.Rasters.Add(new RasterLayer(ds, new RenderOptions { Ramp = ColorRamp.Terrain }, "Output"));
                }
                else
                {
                    r.Dataset?.Dispose();
                }
                result.Metrics.Add(new(new("Algorithms", "Algoritma"), string.Join(", ", GdalCli.AlgorithmNames)));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("command", new("Command", "Perintah"), "gdal raster info --format text {dem}",
                    "gdal raster info --format text {dem}",
                    "gdal vector info --format text {cities}",
                    "gdal raster info --format json --stats {dem}",
                    "gdal vector sql --sql \"SELECT island, COUNT(*) AS n FROM cities GROUP BY island\" --output-format MEM {cities} out",
                    "gdal raster reproject --output-crs EPSG:4326 --output-format MEM {dem} out",
                    "gdal driver --help-doc"),
            ],
            Equivalent = "gdal (unified CLI)",
        };

        yield return new GallerySample(
            "drivers",
            SampleCategory.Utilities,
            new("Formats & drivers", "Format & driver"),
            new("List every raster, vector and multidimensional driver compiled into the native GDAL build, with their capabilities and file extensions.",
                "Daftar semua driver raster, vektor, dan multidimensi pada build GDAL native, lengkap dengan kemampuan dan ekstensi file."),
            """
            foreach (var driver in Gdal.Drivers)
            {
                Console.WriteLine($"{driver.ShortName,-16} {driver.LongName}  " +
                    $"raster={driver.SupportsRaster} vector={driver.SupportsVector} create={driver.SupportsCreate}");
            }
            Console.WriteLine($"GDAL {Gdal.Version}, PROJ {Gdal.ProjVersion}");
            """,
            (ctx, args) =>
            {
                var filter = args.Get("filter");
                var drivers = Gdal.Drivers.Where(d => filter switch
                {
                    "Raster" => d.SupportsRaster,
                    "Vector" => d.SupportsVector,
                    "Multidimensional" => d.SupportsMultidimensional,
                    "Writable" => d.SupportsCreate || d.SupportsCreateCopy,
                    _ => true,
                }).ToList();
                string Y(bool b) => b ? "●" : "·";
                var result = new SampleResult
                {
                    Table = Table(["Driver", "Name", "Raster", "Vector", "MDim", "Create", "Extensions"],
                        drivers.Select(d => new[] { d.ShortName, d.LongName, Y(d.SupportsRaster), Y(d.SupportsVector), Y(d.SupportsMultidimensional), Y(d.SupportsCreate || d.SupportsCreateCopy), string.Join(' ', d.Extensions) })),
                    Output = Gdal.BuildInfo,
                };
                result.Metrics.Add(new(new("Drivers", "Driver"), drivers.Count.ToString()));
                result.Metrics.Add(new(new("GDAL", "GDAL"), Gdal.Version));
                result.Metrics.Add(new(new("PROJ", "PROJ"), Gdal.ProjVersion.ToString()));
                result.Metrics.Add(new(new("Kernels", "Kernel"), Processing.Kernels.Backend));
                return result;
            })
        {
            Parameters = [SampleParameter.Choice("filter", new("Show", "Tampilkan"), "All", "All", "Raster", "Vector", "Multidimensional", "Writable")],
            Equivalent = "gdalinfo --formats / ogrinfo --formats",
        };

        yield return new GallerySample(
            "tiles",
            SampleCategory.Utilities,
            new("Web map tiles (XYZ)", "Tile peta web (XYZ)"),
            new("Cut a raster into Web Mercator XYZ tiles ready for Leaflet, OpenLayers or MapLibre — or serve them on demand from an endpoint.",
                "Potong raster menjadi tile XYZ Web Mercator siap untuk Leaflet, OpenLayers, atau MapLibre — atau sajikan sesuai permintaan dari endpoint."),
            """
            using var tiles = new TileGenerator(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true });
            int written = tiles.Generate("tiles/", minZoom: 9, maxZoom: 13);   // + leaflet.html preview

            // ASP.NET Core endpoint
            app.MapGet("/tiles/{z}/{x}/{y}.png", (int z, int x, int y) =>
                tiles.GetTilePng(new TileId(z, x, y)) is { } png ? Results.File(png, "image/png") : Results.NotFound());
            """,
            (ctx, args) =>
            {
                using var dem = ctx.OpenDem();
                var tiles = new TileGenerator(dem, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true });
                var zoom = (int)args.Number("zoom", 11);
                var ids = TileGenerator.TilesCovering(tiles.Extent, zoom).ToList();
                // Mosaic of the zoom level as a raster in EPSG:3857 so the gallery can display the actual tiles.
                var minX = ids.Min(t => t.X);
                var minY = ids.Min(t => t.Y);
                var cols = ids.Max(t => t.X) - minX + 1;
                var rows = ids.Max(t => t.Y) - minY + 1;
                var size = tiles.TileSize;
                var canvas = new byte[cols * size * rows * size * 4];
                foreach (var id in ids)
                {
                    var img = tiles.RenderTile(id);
                    if (img is null) continue;
                    for (var y = 0; y < size; y++)
                    {
                        Buffer.BlockCopy(img.Pixels, y * size * 4, canvas, (((id.Y - minY) * size + y) * cols * size + (id.X - minX) * size) * 4, size * 4);
                    }
                }
                var mosaic = new RgbaImage(cols * size, rows * size, canvas).ToDataset();
                var nw = TileGenerator.TileBounds(new TileId(zoom, minX, minY));
                mosaic.GeoTransform = GeoTransform.FromOrigin(nw.MinX, nw.MaxY, nw.Width / size, nw.Height / size);
                using (var merc = SpatialReference.WebMercator) mosaic.SpatialReference = merc;
                var outlines = ids.Select(t => Geometry.FromEnvelope(TileGenerator.TileBounds(t))).ToList();
                var result = new SampleResult { Crs = "EPSG:3857" };
                result.Rasters.Add(new RasterLayer(mosaic, new RenderOptions { Mode = RenderMode.Rgb, Stretch = StretchMode.None }, $"Zoom {zoom}"));
                result.Vectors.Add(new StyledVector(VectorData.FromGeometries("Tiles", outlines), VectorStyle.Lines(new Rgba(18, 38, 58, 160), 1), "Tile grid"));
                foreach (var g in outlines) g.Dispose();
                result.Table = Table(["z/x/y", "Bounds (EPSG:3857)"], ids.Take(200).Select(t => new[] { t.ToString(), TileGenerator.TileBounds(t).ToString() }));
                result.Metrics.Add(new(new("Tiles at this zoom", "Tile di zoom ini"), ids.Count.ToString()));
                result.Metrics.Add(new(new("Native zoom", "Zoom asli"), tiles.NativeZoom.ToString()));
                result.Owned.Add(tiles);
                return result;
            })
        {
            Parameters = [SampleParameter.Number("zoom", new("Zoom level", "Level zoom"), 11, 8, 13, 1)],
            Equivalent = "gdal2tiles / gdal raster tile",
        };
    }

    /// <summary>Splits a command line honoring double quotes.</summary>
    internal static List<string> SplitArgs(string line)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (sb.Length > 0) { list.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        if (sb.Length > 0) list.Add(sb.ToString());
        return list;
    }
}
