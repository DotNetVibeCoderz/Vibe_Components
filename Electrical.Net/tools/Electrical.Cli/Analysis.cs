using Electrical.Harmonic;
using Electrical.Protection;
using Electrical.ShortCircuit;
using Electrical.Stability;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchMore(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "sc" or "shortcircuit" => Analysis.ShortCircuit(cli),
            "stability" => Analysis.Stability(cli),
            "pv" => Analysis.PvCurve(cli),
            "modes" => Analysis.Modes(cli),
            "harmonic" => Analysis.Harmonic(cli),
            "arcflash" => Analysis.ArcFlash(cli),
            "relay" => Analysis.Relay(cli),
            _ => null,
        };
        DispatchOperations(cli, ref result);
    }

    static partial void DispatchOperations(Args cli, ref Task<int>? result);
}

public static partial class Commands
{
    static partial void MoreHelp(ref string text)
    {
        text += """

              sc [--bus 4 --type 3ph|slg|ll|llg]     IEC 60909 short circuit (all-bus scan without --bus)
              stability --bus 8 [--clear 0.083] [--trip 8-9]   Transient stability (classical model) + critical clearing time
              pv [--step 0.05]                        Voltage stability PV curve (loadability margin) and L-index
              modes                                   Small-signal eigenvalues (frequency, damping)
              harmonic --bus 9 [--source 6pulse|vfd|led] [--pu 0.1]   Harmonic load flow, THD and IEEE 519 check
              arcflash --kv 0.48 --ika 30 --time 0.2 [--dist 455] [--equipment switchgear]     IEEE 1584 arc flash
              relay --pickup 400 --tms 0.1 [--curve iec-si] --current 4000                     Relay operating time
            """;
        MoreHelpOperations(ref text);
    }

    static partial void MoreHelpOperations(ref string text);
}

internal static class Analysis
{
    private static double Z(double x) => Math.Abs(x) < 5e-5 ? 0 : x;

    public static Task<int> ShortCircuit(Args cli)
    {
        var net = cli.Network();
        if (cli.Get("bus") is { } b)
        {
            var type = cli.Get("type", "3ph").ToLowerInvariant() switch
            {
                "3ph" or "3" => FaultType.ThreePhase,
                "slg" or "1ph" => FaultType.SingleLineToGround,
                "ll" => FaultType.LineToLine,
                "llg" => FaultType.DoubleLineToGround,
                var t => throw new ArgumentException($"Unknown fault type '{t}'."),
            };
            var r = ShortCircuitAnalysis.Calculate(net, int.Parse(b), type);
            Console.WriteLine(r);
            Console.WriteLine($"  κ = {r.Kappa:0.###}, R/X = {r.RxRatio:0.###}, 3I0 = {r.EarthCurrentKa:0.###} kA");
            Console.WriteLine("  Bus voltages during fault [pu]  (a / b / c):");
            foreach (var v in r.Voltages.OrderBy(v => v.Va).Take(cli.Int("rows", 10)))
                Console.WriteLine($"    {v.BusId,-4} {v.Va:0.000} / {v.Vb:0.000} / {v.Vc:0.000}");
            return Commands.Ok();
        }
        Console.WriteLine($"IEC 60909 short-circuit scan (cmax) on {net.Name}");
        Console.WriteLine("  Bus     kV     I\"k3[kA] I\"k2[kA] I\"k1[kA]  ip[kA]   S\"k[MVA]   R/X");
        foreach (var s in ShortCircuitAnalysis.ScanAllBuses(net))
            Console.WriteLine($"  {s.BusId,-5} {s.BaseKv,6:0.#} {s.Ik3Ka,9:0.000} {s.Ik2Ka,8:0.000} {s.Ik1Ka,8:0.000} {s.IpKa,8:0.000} {s.Sk3Mva,10:0.0} {s.RxRatio,6:0.000}");
        return Commands.Ok();
    }

    public static Task<int> Stability(Args cli)
    {
        var net = cli.Network(cli.Get("case") is null && cli.Get("file") is null ? "case9" : null);
        int bus = cli.Int("bus", 8);
        int? trip = cli.Get("trip") is { } t ? TransientStability.BranchIndex(net, int.Parse(t.Split('-')[0]), int.Parse(t.Split('-')[1])) : null;
        double clear = cli.Num("clear", 0.083);
        var model = ClassicalModel.FromPowerFlow(net);
        var r = TransientStability.SimulateFault(model, bus, clear, trip);
        Console.WriteLine($"Transient stability on {net.Name}: 3φ fault at bus {bus}, cleared at {clear:0.000} s{(trip is null ? "" : $" by tripping branch {cli.Get("trip")}")}");
        Console.WriteLine($"  Result: {(r.Stable ? "STABLE" : "UNSTABLE")}");
        double cct = TransientStability.CriticalClearingTime(model, bus, trip);
        Console.WriteLine($"  Critical clearing time: {cct:0.000} s");
        return Commands.Ok();
    }

    public static Task<int> PvCurve(Args cli)
    {
        var net = cli.Network();
        var pv = VoltageStability.PvCurve(net);
        Console.WriteLine($"PV curve on {net.Name}: λmax = {pv.LambdaMax:0.000} (margin {pv.MarginMw:0.0} MW), critical bus {pv.CriticalBusId}");
        var li = VoltageStability.LIndex(net);
        Console.WriteLine($"L-index: critical bus {li.CriticalBusId}");
        return Commands.Ok();
    }

    public static Task<int> Modes(Args cli)
    {
        var net = cli.Network(cli.Get("case") is null && cli.Get("file") is null ? "case9" : null);
        var r = SmallSignal.Analyze(net);
        Console.WriteLine($"Small-signal analysis on {net.Name}: {(r.IsStable() ? "stable" : "UNSTABLE")}, minimum damping ratio {Z(r.MinimumDampingRatio):P2}");
        foreach (var m in r.OscillatoryModes.Where(m => m.Eigenvalue.Imaginary > 0))
            Console.WriteLine($"  λ = {m.Eigenvalue.Real,8:0.0000} ± j{m.Eigenvalue.Imaginary,8:0.0000}   f = {m.Eigenvalue.Imaginary / (2 * Math.PI):0.000} Hz   ζ = {Z(m.DampingRatio):P2}");
        return Commands.Ok();
    }

    public static Task<int> Harmonic(Args cli)
    {
        var net = cli.Network();
        var spectrum = cli.Get("source", "6pulse").ToLowerInvariant() switch
        {
            "6pulse" => TypicalSpectra.SixPulseRectifier(),
            "vfd" => TypicalSpectra.PwmDriveNoChoke(),
            "led" => TypicalSpectra.LedDriver(),
            var s => throw new ArgumentException($"Unknown source '{s}' (6pulse, vfd, led)."),
        };
        int bus = cli.Int("bus", net.Buses[^1].Id);
        var r = HarmonicLoadFlow.Run(net, [new HarmonicInjection(bus, spectrum, cli.Num("pu", 0.1))]);
        Console.WriteLine($"Harmonic load flow on {net.Name}: {spectrum.Name} at bus {bus}");
        Console.WriteLine($"  Worst voltage THD: {r.WorstBus.ThdPercent:0.00} % at bus {r.WorstBus.BusId}");
        foreach (var b in net.Buses.Select(b => (b.Id, Thd: r.ThdPercent(b.Id))).OrderByDescending(x => x.Thd).Take(cli.Int("rows", 8)))
            Console.WriteLine($"  Bus {b.Id,-4} THDv {b.Thd,6:0.00} %");
        return Commands.Ok();
    }

    public static Task<int> ArcFlash(Args cli)
    {
        var eq = Enum.Parse<ArcEquipment>(cli.Get("equipment", "switchgear"), true);
        var r = Protection.ArcFlash.Calculate(new ArcFlashInput(cli.Num("kv", 0.48), cli.Num("ika", 30), cli.Num("time", 0.2), cli.Num("dist", 455), eq));
        Console.WriteLine($"Arc flash ({r.Model}): Ia = {r.ArcingCurrentKa:0.00} kA, E = {r.IncidentEnergyCalCm2:0.00} cal/cm² ({r.IncidentEnergyJCm2:0.0} J/cm²)");
        Console.WriteLine($"  Arc-flash boundary {r.ArcFlashBoundaryMm:0} mm · {r.PpeDescription}");
        return Commands.Ok();
    }

    public static Task<int> Relay(Args cli)
    {
        var curve = cli.Get("curve", "iec-si").ToLowerInvariant() switch
        {
            "iec-si" => CurveType.IecStandardInverse,
            "iec-vi" => CurveType.IecVeryInverse,
            "iec-ei" => CurveType.IecExtremelyInverse,
            "iec-lti" => CurveType.IecLongTimeInverse,
            "ieee-mi" => CurveType.IeeeModeratelyInverse,
            "ieee-vi" => CurveType.IeeeVeryInverse,
            var c => throw new ArgumentException($"Unknown curve '{c}'."),
        };
        var relay = new OvercurrentRelay("R1", cli.Num("pickup", 400), curve, cli.Num("tms", 0.1));
        double i = cli.Num("current", 4000);
        Console.WriteLine($"{curve}, pickup {cli.Num("pickup", 400)} A, TMS {cli.Num("tms", 0.1)}: I = {i} A ({i / cli.Num("pickup", 400):0.0}× pickup) → t = {relay.OperatingTime(i):0.000} s");
        return Commands.Ok();
    }
}
