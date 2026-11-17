using System.Diagnostics;
using System.Globalization;
using GeoPandas.Net;
using GeoPandas.Net.AI;
using GeoPandas.Net.Data;
using GeoPandas.Net.Data.Versioning;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Lazy;
using GeoPandas.Net.PointCloud;
using NetTopologySuite.Geometries;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Network;
using GeoPandas.Net.Plotting;
using GeoPandas.Net.Raster;

Console.OutputEncoding = System.Text.Encoding.UTF8;
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
GeoPandas.Net.CAD.Dxf.Register();

var cli = new Cli(args);
try
{
    return await cli.RunAsync();
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Cli.Error(ex.Message);
    if (Environment.GetEnvironmentVariable("GPD_DEBUG") == "1") Console.Error.WriteLine(ex);
    return 1;
}

internal sealed class Cli(string[] args)
{
    private readonly List<string> _pos = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !args[i - 1].StartsWith("--") || IsFlag(args[i - 1]))).ToList();
    private readonly Dictionary<string, string> _opt = ParseOptions(args);

    private static readonly HashSet<string> Flags = ["--help", "--version", "--indented", "--geodesic", "--directed", "--no-legend", "--explain", "--classes"];
    private static bool IsFlag(string a) => Flags.Contains(a);

    private static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) continue;
            if (IsFlag(a[i]) || i + 1 >= a.Length || a[i + 1].StartsWith("--")) d[a[i][2..]] = "true";
            else d[a[i][2..]] = a[++i];
        }
        return d;
    }

    private string? Opt(string name) => _opt.GetValueOrDefault(name);
    private string Pos(int i, string what) => _pos.Count > i ? _pos[i] : throw new ArgumentException($"Missing argument: {what}. Run 'gpd help'.");

    public static void Error(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.Error.WriteLine($"error: {msg}"); Console.ResetColor(); }
    private static void Ok(string msg) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine($"✔ {msg}"); Console.ResetColor(); }
    private static void Title(string msg) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(msg); Console.ResetColor(); }

    public async Task<int> RunAsync()
    {
        if (_pos.Count == 0 || Opt("help") is not null && _pos.Count == 0) { Help(); return 0; }
        if (Opt("version") is not null) { Console.WriteLine(Gpd.Version); return 0; }
        var sw = Stopwatch.StartNew();
        switch (_pos[0].ToLowerInvariant())
        {
            case "help": Help(); return 0;
            case "about": About(); return 0;
            case "info": Info(); break;
            case "head": Console.WriteLine(Gpd.ReadFile(Pos(1, "file"), Opt("layer")).Head(int.Parse(Opt("n") ?? "10")).ToString(50)); break;
            case "convert" or "reproject": Convert(); break;
            case "buffer": Buffer(); break;
            case "dissolve": Dissolve(); break;
            case "sjoin": SJoin(); break;
            case "clip": Clip(); break;
            case "plot": Plot(); break;
            case "crs": Crs(); break;
            case "transform": Transform(); break;
            case "route": Route(); break;
            case "raster": RasterCmd(); break;
            case "sample": Sample(); break;
            case "ask": await AskAsync(); break;
            case "scan": Scan(); break;
            case "las" or "laz" or "lidar": LasCmd(); break;
            case "netcdf" or "nc": NetCdfCmd(); break;
            case "repo": RepoCmd(); break;
            case "layers": foreach (var l in GeoPackage.ListLayers(Pos(1, "gpkg"))) Console.WriteLine(l); break;
            default: throw new ArgumentException($"Unknown command '{_pos[0]}'. Run 'gpd help'.");
        }
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"done in {sw.Elapsed.TotalMilliseconds:F0} ms");
        Console.ResetColor();
        return 0;
    }

    private static void Help()
    {
        Title($"gpd — GeoPandas.Net CLI {Gpd.Version}");
        Console.WriteLine(Gpd.Credits);
        Console.WriteLine("""

        Vector
          gpd info <file> [--layer L]                       schema, CRS, bounds, geometry types
          gpd head <file> [--n 10]                          print the first rows
          gpd layers <file.gpkg>                            list GeoPackage layers
          gpd convert <in> <out> [--to-crs EPSG:4326] [--layer L]
          gpd buffer <in> <out> --distance 100 [--to-crs EPSG:xxxx]
          gpd dissolve <in> <out> [--by column] [--agg sum|mean|first]
          gpd sjoin <left> <right> <out> [--predicate intersects|within|contains|...] [--how inner|left|right]
          gpd clip <in> <mask> <out>
          gpd plot <in> <out.svg> [--column C] [--cmap viridis] [--scheme quantiles|equal|jenks|continuous] [--title T]
          gpd scan <file> [--bbox x0,y0,x1,y1] [--where "col>=v and col2=x"] [--select a,b] [--limit n]
                   [--to-crs C] [--explain] [--out f]       lazy query, pushed into FlatGeobuf / GeoParquet / GeoPackage

          Formats: GeoJSON, Shapefile, GeoPackage, FlatGeobuf (.fgb), GeoParquet (.parquet), Arrow/Feather (.arrow, .feather), CSV, DXF

        CRS
          gpd crs <definition>                              show PROJ / WKT for EPSG, PROJ or WKT input
          gpd transform <from> <to> <x> <y>                 transform one coordinate

        Network
          gpd route <lines> --from x,y --to x,y [--cost column] [--oneway column] [--out route.geojson]

        Raster
          gpd raster info <file.tif>
          gpd raster slope|aspect|hillshade|fill|flowacc <dem.tif> <out.tif>
          gpd raster contours <dem.tif> <out.geojson> --interval 25
          gpd raster ndvi <nir.tif> <red.tif> <out.tif>
          gpd raster plot <file.tif> <out.svg> [--cmap terrain]
          gpd netcdf info <file.nc>                         ncdump -h style summary and CF time axis
          gpd netcdf raster <file.nc> <variable> <out.tif> [--index t,level]

        Point clouds (LAS 1.0–1.4, LAZ point formats 0–5)
          gpd las info <file.las|laz> [--classes]
          gpd las dsm|dtm|ndsm <file> <out.tif> [--cell 1] [--class 2,6] [--bbox x0,y0,x1,y1]
          gpd las convert <in> <out.las|laz|gpkg|fgb|parquet> [--class 2] [--step 10] [--format 6]

        Dataset versioning
          gpd repo init <dir> [--key id]
          gpd repo commit <dir> <file> --m "message"
          gpd repo log <dir>
          gpd repo diff <dir> [from=HEAD~1] [to=HEAD] [--out changes.geojson]
          gpd repo checkout <dir> <ref> <out>
          gpd repo tag <dir> <name> [ref]

        Data & AI
          gpd sample <districts|roads|pois|water|cities|siteplan|dem|multispectral|lidar|climate> <out>
          gpd ask <file> "question"                         GeoLLM (env: GEOPANDAS_LLM_PROVIDER/ENDPOINT/API_KEY/MODEL)
          gpd about
        """);
    }

    private static void About()
    {
        Title("GeoPandas.Net");
        Console.WriteLine(Gpd.About);
        Console.WriteLine($"Drivers: {string.Join(", ", GeoIO.Drivers.Select(d => d.Name))}");
        Console.WriteLine($"Built-in EPSG codes: {CrsRegistry.WellKnownCodes.Count}+ (plus PROJ / WKT parsing)");
    }

    private GeoDataFrame Load(int i, string what) => Gpd.ReadFile(Pos(i, what), Opt("layer"));

    private GeoDataFrame MaybeReproject(GeoDataFrame gdf) => Opt("to-crs") is { } c ? gdf.ToCrs(c) : gdf;

    private void Info()
    {
        var gdf = Load(1, "file");
        Title(Path.GetFileName(Pos(1, "file")));
        Console.Write(gdf.Info());
        Console.WriteLine($"  CRS: {gdf.Crs?.ToString() ?? "None"}");
        var b = gdf.TotalBounds;
        if (!b.IsNull) Console.WriteLine($"  Bounds: [{b.MinX:G10}, {b.MinY:G10}, {b.MaxX:G10}, {b.MaxY:G10}]");
        foreach (var g in gdf.Geometry.GeomType.NonNull().GroupBy(x => x)) Console.WriteLine($"  {g.Key}: {g.Count()}");
    }

    private void Convert()
    {
        var gdf = MaybeReproject(Load(1, "input"));
        var outPath = Pos(2, "output");
        Gpd.ToFile(gdf, outPath, Opt("out-layer"));
        Ok($"{gdf.RowCount} features → {outPath} ({GeoIO.DriverFor(outPath).Name}, {gdf.Crs?.ToString() ?? "no CRS"})");
    }

    private void Buffer()
    {
        var gdf = MaybeReproject(Load(1, "input"));
        double d = double.Parse(Opt("distance") ?? throw new ArgumentException("--distance is required"), CultureInfo.InvariantCulture);
        if (gdf.IsGeographic) Console.WriteLine("note: geographic CRS — distance is in degrees. Use --to-crs for metres.");
        var res = gdf.Buffer(d);
        Gpd.ToFile(res, Pos(2, "output"));
        Ok($"buffered {res.RowCount} features by {d}");
    }

    private void Dissolve()
    {
        var gdf = Load(1, "input");
        var agg = (Opt("agg") ?? "first").ToLowerInvariant() switch { "sum" => AggFunc.Sum, "mean" => AggFunc.Mean, "max" => AggFunc.Max, "min" => AggFunc.Min, _ => AggFunc.First };
        var res = Opt("by") is { } by ? gdf.Dissolve(by.Split(','), agg) : gdf.Dissolve(aggFunc: agg);
        Gpd.ToFile(res, Pos(2, "output"));
        Ok($"{gdf.RowCount} → {res.RowCount} features");
    }

    private void SJoin()
    {
        var left = Load(1, "left");
        var right = Load(2, "right");
        if (left.Crs is not null && right.Crs is not null && left.Crs != right.Crs) right = right.ToCrs(left.Crs);
        var pred = SpatialPredicateExtensions.Parse(Opt("predicate") ?? "intersects");
        var how = (Opt("how") ?? "inner").ToLowerInvariant() switch { "left" => JoinHow.Left, "right" => JoinHow.Right, _ => JoinHow.Inner };
        var res = left.SJoin(right, pred, how);
        Gpd.ToFile(res, Pos(3, "output"));
        Ok($"sjoin({pred}, {how}): {left.RowCount} × {right.RowCount} → {res.RowCount} rows");
    }

    private void Clip()
    {
        var gdf = Load(1, "input");
        var mask = Load(2, "mask");
        if (gdf.Crs is not null && mask.Crs is not null && gdf.Crs != mask.Crs) mask = mask.ToCrs(gdf.Crs);
        var res = gdf.Clip(mask);
        Gpd.ToFile(res, Pos(3, "output"));
        Ok($"clipped to {res.RowCount} features");
    }

    private void Plot()
    {
        var gdf = Load(1, "input");
        var style = new LayerStyle
        {
            Column = Opt("column"),
            ColorMap = ColorMaps.Get(Opt("cmap") ?? "viridis"),
            Scheme = (Opt("scheme") ?? "quantiles").ToLowerInvariant() switch
            {
                "equal" or "equalinterval" => ClassificationScheme.EqualInterval,
                "jenks" or "naturalbreaks" => ClassificationScheme.NaturalBreaks,
                "continuous" => ClassificationScheme.Continuous,
                _ => ClassificationScheme.Quantiles
            },
            LabelColumn = Opt("label"),
        };
        var scene = gdf.Plot(style, Opt("title") ?? Path.GetFileNameWithoutExtension(Pos(1, "input")));
        scene.ShowLegend = Opt("no-legend") is null;
        scene.SaveSvg(Pos(2, "output"), double.Parse(Opt("width") ?? "1000"), double.Parse(Opt("height") ?? "700"));
        Ok($"map → {Pos(2, "output")}");
    }

    private void Crs()
    {
        var crs = CoordinateReferenceSystem.Parse(Pos(1, "definition"));
        Title(crs.ToString());
        Console.WriteLine($"Kind:  {crs.Kind}");
        Console.WriteLine($"Datum: {crs.Datum.Name} ({crs.Ellipsoid.Name})");
        Console.WriteLine($"PROJ:  {crs.ToProj4()}");
        Console.WriteLine($"WKT:   {crs.ToWkt()}");
    }

    private void Transform()
    {
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Parse(Pos(1, "from")), CoordinateReferenceSystem.Parse(Pos(2, "to")));
        var (x, y) = t.Transform(double.Parse(Pos(3, "x"), CultureInfo.InvariantCulture), double.Parse(Pos(4, "y"), CultureInfo.InvariantCulture));
        Console.WriteLine($"{x:R} {y:R}");
    }

    private static (double, double) XY(string s)
    {
        var p = s.Split(',');
        return (double.Parse(p[0], CultureInfo.InvariantCulture), double.Parse(p[1], CultureInfo.InvariantCulture));
    }

    private void Route()
    {
        var lines = Load(1, "lines");
        var g = NetworkGraph.FromLines(lines, Opt("cost"), Opt("oneway"), Opt("directed") is not null);
        var (fx, fy) = XY(Opt("from") ?? throw new ArgumentException("--from x,y is required"));
        var (tx, ty) = XY(Opt("to") ?? throw new ArgumentException("--to x,y is required"));
        var r = g.ShortestPath(Geo.Point(fx, fy), Geo.Point(tx, ty), PathAlgorithm.AStar);
        Console.WriteLine(g);
        if (!r.Found) { Error("no route found"); return; }
        Ok($"route: {r.Edges.Length} edges, cost {r.Cost:F2}, length {r.Length:F1}");
        if (Opt("out") is { } o)
            Gpd.ToFile(GeoDataFrame.FromGeometries([r.Geometry], lines.Crs, Column.Create("cost", new[] { r.Cost }), Column.Create("length", new[] { r.Length })), o);
    }

    private void RasterCmd()
    {
        var sub = Pos(1, "raster sub-command").ToLowerInvariant();
        switch (sub)
        {
            case "info":
            {
                var r = GeoTiff.Read(Pos(2, "file"));
                Title(r.ToString());
                Console.WriteLine($"Bounds: {r.Bounds}");
                for (int b = 0; b < r.BandCount; b++)
                {
                    var s = r.Statistics(b);
                    Console.WriteLine($"  {r.BandNames[b]}: min {s.Min:G6} max {s.Max:G6} mean {s.Mean:G6} std {s.Std:G6} valid {s.Count}");
                }
                break;
            }
            case "slope" or "aspect" or "hillshade" or "fill" or "flowacc":
            {
                var dem = GeoTiff.Read(Pos(2, "dem"));
                var res = sub switch
                {
                    "slope" => Terrain.Slope(dem),
                    "aspect" => Terrain.Aspect(dem),
                    "hillshade" => Terrain.Hillshade(dem, double.Parse(Opt("azimuth") ?? "315"), double.Parse(Opt("altitude") ?? "45")),
                    "fill" => Hydrology.FillSinks(dem),
                    _ => Hydrology.FlowAccumulation(Hydrology.FlowDirection(Hydrology.FillSinks(dem)))
                };
                GeoTiff.Write(res, Pos(3, "output"));
                Ok($"{sub} → {Pos(3, "output")}");
                break;
            }
            case "contours":
            {
                var dem = GeoTiff.Read(Pos(2, "dem"));
                var c = dem.Contours(double.Parse(Opt("interval") ?? "25", CultureInfo.InvariantCulture));
                Gpd.ToFile(c, Pos(3, "output"));
                Ok($"{c.RowCount} contour levels → {Pos(3, "output")}");
                break;
            }
            case "ndvi":
            {
                var nir = GeoTiff.Read(Pos(2, "nir"));
                var red = GeoTiff.Read(Pos(3, "red"));
                var ndvi = SpectralIndex.Ndvi(nir, red);
                GeoTiff.Write(ndvi, Pos(4, "output"));
                Ok($"NDVI → {Pos(4, "output")} (mean {ndvi.Statistics().Mean:F3})");
                break;
            }
            case "plot":
            {
                var r = GeoTiff.Read(Pos(2, "file"));
                var scene = new MapScene { Title = Path.GetFileNameWithoutExtension(Pos(2, "file")) };
                scene.AddRaster(r, ColorMaps.Get(Opt("cmap") ?? "terrain"), legendTitle: r.BandNames[0]);
                scene.SaveSvg(Pos(3, "output"), 1000, 700);
                Ok($"map → {Pos(3, "output")}");
                break;
            }
            default: throw new ArgumentException($"Unknown raster command '{sub}'.");
        }
    }

    private void Sample()
    {
        var name = Pos(1, "dataset").ToLowerInvariant();
        var output = Pos(2, "output");
        switch (name)
        {
            case "dem": GeoTiff.Write(SampleData.Dem(), output); break;
            case "multispectral": GeoTiff.Write(SampleData.Multispectral(), output); break;
            case "siteplan": File.WriteAllText(output, SampleData.SitePlanDxf()); break;
            case "lidar": Las.Write(SampleData.LidarTile(), output); break;
            case "climate":
            {
                var (grid, times) = SampleData.ClimateGrid(8);
                if (output.EndsWith(".nc", StringComparison.OrdinalIgnoreCase)) NetCdf.WriteClassic(grid, output, "t2m", times, "K", "2 m air temperature");
                else GeoTiff.Write(grid, output);
                break;
            }
            default:
                var gdf = name switch
                {
                    "districts" => SampleData.Districts(),
                    "roads" => SampleData.Roads(),
                    "pois" => SampleData.PointsOfInterest(),
                    "water" => SampleData.WaterNetwork(),
                    "cities" => SampleData.Cities(),
                    _ => throw new ArgumentException($"Unknown dataset '{name}'.")
                };
                Gpd.ToFile(gdf, output);
                break;
        }
        Ok($"sample '{name}' → {output}");
    }

    // ================================================================== Phase 2: lazy scans, point clouds, NetCDF, versioning

    private static Envelope ParseBbox(string s)
    {
        var p = s.Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (p.Length != 4) throw new ArgumentException("--bbox expects minx,miny,maxx,maxy");
        return new Envelope(p[0], p[2], p[1], p[3]);
    }

    private static AttributePredicate ParseWhere(string s)
    {
        foreach (var (sym, op) in new[] { (">=", CompareOp.Ge), ("<=", CompareOp.Le), ("!=", CompareOp.Ne), ("<>", CompareOp.Ne), ("==", CompareOp.Eq), ("=", CompareOp.Eq), (">", CompareOp.Gt), ("<", CompareOp.Lt) })
        {
            int i = s.IndexOf(sym, StringComparison.Ordinal);
            if (i <= 0) continue;
            var col = s[..i].Trim();
            var raw = s[(i + sym.Length)..].Trim().Trim('\'', '"');
            object val = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : bool.TryParse(raw, out var b) ? b : raw;
            return new AttributePredicate(col, op, val);
        }
        throw new ArgumentException($"Cannot parse --where '{s}'. Use e.g. \"population>=50000\" or \"highway=primary\".");
    }

    private void Scan()
    {
        var plan = Gpd.Scan(Pos(1, "file"), Opt("layer"));
        if (Opt("bbox") is { } bb) plan = plan.FilterBbox(ParseBbox(bb));
        if (Opt("where") is { } w)
            foreach (var part in w.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = ParseWhere(part);
                plan = plan.Where(p.Column, p.Op, p.Value);
            }
        if (Opt("select") is { } sel) plan = plan.Select(sel.Split(',', StringSplitOptions.TrimEntries));
        if (Opt("to-crs") is { } crs) plan = plan.ToCrs(CoordinateReferenceSystem.Parse(crs));
        if (Opt("limit") is { } lim) plan = plan.Limit(long.Parse(lim, CultureInfo.InvariantCulture));
        if (Opt("explain") is not null) { Console.Write(plan.Explain()); if (Opt("out") is null) return; }
        var res = plan.Collect();
        if (Opt("out") is { } outPath) { Gpd.ToFile(res, outPath); Ok($"{res.RowCount} rows → {outPath}"); }
        else Console.WriteLine(res.ToString(int.Parse(Opt("n") ?? "20", CultureInfo.InvariantCulture)));
    }

    private void LasCmd()
    {
        var sub = Pos(1, "las command").ToLowerInvariant();
        var input = Pos(2, "input");
        LasReadOptions Opts() => new()
        {
            Classes = Opt("class") is { } c ? c.Split(',').Select(byte.Parse).ToArray() : null,
            Step = Opt("step") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : 1,
            Bbox = Opt("bbox") is { } b ? ParseBbox(b) : null,
        };
        double cell = double.Parse(Opt("cell") ?? "1", CultureInfo.InvariantCulture);
        switch (sub)
        {
            case "info":
            {
                var info = Las.ReadInfo(input);
                Title(Path.GetFileName(input));
                Console.WriteLine($"  {info}");
                Console.WriteLine($"  Software: {info.GeneratingSoftware} · System: {info.SystemIdentifier} · Created: {info.CreationDate?.ToString("yyyy-MM-dd") ?? "-"}");
                foreach (var v in info.Vlrs) Console.WriteLine($"  VLR {v.UserId}/{v.RecordId}: {v.Description} ({v.Length} B)");
                if (Opt("classes") is not null)
                    foreach (var (code, name, count) in Las.Read(input).ClassCounts()) Console.WriteLine($"  class {code,3} {name,-20} {count,12:N0}");
                break;
            }
            case "dsm" or "dtm" or "ndsm":
            {
                var pc = Las.Read(input, Opts());
                var dsm = pc.Dsm(cell);
                var res = sub == "dsm" ? dsm : sub == "dtm" ? pc.Dtm(cell)
                    : GeoPandas.Net.Raster.Raster.Combine(dsm, pc.Dtm(cell), (s, t) => float.IsNaN(s) || float.IsNaN(t) ? float.NaN : Math.Max(0, s - t));
                GeoTiff.Write(res, Pos(3, "output.tif"));
                Ok($"{sub.ToUpperInvariant()} {res.Width}×{res.Height} @ {cell} m from {pc.Count:N0} points → {Pos(3, "output")}");
                break;
            }
            case "convert":
            {
                var pc = Las.Read(input, Opts());
                var output = Pos(3, "output");
                if (output.EndsWith(".las", StringComparison.OrdinalIgnoreCase) || output.EndsWith(".laz", StringComparison.OrdinalIgnoreCase))
                    Las.Write(pc, output, Opt("format") is { } f ? int.Parse(f, CultureInfo.InvariantCulture) : null);
                else Gpd.ToFile(pc.ToGeoDataFrame(), output);
                Ok($"{pc.Count:N0} points → {output}");
                break;
            }
            default: throw new ArgumentException($"Unknown las command '{sub}' (info, dsm, dtm, ndsm, convert).");
        }
    }

    private void NetCdfCmd()
    {
        var sub = Pos(1, "netcdf command").ToLowerInvariant();
        using var nc = NetCdfDataset.Open(Pos(2, "file"));
        switch (sub)
        {
            case "info":
                Console.Write(nc.Describe());
                if (nc.Times() is { Length: > 0 } t) Console.WriteLine($"time: {t[0]:yyyy-MM-dd HH:mm} → {t[^1]:yyyy-MM-dd HH:mm} UTC ({t.Length} steps)");
                break;
            case "raster":
            {
                var variable = Pos(3, "variable");
                int[] idx = Opt("index") is { } s ? s.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToArray() : [];
                var r = nc.ToRaster(variable, idx);
                GeoTiff.Write(r, Pos(4, "output.tif"));
                Ok($"{variable} → {Pos(4, "output")} ({r.Width}×{r.Height}×{r.BandCount}, {r.Crs?.ToString() ?? "no CRS"})");
                break;
            }
            default: throw new ArgumentException($"Unknown netcdf command '{sub}' (info, raster).");
        }
    }

    private void RepoCmd()
    {
        var sub = Pos(1, "repo command").ToLowerInvariant();
        var repo = DatasetRepository.Open(Pos(2, "repository directory"), Opt("key"));
        switch (sub)
        {
            case "init": Ok($"repository at {repo.RootDirectory} (key: {repo.Key ?? "none"})"); break;
            case "commit":
            {
                var gdf = Gpd.ReadFile(Pos(3, "file"), Opt("layer"));
                var before = repo.Head?.Id;
                var c = repo.Commit(gdf, Opt("m") ?? Opt("message") ?? $"commit {Path.GetFileName(Pos(3, "file"))}", Opt("author") ?? Environment.UserName);
                Ok(c.Id == before ? $"unchanged — HEAD stays at {c.ShortId}" : $"{c.ShortId} {c.Message} ({c.Rows} rows{(c.Summary is null ? "" : "; " + c.Summary)})");
                break;
            }
            case "log":
                foreach (var c in repo.Log())
                {
                    var tags = repo.Tags.Where(t => t.Value == c.Id).Select(t => t.Key).ToArray();
                    Console.WriteLine($"{c.ShortId}  {c.Timestamp:yyyy-MM-dd HH:mm}  {c.Author,-12} {c.Message}{(tags.Length > 0 ? $"  [{string.Join(", ", tags)}]" : "")}{(c.Summary is null ? "" : $"  ({c.Summary})")}");
                }
                break;
            case "diff":
            {
                var diff = repo.Diff(_pos.Count > 3 ? _pos[3] : "HEAD~1", _pos.Count > 4 ? _pos[4] : "HEAD");
                Console.WriteLine(diff.Summary);
                foreach (var m in diff.Modified.Take(50)) Console.WriteLine($"  ~ {diff.Key} {m.Key}: {string.Join(", ", m.ChangedColumns)}");
                if (Opt("out") is { } o && diff.ToChangeFrame() is GeoDataFrame g) { Gpd.ToFile(g, o); Ok($"change map → {o}"); }
                break;
            }
            case "checkout":
            {
                var gdf = repo.Checkout(Pos(3, "reference"));
                Gpd.ToFile(gdf, Pos(4, "output"));
                Ok($"{Pos(3, "reference")} → {Pos(4, "output")} ({gdf.RowCount} rows)");
                break;
            }
            case "tag":
                repo.Tag(Pos(3, "name"), _pos.Count > 4 ? _pos[4] : "HEAD");
                Ok($"tag {Pos(3, "name")} → {repo.Resolve(Pos(3, "name")).ShortId}");
                break;
            default: throw new ArgumentException($"Unknown repo command '{sub}' (init, commit, log, diff, checkout, tag).");
        }
    }

    private async Task AskAsync()
    {
        var gdf = Load(1, "file");
        var question = Pos(2, "question");
        using var llm = OpenAiChatClient.FromEnvironment() ?? throw new InvalidOperationException(
            "LLM not configured. Set GEOPANDAS_LLM_PROVIDER (openai|azure|compatible), GEOPANDAS_LLM_ENDPOINT, GEOPANDAS_LLM_API_KEY and GEOPANDAS_LLM_MODEL.");
        var answer = await new GeoAssistant(llm).AskAsync(gdf, question);
        Title("Plan");
        foreach (var s in answer.Plan.Steps) Console.WriteLine($"  • {s.Op} {string.Join(" ", s.Args.Select(a => $"{a.Key}={a.Value.GetRawText()}"))}");
        Title("Answer");
        Console.WriteLine(answer.Explanation);
        Console.WriteLine(answer.Result is GeoDataFrame g ? g.Attributes().ToString(20) : answer.Result.ToString(20));
        if (Opt("out") is { } o && answer.Result is GeoDataFrame rg) { Gpd.ToFile(rg, o); Ok($"result → {o}"); }
    }
}
