using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Numerics;
using OpenSceneGraph.Net;
using OpenSceneGraph.Net.Gallery;
using OpenSceneGraph.Net.Geo;
using OpenSceneGraph.Net.Interop;
using OpenSceneGraph.Net.IO;
using OpenSceneGraph.Net.Native;
using OpenSceneGraph.Net.Scene;
using OpenSceneGraph.Net.Simulation;
using OpenSceneGraph.Net.Terrain;
using Spectre.Console;

namespace OpenSceneGraph.Net.Cli;

/// <summary>osgnet — the OpenSceneGraph.Net command line tool.</summary>
public static class Program
{
    private const string Brand = "[grey]Created by Gravicode Studios, led by Kang Fadhil[/]";

    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") return Help();
        var opts = Options.Parse(args.Skip(1));
        try
        {
            return args[0] switch
            {
                "info" => Info(),
                "inspect" => Inspect(opts),
                "convert" => Convert(opts),
                "optimize" => Optimize(opts),
                "view" => View(opts),
                "render" => Render(opts),
                "terrain" => TerrainCmd(opts),
                "samples" => Samples(),
                "sample" => ExportSample(opts),
                "dis" => Dis(opts),
                "geo" => GeoCmd(opts),
                "--version" or "version" => Version(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidDataException or ArgumentException or InvalidOperationException or PlatformNotSupportedException)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int Unknown(string cmd)
    {
        AnsiConsole.MarkupLine($"[red]unknown command[/] '{Markup.Escape(cmd)}'. Run [yellow]osgnet help[/].");
        return 2;
    }

    private static int Version()
    {
        Console.WriteLine(typeof(Viewer).Assembly.GetName().Version);
        return 0;
    }

    private static int Help()
    {
        AnsiConsole.Write(new FigletText("osgnet").Color(Spectre.Console.Color.DeepSkyBlue1));
        AnsiConsole.MarkupLine($"[bold]OpenSceneGraph.Net[/] command line tool  {Brand}\n");
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Command").AddColumn("Description");
        void Row(string c, string d) => t.AddRow($"[yellow]{Markup.Escape(c)}[/]", Markup.Escape(d));
        Row("info", "Versions, native OpenSceneGraph runtime, plugins and acceleration");
        Row("inspect <file> [--tree] [--native]", "Statistics, bounds and scene tree of a model");
        Row("convert <in> <out> [--native]", "Convert between formats (.osgn .obj .stl .ply + every OSG plugin)");
        Row("optimize <in> <out>", "Run osgUtil::Optimizer (native) and write the result");
        Row("view <file|sample:id>", "Open a native osgViewer window (s = stats, w = wireframe, esc = quit)");
        Row("render <file|sample:id> <out.png> [--width 1280 --height 720]", "Offscreen render to PNG (native)");
        Row("terrain <out> [--dem file.tif|.asc] [--size 257 --spacing 10 --height 400 --seed 1]", "Generate a tiled LOD terrain");
        Row("samples", "List gallery samples");
        Row("sample <id> <out.osgn|.osgt|.obj>", "Export a gallery sample scene");
        Row("dis listen [--port 3000]", "Print DIS Entity State PDUs received on UDP");
        Row("dis send [--port 3000 --target 255.255.255.255 --count 0]", "Broadcast a test entity flying a circle");
        Row("geo <lat> <lon> [height]", "Convert a position to ECEF, UTM and map tiles");
        AnsiConsole.Write(t);
        return 0;
    }

    private static int Info()
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Item").AddColumn("Value");
        t.AddRow("OpenSceneGraph.Net", typeof(Viewer).Assembly.GetName().Version?.ToString() ?? "?");
        t.AddRow(".NET", Environment.Version.ToString());
        t.AddRow("OS", Markup.Escape(System.Runtime.InteropServices.RuntimeInformation.OSDescription));
        bool native = OsgNative.Initialize();
        t.AddRow("Native OpenSceneGraph", native ? $"[green]{OsgNative.OsgVersion}[/]" : "[yellow]not available[/] (managed formats only)");
        t.AddRow("Rust fast path (osgnet_fast)", FastPath.IsAvailable ? "[green]available[/]" : "[yellow]not found[/] (managed fallbacks)");
        t.AddRow("Managed formats", string.Join(", ", Registry.ReaderWriters.SelectMany(r => r.Extensions)));
        t.AddRow("Image formats", string.Join(", ", Registry.ImageReaders.SelectMany(r => r.Extensions)));
        if (native)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "osgPlugins-3.6.5");
            if (Directory.Exists(dir))
            {
                var plugins = Directory.GetFiles(dir, "osgdb_*.dll").Select(f => Path.GetFileNameWithoutExtension(f)[6..]).Where(p => !p.StartsWith("deprecated_") && !p.StartsWith("serializers_")).ToArray();
                t.AddRow($"OSG plugins ({plugins.Length})", string.Join(", ", plugins));
            }
        }
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine(Brand);
        return 0;
    }

    private static Node Load(string path, bool forceNative)
    {
        if (path.StartsWith("sample:", StringComparison.OrdinalIgnoreCase)) return BuildSample(path[7..], out _);
        if (forceNative)
        {
            if (!OsgNative.Initialize()) throw new PlatformNotSupportedException("Native OpenSceneGraph is not available.");
            using var n = NativeNode.Read(path);
            return n.ToManaged();
        }
        OsgNative.Initialize();
        return Registry.ReadNodeFile(path);
    }

    private static Node BuildSample(string id, out Viewer viewer)
    {
        var s = SampleCatalog.Find(id) ?? throw new ArgumentException($"Unknown sample '{id}'. Run 'osgnet samples'.");
        viewer = new Viewer { FixedTimeStep = 1 / 30.0 };
        var node = s.Build(new SampleContext(viewer));
        viewer.SceneData = node;
        for (int i = 0; i < 60; i++) viewer.Frame(); // let animations settle
        return node;
    }

    private static int Inspect(Options o)
    {
        var path = o.Positional(0, "file");
        var sw = Stopwatch.StartNew();
        var node = Load(path, o.Has("native"));
        var load = sw.Elapsed;
        var stats = new StatsVisitor();
        node.Accept(stats);
        var b = node.Bound;
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Property").AddColumn("Value");
        t.AddRow("File", Markup.Escape(path));
        t.AddRow("Load time", $"{load.TotalMilliseconds:F0} ms");
        t.AddRow("Nodes / groups / transforms", $"{stats.Nodes} / {stats.Groups} / {stats.Transforms}");
        t.AddRow("Drawables (unique)", $"{stats.Drawables} ({stats.UniqueDrawables})");
        t.AddRow("Vertices", stats.Vertices.ToString("N0"));
        t.AddRow("Triangles", stats.Triangles.ToString("N0"));
        t.AddRow("Bound centre", $"{b.Center.X:F3}, {b.Center.Y:F3}, {b.Center.Z:F3}");
        t.AddRow("Bound radius", $"{b.Radius:F3}");
        AnsiConsole.Write(t);
        if (o.Has("tree"))
        {
            var tree = new Tree($"[bold]{Markup.Escape(node.ToString())}[/]");
            AddTree(tree, node, 0);
            AnsiConsole.Write(tree);
        }
        return 0;
    }

    private static void AddTree(IHasTreeNodes parent, Node node, int depth)
    {
        if (node is not Group g || depth > 8) return;
        int shown = 0;
        foreach (var c in g.Children)
        {
            if (shown++ >= 40) { parent.AddNode($"[grey]… {g.ChildCount - 40} more[/]"); break; }
            string label = c is Geometry geo ? $"{c.GetType().Name} [grey]{Markup.Escape(c.Name ?? "")} {geo.VertexCount} verts {geo.PrimitiveType}[/]" : $"{c.GetType().Name} [grey]{Markup.Escape(c.Name ?? "")}[/]";
            AddTree(parent.AddNode(label), c, depth + 1);
        }
    }

    private static int Convert(Options o)
    {
        var input = o.Positional(0, "input");
        var output = o.Positional(1, "output");
        var ext = Registry.Extension(output);
        bool managedWriter = Registry.FindReaderWriter(ext) is { CanWrite: true };
        if (managedWriter && !o.Has("native"))
        {
            Registry.WriteNodeFile(Load(input, false), output);
        }
        else
        {
            if (!OsgNative.Initialize()) throw new PlatformNotSupportedException($"Writing .{ext} needs the native OpenSceneGraph runtime.");
            using var native = input.StartsWith("sample:") || Registry.FindReaderWriter(Registry.Extension(input)) is not null
                ? NativeSceneConverter.ToNative(Load(input, false))
                : NativeNode.Read(input);
            native.Write(output);
        }
        AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(input)} → {Markup.Escape(output)} ({new FileInfo(output).Length / 1024.0:F1} KiB)");
        return 0;
    }

    private static int Optimize(Options o)
    {
        if (!OsgNative.Initialize()) throw new PlatformNotSupportedException("optimize needs the native OpenSceneGraph runtime.");
        using var n = NativeNode.Read(o.Positional(0, "input"));
        var before = n.Statistics;
        n.Optimize();
        var after = n.Statistics;
        n.Write(o.Positional(1, "output"));
        AnsiConsole.MarkupLine($"[green]✓[/] drawables {before.Drawables} → {after.Drawables}, triangles {before.Triangles:N0} → {after.Triangles:N0}");
        return 0;
    }

    private static int View(Options o)
    {
        if (!OsgNative.Initialize()) throw new PlatformNotSupportedException("view needs the native OpenSceneGraph runtime.");
        var path = o.Positional(0, "file");
        using var viewer = NativeViewer.CreateWindow(o.Int("width", 1280), o.Int("height", 720), $"osgnet — {Path.GetFileName(path)}");
        if (path.StartsWith("sample:") || Registry.FindReaderWriter(Registry.Extension(path)) is not null) viewer.SetScene(Load(path, false));
        else viewer.Scene = NativeNode.Read(path);
        viewer.AddStatsHandler();
        viewer.SetClearColor(new Vector4(0.06f, 0.08f, 0.11f, 1));
        AnsiConsole.MarkupLine("[grey]osgViewer running — press 's' for stats, 'w' wireframe, 'l' lighting, Esc to quit[/]");
        return viewer.Run();
    }

    private static int Render(Options o)
    {
        if (!OsgNative.Initialize()) throw new PlatformNotSupportedException("render needs the native OpenSceneGraph runtime.");
        var path = o.Positional(0, "file");
        var output = o.Positional(1, "output.png");
        Matrix4x4? view = null;
        Node scene;
        Vector4 clear = new(0.06f, 0.08f, 0.11f, 1);
        if (path.StartsWith("sample:"))
        {
            scene = BuildSample(path[7..], out var v);
            view = v.Camera.ViewMatrix;
            clear = v.Camera.ClearColor;
        }
        else scene = Load(path, false);
        int w = o.Int("width", 1280), h = o.Int("height", 720);
        if (view is null)
        {
            var b = scene.Bound;
            view = Matrix4x4.CreateLookAt(b.Center + new Vector3(b.Radius * 1.5f, -b.Radius * 1.8f, b.Radius * 1.1f), b.Center, Vector3.UnitZ);
        }
        var img = NativeViewer.RenderToImage(scene, w, h, view, clear);
        PngReader.Write(output, img);
        AnsiConsole.MarkupLine($"[green]✓[/] rendered {w}×{h} → {Markup.Escape(output)}");
        return 0;
    }

    private static int TerrainCmd(Options o)
    {
        var output = o.Positional(0, "output");
        HeightField hf = o.Get("dem") is { } dem
            ? HeightFieldReaderWriter.ReadHeightField(dem)
            : TerrainGenerator.Fractal(o.Int("size", 257), o.Float("spacing", 10), o.Float("height", 400), o.Int("seed", 1));
        var (min, max) = hf.Range();
        var node = TerrainBuilder.Build(hf, o.Int("tiles", 4), o.Int("levels", 3));
        if (Registry.FindReaderWriter(Registry.Extension(output)) is { CanWrite: true }) Registry.WriteNodeFile(node, output);
        else
        {
            if (!OsgNative.Initialize()) throw new PlatformNotSupportedException("Writing this format needs the native runtime.");
            using var n = NativeSceneConverter.ToNative(node);
            n.Write(output);
        }
        AnsiConsole.MarkupLine($"[green]✓[/] terrain {hf.Columns}×{hf.Rows}, {hf.Width:F0}×{hf.Depth:F0} m, elevation {min:F0}..{max:F0} m → {Markup.Escape(output)}");
        if (hf.GeoOrigin is { } g) AnsiConsole.MarkupLine($"[grey]georeference origin {g.X}, {g.Y} {hf.Crs}[/]");
        return 0;
    }

    private static int Samples()
    {
        var t = new Table().Border(TableBorder.Rounded).AddColumn("Id").AddColumn("Category").AddColumn("Title").AddColumn("Judul");
        foreach (var s in SampleCatalog.All)
            t.AddRow($"[yellow]{s.Id}[/]", SampleCatalog.CategoryName(s.Category).En, Markup.Escape(s.Title.En), Markup.Escape(s.Title.Id));
        AnsiConsole.Write(t);
        AnsiConsole.MarkupLine("[grey]Use: osgnet render sample:<id> out.png  |  osgnet view sample:<id>  |  osgnet sample <id> out.osgn[/]");
        return 0;
    }

    private static int ExportSample(Options o)
    {
        var id = o.Positional(0, "id");
        var output = o.Positional(1, "output");
        return Convert(new Options([$"sample:{id}", output], o.Named));
    }

    private static int Dis(Options o)
    {
        var mode = o.Positional(0, "listen|send");
        int port = o.Int("port", 3000);
        if (mode == "listen")
        {
            using var net = new DisNetwork(port);
            AnsiConsole.MarkupLine($"[grey]listening for DIS on UDP {port} — Ctrl+C to stop[/]");
            net.EntityUpdated += (pdu, from) =>
            {
                var geo = Ellipsoid.Wgs84.FromEcef(pdu.Location);
                AnsiConsole.MarkupLine($"[yellow]{pdu.Id}[/] {Markup.Escape(pdu.Marking),-11} {pdu.Force,-9} {Markup.Escape(geo.ToString())}  [grey]from {from}[/]");
            };
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();
            return 0;
        }
        if (mode == "send")
        {
            var target = IPAddress.Parse(o.Get("target") ?? "255.255.255.255");
            using var net = new DisNetwork(port, target, listen: false);
            var origin = new GeoPoint(-6.9175, 107.6191, 0); // Bandung
            var plane = new Aircraft { Position = new Vector3(0, 0, 1500) };
            plane.Controls.Throttle = 0.7f;
            plane.Controls.Roll = 0.3f;
            int count = o.Int("count", 0);
            AnsiConsole.MarkupLine($"[grey]sending Entity State PDUs to {target}:{port} at 5 Hz — Ctrl+C to stop[/]");
            for (int i = 0; count == 0 || i < count; i++)
            {
                for (int k = 0; k < 10; k++) plane.Step(0.02f);
                var pdu = plane.ToPdu(new EntityId(1, 3001, 1), EntityType.FighterAircraft(), origin, "OSGNET-1");
                net.SendAsync(pdu).AsTask().Wait();
                AnsiConsole.MarkupLine($"[green]→[/] #{i + 1} {Markup.Escape(Ellipsoid.Wgs84.FromEcef(pdu.Location).ToString())}");
                Thread.Sleep(200);
            }
            return 0;
        }
        return Unknown("dis " + mode);
    }

    private static int GeoCmd(Options o)
    {
        double lat = double.Parse(o.Positional(0, "lat"), CultureInfo.InvariantCulture);
        double lon = double.Parse(o.Positional(1, "lon"), CultureInfo.InvariantCulture);
        double h = o.PositionalCount > 2 ? double.Parse(o.Positional(2, "height"), CultureInfo.InvariantCulture) : 0;
        var p = new GeoPoint(lat, lon, h);
        var e = Ellipsoid.Wgs84.ToEcef(p);
        var u = Utm.FromGeo(p);
        var t = new Table().Border(TableBorder.Rounded).AddColumn("System").AddColumn("Value");
        t.AddRow("Geodetic (WGS84)", Markup.Escape(p.ToString()));
        t.AddRow("ECEF (m)", $"{e.X:F2}, {e.Y:F2}, {e.Z:F2}");
        t.AddRow("UTM", $"{u.Zone}{Utm.LatitudeBand(lat)} {u.Easting:F2} E {u.Northing:F2} N");
        foreach (int z in (int[])[5, 10, 15]) t.AddRow($"Tile z{z}", TileMath.FromGeo(lat, lon, z).ToString());
        AnsiConsole.Write(t);
        return 0;
    }
}

/// <summary>Minimal argument parser: positionals plus --name value / --flag.</summary>
public sealed class Options(List<string> positionals, Dictionary<string, string?> named)
{
    public Dictionary<string, string?> Named { get; } = named;
    public int PositionalCount => positionals.Count;

    public static Options Parse(IEnumerable<string> args)
    {
        var pos = new List<string>();
        var named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].StartsWith("--"))
            {
                string key = list[i][2..];
                string? value = i + 1 < list.Count && !list[i + 1].StartsWith("--") ? list[++i] : null;
                named[key] = value;
            }
            else pos.Add(list[i]);
        }
        return new Options(pos, named);
    }

    public string Positional(int i, string name) =>
        i < positionals.Count ? positionals[i] : throw new ArgumentException($"missing argument <{name}>");

    public bool Has(string key) => Named.ContainsKey(key);
    public string? Get(string key) => Named.TryGetValue(key, out var v) ? v : null;
    public int Int(string key, int def) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : def;
    public float Float(string key, float def) => Get(key) is { } v ? float.Parse(v, CultureInfo.InvariantCulture) : def;
}
