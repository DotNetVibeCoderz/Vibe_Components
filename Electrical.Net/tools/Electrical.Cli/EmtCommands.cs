using Electrical.Emt;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchEmt(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "emt" => EmtCommands.Run(cli),
            _ => null,
        };
        DispatchDynamics(cli, ref result);
    }

    static partial void DispatchDynamics(Args cli, ref Task<int>? result);
}

public static partial class Commands
{
    static partial void MoreHelpEmt(ref string text)
    {
        text += """

              emt [--case line|capbank] [--km 150] [--kv 230] [--dt 50e-6]   Electromagnetic transients: line energization overvoltage or capacitor inrush
            """;
        MoreHelpDynamics(ref text);
    }

    static partial void MoreHelpDynamics(ref string text);
}

internal static class EmtCommands
{
    public static Task<int> Run(Args cli)
    {
        double kv = cli.Num("kv", 230), f = cli.Num("f", 50), dt = cli.Num("dt", 50e-6);
        var net = new EmtNetwork();
        var opts = new EmtOptions { TimeStep = dt, StopTime = cli.Num("stop", 0.06) };
        string kind = cli.Get("case", "line").ToLowerInvariant();
        double vPeakPhase = kv * 1000 * Math.Sqrt(2.0 / 3.0);
        if (kind == "line")
        {
            // Source behind 10 kA short-circuit level → breaker (closes at 5 ms) → transposed overhead line, open at the far end.
            double km = cli.Num("km", 150);
            double xs = kv * 1000 / (Math.Sqrt(3) * 10_000);
            net.AddThreePhaseSource("Grid", "S", kv * 1000, f, l: xs / (2 * Math.PI * f));
            net.AddThreePhaseSwitch("CB", "S", "A", closeTime: 0.005);
            net.AddThreePhaseLine("Line", "A", "B", 0.03, 0.93e-3, 12.3e-9, 0.30, 3.0e-3, 7.9e-9, km);
            var r = EmtSimulation.Simulate(net, opts);
            Console.WriteLine($"Line energization: {kv} kV, {km} km open-ended line, breaker closes at 5 ms (Δt {dt * 1e6:0} µs, {r.Steps} steps, {r.Backend}, {r.Elapsed.TotalMilliseconds:0} ms)");
            foreach (var ph in new[] { "a", "b", "c" })
            {
                var v = r.Voltage($"B.{ph}");
                Console.WriteLine($"  Open end phase {ph}: peak {Math.Max(v.Peak(), -Neg(v)) / vPeakPhase:0.00} pu at {v.TimeOfPeak() * 1000:0.00} ms");
            }
        }
        else if (kind == "capbank")
        {
            // 11 kV bus (250 MVA short-circuit) energizing a 5 Mvar wye-grounded capacitor bank at the voltage peak of phase a.
            kv = cli.Num("kv", 11);
            vPeakPhase = kv * 1000 * Math.Sqrt(2.0 / 3.0);
            double ssc = 250e6, q = cli.Num("mvar", 5) * 1e6;
            double ls = kv * 1000 * kv * 1000 / ssc / (2 * Math.PI * f);
            double c = q / (kv * 1000 * kv * 1000 * 2 * Math.PI * f);
            net.AddThreePhaseSource("Grid", "S", kv * 1000, f, l: ls);
            net.AddThreePhaseSwitch("CB", "S", "C", closeTime: 0.02);
            net.AddThreePhaseCapacitorBank("Bank", "C", c);
            var r = EmtSimulation.Simulate(net, opts with { RecordCurrents = true });
            double iPeak = new[] { "a", "b", "c" }.Max(ph => { var ch = r.Current($"CB.{ph}"); return Math.Max(ch.Peak(), -Neg(ch)); });
            double iRated = q / (Math.Sqrt(3) * kv * 1000) * Math.Sqrt(2);
            double fRing = 1 / (2 * Math.PI * Math.Sqrt(ls * c));
            Console.WriteLine($"Capacitor bank energization: {q / 1e6:0.#} Mvar at {kv} kV, S\"k {ssc / 1e6:0} MVA (Δt {dt * 1e6:0} µs, {r.Backend})");
            Console.WriteLine($"  Inrush peak {iPeak:0} A = {iPeak / iRated:0.0} × rated peak; ring frequency ≈ {fRing:0} Hz");
            double vMax = new[] { "a", "b", "c" }.Max(ph => { var v = r.Voltage($"C.{ph}"); return Math.Max(v.Peak(), -Neg(v)); });
            Console.WriteLine($"  Bus overvoltage peak {vMax / vPeakPhase:0.00} pu");
        }
        else throw new ArgumentException($"Unknown EMT case '{kind}' (line, capbank).");
        return Commands.Ok();
    }

    private static double Neg(EmtChannel ch) => ch.Values.Min();
}
