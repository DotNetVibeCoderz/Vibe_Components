using System.Diagnostics;
using Electrical.Core.Hosting;
using Electrical.Core.Numerics;
using Electrical.Emt;
using Electrical.Native;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

/// <summary>EMT engine: native/managed kernel equivalence, switching devices, transformers, DI and performance.</summary>
public class EmtEngineTests(ITestOutputHelper output)
{
    [Fact]
    public void Kernels_NativeAndManaged_AreBitIdentical()
    {
        var rnd = new Random(1234);
        int nb = 500, nv = 120;
        var from = new int[nb]; var to = new int[nb];
        var cur = new double[6 * nb]; var nxt = new double[6 * nb];
        for (int k = 0; k < nb; k++)
        {
            from[k] = rnd.Next(-1, nv); to[k] = rnd.Next(-1, nv);
            for (int j = 0; j < 6; j++) { cur[6 * k + j] = rnd.NextDouble() - 0.5; nxt[6 * k + j] = rnd.NextDouble() - 0.5; }
        }
        var v = Enumerable.Range(0, nv).Select(_ => rnd.NextDouble() * 100).ToArray();
        var s1 = Enumerable.Range(0, 3 * nb).Select(_ => rnd.NextDouble()).ToArray(); var s2 = (double[])s1.Clone();
        var r1 = new double[nv]; var r2 = new double[nv];
        EmtKernels.BranchUpdate(from, to, v, cur, nxt, s1, r1);
        EmtKernels.ManagedBranchUpdate(from, to, v, cur, nxt, s2, r2);
        Assert.Equal(s2, s1);
        Assert.Equal(r2, r1);

        // LU substitution against SparseLU.Solve on a diagonally dominant random sparse matrix
        int n = 200;
        var b = new SparseBuilder<double>(n, n);
        for (int i = 0; i < n; i++)
        {
            b.Add(i, i, 10 + rnd.NextDouble());
            for (int e = 0; e < 3; e++) { int j = rnd.Next(n); double g = rnd.NextDouble(); b.Add(i, j, -g); b.Add(j, i, -g); b.Add(i, i, g); b.Add(j, j, g); }
        }
        var lu = SparseLU<double>.Factorize(b.ToCsr());
        var f = lu.ExportFactors()!;
        var rhs = Enumerable.Range(0, n).Select(_ => rnd.NextDouble()).ToArray();
        var x1 = new double[n]; var x2 = new double[n]; var w = new double[n];
        EmtKernels.LuSolve(f.Permutation, f.LRowPtr, f.LColIdx, f.LValues, f.URowPtr, f.UColIdx, f.UValues, f.Diagonal, rhs, x1, w);
        EmtKernels.ManagedLuSolve(f.Permutation, f.LRowPtr, f.LColIdx, f.LValues, f.URowPtr, f.UColIdx, f.UValues, f.Diagonal, rhs, x2, w);
        Assert.Equal(lu.Solve(rhs), x2);
        Assert.Equal(x2, x1);
        output.WriteLine($"EMT kernels backend: {EmtKernels.Backend} (native available: {EmtKernels.IsNativeAvailable})");
    }

    [Fact]
    public void Simulation_NativeAndManaged_ProduceIdenticalWaveforms()
    {
        var net = SyntheticGrid(8);
        var a = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 50e-6, StopTime = 0.06, PreferNative = true });
        var b = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 50e-6, StopTime = 0.06, PreferNative = false });
        Assert.Equal("managed", b.Backend);
        foreach (var node in a.NodeNames) Assert.Equal(b.Voltage(node).Values, a.Voltage(node).Values);
        foreach (var c in a.CurrentNames) Assert.Equal(b.Current(c).Values, a.Current(c).Values);
        Assert.True(a.Factorizations >= 3);
    }

    [Fact]
    public void Performance_ThreePhase100Nodes_RunsWellUnderOneSecond()
    {
        var net = SyntheticGrid(33);
        var opts = new EmtOptions { TimeStep = 50e-6, StopTime = 0.2 };
        EmtSimulation.Simulate(net, opts with { StopTime = 0.01 }); // warm-up (JIT)
        var sim = new EmtSimulation(net, opts);
        Assert.InRange(sim.NodeCount, 100, 110);
        var sw = Stopwatch.StartNew();
        var nat = sim.Run();
        double tNative = sw.Elapsed.TotalSeconds;
        sw.Restart();
        var man = EmtSimulation.Simulate(net, opts with { PreferNative = false });
        double tManaged = sw.Elapsed.TotalSeconds;
        output.WriteLine($"{sim.NodeCount} nodes ({sim.UnknownCount} unknown), {nat.Steps} steps: {nat.Backend} {tNative * 1e3:F0} ms, managed {tManaged * 1e3:F0} ms, factorizations {nat.Factorizations}");
        Assert.True(tNative < 1.0 && tManaged < 1.0, $"native {tNative:F3} s, managed {tManaged:F3} s");
        Assert.Equal(man.Voltage("b32.a").Values, nat.Voltage("b32.a").Values);
        Assert.True(nat.Voltage("b32.a").RmsLastCycles(50) > 0.8 * 132e3 / Math.Sqrt(3));
    }

    /// <summary>
    /// Deterministic radial/meshed 132 kV three-phase grid with n buses: alternating Bergeron lines and π sections, loads,
    /// a capacitor bank switched in at 30 ms and a breaker opening at current zero after 45 ms.
    /// </summary>
    internal static EmtNetwork SyntheticGrid(int buses)
    {
        var net = new EmtNetwork();
        net.AddThreePhaseSource("G", "b0", 132e3, 50, 0, r: 0.2, l: 0.01);
        for (int i = 1; i < buses; i++)
        {
            string a = "b" + (i - 1), b = "b" + i;
            double len = 20 + 7 * (i % 5);
            if (i % 2 == 1) net.AddThreePhaseLine("L" + i, a, b, 0.04, 1.0e-3, 11.5e-9, 0.3, 3.0e-3, 7.5e-9, len);
            else net.AddThreePhasePiSection("L" + i, a, b, 0.04, 1.0e-3, 11.5e-9, 0.3, 3.0e-3, 7.5e-9, len);
            if (i % 3 == 0) net.AddThreePhaseLoad("LD" + i, b, 2000 + 100 * i, 1.5);
        }
        net.AddThreePhaseSwitch("CBc", "b" + (buses / 2), "cap", closeTime: 0.03);
        net.AddThreePhaseCapacitorBank("C", "cap", 2e-6);
        net.AddThreePhaseSwitch("CBl", "b" + (buses - 1), "ld", initiallyClosed: true, openTime: 0.045, openAtCurrentZero: true);
        net.AddThreePhaseLoad("LDend", "ld", 500, 0.5);
        return net;
    }

    [Fact]
    public void Breaker_OpensAtCurrentZero_WithoutChopping()
    {
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Cosine(10e3, 50));
        net.AddSwitch("cb", "s", "x", initiallyClosed: true, openTime: 0.0123, openAtCurrentZero: true);
        net.AddSeriesRL("load", "x", "0", 10, 0.05);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.04 });
        var i = res.Current("cb");
        double ipk = i.Peak(0.0, 0.0123);
        Assert.True(Math.Abs(i.At(0.0124)) > 0.05 * ipk);          // still conducting after the command
        Assert.True(i.Peak(0.025, 0.04) < 1e-3 * ipk);              // interrupted
        // the last conducting sample is within one step of a current zero
        int last = 0;
        for (int k = 0; k < i.Count; k++) if (Math.Abs(i[k]) > 1e-3 * ipk) last = k;
        Assert.True(Math.Abs(i[last]) < 0.1 * ipk, $"chopped at {i[last]:F1} A of {ipk:F1} A");
        Assert.InRange(i.TimeAt(last), 0.0123, 0.0123 + 0.011);
    }

    [Fact]
    public void Diode_HalfWaveRectifier_AverageIsVpkOverPi()
    {
        const double vpk = 100;
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Sine(vpk, 50));
        net.AddDiode("D", "s", "o");
        net.AddResistor("R", "o", "0", 100);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.1 });
        var vo = res.Voltage("o").Window(0.06, 0.1 - 20e-6);
        Assert.Equal(vpk / Math.PI, vo.ToArray().Average(), 0.01 * vpk);
        Assert.True(res.Voltage("o").Values.Min() > -0.01);
        Assert.True(res.Factorizations > 8);
    }

    [Fact]
    public void Thyristor_FiringAngleControlsConduction()
    {
        const double vpk = 100, alphaDeg = 90;
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Sine(vpk, 50));
        net.AddThyristor("T", "s", "o", t => (t * 50 % 1.0) * 360 >= alphaDeg && (t * 50 % 1.0) * 360 < alphaDeg + 30);
        net.AddResistor("R", "o", "0", 100);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.1 });
        var vo = res.Voltage("o").Window(0.06, 0.1 - 20e-6).ToArray();
        // average of a half-wave controlled rectifier: Vpk/(2π)·(1 + cos α)
        Assert.Equal(vpk / (2 * Math.PI) * (1 + Math.Cos(alphaDeg * Math.PI / 180)), vo.Average(), 0.01 * vpk);
    }

    [Fact]
    public void Transformer_RatioAndLeakageDrop()
    {
        const double ratio = 20e3 / 400.0, rl = 4.0; // 20 kV / 400 V, small leakage on the LV side
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "hv", EmtWaveform.Cosine(20e3 * Math.Sqrt(2), 50));
        net.AddTransformer("T", "hv", "0", "lv", "0", ratio, 0.001, 1e-6);
        net.AddResistor("RL", "lv", "0", rl);
        var res = EmtSimulation.Simulate(net, new EmtOptions { TimeStep = 20e-6, StopTime = 0.1 });
        double vlv = res.Voltage("lv").RmsLastCycles(50, 2), ilv = res.Current("T").RmsLastCycles(50, 2), ihv = res.Current("T.p").RmsLastCycles(50, 2);
        Assert.Equal(400, vlv, 400 * 0.002);
        Assert.Equal(vlv / rl, ilv, 1e-3 * ilv);
        Assert.Equal(ilv / ratio, ihv, 1e-3 * ihv);
        Assert.Equal(ihv, res.Current("V").RmsLastCycles(50, 2), 1e-3 * ihv);   // ideal source delivers the primary current
    }

    [Fact]
    public void AddEmt_RegistersServiceAndValidatesInput()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddEmt().AddEmt()).BuildServiceProvider();
        var svc = sp.GetRequiredService<IEmtService>();
        Assert.Contains("Emt", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        var net = new EmtNetwork();
        net.AddVoltageSource("V", "s", EmtWaveform.Dc(1));
        net.AddResistor("R", "s", "0", 2);
        Assert.Equal(0.5, svc.Simulate(net, new EmtOptions { StopTime = 1e-3 }).Current("R").Values[^1], 12);
        Assert.Equal(0.5, svc.Simulate(net, new EmtOptions { StopTime = 1e-3 }).Current("V").Values[^1], 12);
        var bad = new EmtNetwork();
        bad.AddVoltageSource("V", "s", EmtWaveform.Dc(1), r: 1);
        bad.Add(BergeronLine.SinglePhase("ln", "s", "e", 300, 10e-6));
        Assert.Throws<ArgumentException>(() => new EmtSimulation(bad, new EmtOptions { TimeStep = 50e-6 }));
        Assert.Throws<ArgumentException>(() => net.AddResistor("R", "s", "0", 1)); // duplicate name
    }
}
