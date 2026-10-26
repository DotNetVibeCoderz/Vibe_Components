using GDAL.Net.Processing;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net.Gallery;

/// <summary>
/// Sample datasets of the gallery, generated on first use with GDAL.Net itself (no downloads):
/// a DEM of a volcanic basin modelled on Bandung (UTM 48S), a 4-band Sentinel-2-like scene of the same area,
/// Indonesian cities, land cover polygons, DEM tiles for mosaics and a monthly climate cube (netCDF).
/// </summary>
public sealed class SampleContext
{
    /// <summary>Bump when the generated data changes so old caches are regenerated.</summary>
    private const string DataVersion = "3";

    public SampleContext(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, "gallery-data");
    }

    public string DataDirectory { get; }

    public string DemPath => Path.Combine(DataDirectory, "dem_bandung.tif");
    public string ScenePath => Path.Combine(DataDirectory, "scene_bandung.tif");
    public string CitiesPath => Path.Combine(DataDirectory, "indonesia_cities.gpkg");
    public string LandCoverPath => Path.Combine(DataDirectory, "land_cover.gpkg");
    public string ClimatePath => Path.Combine(DataDirectory, "climate_indonesia.nc");
    public string TilesDirectory => Path.Combine(DataDirectory, "dem_tiles");
    public string ScratchDirectory => Path.Combine(DataDirectory, "scratch");

    /// <summary>Approximate Bandung city center in UTM 48S.</summary>
    public static (double X, double Y) BandungUtm => (788_600, 9_235_000);

    /// <summary>Tangkuban Perahu summit (the DEM's highest peak) in UTM 48S.</summary>
    public static (double X, double Y) VolcanoUtm => (787_500, 9_251_500);

    private readonly Lock _lock = new();
    private bool _ready;

    /// <summary>Generates the sample data when missing. Safe to call repeatedly / concurrently.</summary>
    public void EnsureData(IProgress<string>? progress = null)
    {
        lock (_lock)
        {
            if (_ready) return;
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(ScratchDirectory);
            var stamp = Path.Combine(DataDirectory, ".version");
            var upToDate = File.Exists(stamp) && File.ReadAllText(stamp) == DataVersion;
            if (!upToDate || !File.Exists(DemPath))
            {
                progress?.Report("DEM");
                SampleDataGenerator.CreateDem(DemPath);
            }
            if (!upToDate || !File.Exists(ScenePath))
            {
                progress?.Report("Scene");
                SampleDataGenerator.CreateScene(DemPath, ScenePath);
            }
            if (!upToDate || !File.Exists(CitiesPath))
            {
                progress?.Report("Cities");
                SampleDataGenerator.CreateCities(CitiesPath);
            }
            if (!upToDate || !File.Exists(LandCoverPath))
            {
                progress?.Report("Land cover");
                SampleDataGenerator.CreateLandCover(ScenePath, DemPath, LandCoverPath);
            }
            if (!upToDate || !Directory.Exists(TilesDirectory))
            {
                progress?.Report("Tiles");
                SampleDataGenerator.CreateTiles(DemPath, TilesDirectory);
            }
            if (!upToDate || !File.Exists(ClimatePath))
            {
                progress?.Report("Climate cube");
                SampleDataGenerator.CreateClimateCube(ClimatePath);
            }
            File.WriteAllText(stamp, DataVersion);
            _ready = true;
        }
    }

    public Dataset OpenDem() { EnsureData(); return Gdal.OpenRaster(DemPath); }
    public Dataset OpenScene() { EnsureData(); return Gdal.OpenRaster(ScenePath); }
    public Dataset OpenCities() { EnsureData(); return Gdal.OpenVector(CitiesPath); }
    public Dataset OpenLandCover() { EnsureData(); return Gdal.OpenVector(LandCoverPath); }

    /// <summary>A unique scratch file path (deleted when the app restarts).</summary>
    public string Scratch(string fileName) => Path.Combine(ScratchDirectory, $"{Guid.NewGuid():N}_{fileName}");
}

/// <summary>Procedural generators for the gallery data.</summary>
public static class SampleDataGenerator
{
    public const int DemWidth = 1200;
    public const int DemHeight = 900;
    public const double DemPixel = 30;
    public const double DemOriginX = 770_000;
    public const double DemOriginY = 9_256_000;

    // ------------------------------------------------------------------ noise

    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return h / (double)uint.MaxValue;
        }
    }

    private static double ValueNoise(double x, double y, int seed)
    {
        var xi = (int)Math.Floor(x);
        var yi = (int)Math.Floor(y);
        var xf = x - xi;
        var yf = y - yi;
        var u = xf * xf * (3 - 2 * xf);
        var v = yf * yf * (3 - 2 * yf);
        var a = Hash(xi, yi, seed);
        var b = Hash(xi + 1, yi, seed);
        var c = Hash(xi, yi + 1, seed);
        var d = Hash(xi + 1, yi + 1, seed);
        return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
    }

    /// <summary>Fractal Brownian motion in [0, 1].</summary>
    public static double Fbm(double x, double y, int octaves = 6, int seed = 7)
    {
        double sum = 0, amp = 0.5, freq = 1, norm = 0;
        for (var i = 0; i < octaves; i++)
        {
            sum += amp * ValueNoise(x * freq, y * freq, seed + i * 31);
            norm += amp;
            amp *= 0.5;
            freq *= 2.03;
        }
        return sum / norm;
    }

    // ------------------------------------------------------------------ DEM

    /// <summary>Elevation (m) at a UTM 48S position: basin floor, a northern stratovolcano with a crater, southern ridges.</summary>
    public static double Elevation(double x, double y)
    {
        var (vx, vy) = SampleContext.VolcanoUtm;
        var (bx, by) = SampleContext.BandungUtm;

        // Stratovolcano: concave cone + crater.
        var dv = Math.Sqrt((x - vx) * (x - vx) + (y - vy) * (y - vy)) / 1000.0;
        var cone = 1450 * Math.Exp(-Math.Pow(dv / 6.5, 1.25));
        var crater = -160 * Math.Exp(-Math.Pow(dv / 0.6, 2));

        // Southern ridge belt rising towards the map's south edge.
        var south = Math.Clamp((9_238_000 - y) / 9_000.0, 0, 1);
        var ridge = 520 * south * south * (0.6 + 0.4 * Math.Sin((x - 770_000) / 2_600.0));

        // Flat lacustrine basin around the city (ancient Lake Bandung).
        var db = Math.Sqrt((x - bx) * (x - bx) + 1.6 * (y - by) * (y - by)) / 1000.0;
        var basin = -90 * Math.Exp(-Math.Pow(db / 7, 2));

        var nx = (x - 770_000) / 4_000.0;
        var ny = (9_256_000 - y) / 4_000.0;
        var hills = 330 * Math.Pow(Fbm(nx, ny), 2.2) * (0.35 + 0.65 * Math.Min(1, dv / 9));
        var detail = 25 * (Fbm(nx * 6, ny * 6, 4, 99) - 0.5);

        // Drainage: a meandering river valley crossing the basin from east to west.
        var riverY = 9_231_500 + 1_800 * Math.Sin((x - 770_000) / 5_500.0);
        var river = -35 * Math.Exp(-Math.Pow((y - riverY) / 350.0, 2));

        return 690 + cone + crater + ridge + basin + hills + detail + river;
    }

    public static void CreateDem(string path)
    {
        var data = new float[DemWidth * DemHeight];
        Parallel.For(0, DemHeight, row =>
        {
            var y = DemOriginY - (row + 0.5) * DemPixel;
            for (var col = 0; col < DemWidth; col++)
            {
                var x = DemOriginX + (col + 0.5) * DemPixel;
                data[row * DemWidth + col] = (float)Elevation(x, y);
            }
        });

        using var srs = SpatialReference.FromEpsg(32748);
        using var mem = RasterFactory.FromArray<float>(data, DemWidth, DemHeight, GeoTransform.FromOrigin(DemOriginX, DemOriginY, DemPixel, DemPixel), srs, -9999);
        mem.GetBand(1).UnitType = "m";
        mem.SetMetadataItem("TIFFTAG_IMAGEDESCRIPTION", "Synthetic DEM of a volcanic basin modelled on Bandung, West Java — GDAL.Net Gallery");
        mem.SetMetadataItem("TIFFTAG_ARTIST", "Gravicode Studios");
        if (File.Exists(path)) Gdal.GetDriver("GTiff").Delete(path);
        using (GdalUtils.Translate(mem, path, new TranslateOptions
        {
            Format = "COG",
            CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE", ["PREDICTOR"] = "YES", ["OVERVIEWS"] = "AUTO", ["BLOCKSIZE"] = "256" },
        })) { }
    }

    // ------------------------------------------------------------------ multispectral scene

    /// <summary>Land cover classes used by the scene and the land cover layer.</summary>
    public static readonly (int Code, string Name, string NameId, string Color, float[] Reflectance)[] LandCoverClasses =
    [
        (1, "Water", "Air", "#2E86AB", [0.060f, 0.055f, 0.035f, 0.020f]),
        (2, "Urban", "Permukiman", "#C8553D", [0.120f, 0.130f, 0.150f, 0.210f]),
        (3, "Cropland", "Pertanian", "#E9C46A", [0.055f, 0.095f, 0.075f, 0.300f]),
        (4, "Forest", "Hutan", "#2D6A4F", [0.030f, 0.060f, 0.035f, 0.420f]),
        (5, "Bare / volcanic", "Lahan terbuka / vulkanik", "#8D7B68", [0.140f, 0.150f, 0.170f, 0.230f]),
    ];

    /// <summary>Land cover class at a pixel, from elevation + noise (deterministic).</summary>
    public static int ClassifyLand(double x, double y, double elevation)
    {
        var (bx, by) = SampleContext.BandungUtm;
        var (vx, vy) = SampleContext.VolcanoUtm;
        var nx = (x - 770_000) / 3_000.0;
        var ny = (9_256_000 - y) / 3_000.0;
        var n = Fbm(nx, ny, 5, 1234);

        var dv = Math.Sqrt((x - vx) * (x - vx) + (y - vy) * (y - vy));
        if (dv < 900) return 5;

        // Reservoirs in the lowest valley floor + a lake west of the city.
        var lake = Math.Pow((x - 774_500) / 1_900.0, 2) + Math.Pow((y - 9_233_500) / 1_100.0, 2);
        if (lake < 1 + 0.25 * (n - 0.5)) return 1;

        var db = Math.Sqrt((x - bx) * (x - bx) + 1.4 * (y - by) * (y - by));
        if (db < 5_200 + 2_200 * (n - 0.5) && elevation < 900) return 2;
        if (elevation > 1_250 + 200 * (n - 0.5)) return 4;
        return n > 0.62 ? 4 : 3;
    }

    public static void CreateScene(string demPath, string path)
    {
        const int factor = 2; // 60 m pixels
        var w = DemWidth / factor;
        var h = DemHeight / factor;
        var bands = new ushort[4][];
        for (var b = 0; b < 4; b++) bands[b] = new ushort[w * h];

        Parallel.For(0, h, row =>
        {
            var y = DemOriginY - (row + 0.5) * DemPixel * factor;
            for (var col = 0; col < w; col++)
            {
                var x = DemOriginX + (col + 0.5) * DemPixel * factor;
                var elev = Elevation(x, y);
                var cls = ClassifyLand(x, y, elev);
                var refl = LandCoverClasses[cls - 1].Reflectance;
                // Illumination from the terrain slope + sensor noise.
                var shade = 0.85 + 0.3 * (Fbm(x / 900.0, y / 900.0, 3, 77) - 0.5);
                for (var b = 0; b < 4; b++)
                {
                    var noise = 1 + 0.12 * (Hash(col, row, 500 + b) - 0.5);
                    bands[b][row * w + col] = (ushort)Math.Clamp(refl[b] * shade * noise * 10_000, 1, 10_000);
                }
            }
        });

        using var mem = Gdal.CreateMemoryRaster(w, h, 4, DataType.UInt16);
        mem.GeoTransform = GeoTransform.FromOrigin(DemOriginX, DemOriginY, DemPixel * factor, DemPixel * factor);
        using (var srs = SpatialReference.FromEpsg(32748)) mem.SpatialReference = srs;
        string[] names = ["B2 Blue", "B3 Green", "B4 Red", "B8 NIR"];
        ColorInterpretation[] interp = [ColorInterpretation.Blue, ColorInterpretation.Green, ColorInterpretation.Red, ColorInterpretation.NearInfrared];
        for (var b = 0; b < 4; b++)
        {
            var band = mem.GetBand(b + 1);
            band.WriteAll<ushort>(bands[b]);
            band.Description = names[b];
            band.ColorInterpretation = interp[b];
            band.Scale = 0.0001;
        }
        mem.SetMetadataItem("SENSOR", "Synthetic Sentinel-2 L2A-like (GDAL.Net Gallery)");
        if (File.Exists(path)) Gdal.GetDriver("GTiff").Delete(path);
        using (GdalUtils.Translate(mem, path, new TranslateOptions
        {
            Format = "GTiff",
            CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "ZSTD", ["TILED"] = "YES", ["PHOTOMETRIC"] = "MINISBLACK" },
        })) { }
    }

    // ------------------------------------------------------------------ vector data

    /// <summary>Major Indonesian cities (approximate coordinates, city-proper population).</summary>
    public static readonly (string Name, string Province, string Island, double Lon, double Lat, long Population)[] Cities =
    [
        ("Jakarta", "DKI Jakarta", "Java", 106.8456, -6.2088, 10_679_951),
        ("Surabaya", "Jawa Timur", "Java", 112.7521, -7.2575, 2_874_314),
        ("Bekasi", "Jawa Barat", "Java", 106.9896, -6.2383, 2_543_676),
        ("Bandung", "Jawa Barat", "Java", 107.6191, -6.9175, 2_444_160),
        ("Medan", "Sumatera Utara", "Sumatra", 98.6722, 3.5952, 2_435_252),
        ("Depok", "Jawa Barat", "Java", 106.8186, -6.4025, 2_056_335),
        ("Tangerang", "Banten", "Java", 106.6300, -6.1783, 1_895_486),
        ("Palembang", "Sumatera Selatan", "Sumatra", 104.7458, -2.9761, 1_668_848),
        ("Semarang", "Jawa Tengah", "Java", 110.4203, -6.9667, 1_653_524),
        ("Makassar", "Sulawesi Selatan", "Sulawesi", 119.4327, -5.1477, 1_423_877),
        ("Batam", "Kepulauan Riau", "Riau Islands", 104.0305, 1.0456, 1_196_396),
        ("Bandar Lampung", "Lampung", "Sumatra", 105.2667, -5.4500, 1_184_949),
        ("Bogor", "Jawa Barat", "Java", 106.7972, -6.5950, 1_043_070),
        ("Pekanbaru", "Riau", "Sumatra", 101.4478, 0.5071, 983_356),
        ("Padang", "Sumatera Barat", "Sumatra", 100.3543, -0.9471, 909_040),
        ("Malang", "Jawa Timur", "Java", 112.6304, -7.9666, 843_810),
        ("Samarinda", "Kalimantan Timur", "Kalimantan", 117.1536, -0.5022, 827_994),
        ("Denpasar", "Bali", "Bali", 115.2167, -8.6500, 725_314),
        ("Balikpapan", "Kalimantan Timur", "Kalimantan", 116.8312, -1.2379, 688_318),
        ("Banjarmasin", "Kalimantan Selatan", "Kalimantan", 114.5908, -3.3194, 657_663),
        ("Pontianak", "Kalimantan Barat", "Kalimantan", 109.3333, -0.0263, 658_685),
        ("Jambi", "Jambi", "Sumatra", 103.6131, -1.6101, 606_200),
        ("Manado", "Sulawesi Utara", "Sulawesi", 124.8421, 1.4748, 451_916),
        ("Kupang", "Nusa Tenggara Timur", "Nusa Tenggara", 123.5970, -10.1772, 442_758),
        ("Mataram", "Nusa Tenggara Barat", "Nusa Tenggara", 116.1165, -8.5833, 429_651),
        ("Jayapura", "Papua", "Papua", 140.7181, -2.5337, 398_478),
        ("Yogyakarta", "DI Yogyakarta", "Java", 110.3695, -7.7956, 373_589),
        ("Palu", "Sulawesi Tengah", "Sulawesi", 119.8707, -0.8917, 373_218),
        ("Ambon", "Maluku", "Maluku", 128.1814, -3.6954, 347_288),
        ("Kendari", "Sulawesi Tenggara", "Sulawesi", 122.5150, -3.9985, 345_107),
        ("Banda Aceh", "Aceh", "Sumatra", 95.3238, 5.5483, 252_899),
        ("Ternate", "Maluku Utara", "Maluku", 127.3836, 0.7893, 205_001),
    ];

    public static void CreateCities(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var ds = Gdal.GetDriver("GPKG").CreateVector(path);
        using var srs = SpatialReference.Wgs84;
        var layer = ds.CreateLayer("cities", srs, GeometryType.Point);
        layer.CreateField("name", FieldType.String, 40);
        layer.CreateField("province", FieldType.String, 40);
        layer.CreateField("island", FieldType.String, 20);
        layer.CreateField("population", FieldType.Integer64);
        using var tx = ds.BeginTransaction();
        foreach (var c in Cities)
        {
            using var p = Geometry.Point(c.Lon, c.Lat);
            layer.AddFeature(p, new Dictionary<string, object?>
            {
                ["name"] = c.Name,
                ["province"] = c.Province,
                ["island"] = c.Island,
                ["population"] = c.Population,
            });
        }
        tx.Commit();
    }

    /// <summary>Classifies the scene, removes speckles (sieve) and polygonizes it into a land cover GeoPackage.</summary>
    public static void CreateLandCover(string scenePath, string demPath, string path)
    {
        using var scene = Gdal.OpenRaster(scenePath);
        var w = scene.RasterWidth / 2;
        var h = scene.RasterHeight / 2;
        using var classes = Gdal.CreateMemoryRaster(w, h, 1, DataType.Byte);
        var gt = scene.GeoTransform;
        classes.GeoTransform = gt with { PixelWidth = gt.PixelWidth * 2, PixelHeight = gt.PixelHeight * 2 };
        using (var srs = scene.SpatialReference) classes.SpatialReference = srs;
        var px = new byte[w * h];
        var cgt = classes.GeoTransform;
        for (var row = 0; row < h; row++)
        {
            for (var col = 0; col < w; col++)
            {
                var (x, y) = cgt.PixelToWorld(col + 0.5, row + 0.5);
                px[row * w + col] = (byte)ClassifyLand(x, y, Elevation(x, y));
            }
        }
        var band = classes.GetBand(1);
        band.WriteAll<byte>(px);
        RasterAlgorithms.Sieve(band, band, 12, eightConnected: true);

        if (File.Exists(path)) File.Delete(path);
        using var ds = Gdal.GetDriver("GPKG").CreateVector(path);
        using var srs2 = SpatialReference.FromEpsg(32748);
        var layer = ds.CreateLayer("land_cover", srs2, GeometryType.Polygon);
        layer.CreateField("class", FieldType.Integer);
        layer.CreateField("name", FieldType.String, 30);
        layer.CreateField("area_ha", FieldType.Real);
        using (var tx = ds.BeginTransaction())
        {
            RasterAlgorithms.Polygonize(band, layer, 0, eightConnected: true);
            tx.Commit();
        }
        using (var tx = ds.BeginTransaction())
        {
            var updates = new List<(long Fid, int Class, double Area)>();
            layer.ForEach(f => updates.Add((f.Id, f.GetInt(0), f.Geometry!.Area / 10_000)));
            foreach (var (fid, cls, area) in updates)
            {
                using var f = layer.GetFeature(fid)!;
                f["name"] = LandCoverClasses[Math.Clamp(cls, 1, 5) - 1].Name;
                f["area_ha"] = Math.Round(area, 2);
                layer.SetFeature(f);
            }
            tx.Commit();
        }
    }

    /// <summary>Splits the DEM into 2×2 GeoTIFF tiles for the mosaic samples.</summary>
    public static void CreateTiles(string demPath, string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        using var dem = Gdal.OpenRaster(demPath);
        var tw = dem.RasterWidth / 2;
        var th = dem.RasterHeight / 2;
        for (var ty = 0; ty < 2; ty++)
        {
            for (var tx = 0; tx < 2; tx++)
            {
                var file = Path.Combine(directory, $"dem_r{ty}_c{tx}.tif");
                using (GdalUtils.Translate(dem, file, new TranslateOptions
                {
                    Format = "GTiff",
                    SourceWindow = (tx * tw, ty * th, tw, th),
                    CreationOptions = new Dictionary<string, string> { ["COMPRESS"] = "DEFLATE", ["TILED"] = "YES" },
                })) { }
            }
        }
    }

    // ------------------------------------------------------------------ climate cube

    public const int ClimateMonths = 12;
    public const int ClimateLat = 70;
    public const int ClimateLon = 190;
    public const double ClimateWest = 94.5;
    public const double ClimateNorth = 6.5;
    public const double ClimateStep = 0.25;

    /// <summary>Monthly mean near-surface temperature (°C) and rainfall (mm) over Indonesia (synthetic, smooth seasonal patterns).</summary>
    public static void CreateClimateCube(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var ds = Gdal.GetDriver("netCDF").CreateMultidimensional(path);
        using var root = ds.RootGroup!;
        root.SetAttribute("title", "Synthetic monthly climate of Indonesia — GDAL.Net Gallery");
        root.SetAttribute("institution", "Gravicode Studios");

        var dims = new (string, ulong, string?, string?)[]
        {
            ("time", ClimateMonths, "TEMPORAL", null),
            ("lat", ClimateLat, "HORIZONTAL_Y", "NORTH"),
            ("lon", ClimateLon, "HORIZONTAL_X", "EAST"),
        };

        using (var time = root.CreateArray("time", DataType.Float64, [dims[0]]))
        {
            time.WriteAll<double>(Enumerable.Range(0, ClimateMonths).Select(m => (double)m).ToArray());
            time.SetAttribute("units", "months since 2025-01-01");
        }
        using (var lat = root.CreateArray("lat", DataType.Float64, [dims[1]]))
        {
            lat.WriteAll<double>(Enumerable.Range(0, ClimateLat).Select(i => ClimateNorth - (i + 0.5) * ClimateStep).ToArray());
            lat.SetAttribute("units", "degrees_north");
        }
        using (var lon = root.CreateArray("lon", DataType.Float64, [dims[2]]))
        {
            lon.WriteAll<double>(Enumerable.Range(0, ClimateLon).Select(i => ClimateWest + (i + 0.5) * ClimateStep).ToArray());
            lon.SetAttribute("units", "degrees_east");
        }

        var temp = new float[ClimateMonths * ClimateLat * ClimateLon];
        var rain = new float[temp.Length];
        Parallel.For(0, ClimateMonths, m =>
        {
            for (var i = 0; i < ClimateLat; i++)
            {
                var la = ClimateNorth - (i + 0.5) * ClimateStep;
                for (var j = 0; j < ClimateLon; j++)
                {
                    var lo = ClimateWest + (j + 0.5) * ClimateStep;
                    var idx = (m * ClimateLat + i) * ClimateLon + j;
                    var terrain = Fbm(lo / 2.5, la / 2.5, 4, 11);
                    var highland = Math.Max(0, terrain - 0.55) * 2600; // pseudo elevation (m)
                    // Austral summer monsoon (Dec–Mar) is wet in the south; dry season (Jun–Sep) in Nusa Tenggara.
                    var season = Math.Cos(2 * Math.PI * (m - 0.5) / 12.0);
                    temp[idx] = (float)(27.2 - 0.0065 * highland + 0.9 * season * Math.Sin(la * Math.PI / 30) - 0.03 * Math.Abs(la) + 0.6 * (terrain - 0.5));
                    var southFactor = Math.Clamp((-la + 2) / 10.0, 0, 1);
                    rain[idx] = (float)Math.Max(5, 210 + 160 * season * southFactor - 120 * (1 - season) * southFactor * Math.Clamp((lo - 114) / 10, 0, 1) + 140 * (terrain - 0.5) + 60 * (1 - southFactor));
                }
            }
        });

        using (var t = root.CreateArray("temperature", DataType.Float32, dims))
        {
            t.WriteAll<float>(temp);
            t.Unit = "degC";
            t.SetAttribute("long_name", "Monthly mean near-surface air temperature");
        }
        using (var r = root.CreateArray("rainfall", DataType.Float32, dims))
        {
            r.WriteAll<float>(rain);
            r.Unit = "mm";
            r.SetAttribute("long_name", "Monthly rainfall");
        }
    }
}
