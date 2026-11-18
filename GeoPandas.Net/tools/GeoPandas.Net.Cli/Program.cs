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
using GeoPandas.Net.RemoteSensing;
using GeoPandas.Net.Survey;

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
            case "geoid": GeoidCmd(); break;
            case "survey": SurveyCmd(); break;
            case "gnss": GnssCmd(); break;
            case "copc": CopcCmd(); break;
            case "s2" or "sentinel2": Sentinel2Cmd(); break;
            case "landsat": LandsatCmd(); break;
            case "stac": StacCmd(); break;
            case "regress": RegressCmd(); break;
            case "water": WaterCmd(); break;
            case "topology": TopologyCmd(); break;
            case "ifc" or "bim": IfcCmd(); break;
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

          Formats: GeoJSON, Shapefile, GeoPackage, FlatGeobuf (.fgb), GeoParquet (.parquet), Arrow/Feather (.arrow, .feather), CSV, DXF, DWG, DGN (v7)

        CRS
          gpd crs <definition>                              show PROJ / WKT for EPSG, PROJ or WKT input
          gpd crs search <text>                             search the EPSG dataset (5 800+ CRSs) by name
          gpd crs at <lon> <lat>                            projected CRSs whose area of use contains the point
          gpd transform <from> <to> <x> <y> [--z h]         transform one coordinate (grids: +nadgrids, +geoidgrids)
          gpd geoid <lon> <lat> [--model file.gtx|.isg|.tif] [--h ellipsoidal]   geoid undulation N and H = h - N

        Surveying & GNSS
          gpd survey traverse <legs.csv> --start E,N --backsight AZ [--close-angle A | --close-on E,N]
                   [--method bowditch|transit] [--out stations.geojson]   legs.csv columns: to,distance,angle|azimuth
          gpd survey adjust <network.txt> [--out ellipses.geojson]       least squares (2D and leveling)
                   lines: fixed|station ID E N | benchmark ID H | height ID | dist|az|dir FROM TO VALUE SIGMA
                          angle FROM AT TO DEG SIGMA | dh FROM TO DH SIGMA
          gpd gnss spp <obs.o> <nav.n> [--out track.geojson] [--mask 10]   RINEX single point positioning

        Spatial statistics
          gpd regress <file> <y> <x1,x2> [--model ols|lag|error|gwr] [--weights queen|rook|knn:6]
                   [--bandwidth n] [--kernel bisquare|gaussian] [--fixed] [--out result.gpkg]

        Utility networks
          gpd water info|solve <network.inp> [--crs EPSG:x] [--close P1,P2] [--step 0]
                   [--out-nodes nodes.gpkg] [--out-links links.gpkg]   EPANET-compatible hydraulics
          gpd topology <lines> [--tolerance 0.001] [--snap 1] [--sources pts] [--out issues.geojson]

        BIM
          gpd ifc info|elements|footprint <model.ifc> [--out f]   IFC2x3/IFC4 → GIS (map coordinates)
          gpd ifc from <buildings> <out.ifc> [--height height] [--base z0]   footprints → georeferenced IFC4

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

        Remote sensing
          gpd s2 info|ndvi <product.SAFE> [out.tif]         Sentinel-2 L1C/L2A metadata, bands, NDVI
          gpd landsat info|ndvi <scene dir|MTL> [out.tif]   Landsat Collection 2, cloud-masked NDVI
          gpd stac info <item.json>
          gpd stac get <item.json> <asset> <out.tif>

        Point clouds (LAS 1.0–1.4, LAZ point formats 0–10, COPC)
          gpd las info <file.las|laz> [--classes]
          gpd las dsm|dtm|ndsm <file> <out.tif> [--cell 1] [--class 2,6] [--bbox x0,y0,x1,y1]
          gpd las convert <in> <out.las|laz|gpkg|fgb|parquet> [--class 2] [--step 10] [--format 6]
          gpd copc info <file.copc.laz>                     octree hierarchy per depth
          gpd copc read <file.copc.laz> <out> [--bbox x0,y0,x1,y1] [--depth n]

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
        if (_pos.Count > 1 && _pos[1] is "search" or "at")
        {
            var hits = _pos[1] == "search" ? CrsRegistry.Search(Pos(2, "text"), int.Parse(Opt("max") ?? "50", CultureInfo.InvariantCulture))
                : CrsRegistry.AtLocation(D(Pos(2, "lon")), D(Pos(3, "lat")), int.Parse(Opt("max") ?? "100", CultureInfo.InvariantCulture));
            Title($"{hits.Count} CRS (EPSG dataset {EpsgDatabase.Version})");
            foreach (var e in hits)
                Console.WriteLine($"  EPSG:{e.Code,-6} {e.Name}{(e.AreaOfUse is { } ar ? $"  [{ar.West:F1}, {ar.South:F1}, {ar.East:F1}, {ar.North:F1}]" : "")}");
            return;
        }
        var crs = CoordinateReferenceSystem.Parse(Pos(1, "definition"));
        Title(crs.ToString());
        Console.WriteLine($"Kind:  {crs.Kind}");
        Console.WriteLine($"Datum: {crs.Datum.Name} ({crs.Ellipsoid.Name})");
        Console.WriteLine($"PROJ:  {crs.ToProj4()}");
        Console.WriteLine($"WKT:   {crs.ToWkt()}");
        if (crs.AreaOfUse is { } a) Console.WriteLine($"Area:  [{a.West}, {a.South}, {a.East}, {a.North}]");
    }

    private void Transform()
    {
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Parse(Pos(1, "from")), CoordinateReferenceSystem.Parse(Pos(2, "to")));
        double x = D(Pos(3, "x")), y = D(Pos(4, "y"));
        if (Opt("z") is { } zs)
        {
            double z = D(zs);
            t.Transform(ref x, ref y, ref z);
            Console.WriteLine($"{x:R} {y:R} {z:R}");
        }
        else
        {
            (x, y) = t.Transform(x, y);
            Console.WriteLine($"{x:R} {y:R}");
        }
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

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private void GeoidCmd()
    {
        double lon = D(Pos(1, "lon")), lat = D(Pos(2, "lat"));
        var model = Opt("model") is { } m ? GeoidModel.Load(m) : GeoidModel.Egm96Coarse;
        double n = model.Undulation(lon, lat);
        Title(model.Name);
        Console.WriteLine($"N = {n:F3} m");
        if (Opt("h") is { } h) Console.WriteLine($"H = h - N = {D(h) - n:F3} m");
        if (Opt("model") is null) Console.WriteLine("(embedded 1° EGM96, about 0.5 m RMS; pass --model with an EGM2008 / InaGeoid grid for survey work)");
    }

    private void SurveyCmd()
    {
        switch (Pos(1, "traverse|adjust"))
        {
            case "traverse":
            {
                var rows = Csv.ReadDataFrame(Pos(2, "legs.csv"));
                bool hasAngle = rows.Columns.Any(c => c.Name == "angle"), hasAz = rows.Columns.Any(c => c.Name == "azimuth");
                string Cell(string col, int i) => rows[col].GetValue(i)?.ToString() ?? "";
                var legs = Enumerable.Range(0, rows.RowCount).Select(i => new TraverseLeg(Cell("to", i), D(Cell("distance", i)),
                    AngleRight: hasAngle && Cell("angle", i).Length > 0 ? Angles.ParseDms(Cell("angle", i)) : null,
                    Azimuth: hasAz && Cell("azimuth", i).Length > 0 ? Angles.ParseDms(Cell("azimuth", i)) : null)).ToList();
                var (e, n) = XY(Opt("start") ?? throw new ArgumentException("--start E,N is required"));
                var method = (Opt("method") ?? "bowditch").ToLowerInvariant() switch { "transit" => TraverseAdjustment.Transit, "none" => TraverseAdjustment.None, _ => TraverseAdjustment.Bowditch };
                (double E, double N)? closeOn = Opt("close-on") is { } co ? XY(co) : null;
                var r = Traverse.Compute(Opt("from") ?? "START", e, n, Angles.ParseDms(Opt("backsight") ?? throw new ArgumentException("--backsight AZ is required")), legs,
                    closeOn: closeOn, closingAngleRight: Opt("close-angle") is { } ca ? Angles.ParseDms(ca) : null, adjustment: method,
                    crs: Opt("crs") is { } c ? CoordinateReferenceSystem.Parse(c) : null);
                Title($"Traverse ({method}), {legs.Count} legs, {r.TotalLength:F3} m");
                Console.WriteLine($"Angular misclosure {r.AngularMisclosureSeconds:F1}\", linear {r.LinearMisclosure:F4} m (dE {r.MisclosureE:F4}, dN {r.MisclosureN:F4}), precision 1:{r.PrecisionRatio:F0}");
                Console.WriteLine(r.Stations.ToString(50));
                if (Opt("out") is { } o) { r.StationPoints().ToFile(o); Ok($"stations → {o}"); }
                break;
            }
            case "adjust":
            {
                var net = new AdjustmentNetwork();
                bool leveling = false, planar = false;
                foreach (var raw in File.ReadAllLines(Pos(2, "network.txt")))
                {
                    var t = raw.Split('#')[0].Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0) continue;
                    switch (t[0].ToLowerInvariant())
                    {
                        case "fixed": net.AddFixed(t[1], D(t[2]), D(t[3])); break;
                        case "station": net.AddStation(t[1], D(t[2]), D(t[3])); break;
                        case "benchmark": net.AddBenchmark(t[1], D(t[2])); leveling = true; break;
                        case "height": net.AddHeightPoint(t[1], t.Length > 2 ? D(t[2]) : 0); leveling = true; break;
                        case "dist": net.Distance(t[1], t[2], D(t[3]), D(t[4])); planar = true; break;
                        case "az": net.Azimuth(t[1], t[2], Angles.ParseDms(t[3]), D(t[4])); planar = true; break;
                        case "dir": net.Direction(t[1], t[2], Angles.ParseDms(t[3]), D(t[4])); planar = true; break;
                        case "angle": net.Angle(t[1], t[2], t[3], Angles.ParseDms(t[4]), D(t[5])); planar = true; break;
                        case "dh": net.HeightDifference(t[1], t[2], D(t[3]), D(t[4])); leveling = true; break;
                        default: throw new FormatException($"Unknown observation '{t[0]}' in line: {raw}");
                    }
                }
                if (planar)
                {
                    var r = net.Adjust2D();
                    Title("2D network adjustment"); Console.WriteLine(r); Console.WriteLine(r.Stations.ToString(50));
                    if (r.Outliers.Count > 0) Console.WriteLine($"Outliers (|w| > 3.29): {string.Join(", ", r.Outliers)}");
                    if (Opt("out") is { } o) { r.ErrorEllipses().ToFile(o); Ok($"95 % error ellipses → {o}"); }
                }
                if (leveling)
                {
                    var r = net.AdjustLeveling();
                    Title("Leveling adjustment"); Console.WriteLine(r); Console.WriteLine(r.Stations.ToString(50));
                }
                break;
            }
            default: throw new ArgumentException("survey: traverse | adjust");
        }
    }

    private void GnssCmd()
    {
        if (Pos(1, "spp") != "spp") throw new ArgumentException("gnss: spp");
        var obs = RinexObservation.Read(Pos(2, "obs"));
        var nav = RinexNavigation.Read(Pos(3, "nav"));
        var sol = Gnss.SinglePointPositioning(obs, nav, new SppOptions { ElevationMask = D(Opt("mask") ?? "10") });
        Title($"SPP: {sol.RowCount}/{obs.Epochs.Count} epochs, {nav.Ephemerides.Count} ephemerides");
        Console.WriteLine(sol.Head(int.Parse(Opt("n") ?? "10", CultureInfo.InvariantCulture)).ToString(50));
        if (obs.ApproxPosition is { } ap && sol.RowCount > 0)
        {
            var d = Enumerable.Range(0, sol.RowCount).Select(i => Math.Sqrt(Math.Pow(sol["x"].GetDouble(i) - ap.X, 2) + Math.Pow(sol["y"].GetDouble(i) - ap.Y, 2) + Math.Pow(sol["z"].GetDouble(i) - ap.Z, 2))).ToArray();
            Console.WriteLine($"Distance to the header's approximate position: mean {d.Average():F2} m, max {d.Max():F2} m");
        }
        if (Opt("out") is { } o) { sol.ToFile(o); Ok($"track → {o}"); }
    }

    private void CopcCmd()
    {
        var path = Pos(2, "file.copc.laz");
        switch (Pos(1, "info|read"))
        {
            case "info":
            {
                var info = Copc.ReadInfo(path);
                Title($"COPC {Path.GetFileName(path)}: {info.Nodes.Sum(n => (long)n.PointCount):N0} points in {info.Nodes.Count} nodes");
                Console.WriteLine($"Cube centre ({info.CenterX:F2}, {info.CenterY:F2}, {info.CenterZ:F2}), half size {info.HalfSize:F2}, root spacing {info.Spacing:F3}");
                foreach (var g in info.Nodes.GroupBy(n => n.Depth).OrderBy(g => g.Key))
                    Console.WriteLine($"  depth {g.Key}: {g.Count(),5} nodes {g.Sum(n => (long)n.PointCount),12:N0} points");
                break;
            }
            case "read":
            {
                Envelope? bbox = null;
                if (Opt("bbox") is { } b) { var p = b.Split(',').Select(D).ToArray(); bbox = new Envelope(p[0], p[2], p[1], p[3]); }
                var pc = Copc.Read(path, bbox, Opt("depth") is { } dp ? int.Parse(dp, CultureInfo.InvariantCulture) : null);
                var o = Pos(3, "out");
                if (o.EndsWith(".las", StringComparison.OrdinalIgnoreCase) || o.EndsWith(".laz", StringComparison.OrdinalIgnoreCase)) Las.Write(pc, o);
                else pc.ToGeoDataFrame().ToFile(o);
                Ok($"{pc.Count:N0} points → {o}");
                break;
            }
            default: throw new ArgumentException("copc: info | read");
        }
    }

    private static void WriteTif(GeoPandas.Net.Raster.Raster r, string path) { GeoTiff.Write(r, path); Ok($"{r} → {path}"); }

    private void Sentinel2Cmd()
    {
        var s2 = Sentinel2Product.Open(Pos(2, "product.SAFE"));
        switch (Pos(1, "info|ndvi"))
        {
            case "info": Title(s2.ToString()); Console.WriteLine($"CRS: {s2.Crs}"); Console.WriteLine($"Bands: {string.Join(", ", s2.Bands)}"); break;
            case "ndvi": WriteTif(s2.Ndvi(), Pos(3, "out.tif")); break;
            default: throw new ArgumentException("s2: info | ndvi");
        }
    }

    private void LandsatCmd()
    {
        var sc = LandsatScene.Open(Pos(2, "scene"));
        switch (Pos(1, "info|ndvi"))
        {
            case "info": Title(sc.ToString()); Console.WriteLine($"Sun elevation {sc.SunElevation}°, azimuth {sc.SunAzimuth}°"); break;
            case "ndvi": WriteTif(sc.Ndvi(), Pos(3, "out.tif")); break;
            default: throw new ArgumentException("landsat: info | ndvi");
        }
    }

    private void StacCmd()
    {
        var item = StacItem.Load(Pos(2, "item.json"));
        switch (Pos(1, "info|get"))
        {
            case "info":
                Title(item.ToString());
                if (item.Footprint is { } f) Console.WriteLine($"Footprint: {f.EnvelopeInternal}");
                foreach (var a in item.Assets) Console.WriteLine($"  {a.Key,-14} {a.Type ?? "",-40} {a.Href}");
                break;
            case "get": WriteTif(item.ReadAsset(Pos(3, "asset")), Pos(4, "out.tif")); break;
            default: throw new ArgumentException("stac: info | get");
        }
    }

    private void RegressCmd()
    {
        var gdf = Load(1, "file");
        string y = Pos(2, "dependent");
        var xs = Pos(3, "x1,x2,…").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string model = (Opt("model") ?? "ols").ToLowerInvariant();
        GeoPandas.Net.Analytics.SpatialWeights W()
        {
            var spec = (Opt("weights") ?? "queen").ToLowerInvariant();
            var w = spec.StartsWith("knn", StringComparison.Ordinal) ? GeoPandas.Net.Analytics.SpatialWeights.KNearest(gdf, int.Parse(spec.Split(':').ElementAtOrDefault(1) ?? "6", CultureInfo.InvariantCulture))
                : spec == "rook" ? GeoPandas.Net.Analytics.SpatialWeights.Rook(gdf) : GeoPandas.Net.Analytics.SpatialWeights.Queen(gdf);
            return w.RowStandardize();
        }
        if (model == "gwr")
        {
            var r = GeoPandas.Net.Analytics.Gwr.Fit(gdf, y, xs, Opt("bandwidth") is { } bw ? D(bw) : null,
                (Opt("kernel") ?? "bisquare").ToLowerInvariant() == "gaussian" ? GeoPandas.Net.Analytics.GwrKernel.Gaussian : GeoPandas.Net.Analytics.GwrKernel.Bisquare,
                Opt("fixed") is not null);
            Title(r.ToString());
            if (Opt("out") is { } o) { r.AddTo(gdf).ToFile(o); Ok($"local coefficients → {o}"); }
            return;
        }
        var res = model switch
        {
            "lag" => GeoPandas.Net.Analytics.SpatialRegression.SpatialLag(gdf, y, xs, W()),
            "error" => GeoPandas.Net.Analytics.SpatialRegression.SpatialError(gdf, y, xs, W()),
            _ => GeoPandas.Net.Analytics.SpatialRegression.Ols(gdf, y, xs, Opt("weights") is null ? null : W())
        };
        Console.Write(res);
        if (Opt("out") is { } out2) { gdf.WithColumn(Column.Create("predicted", res.Predicted)).WithColumn(Column.Create("residual", res.Residuals)).ToFile(out2); Ok($"predictions → {out2}"); }
    }

    private void WaterCmd()
    {
        var sub = Pos(1, "info|solve");
        var path = Pos(2, "network.inp");
        var crs = Opt("crs") is { } c ? CoordinateReferenceSystem.Parse(c) : null;
        var net = GeoPandas.Net.Network.Utility.WaterNetwork.ReadInp(path, crs);
        Title(net.ToString());
        if (sub == "info")
        {
            Console.WriteLine($"Patterns: {net.Patterns.Count}, curves: {net.Curves.Count}, controls: {net.Controls.Count}");
            if (Opt("out") is { } o) { net.LinksFrame().ToFile(o); Ok($"links → {o}"); }
            return;
        }
        var closed = Opt("close")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var res = GeoPandas.Net.Network.Utility.Hydraulics.Solve(net, new GeoPandas.Net.Network.Utility.HydraulicOptions
        {
            ClosedLinks = closed, PatternStep = int.Parse(Opt("step") ?? "0", CultureInfo.InvariantCulture)
        });
        Console.WriteLine(res);
        var nodes = res.NodesFrame();
        Console.WriteLine(nodes.Attributes().SortBy("pressure").Head(int.Parse(Opt("n") ?? "10", CultureInfo.InvariantCulture)).ToString(50));
        if (Opt("out-nodes") is { } on) { nodes.ToFile(on); Ok($"nodes → {on}"); }
        if (Opt("out-links") is { } ol) { res.LinksFrame().ToFile(ol); Ok($"links → {ol}"); }
    }

    private void TopologyCmd()
    {
        var lines = Load(1, "lines");
        var sources = Opt("sources") is { } s ? Gpd.ReadFile(s) : null;
        var v = GeoPandas.Net.Network.Utility.NetworkTopology.Validate(lines, D(Opt("tolerance") ?? "0.001"), D(Opt("snap") ?? "1"), sources: sources);
        Title($"{v.RowCount} topology issue(s) in {lines.RowCount} lines");
        Console.WriteLine(v.Attributes().GroupBy("rule").Agg(("line", AggFunc.Count, "count")).ToString(50));
        if (Opt("out") is { } o) { v.ToFile(o); Ok($"issues → {o}"); }
    }

    private void IfcCmd()
    {
        var sub = Pos(1, "info|elements|footprint|from");
        if (sub == "from")
        {
            var b = Load(2, "buildings");
            var outPath = Pos(3, "out.ifc");
            GeoPandas.Net.BIM.IfcWriter.WriteBuildings(b, outPath, Opt("height") ?? "height", Opt("base"), Opt("name") ?? "name");
            Ok($"{b.RowCount} buildings → {outPath}");
            return;
        }
        var model = GeoPandas.Net.BIM.IfcModel.Read(Pos(2, "model.ifc"));
        Title(model.ToString());
        switch (sub)
        {
            case "info":
                foreach (var st in model.Storeys) Console.WriteLine($"  storey {st.Name,-24} elevation {st.Elevation,8:F3} m");
                foreach (var g in model.Elements.GroupBy(e => e.IfcType).OrderByDescending(g => g.Count())) Console.WriteLine($"  {g.Key,-28} {g.Count(),6}");
                if (model.Georeference is { } geo) Console.WriteLine($"  georeference: E {geo.Eastings:F3} N {geo.Northings:F3} H {geo.OrthogonalHeight:F3}, rotation {geo.Rotation * 180 / Math.PI:F4}°, {geo.Source}");
                break;
            case "elements":
            {
                var f = model.ElementsFrame(Opt("no-props") is null);
                Console.WriteLine(f.Head(int.Parse(Opt("n") ?? "10", CultureInfo.InvariantCulture)).ToString(50));
                if (Opt("out") is { } o) { f.ToFile(o); Ok($"{f.RowCount} elements → {o}"); }
                break;
            }
            case "footprint":
            {
                var f = model.BuildingFootprint();
                Console.WriteLine(f.Attributes().ToString(50));
                if (Opt("out") is { } o) { f.ToFile(o); Ok($"LOD1 footprint → {o}"); }
                break;
            }
            default: throw new ArgumentException("ifc: info | elements | footprint | from");
        }
    }
}
