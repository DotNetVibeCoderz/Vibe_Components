using System.Net;
using System.Numerics;
using Electrical.AI.Learning;
using Electrical.CIM;
using Electrical.Core.Data;
using Electrical.DigitalTwin.ThreeD;
using Electrical.Forecasting;
using Electrical.Pmu;
using Electrical.Pmu.C37118;
using Electrical.PowerFlow;
using Electrical.SCADA;
using Electrical.SCADA.Dnp3;
using Electrical.SCADA.Iec104;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchDataAi(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "pmu" => DataAiCommands.Pmu(cli),
            "dnp3" => DataAiCommands.Dnp3Async(cli),
            "iec104" => DataAiCommands.Iec104Async(cli),
            "cgmes" => DataAiCommands.Cgmes(cli),
            "learn" => DataAiCommands.Learn(cli),
            "twin3d" => DataAiCommands.Twin3D(cli),
            _ => null,
        };
    }
}

public static partial class Commands
{
    static partial void MoreHelpDataAi(ref string text)
    {
        text += """

              pmu [--buses 2,6,7,9] [--offnominal 1.5]  IEEE C37.118 frames from a PMU simulator, phasor estimation (TVE) and linear PMU state estimation
              dnp3                                    DNP3 outstation (power-flow RTU) ↔ master over loopback: integrity poll and CROB
              iec104                                  IEC 60870-5-104 server ↔ client over loopback: general interrogation and single command
              cgmes --out dir|file.zip [--validate file.zip]   CGMES 3.0 export (EQ/TP/SSH/SV) and validation
              learn forecast|faults|topology|voltvar  Machine learning: load forecasting benchmark, fault classification, topology identification, RL Volt/VAR
              twin3d --out grid.glb [--png view.png] [--scale 1.2]   3D digital twin scene coloured by power flow (GLB export, off-screen render)
            """;
    }
}

internal static class DataAiCommands
{
    public static Task<int> Pmu(Args cli)
    {
        var net = cli.Network();
        var pf = PowerFlowSolver.Solve(net);
        var buses = cli.Get("buses", "2,6,7,9").Split(',').Select(int.Parse).ToArray();
        var sim = new PmuSimulator(pf, buses, new PmuSimulatorOptions { FrequencyOffsetHz = 0.02 });
        var frame = sim.CreateFrame(new DateTimeOffset(2026, 11, 15, 8, 0, 0, TimeSpan.Zero));
        var bytes = C37Codec.Encode(frame, sim.Configuration);
        Console.WriteLine($"IEEE C37.118.2 data frame from {buses.Length} PMUs on {net.Name}: {bytes.Length} bytes, CRC-CCITT 0x{CrcCcitt.Compute(bytes.AsSpan(0, bytes.Length - 2)):X4}");
        Console.WriteLine("  " + Convert.ToHexString(bytes.AsSpan(0, Math.Min(32, bytes.Length))) + (bytes.Length > 32 ? "…" : ""));

        double fNom = net.FrequencyHz, off = cli.Num("offnominal", 1.5);
        double fs = fNom * 20; // 1 kHz at 50 Hz, 1.2 kHz at 60 Hz (must be an integer multiple of the nominal frequency)
        Console.WriteLine($"Phasor estimation at {fNom + off:0.0} Hz ({off:+0.0;-0.0} Hz off-nominal), {fs:0} Hz sampling:");
        foreach (var cls in new[] { PmuClass.P, PmuClass.M })
        {
            var est = new PhasorEstimator(new PhasorEstimatorOptions { NominalFrequencyHz = fNom, SampleRateHz = fs, ReportingRate = fNom, Class = cls });
            var truth = Complex.FromPolarCoordinates(1.0, 0.3);
            var x = PhasorEstimator.Sinusoid(truth, fNom + off, fs, (int)fs);
            var e = est.Estimate(x, 0)[^5];
            var reference = Complex.FromPolarCoordinates(1.0, 0.3 + 2 * Math.PI * off * e.TimeS);
            Console.WriteLine($"  {cls} class: |V| {e.Magnitude:0.0000}, f {e.FrequencyHz:0.0000} Hz, TVE {PhasorEstimator.Tve(e.Phasor, reference):P3}");
        }

        var meas = LinearStateEstimator.FromPowerFlow(pf, buses);
        var se = LinearStateEstimator.Estimate(net, meas);
        double maxDv = pf.Buses.Max(b => Math.Abs(b.Vm - se.Vm(b.BusId)));
        Console.WriteLine($"Linear PMU state estimation ({meas.Count} phasor measurements): max |ΔV| {maxDv:0.0000} pu, J {se.Objective:0.00} ({se.DegreesOfFreedom} dof)");
        return Commands.Ok();
    }

    public static async Task<int> Dnp3Async(Args cli)
    {
        var net = cli.Network();
        var rtu = new RtuSimulator(PowerFlowSolver.Solve(net));
        var map = rtu.CreateDnp3Map();
        var db = map.CreateDatabase();
        var tags = new TagDatabase();
        using var link = map.Attach(tags, db);
        rtu.UpdateTags(tags); // after Attach so the outstation database receives the values
        await using var outstation = new Dnp3Outstation(db, address: IPAddress.Loopback, port: 0);
        outstation.Start();
        var master = new Dnp3Master();
        await master.ConnectAsync("127.0.0.1", outstation.Port);
        var r = await master.IntegrityPollAsync();
        Console.WriteLine($"DNP3 outstation (RTU for {net.Name}) on 127.0.0.1:{outstation.Port}: integrity poll → {r.Measurements.AnalogInputs.Count} analog inputs, " +
                          $"{r.Measurements.BinaryInputs.Count} binary inputs, {r.Fragments} fragment(s), IIN {r.Iin}");
        foreach (var p in r.Measurements.AnalogInputs.Values.OrderBy(p => p.Index).Take(6)) Console.WriteLine($"  AI {p.Index,4}: {p.Value:0.0000}");
        if (r.Measurements.BinaryOutputs.Count > 0)
        {
            var status = await master.OperateBinaryAsync(0, Crob.LatchOff(), selectBeforeOperate: true);
            Console.WriteLine($"  CROB select-before-operate on BO 0 (latch off): {status}");
        }
        return 0;
    }

    public static async Task<int> Iec104Async(Args cli)
    {
        var net = cli.Network();
        var rtu = new RtuSimulator(PowerFlowSolver.Solve(net));
        var tags = new TagDatabase();
        rtu.UpdateTags(tags);
        var map = rtu.CreateIec104Map();
        await using var server = new Iec104Server(tags, map, address: IPAddress.Loopback, port: 0);
        server.Start();
        await using var client = new Iec104Client();
        await client.ConnectAsync("127.0.0.1", server.Port);
        await client.StartDataTransferAsync();
        var gi = await client.GeneralInterrogationAsync();
        Console.WriteLine($"IEC 60870-5-104 server (RTU for {net.Name}) on 127.0.0.1:{server.Port}: general interrogation → {gi.Count} information objects");
        foreach (var (type, obj) in gi.Take(6)) Console.WriteLine($"  {type,-12} IOA {obj.Address,6}: {obj.Value:0.0000} ({obj.Quality})");
        return 0;
    }

    public static Task<int> Cgmes(Args cli)
    {
        if (cli.Get("validate") is { } path)
        {
            var report = CgmesValidator.Validate(LoadDocs(path));
            Console.WriteLine($"CGMES validation of {path}: {(report.IsValid ? "valid" : "INVALID")}, {report.Errors.Count()} error(s), {report.Warnings.Count()} warning(s)");
            foreach (var i in report.Issues.Take(cli.Int("rows", 20))) Console.WriteLine($"  {i}");
            return Commands.Ok();
        }
        var net = cli.Network();
        var pf = PowerFlowSolver.Solve(net);
        var set = Cgmes3Exporter.Export(net, pf);
        var outPath = cli.Get("out", $"{net.Name}-cgmes3.zip");
        if (outPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(outPath, set.ToZip()); else set.Save(outPath);
        var rep = CgmesValidator.Validate(set);
        Console.WriteLine($"CGMES 3.0 export of {net.Name}: {string.Join(", ", set.Files().Select(f => f.FileName))} → {outPath}");
        Console.WriteLine($"  Validation: {(rep.IsValid ? "valid" : "INVALID")}, {rep.Errors.Count()} error(s), {rep.Warnings.Count()} warning(s), {CgmesValidator.Rules.Count} rules");
        var back = CgmesImporter.Import(set).Network;
        Console.WriteLine($"  Round trip: losses {pf.LossesMw:0.000000} MW → {PowerFlowSolver.Solve(back).LossesMw:0.000000} MW");
        return Commands.Ok();
    }

    private static IEnumerable<System.Xml.Linq.XDocument> LoadDocs(string path)
    {
        if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) { using var fs = File.OpenRead(path); return CgmesDocumentSet.ReadZip(fs).ToList(); }
        if (Directory.Exists(path)) return Directory.GetFiles(path, "*.xml").Select(System.Xml.Linq.XDocument.Load).ToList();
        return [System.Xml.Linq.XDocument.Load(path)];
    }

    public static Task<int> Learn(Args cli)
    {
        string what = cli.Positional.FirstOrDefault() ?? "forecast";
        switch (what)
        {
            case "forecast":
            {
                var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
                var temp = SyntheticProfiles.Temperature(start, 49, TimeSpan.FromHours(1), meanC: 28, dailyAmplitudeC: 4, noiseC: 1.0, seed: 21);
                var load = SyntheticProfiles.Load(LoadProfileKind.Residential, start, 49, TimeSpan.FromHours(1), peakMw: 10, noise: 0.01, seed: 22,
                    temperature: temp, coolingPerDegree: 0.03);
                var r = LoadForecastBenchmark.Run(load, temp, new LoadForecastBenchmarkOptions { TestDays = 7 });
                Console.WriteLine($"Day-ahead load forecast benchmark (temperature-driven residential load, {r.TestDays} test days), MAPE:");
                foreach (var (k, v) in r.Mape.OrderBy(kv => kv.Value)) Console.WriteLine($"  {k,-30} {v,6:0.00} %");
                break;
            }
            case "faults":
            {
                var ds = FaultDatasetGenerator.Generate(new FaultDatasetOptions { Samples = cli.Int("samples", 1600) });
                var (_, r) = FaultLearning.Train(ds, new ClassificationTrainingOptions { Trees = 50 });
                Console.WriteLine($"Fault classification / location on case33bw (random forest): {r}");
                break;
            }
            case "topology":
            {
                var o = new TopologySnapshotOptions { SnapshotsPerConfiguration = 50 };
                var (_, _, r) = TopologyIdentification.Train(TopologyIdentification.GenerateSnapshots(o), o, new ClassificationTrainingOptions { Trees = 60 });
                Console.WriteLine($"Topology identification on case33bw with 5 tie switches: {r}");
                break;
            }
            case "voltvar":
            {
                var cmp = VoltVarAgentTrainer.Compare(options: new VoltVarRlOptions { Steps = cli.Int("steps", 6000) });
                Console.WriteLine($"Volt/VAR control on case33bw, average reward (higher is better): {cmp}");
                break;
            }
            default: throw new ArgumentException($"Unknown learn task '{what}' (forecast, faults, topology, voltvar).");
        }
        return Commands.Ok();
    }

    public static Task<int> Twin3D(Args cli)
    {
        var net = cli.Network();
        double k = cli.Num("scale", 1.0);
        foreach (var l in net.Loads) { l.P *= k; l.Q *= k; }
        var pf = PowerFlowSolver.Solve(net);
        var scene = GridSceneBuilder.Build(net);
        scene.ApplyPowerFlow(pf);
        var outPath = cli.Get("out", $"{net.Name}-twin.glb");
        GlbExporter.Export(scene, outPath);
        Console.WriteLine($"3D twin of {net.Name} (load ×{k:0.00}): {scene.Objects.Count} scene objects → {outPath} ({new FileInfo(outPath).Length / 1024.0:0.0} KiB GLB)");
        if (cli.Get("png") is { } png)
        {
            try
            {
                using var view = ThreeNetGridScene.Create(scene);
                File.WriteAllBytes(png, view.RenderPng(cli.Int("width", 1280), cli.Int("height", 720)));
                Console.WriteLine($"Rendered {png}");
            }
            catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or NotSupportedException)
            {
                Console.Error.WriteLine($"warning: off-screen rendering unavailable ({e.Message})");
            }
        }
        return Commands.Ok();
    }
}
