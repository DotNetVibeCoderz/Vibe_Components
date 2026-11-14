using System.Numerics;
using Electrical.Core.Network;
using Electrical.HPC;
using Electrical.Native;

namespace Electrical.Tests;

public class NativeHpcTests
{
    [Fact]
    public void NativeLibrary_IsLoadedOnDevMachine()
    {
        // The Rust build copies the binary into runtimes/<rid>/native; CI provides it for every RID.
        Assert.True(GridKernels.IsNativeAvailable, "electrical_native not found — run build/build-native.ps1");
    }

    [Fact]
    public void NormalSamples_NativeAndManagedAreIdentical()
    {
        var a = new double[1001]; var b = new double[1001];
        GridKernels.PreferNative = true;
        GridKernels.NormalSamples(7, 1, 0.5, a);
        GridKernels.PreferNative = false;
        try { GridKernels.NormalSamples(7, 1, 0.5, b); }
        finally { GridKernels.PreferNative = true; }
        Assert.Equal(a, b);
    }

    [Fact]
    public void Fft_NativeMatchesManaged()
    {
        var x = Enumerable.Range(0, 256).Select(i => new Complex(Math.Sin(i * 0.3), Math.Cos(i * 0.11))).ToArray();
        var y = (Complex[])x.Clone();
        GridKernels.Fft(x);
        GridKernels.ManagedFft(y, false);
        for (int i = 0; i < x.Length; i++) Assert.True((x[i] - y[i]).Magnitude < 1e-9);
    }

    [Fact]
    public void Harmonics_Thd()
    {
        double fs = 10_000, f0 = 50;
        var x = Enumerable.Range(0, 2000).Select(i => 230 * Math.Sqrt(2) * Math.Sin(2 * Math.PI * f0 * i / fs)
                                                    + 10 * Math.Sin(2 * Math.PI * 3 * f0 * i / fs)).ToArray();
        var h = GridKernels.Harmonics(x, fs, f0, 9);
        Assert.Equal(230 * Math.Sqrt(2), h[0], 6);
        Assert.Equal(10, h[2], 6);
        Assert.Equal(100 * 10 / (230 * Math.Sqrt(2)), GridKernels.Thd(h), 6);
        Assert.Equal(Math.Sqrt(230 * 230 + 50), GridKernels.Rms(x), 6);
    }

    [Fact]
    public void PowerInjections_MatchAdmittanceMatrix()
    {
        var net = TestCases.Case14();
        var r = Electrical.PowerFlow.PowerFlowSolver.Solve(net);
        var y = AdmittanceMatrix.Build(net);
        var expected = y.Injections(r.Voltages);
        var s = new Complex[net.BusCount];
        GridKernels.PowerInjections(y.Y.RowPtr, y.Y.ColIdx, y.Y.Values, r.Voltages, s);
        for (int i = 0; i < s.Length; i++) Assert.True((s[i] - expected[i]).Magnitude < 1e-12);
    }

    [Fact]
    public void ProbabilisticLoadFlow_IsDeterministicAndCentered()
    {
        var net = TestCases.Case33BW();
        var opts = new ProbabilisticLoadFlowOptions { Samples = 200, Seed = 11 };
        var a = ProbabilisticLoadFlow.Run(net, opts);
        var b = ProbabilisticLoadFlow.Run(net, opts);
        Assert.Equal(200, a.Converged);
        Assert.Equal(a.LossesMw, b.LossesMw);
        Assert.InRange(a.MeanLossesMw * 1000, 185, 225);          // base case 202.7 kW
        Assert.InRange(a.Buses.First(x => x.BusId == 18).Mean, 0.905, 0.92);
        Assert.True(a.ProbabilityAnyUnderVoltage(0.95) > 0.9);      // 33-bus feeder is under-voltage at base load
    }

    [Fact]
    public void LoadSweep_LossesGrowWithLoad()
    {
        var res = BatchPowerFlow.LoadSweep(TestCases.Case14(), [0.8, 1.0, 1.2]);
        Assert.True(res[0].Result.LossesMw < res[1].Result.LossesMw && res[1].Result.LossesMw < res[2].Result.LossesMw);
        Assert.Equal(13.393, res[1].Result.LossesMw, 2);
    }
}
