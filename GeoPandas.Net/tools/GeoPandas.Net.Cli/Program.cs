using System.Diagnostics;
using System.Globalization;
using GeoPandas.Net;
using GeoPandas.Net.AI;
using GeoPandas.Net.Data;
using GeoPandas.Net.Datasets;
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

    private static readonly HashSet<string> Flags = ["--help", "--version", "--indented", "--geodesic", "--directed", "--no-legend"];
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

        Data & AI
          gpd sample <districts|roads|pois|water|cities|siteplan|dem|multispectral> <out>
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
