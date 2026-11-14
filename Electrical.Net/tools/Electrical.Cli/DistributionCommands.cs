using System.Numerics;
using Electrical.Distribution;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchDistribution(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "dist" => DistributionCommands.Dist(cli),
            "dss" => DistributionCommands.Dss(cli),
            "linecode" => DistributionCommands.LineCodeCmd(cli),
            _ => null,
        };
    }
}

public static partial class Commands
{
    static partial void MoreHelpDistribution(ref string text)
    {
        text += """

              dist [--feeder ieee13|ieee4] [--auto] [--delta] [--unbalanced]   Unbalanced three-phase power flow on an IEEE test feeder
              dss --file feeder.dss [--auto]          Read an OpenDSS script and run the unbalanced power flow
              linecode --config 601                   Carson + Kron phase impedance matrix of an IEEE 13-node line configuration (601–607)
            """;
    }
}

internal static class DistributionCommands
{
    public static Task<int> Dist(Args cli)
    {
        bool ieee4 = cli.Get("feeder", "ieee13").Equals("ieee4", StringComparison.OrdinalIgnoreCase);
        var net = ieee4
            ? DistributionTestCases.Ieee4Node(cli.Flag("delta") ? Ieee4NodeConnection.DeltaGrY : Ieee4NodeConnection.GrYGrY, cli.Flag("unbalanced"))
            : DistributionTestCases.Ieee13Node(cli.Flag("auto"));
        Print(net, cli.Flag("auto"), ieee4 ? null : DistributionTestCases.Ieee13NodePublished);
        return Commands.Ok();
    }

    public static Task<int> Dss(Args cli)
    {
        var f = cli.Get("file") ?? throw new ArgumentException("--file <script.dss> is required.");
        var net = OpenDssReader.ParseFile(f);
        foreach (var w in net.Warnings) Console.Error.WriteLine($"warning: {w}");
        Print(net, cli.Flag("auto"), null);
        return Commands.Ok();
    }

    private static void Print(DistributionNetwork net, bool auto, IReadOnlyDictionary<string, PublishedVoltage[]>? published)
    {
        var r = UnbalancedPowerFlow.Solve(net, new DistributionPowerFlowOptions { ControlMode = auto ? ControlMode.Static : ControlMode.Off });
        Console.WriteLine($"Unbalanced power flow: {(r.Converged ? "converged" : "NOT converged")} in {r.Iterations} it ({r.Method}), source {r.TotalSourceKva.Real:0.0} kW / {r.TotalSourceKva.Imaginary:0.0} kvar, losses {r.LossesKw:0.0} kW");
        foreach (var (name, taps) in r.RegulatorTaps) Console.WriteLine($"  Regulator {name} taps: {string.Join("/", taps)}");
        Console.WriteLine($"  Max voltage unbalance (IEC): {r.MaxVoltageUnbalance:P2}");
        Console.WriteLine(published is null ? "  Bus          A [pu]          B [pu]          C [pu]" : "  Bus          A [pu]          B [pu]          C [pu]        max |Δ| vs IEEE");
        foreach (var b in r.Buses)
        {
            string Cell(int p) => b.Phases.Has(p) ? $"{b.VmPu[p]:0.0000}∠{b.VaDeg[p],7:0.00}" : "       -       ";
            string line = $"  {b.Name,-8} {Cell(0)} {Cell(1)} {Cell(2)}";
            if (published is not null && published.TryGetValue(b.Name, out var pv))
            {
                double err = b.Phases.Indices().Where(p => p < pv.Length && pv[p].Magnitude > 0).Select(p => Math.Abs(b.VmPu[p] - pv[p].Magnitude)).DefaultIfEmpty(0).Max();
                line += $"   {err * 100,6:0.000} %";
            }
            Console.WriteLine(line);
        }
    }

    public static Task<int> LineCodeCmd(Args cli)
    {
        int id = cli.Int("config", 601);
        var code = DistributionTestCases.Ieee13Config(id);
        var z = code.ZOhmPerMile;
        Console.WriteLine($"IEEE 13-node configuration {id} ({code.PhaseCount} phase{(code.PhaseCount > 1 ? "s" : "")}): phase impedance matrix [Ω/mile]");
        for (int i = 0; i < z.GetLength(0); i++)
            Console.WriteLine("  " + string.Join("   ", Enumerable.Range(0, z.GetLength(1)).Select(j => Fmt(z[i, j]))));
        if (id == 601)
        {
            var cfg = OverheadConfiguration.Spacing500(ConductorLibrary.Acsr556_5_26_7, ConductorLibrary.Acsr4_0_6_1,
                ConductorRole.PhaseB, ConductorRole.PhaseA, ConductorRole.PhaseC);
            var geo = LineConstants.Overhead(cfg);
            Console.WriteLine($"  Computed from conductors + pole spacing with modified Carson and Kron: z_aa = {Fmt(geo.ZOhmPerMile[0, 0])} Ω/mile");
            Console.WriteLine($"  Sequence impedances: z1 = {Fmt(geo.Sequence.Positive)}, z0 = {Fmt(geo.Sequence.Zero)} Ω/mile");
        }
        return Commands.Ok();
    }

    private static string Fmt(Complex c) => $"{c.Real,7:0.0000}{(c.Imaginary < 0 ? "-" : "+")}j{Math.Abs(c.Imaginary):0.0000}";
}
