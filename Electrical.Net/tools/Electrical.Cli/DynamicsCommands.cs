using Electrical.Dynamics;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchDynamics(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "dyn" => DynamicsCommands.Run(cli),
            _ => null,
        };
    }
}

public static partial class Commands
{
    static partial void MoreHelpDynamics(ref string text)
    {
        text += """

              dyn [--case kundur|case9] [--exc const|st|pss] [--fault 8] [--clear 0.1] [--trip 7-8] [--end 10] [--modes]
                                                      Detailed dynamics (GENROU, exciters, PSS): fault simulation and electromechanical modes
            """;
    }
}

internal static class DynamicsCommands
{
    public static Task<int> Run(Args cli)
    {
        bool kundur = !cli.Get("case", "kundur").Equals("case9", StringComparison.OrdinalIgnoreCase);
        var exc = cli.Get("exc", "pss").ToLowerInvariant() switch
        {
            "const" or "manual" => KundurExcitation.ConstantField,
            "st" or "static" => KundurExcitation.StaticExciter,
            "pss" => KundurExcitation.StaticExciterWithPss,
            var e => throw new ArgumentException($"Unknown excitation '{e}' (const, st, pss)."),
        };
        var dcase = kundur ? DynamicTestCases.KundurTwoArea(exc) : DynamicTestCases.Case9();
        var sys = dcase.Build();
        Console.WriteLine($"{dcase.Name}: {dcase.Description}");

        var ss = DynamicSmallSignal.Analyze(sys);
        Console.WriteLine($"Electromechanical modes ({(ss.IsStable() ? "stable" : "UNSTABLE")}):");
        foreach (var m in ss.ElectromechanicalModes().Where(m => m.Eigenvalue.Imaginary > 0).OrderBy(m => m.FrequencyHz))
            Console.WriteLine($"  {m.FrequencyHz,6:0.000} Hz  ζ = {m.DampingRatio,7:P1}  λ = {m.Eigenvalue.Real:0.0000} ± j{m.Eigenvalue.Imaginary:0.0000}  " +
                              $"{string.Join(", ", m.Dominant.Take(3).Select(d => $"{d.State} {d.Participation:0.00}"))}");
        if (cli.Flag("modes")) return Commands.Ok();

        int faultBus = cli.Int("fault", kundur ? 8 : 7);
        double clear = cli.Num("clear", 0.1), end = cli.Num("end", 10);
        var events = new List<DynamicEvent> { DynamicEvent.Fault(1.0, faultBus), DynamicEvent.Clear(1.0 + clear, faultBus) };
        if (cli.Get("trip") is { } trip)
        {
            var ends = trip.Split('-').Select(int.Parse).ToArray();
            int idx = dcase.Network.Branches.FindIndex(b => (b.From == ends[0] && b.To == ends[1]) || (b.From == ends[1] && b.To == ends[0]));
            if (idx < 0) throw new ArgumentException($"Branch {trip} not found.");
            events.Add(DynamicEvent.TripBranch(1.0 + clear, idx));
        }
        var r = DynamicSimulation.Run(sys, events, new SimulationOptions { EndTimeS = end });
        Console.WriteLine($"Three-phase fault at bus {faultBus} (t = 1 s), cleared after {clear * 1000:0} ms{(cli.Get("trip") is { } t2 ? $" by tripping {t2}" : "")}: " +
                          $"{(r.Stable ? "STABLE" : "UNSTABLE")}, {r.Steps} steps in {r.Elapsed.TotalMilliseconds:0} ms");
        Console.WriteLine("  Unit   δ(COI) max swing   speed range [pu]        V term min [pu]   Efd max [pu]");
        for (int u = 0; u < r.UnitNames.Count; u++)
        {
            var a = r.AngleCoiDeg[u]; var w = r.SpeedPu[u]; var v = r.TerminalVoltagePu[u]; var e = r.FieldVoltagePu[u];
            Console.WriteLine($"  {r.UnitNames[u],-6} {a.Max() - a.Min(),12:0.00}°   {w.Min():0.00000}–{w.Max():0.00000}   {v.Min(),12:0.000}   {e.Max(),12:0.000}");
        }
        return Commands.Ok();
    }
}
