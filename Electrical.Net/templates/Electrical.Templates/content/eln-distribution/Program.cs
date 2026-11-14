// Distribution feeder study — built with Electrical.Net (Gravicode Studios, led by Kang Fadhil).
// Usage: dotnet run                      (IEEE 13-node test feeder)
//        dotnet run -- path/to/feeder.dss (any OpenDSS script in the supported subset)
using Electrical.Distribution;

var net = args.Length > 0 ? OpenDssReader.ParseFile(args[0]) : DistributionTestCases.Ieee13Node(automaticRegulator: true);
foreach (var w in net.Warnings) Console.WriteLine($"warning: {w}");

// 1. Unbalanced three-phase power flow with automatic regulator control
var pf = UnbalancedPowerFlow.Solve(net);
Console.WriteLine($"Converged: {pf.Converged} ({pf.Method}), source {pf.TotalSourceKva.Real:0.0} kW, losses {pf.LossesKw:0.0} kW");
foreach (var (name, taps) in pf.RegulatorTaps) Console.WriteLine($"Regulator {name}: taps {string.Join("/", taps)}");
foreach (var bus in pf.Buses) Console.WriteLine($"  {bus}");
Console.WriteLine($"Max voltage unbalance: {pf.MaxVoltageUnbalance:P2}");

// 2. Line impedances from conductor data and pole geometry (modified Carson + Kron reduction)
var config601 = OverheadConfiguration.Spacing500(ConductorLibrary.Acsr556_5_26_7, ConductorLibrary.Acsr4_0_6_1,
    ConductorRole.PhaseB, ConductorRole.PhaseA, ConductorRole.PhaseC);
var z = LineConstants.Overhead(config601);
Console.WriteLine($"\nConfiguration 601: z_aa = {Fmt(z.ZOhmPerMile[0, 0])} Ω/mile, z1 = {Fmt(z.Sequence.Positive)}, z0 = {Fmt(z.Sequence.Zero)} Ω/mile");

// 3. Daily time series with the feeder's controllers (regulator taps, capacitors)
var day = DistributionTimeSeries.Run(net, DistributionTimeSeries.DailyLoadShape(), stepSeconds: 3600);
Console.WriteLine($"\n24 h: energy {day.EnergyKwh:0} kWh, losses {day.LossesKwh:0.0} kWh, V {day.MinVoltagePu:0.000}–{day.MaxVoltagePu:0.000} pu, " +
                  $"{day.TapOperations} tap operations, {day.CapacitorSwitchings} capacitor switchings");

static string Fmt(System.Numerics.Complex c) => $"{c.Real:0.0000}{(c.Imaginary < 0 ? "-" : "+")}j{Math.Abs(c.Imaginary):0.0000}";
