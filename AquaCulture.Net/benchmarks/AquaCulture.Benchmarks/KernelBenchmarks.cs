using AquaCulture.Core.Data;
using AquaCulture.Native;
using BenchmarkDotNet.Attributes;

namespace AquaCulture.Benchmarks;

/// <summary>The Rust kernels against their managed fallback (identical formulas, so only speed differs).</summary>
[MemoryDiagnoser]
public class KernelBenchmarks
{
    private const int Nx = 200, Ny = 200, Samples = 10_000;
    private float[] _c = [], _out = [], _u = [], _v = [], _src = [], _field = [];
    private byte[] _wet = [];
    private double[] _temp = [], _sal = [], _ph = [], _doOut = [], _days = [];

    [Params(KernelBackend.Native, KernelBackend.Managed)]
    public KernelBackend Backend { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        AquaKernels.ForceManaged = Backend == KernelBackend.Managed;
        if (AquaKernels.Backend != Backend)
            throw new InvalidOperationException($"Backend {Backend} requested but {AquaKernels.Backend} is active: build the Rust kernels first (build/build-native.ps1).");
        var rng = new DeterministicRandom(42);
        int n = Nx * Ny;
        _c = new float[n]; _out = new float[n]; _u = new float[n]; _v = new float[n]; _src = new float[n]; _wet = new byte[n]; _field = new float[100_000];
        for (int k = 0; k < n; k++)
        {
            _c[k] = (float)(rng.NextDouble() * 5);
            _u[k] = (float)(rng.NextDouble() * 0.2 - 0.1);
            _v[k] = (float)(rng.NextDouble() * 0.2 - 0.1);
            _wet[k] = (byte)(k % Nx is 0 or Nx - 1 ? 0 : 1);
        }
        _src[n / 2 + Nx / 2] = 1;
        for (int k = 0; k < _field.Length; k++) _field[k] = (float)rng.NextGaussian();
        _temp = new double[Samples]; _sal = new double[Samples]; _ph = new double[Samples]; _doOut = new double[Samples];
        for (int k = 0; k < Samples; k++) { _temp[k] = 24 + rng.NextDouble() * 8; _sal[k] = rng.NextDouble() * 35; _ph[k] = 7 + rng.NextDouble() * 1.8; }
        _days = Enumerable.Range(0, 120).Select(d => 28 + Math.Sin(d / 10.0)).ToArray();
    }

    [GlobalCleanup]
    public void Cleanup() => AquaKernels.ForceManaged = false;

    /// <summary>One advection–diffusion step on a 200 × 200 grid.</summary>
    [Benchmark]
    public void AdvectDiffuse200() => AquaKernels.AdvectDiffuseStep(_c, _out, _u, _v, _wet, _src, Nx, Ny, 5f, 10f, 0.5f, 1e-5f);

    /// <summary>DO saturation for 10 000 samples.</summary>
    [Benchmark]
    public void DoSaturation10k() => AquaKernels.DoSaturationBatch(_temp, _sal, 101.325, _doOut);

    /// <summary>Un-ionised ammonia fraction for 10 000 samples.</summary>
    [Benchmark]
    public void Nh3Fraction10k() => AquaKernels.Nh3FractionBatch(_temp, _ph, _sal, _doOut);

    /// <summary>Mean, SD, min, max of 100 000 values.</summary>
    [Benchmark]
    public FieldStats Stats100k() => AquaKernels.Stats(_field);

    /// <summary>1 000 Monte-Carlo growth runs over 120 days.</summary>
    [Benchmark]
    public double[] TgcMonteCarlo1k() => AquaKernels.TgcMonteCarlo(2, 2.4, _days, 1000, 0.8, 7);
}
