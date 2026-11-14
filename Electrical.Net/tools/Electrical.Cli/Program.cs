using System.Globalization;
using Electrical.Cli;
using Electrical.Cloud;
using Electrical.Core;
using Electrical.Core.Network;
using Electrical.HPC;
using Electrical.Native;
using Electrical.PowerFlow;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = System.Text.Encoding.UTF8;
var cli = new Args(args);
try
{
    return await (cli.Command switch
    {
        "info" or "" => Commands.Info(),
        "help" or "--help" or "-h" => Commands.Help(),
        "cases" => Commands.Cases(),
        "pf" or "powerflow" => Commands.PowerFlow(cli),
        "n1" or "contingency" => Commands.N1(cli),
        "plf" => Commands.Plf(cli),
        "bench" => Commands.Bench(cli),
        "sld" => Commands.Sld(cli),
        "geojson" => Commands.GeoJson(cli),
        "convert" => Commands.Convert(cli),
        "rtu" => Commands.RtuAsync(cli),
        "serve" => Commands.ServeAsync(cli),
        _ => Extra.Dispatch(cli) ?? Commands.Fail($"Unknown command '{cli.Command}'. Run 'eln help'."),
    });
}
catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException or IOException
                               or System.Text.Json.JsonException or System.Xml.XmlException)
{
    return await Commands.Fail(ex.Message);
}

namespace Electrical.Cli
{
    /// <summary>Minimal argument parser: first token is the command; --key value pairs and --flags.</summary>
    public sealed class Args
    {
        private readonly Dictionary<string, string> _opts = new(StringComparer.OrdinalIgnoreCase);

        public Args(string[] args)
        {
            var pos = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    string key = args[i][2..];
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) _opts[key] = args[++i];
                    else _opts[key] = "true";
                }
                else pos.Add(args[i]);
            }
            Command = pos.Count > 0 ? pos[0].ToLowerInvariant() : "";
            Positional = pos.Skip(1).ToList();
        }

        public string Command { get; }
        public List<string> Positional { get; }
        public string? Get(string key) => _opts.TryGetValue(key, out var v) ? v : null;
        public string Get(string key, string def) => Get(key) ?? def;
        public double Num(string key, double def) => Get(key) is { } v ? double.Parse(v, CultureInfo.InvariantCulture) : def;
        public int Int(string key, int def) => Get(key) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : def;
        public bool Flag(string key) => Get(key) is "true" or "1" or "yes";

        /// <summary>Loads a network from --file (.m MATPOWER, .json, .xml CIM) or --case (default case14).</summary>
        public PowerNetwork Network(string? defaultCase = null)
        {
            if (Get("file") is { } f)
            {
                return Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".m" => Matpower.Load(f),
                    ".json" or ".geojson" when !f.EndsWith(".geojson", StringComparison.OrdinalIgnoreCase) => NetworkJson.Parse(File.ReadAllText(f)),
                    ".xml" or ".rdf" => Electrical.CIM.CimImporter.Import(File.ReadAllText(f)),
                    var e => throw new ArgumentException($"Unsupported network file type '{e}' (use .m, .json or .xml)."),
                };
            }
            return TestCases.ByName(Get("case") ?? defaultCase ?? "case14");
        }
    }

    /// <summary>Hook for commands added by other modules (see Extra.cs).</summary>
    public static partial class Extra
    {
        public static Task<int>? Dispatch(Args cli)
        {
            Task<int>? result = null;
            DispatchMore(cli, ref result);
            return result;
        }

        static partial void DispatchMore(Args cli, ref Task<int>? result);
    }

    public static partial class Commands
    {
        public static Task<int> Ok() => Task.FromResult(0);

        public static Task<int> Fail(string msg)
        {
            Console.Error.WriteLine($"error: {msg}");
            return Task.FromResult(1);
        }

        public static void Save(string path, string content)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            Console.WriteLine($"Saved {path}");
        }

        public static Task<int> Info()
        {
            Console.WriteLine($"{ElectricalInfo.Name} {ElectricalInfo.Version} — {ElectricalInfo.Tagline}");
            Console.WriteLine($"{ElectricalInfo.TaglineId}");
            Console.WriteLine($"{ElectricalInfo.CreditEn} · {ElectricalInfo.CreditId}");
            Console.WriteLine();
            Console.WriteLine($"Modules ({ElectricalInfo.Modules.Length}): {string.Join(", ", ElectricalInfo.Modules)}");
            Console.WriteLine($"Native kernels: {(GridKernels.IsNativeAvailable ? "Rust library loaded" : "not found — managed fallback")} ({GridKernels.Backend})");
            Console.WriteLine($"Test cases: {string.Join(", ", TestCases.Names)}");
            return Ok();
        }

        public static Task<int> Cases()
        {
            foreach (var n in TestCases.Names) Console.WriteLine($"  {TestCases.ByName(n)}");
            return Ok();
        }

        public static Task<int> PowerFlow(Args cli)
        {
            var net = cli.Network();
            if (cli.Num("scale", 1) is var k && k != 1) foreach (var l in net.Loads) { l.P *= k; l.Q *= k; }
            var method = cli.Get("method", "nr").ToLowerInvariant() switch
            {
                "nr" or "newton" => PowerFlowMethod.NewtonRaphson,
                "fd" or "fdlf" or "fast" => PowerFlowMethod.FastDecoupled,
                "gs" => PowerFlowMethod.GaussSeidel,
                "dc" => PowerFlowMethod.DC,
                "bfs" or "sweep" => PowerFlowMethod.BackwardForwardSweep,
                var m => throw new ArgumentException($"Unknown method '{m}' (nr, fd, gs, dc, bfs)."),
            };
            var r = PowerFlowSolver.Solve(net, new PowerFlowOptions { Method = method, EnforceQLimits = cli.Flag("qlim") });
            if (cli.Flag("json")) { Console.WriteLine(NetworkJson.ToJson(r).ToJsonString(new() { WriteIndented = true })); return Ok(); }
            Console.WriteLine(net);
            Console.WriteLine(r);
            Console.WriteLine();
            Console.WriteLine("  Bus     Vm[pu]   Va[deg]    Pg[MW]   Qg[Mvar]    Pd[MW]   Qd[Mvar]");
            foreach (var b in r.Buses.Take(cli.Int("rows", 40)))
                Console.WriteLine($"  {b.BusId,-5} {b.Vm,8:0.0000} {b.VaDeg,9:0.000} {b.PGen,9:0.00} {b.QGen,10:0.00} {b.PLoad,9:0.00} {b.QLoad,10:0.00}");
            Console.WriteLine();
            Console.WriteLine($"  Losses: {r.LossesMw:0.###} MW / {r.LossesMvar:0.###} Mvar · Vmin {r.MinVoltageBus.Vm:0.0000} pu @ bus {r.MinVoltageBus.BusId} · {r.Elapsed.TotalMilliseconds:0.0} ms");
            var viol = r.VoltageViolations().ToList();
            if (viol.Count > 0) Console.WriteLine($"  Voltage violations: {string.Join(", ", viol.Select(v => $"{v.BusId} ({v.Vm:0.000})"))}");
            if (cli.Get("out") is { } o) Save(o, Electrical.Visualization.SldRenderer.Render(net, r));
            return Ok();
        }

        public static Task<int> N1(Args cli)
        {
            var net = cli.Network();
            var res = ContingencyAnalysis.RunN1(net);
            Console.WriteLine($"N-1 contingency screening on {net.Name}: {res.Count} branch outages");
            Console.WriteLine("  Outage        Conv  Island  MaxLoad%  MostLoaded   Vmin@bus        Severity");
            foreach (var c in res.Take(cli.Int("top", 15)))
                Console.WriteLine($"  {c.Outage,-12}  {(c.Converged ? "yes" : "NO"),-4}  {(c.Islanding ? "YES" : "-"),-6}  {c.MaxLoadingPercent,8:0.0}  {c.MostLoadedBranch ?? "-",-11}  {c.MinVoltage:0.000}@{c.MinVoltageBus,-6}  {(double.IsInfinity(c.Severity) ? "∞" : c.Severity.ToString("0.0"))}");
            return Ok();
        }

        public static Task<int> Plf(Args cli)
        {
            var net = cli.Network();
            var r = ProbabilisticLoadFlow.Run(net, new ProbabilisticLoadFlowOptions
            {
                Samples = cli.Int("samples", 500), LoadStdFraction = cli.Num("std", 0.1), Seed = (ulong)cli.Int("seed", 2026),
            });
            Console.WriteLine($"Probabilistic load flow on {net.Name}: {r.Converged}/{r.Samples} samples converged, backend {r.Backend}, {r.Elapsed.TotalMilliseconds:0} ms");
            Console.WriteLine($"  Losses mean {r.MeanLossesMw:0.###} MW, P5..P95 {Core.Data.Stats.Percentile(r.LossesMw, 0.05):0.###}..{Core.Data.Stats.Percentile(r.LossesMw, 0.95):0.###} MW");
            Console.WriteLine($"  P(any bus < 0.95 pu) = {r.ProbabilityAnyUnderVoltage(0.95):P1}");
            foreach (var b in r.Buses.OrderBy(b => b.Mean).Take(5))
                Console.WriteLine($"  Bus {b.BusId,-4} mean {b.Mean:0.0000} σ {b.StdDev:0.0000} P5 {b.P05:0.0000} P(V<Vmin) {b.ProbabilityUnderVoltage:P1}");
            return Ok();
        }

        public static Task<int> Bench(Args cli)
        {
            var sizes = cli.Get("sizes", "100,500,1000,3000").Split(',').Select(int.Parse);
            Console.WriteLine("  Case            Buses  Branches  Iter      ms");
            foreach (var b in PowerFlowBenchmark.Run(sizes))
                Console.WriteLine($"  {b.Case,-14} {b.Buses,6} {b.Branches,9} {b.Iterations,5} {b.Milliseconds,8:0.0}");
            return Ok();
        }

        public static Task<int> Sld(Args cli)
        {
            var net = cli.Network();
            var r = PowerFlowSolver.Solve(net);
            var svg = Electrical.Visualization.SldRenderer.Render(net, r);
            if (cli.Flag("literal")) svg = Electrical.Visualization.SvgTheme.Resolve(svg, cli.Flag("dark"));
            Save(cli.Get("out", $"{net.Name}-sld.svg"), svg);
            return Ok();
        }

        public static Task<int> GeoJson(Args cli)
        {
            var net = cli.Network();
            if (net.Buses.Any(b => b.Location is null))
                Electrical.GIS.GeoLayout.ForceDirected(net, new GeoPoint(cli.Num("lat", -6.2), cli.Num("lon", 106.8)));
            var r = PowerFlowSolver.Solve(net);
            Save(cli.Get("out", $"{net.Name}.geojson"), Electrical.GIS.GeoJsonExporter.Export(net, r));
            if (cli.Get("svg") is { } svg) Save(svg, Electrical.GIS.GisSvgRenderer.Render(net, r));
            return Ok();
        }

        public static Task<int> Convert(Args cli)
        {
            var net = cli.Network();
            var o = cli.Get("out") ?? throw new ArgumentException("--out <file.m|file.json|file.xml> is required.");
            string content = Path.GetExtension(o).ToLowerInvariant() switch
            {
                ".m" => Matpower.Write(net),
                ".json" => NetworkJson.Serialize(net, indented: true),
                ".xml" or ".rdf" => Electrical.CIM.CimExporter.ExportString(net),
                var e => throw new ArgumentException($"Unsupported output type '{e}'."),
            };
            Save(o, content);
            return Ok();
        }

        public static async Task<int> RtuAsync(Args cli)
        {
            var net = cli.Network();
            var r = PowerFlowSolver.Solve(net);
            var rtu = new Electrical.SCADA.RtuSimulator(r);
            var server = new Electrical.SCADA.Modbus.ModbusTcpServer(rtu.Map, System.Net.IPAddress.Loopback, cli.Int("port", 1502));
            server.Start();
            Console.WriteLine($"Modbus TCP RTU simulator for {net.Name} on 127.0.0.1:{server.Port} (Float32 input registers: buses @0, branches @2000, generators @6000). Ctrl+C to stop.");
            var done = new TaskCompletionSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
            int seconds = cli.Int("seconds", 0);
            await (seconds > 0 ? Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds))) : done.Task);
            await server.StopAsync();
            return 0;
        }

        public static async Task<int> ServeAsync(Args cli)
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateSlimBuilder();
            Microsoft.AspNetCore.Hosting.HostingAbstractionsWebHostBuilderExtensions.UseUrls(builder.WebHost, cli.Get("urls", "http://localhost:5300"));
            Electrical.Core.Hosting.ElectricalServiceCollectionExtensions.AddElectrical(builder.Services, b => b.AddCloud().AddPowerFlow());
            var app = builder.Build();
            app.MapElectricalApi();
            Console.WriteLine($"Electrical.Net API on {cli.Get("urls", "http://localhost:5300")}/api/electrical (info, cases, studies/{{kind}}, jobs, scenarios). Ctrl+C to stop.");
            await app.RunAsync();
            return 0;
        }
    }
}
