using System.Globalization;
using System.Text;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NetTopologySuite.Geometries;
using NetTopologySuite.Triangulate;
using RasterGrid = GeoPandas.Net.Raster.Raster;
using GeoTransform = GeoPandas.Net.Raster.GeoTransform;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Datasets;

/// <summary>
/// Reproducible sample data around a fictional city "Kota Nusantara" (UTM zone 48S, EPSG:32748, near Jakarta).
/// All generators are deterministic for a given seed.
/// </summary>
public static class SampleData
{
    /// <summary>CRS of all synthetic layers (WGS 84 / UTM zone 48S).</summary>
    public static CoordinateReferenceSystem Crs => CoordinateReferenceSystem.FromEpsg(32748);
    /// <summary>City centre (UTM metres).</summary>
    public const double CenterX = 702_000, CenterY = 9_312_000;
    /// <summary>Approximate city radius (m).</summary>
    public const double Radius = 9_000;

    private static readonly string[] DistrictNames =
    [
        "Menteng", "Kemang", "Cempaka", "Sawah Besar", "Tanah Abang", "Gambir", "Senen", "Kebayoran", "Cilandak", "Pasar Minggu",
        "Jatinegara", "Duren Sawit", "Cakung", "Kelapa Gading", "Koja", "Tanjung Priok", "Penjaringan", "Grogol", "Kembangan", "Cengkareng",
        "Kalideres", "Palmerah", "Setiabudi", "Tebet", "Pancoran", "Mampang", "Pesanggrahan", "Ciracas", "Cipayung", "Makasar",
        "Matraman", "Pulogadung", "Johar Baru", "Kemayoran", "Tambora", "Taman Sari", "Sukmajaya", "Beji", "Cimanggis", "Sawangan"
    ];

    /// <summary>The city boundary (irregular polygon).</summary>
    public static Polygon CityBoundary(int seed = 7)
    {
        var rng = new Random(seed);
        var pts = new (double, double)[72];
        double phase = rng.NextDouble() * Math.PI;
        for (int i = 0; i < pts.Length; i++)
        {
            double a = 2 * Math.PI * i / pts.Length;
            double r = Radius * (0.82 + 0.12 * Math.Sin(3 * a + phase) + 0.06 * Math.Cos(5 * a) + 0.03 * (rng.NextDouble() - 0.5));
            pts[i] = (CenterX + r * Math.Cos(a) * 1.15, CenterY + r * Math.Sin(a) * 0.9);
        }
        return Geo.Polygon(pts);
    }

    /// <summary>Administrative districts (Voronoi cells clipped to the boundary) with demographic attributes.</summary>
    public static GeoDataFrame Districts(int count = 32, int seed = 7)
    {
        var rng = new Random(seed);
        var boundary = CityBoundary(seed);
        var env = boundary.EnvelopeInternal;
        var sites = new List<Coordinate>();
        while (sites.Count < count)
        {
            var c = new Coordinate(env.MinX + rng.NextDouble() * env.Width, env.MinY + rng.NextDouble() * env.Height);
            if (boundary.Contains(Geo.Factory.CreatePoint(c)) && sites.All(s => s.Distance(c) > 1200)) sites.Add(c);
        }
        var vb = new VoronoiDiagramBuilder();
        vb.SetSites(sites);
        vb.ClipEnvelope = new Envelope(env.MinX - 1000, env.MaxX + 1000, env.MinY - 1000, env.MaxY + 1000);
        var cells = vb.GetDiagram(Geo.Factory);
        var polys = new List<NtsGeometry?>();
        var seedPts = new List<Coordinate>();
        for (int i = 0; i < cells.NumGeometries; i++)
        {
            var cell = cells.GetGeometryN(i);
            var clipped = GeometryOps.Intersection(cell, boundary);
            if (clipped.IsEmpty) continue;
            polys.Add(clipped);
            seedPts.Add((Coordinate)cell.UserData);
        }
        int n = polys.Count;
        var names = new string?[n]; var region = new string?[n]; var pop = new long[n]; var income = new double[n]; var green = new double[n]; var area = new double[n];
        for (int i = 0; i < n; i++)
        {
            var c = polys[i]!.Centroid;
            double dx = c.X - CenterX, dy = c.Y - CenterY;
            double dist = Math.Sqrt(dx * dx + dy * dy) / Radius;
            names[i] = DistrictNames[i % DistrictNames.Length] + (i >= DistrictNames.Length ? $" {i / DistrictNames.Length + 1}" : "");
            region[i] = dist < 0.3 ? "Pusat" : Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? "Timur" : "Barat") : (dy > 0 ? "Utara" : "Selatan");
            area[i] = polys[i]!.Area / 1e6;
            double density = 18_000 * Math.Exp(-1.6 * dist) + 2_500 + rng.NextDouble() * 3_000; // people per km²
            pop[i] = (long)(density * area[i]);
            income[i] = Math.Round(4.5 + 9 * Math.Exp(-2.2 * dist) + rng.NextDouble() * 2.5 + (dx > 0 && dy < 0 ? 2 : 0), 2); // million IDR / month
            green[i] = Math.Round(Math.Clamp(0.08 + 0.35 * dist + (rng.NextDouble() - 0.5) * 0.15, 0.02, 0.7), 3);
        }
        return GeoDataFrame.FromGeometries(polys, Crs,
            Column.Create("district_id", Enumerable.Range(1, n).Select(i => (long)i).ToArray()),
            new Column<string?>("name", names),
            new Column<string?>("region", region),
            Column.Create("population", pop),
            Column.Create("area_km2", area.Select(a => Math.Round(a, 3)).ToArray()),
            Column.Create("income_mio_idr", income),
            Column.Create("green_ratio", green));
    }

    /// <summary>Noded road network (perturbed grid + ring road + radials) with class, speed and one-way flags.</summary>
    public static GeoDataFrame Roads(int seed = 7, double spacing = 900)
    {
        var rng = new Random(seed);
        var boundary = CityBoundary(seed);
        var env = boundary.EnvelopeInternal;
        int nx = (int)(env.Width / spacing) + 1, ny = (int)(env.Height / spacing) + 1;
        var grid = new Coordinate[nx + 1, ny + 1];
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
                grid[i, j] = new Coordinate(env.MinX + i * spacing + (rng.NextDouble() - 0.5) * spacing * 0.35, env.MinY + j * spacing + (rng.NextDouble() - 0.5) * spacing * 0.35);

        var geoms = new List<NtsGeometry?>(); var cls = new List<string?>(); var speed = new List<double>(); var oneway = new List<bool>(); var names = new List<string?>();
        var prepared = NetTopologySuite.Geometries.Prepared.PreparedGeometryFactory.Prepare(boundary);
        void Seg(Coordinate a, Coordinate b, string c, double kmh, bool ow, string name)
        {
            var ls = Geo.Factory.CreateLineString([a, b]);
            if (!prepared.Contains(ls)) return;
            geoms.Add(ls); cls.Add(c); speed.Add(kmh); oneway.Add(ow); names.Add(name);
        }
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= ny; j++)
            {
                bool arterialX = j % 4 == 0, arterialY = i % 4 == 0;
                if (i < nx) Seg(grid[i, j], grid[i + 1, j], arterialX ? "primary" : "residential", arterialX ? 50 : 25, !arterialX && rng.NextDouble() < 0.12, arterialX ? $"Jalan Raya {j / 4 + 1}" : $"Jalan {i}-{j}");
                if (j < ny) Seg(grid[i, j], grid[i, j + 1], arterialY ? "primary" : "residential", arterialY ? 50 : 25, !arterialY && rng.NextDouble() < 0.12, arterialY ? $"Jalan Protokol {i / 4 + 1}" : $"Gang {i}-{j}");
                // a few diagonals (secondary roads)
                if (i < nx && j < ny && (i + j) % 7 == 0) Seg(grid[i, j], grid[i + 1, j + 1], "secondary", 40, false, $"Jalan Diagonal {i}-{j}");
            }
        return GeoDataFrame.FromGeometries(geoms, Crs,
            new Column<string?>("name", names.ToArray()),
            new Column<string?>("highway", cls.ToArray()),
            Column.Create("maxspeed_kmh", speed.ToArray()),
            Column.Create("oneway", oneway.ToArray()))
            .WithColumn<double>("travel_time_s", r => r.Frame is GeoDataFrame g ? g.Geometry[r.Index]!.Length / (r.GetDouble("maxspeed_kmh") / 3.6) : 0);
    }

    /// <summary>Points of interest (schools, hospitals, mosques, markets, parks, transit) with clustered placement.</summary>
    public static GeoDataFrame PointsOfInterest(int count = 400, int seed = 7)
    {
        var rng = new Random(seed);
        var boundary = CityBoundary(seed);
        var env = boundary.EnvelopeInternal;
        string[] cats = ["school", "hospital", "mosque", "market", "park", "transit", "cafe"];
        double[] weights = [0.2, 0.05, 0.2, 0.12, 0.08, 0.1, 0.25];
        var centers = Enumerable.Range(0, 6).Select(_ => (X: env.MinX + rng.NextDouble() * env.Width, Y: env.MinY + rng.NextDouble() * env.Height)).ToArray();
        var geoms = new List<NtsGeometry?>(); var cat = new List<string?>(); var rating = new List<double>(); var capacity = new List<long>(); var names = new List<string?>();
        while (geoms.Count < count)
        {
            double x, y;
            if (rng.NextDouble() < 0.6)
            {
                var c = centers[rng.Next(centers.Length)];
                x = c.X + Gauss(rng) * 900; y = c.Y + Gauss(rng) * 900;
            }
            else { x = env.MinX + rng.NextDouble() * env.Width; y = env.MinY + rng.NextDouble() * env.Height; }
            var p = Geo.Point(x, y);
            if (!boundary.Contains(p)) continue;
            double r = rng.NextDouble(), acc = 0; int k = 0;
            for (; k < weights.Length - 1; k++) { acc += weights[k]; if (r < acc) break; }
            geoms.Add(p); cat.Add(cats[k]);
            names.Add($"{char.ToUpperInvariant(cats[k][0])}{cats[k][1..]} {geoms.Count}");
            rating.Add(Math.Round(3 + rng.NextDouble() * 2, 1));
            capacity.Add(cats[k] switch { "school" => 200 + rng.Next(800), "hospital" => 50 + rng.Next(450), "mosque" => 100 + rng.Next(1900), _ => 20 + rng.Next(300) });
        }
        return GeoDataFrame.FromGeometries(geoms, Crs,
            new Column<string?>("name", names.ToArray()),
            new Column<string?>("category", cat.ToArray()),
            Column.Create("rating", rating.ToArray()),
            Column.Create("capacity", capacity.ToArray()));
    }

    /// <summary>Tree-shaped water distribution network with pipe diameters and valves (status open/closed).</summary>
    public static GeoDataFrame WaterNetwork(int seed = 7, int depth = 5)
    {
        var rng = new Random(seed);
        var geoms = new List<NtsGeometry?>(); var diam = new List<double>(); var valve = new List<string?>(); var material = new List<string?>(); var ids = new List<string?>();
        void Branch(double x, double y, double angle, double len, int level)
        {
            if (level > depth) return;
            double x2 = x + len * Math.Cos(angle), y2 = y + len * Math.Sin(angle);
            var mid = new Coordinate((x + x2) / 2 + (rng.NextDouble() - 0.5) * len * 0.1, (y + y2) / 2 + (rng.NextDouble() - 0.5) * len * 0.1);
            geoms.Add(Geo.Factory.CreateLineString([new Coordinate(x, y), mid, new Coordinate(x2, y2)]));
            diam.Add(Math.Round(600 / Math.Pow(1.6, level)));
            valve.Add(rng.NextDouble() < 0.06 && level > 1 ? "closed" : "open");
            material.Add(level < 2 ? "steel" : level < 4 ? "ductile_iron" : "hdpe");
            ids.Add($"P-{level}-{geoms.Count:D3}");
            int children = level == 0 ? 3 : 2;
            for (int c = 0; c < children; c++)
                Branch(x2, y2, angle + (c - (children - 1) / 2.0) * (0.9 - level * 0.08) + (rng.NextDouble() - 0.5) * 0.2, len * 0.72, level + 1);
        }
        Branch(CenterX - 6_500, CenterY - 4_000, Math.PI / 4, 3_200, 0);
        return GeoDataFrame.FromGeometries(geoms, Crs,
            new Column<string?>("pipe_id", ids.ToArray()),
            Column.Create("diameter_mm", diam.ToArray()),
            new Column<string?>("material", material.ToArray()),
            new Column<string?>("valve", valve.ToArray()));
    }

    /// <summary>Procedural DEM (fractal value noise + a river valley), 30 m cells, metres above sea level.</summary>
    public static RasterGrid Dem(int width = 256, int height = 200, int seed = 7, double cellSize = 90)
    {
        var rng = new Random(seed);
        var perm = Enumerable.Range(0, 512).Select(_ => rng.NextDouble()).ToArray();
        double Noise(double x, double y)
        {
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            double fx = x - xi, fy = y - yi;
            double H(int a, int b) => perm[((a * 73856093) ^ (b * 19349663)) & 511];
            double sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
            double a0 = H(xi, yi) + sx * (H(xi + 1, yi) - H(xi, yi));
            double a1 = H(xi, yi + 1) + sx * (H(xi + 1, yi + 1) - H(xi, yi + 1));
            return a0 + sy * (a1 - a0);
        }
        var z = new float[width * height];
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
            {
                double x = c / 40.0, y = r / 40.0, v = 0, amp = 1, freq = 1;
                for (int o = 0; o < 5; o++) { v += amp * Noise(x * freq, y * freq); amp *= 0.5; freq *= 2; }
                double ridge = 1 - Math.Abs(c / (double)width - 0.25 - 0.2 * Math.Sin(r / (double)height * 4)) * 2.2;
                double valley = Math.Max(0, ridge);
                double southRise = (height - r) / (double)height;
                z[r * width + c] = (float)(25 + 420 * v * (0.35 + southRise) - 140 * valley * valley + 160 * southRise * southRise);
            }
        var originX = CenterX - width * cellSize / 2;
        var originY = CenterY + height * cellSize / 2;
        var ras = new RasterGrid(width, height, [z], GeoTransform.NorthUp(originX, originY, cellSize), Crs, -9999);
        ras.BandNames = ["elevation"];
        return ras;
    }

    /// <summary>
    /// Synthetic 4-band reflectance image (green, red, nir, swir1) over the city: water, vegetation and urban classes.
    /// <paramref name="urbanGrowth"/> &gt; 0 simulates a later acquisition with urban expansion (for change detection).
    /// </summary>
    public static RasterGrid Multispectral(int width = 256, int height = 200, int seed = 7, double urbanGrowth = 0, double cellSize = 90)
    {
        var dem = Dem(width, height, seed, cellSize);
        var z = dem.Bands[0];
        var rng = new Random(seed + 99);
        var g = new float[width * height]; var red = new float[width * height]; var nir = new float[width * height]; var swir = new float[width * height];
        double urbanRadius = 0.32 + urbanGrowth;
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
            {
                int i = r * width + c;
                double dx = (c - width / 2.0) / width, dy = (r - height / 2.0) / height;
                double d = Math.Sqrt(dx * dx * 1.2 + dy * dy);
                double noise = (rng.NextDouble() - 0.5) * 0.02;
                if (z[i] < 40) { g[i] = 0.08f; red[i] = 0.05f; nir[i] = 0.03f; swir[i] = 0.02f; }                 // water
                else if (d < urbanRadius + 0.05 * Math.Sin(c * 0.15)) { g[i] = 0.16f; red[i] = 0.2f; nir[i] = 0.24f; swir[i] = 0.32f; } // urban
                else { g[i] = 0.09f; red[i] = 0.06f; nir[i] = (float)(0.38 + 0.1 * Math.Sin(r * 0.08)); swir[i] = 0.18f; }              // vegetation
                g[i] += (float)noise; red[i] += (float)noise; nir[i] += (float)noise; swir[i] += (float)noise;
            }
        var ras = new RasterGrid(width, height, [g, red, nir, swir], dem.Transform, Crs);
        ras.BandNames = ["green", "red", "nir", "swir1"];
        return ras;
    }

    /// <summary>Major cities (lon/lat, WGS 84) with approximate metro populations.</summary>
    public static GeoDataFrame Cities()
    {
        (string Name, string Country, double Lon, double Lat, double PopMio)[] data =
        [
            ("Jakarta", "Indonesia", 106.8456, -6.2088, 34.5), ("Surabaya", "Indonesia", 112.7521, -7.2575, 10.0),
            ("Bandung", "Indonesia", 107.6191, -6.9175, 8.6), ("Medan", "Indonesia", 98.6722, 3.5952, 4.7),
            ("Semarang", "Indonesia", 110.4203, -6.9667, 6.0), ("Makassar", "Indonesia", 119.4327, -5.1477, 2.7),
            ("Palembang", "Indonesia", 104.7458, -2.9761, 2.6), ("Yogyakarta", "Indonesia", 110.3695, -7.7956, 4.0),
            ("Denpasar", "Indonesia", 115.2126, -8.6705, 2.2), ("Balikpapan", "Indonesia", 116.8312, -1.2379, 0.7),
            ("Nusantara (IKN)", "Indonesia", 116.7020, -0.9737, 0.1), ("Jayapura", "Indonesia", 140.7181, -2.5337, 0.4),
            ("Singapore", "Singapore", 103.8198, 1.3521, 6.0), ("Kuala Lumpur", "Malaysia", 101.6869, 3.1390, 8.6),
            ("Bangkok", "Thailand", 100.5018, 13.7563, 17.4), ("Manila", "Philippines", 120.9842, 14.5995, 24.9),
            ("Ho Chi Minh City", "Vietnam", 106.6297, 10.8231, 14.0), ("Tokyo", "Japan", 139.6917, 35.6895, 37.1),
            ("Sydney", "Australia", 151.2093, -33.8688, 5.4), ("London", "United Kingdom", -0.1276, 51.5072, 14.8),
            ("New York", "United States", -74.0060, 40.7128, 19.6), ("São Paulo", "Brazil", -46.6333, -23.5505, 22.6),
            ("Cairo", "Egypt", 31.2357, 30.0444, 22.6), ("Mumbai", "India", 72.8777, 19.0760, 21.7),
        ];
        return GeoDataFrame.FromGeometries(data.Select(d => (NtsGeometry?)Geo.Point(d.Lon, d.Lat)), CoordinateReferenceSystem.Wgs84,
            new Column<string?>("name", data.Select(d => (string?)d.Name).ToArray()),
            new Column<string?>("country", data.Select(d => (string?)d.Country).ToArray()),
            Column.Create("pop_mio", data.Select(d => d.PopMio).ToArray()));
    }

    /// <summary>A small DXF site plan (parcels, building footprints, road centreline, labels) in local CAD units (metres).</summary>
    public static string SitePlanDxf()
    {
        var sb = new StringBuilder();
        void P(int c, object v) => sb.Append(c).Append('\n').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
        P(0, "SECTION"); P(2, "TABLES"); P(0, "TABLE"); P(2, "LAYER");
        foreach (var (name, color) in new[] { ("PARCEL", 3), ("BUILDING", 1), ("ROAD", 8), ("TEXT", 7), ("TREE", 3) }) { P(0, "LAYER"); P(2, name); P(70, 0); P(62, color); P(6, "CONTINUOUS"); }
        P(0, "ENDTAB"); P(0, "ENDSEC");
        P(0, "SECTION"); P(2, "ENTITIES");
        void Poly(string layer, params (double X, double Y)[] pts)
        {
            P(0, "LWPOLYLINE"); P(8, layer); P(90, pts.Length); P(70, 1);
            foreach (var (x, y) in pts) { P(10, x); P(20, y); }
        }
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 2; j++)
            {
                double x0 = i * 40, y0 = j * 50 + (j == 1 ? 20 : 0);
                Poly("PARCEL", (x0, y0), (x0 + 40, y0), (x0 + 40, y0 + 50), (x0, y0 + 50));
                Poly("BUILDING", (x0 + 8, y0 + 10), (x0 + 30, y0 + 10), (x0 + 30, y0 + 38), (x0 + 8, y0 + 38));
                P(0, "TEXT"); P(8, "TEXT"); P(10, x0 + 12); P(20, y0 + 22); P(40, 3); P(1, $"Lot {i * 2 + j + 1}");
                P(0, "CIRCLE"); P(8, "TREE"); P(10, x0 + 35); P(20, y0 + 5); P(40, 2.5);
            }
        P(0, "LINE"); P(8, "ROAD"); P(10, -10); P(20, 60); P(11, 170); P(21, 60);
        P(0, "ARC"); P(8, "ROAD"); P(10, 170); P(20, 80); P(40, 20); P(50, 270); P(51, 360);
        P(0, "ENDSEC"); P(0, "EOF");
        return sb.ToString();
    }

    private static double Gauss(Random rng)
    {
        double u1 = 1 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
