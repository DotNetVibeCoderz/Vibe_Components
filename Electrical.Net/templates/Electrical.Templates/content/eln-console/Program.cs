// Electrical.Net power-system study — built with Electrical.Net (Gravicode Studios, led by Kang Fadhil).
using Electrical.Core.Network;
using Electrical.HPC;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;
using Electrical.Visualization;

// 1. Load a network: a built-in test case, or Matpower.Load("mycase.m")
var net = TestCases.ByName("CASE_NAME");
Console.WriteLine(net);

// 2. AC power flow (sparse Newton–Raphson)
var pf = PowerFlowSolver.Solve(net, new PowerFlowOptions { EnforceQLimits = true });
Console.WriteLine(pf);
foreach (var b in pf.Buses)
    Console.WriteLine($"  Bus {b.BusId,3}: {b.Vm:0.0000} pu ∠ {b.VaDeg,8:0.00}°  Pg {b.PGen,8:0.0} MW  Pd {b.PLoad,7:0.0} MW");

// 3. N-1 contingency screening (worst five outages)
Console.WriteLine("\nN-1 contingencies (worst first):");
foreach (var c in ContingencyAnalysis.RunN1(net).Take(5))
    Console.WriteLine($"  {c.Outage,-8} converged={c.Converged} islanding={c.Islanding} maxLoading={c.MaxLoadingPercent:0.0}% Vmin={c.MinVoltage:0.000}");

// 4. IEC 60909 short-circuit levels at every bus
Console.WriteLine("\nShort-circuit levels (IEC 60909, cmax):");
foreach (var s in ShortCircuitAnalysis.ScanAllBuses(net).Take(10))
    Console.WriteLine($"  Bus {s.BusId,3}: I\"k3 {s.Ik3Ka,7:0.00} kA  I\"k1 {s.Ik1Ka,7:0.00} kA  ip {s.IpKa,7:0.00} kA  S\"k {s.Sk3Mva,8:0} MVA");

// 5. Probabilistic load flow (Monte-Carlo, parallel, Rust kernels when available)
var plf = ProbabilisticLoadFlow.Run(net, new ProbabilisticLoadFlowOptions { Samples = 300 });
Console.WriteLine($"\nProbabilistic load flow: mean losses {plf.MeanLossesMw:0.000} MW, P(V<0.95) = {plf.ProbabilityAnyUnderVoltage():P1} [{plf.Backend}]");

// 6. Single-line diagram with power-flow overlay
File.WriteAllText("single-line.svg", SvgTheme.Resolve(SldRenderer.Render(net, pf), dark: false));
Console.WriteLine("Saved single-line.svg");
