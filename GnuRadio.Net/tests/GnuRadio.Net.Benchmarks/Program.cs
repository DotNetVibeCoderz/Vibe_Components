using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using GnuRadio;
using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Kernels;

// dotnet run -c Release --project tests/GnuRadio.Net.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(KernelBenchmarks).Assembly).Run(args);

/// <summary>Managed SIMD kernels against the optional Rust library, on the same data.</summary>
[MemoryDiagnoser]
public class KernelBenchmarks
{
    private Complex32[] _x = [];
    private Complex32[] _y = [];
    private Complex32[] _dst = [];
    private FirFilter<Complex32> _fir = null!;
    private FftPlan _fft = null!;

    [Params(false, true)]
    public bool Native { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Volk.UseNative = Native && Volk.NativeAvailable;
        var rng = new FastRandom(1);
        _x = Enumerable.Range(0, 65536).Select(_ => rng.NextComplexGaussian()).ToArray();
        _y = Enumerable.Range(0, 65536).Select(_ => rng.NextComplexGaussian()).ToArray();
        _dst = new Complex32[65536];
        _fir = new FirFilter<Complex32>(Firdes.LowPass(1, 1, 0.1, 0.02));
        _fft = new FftPlan(4096);
    }

    [Benchmark(Description = "FIR ccf, 65536 samples, ~165 taps")]
    public void Fir() => _fir.Filter(_x, _dst);

    [Benchmark(Description = "Complex multiply, 65536 samples")]
    public void Multiply() => Volk.Multiply(_x, _y, _dst);

    [Benchmark(Description = "FFT 4096")]
    public void Fft() => _fft.Execute(_dst.AsSpan(0, 4096));
}

/// <summary>End-to-end flowgraph throughput: source → FIR → FFT sink.</summary>
[MemoryDiagnoser]
public class FlowgraphBenchmarks
{
    [Benchmark(Description = "1M complex samples: source → FIR decim 4 → null sink")]
    public void FirChain()
    {
        var fg = new Flowgraph();
        fg.Connect(new GnuRadio.Blocks.NoiseSource<Complex32>(GnuRadio.Blocks.NoiseType.Gaussian, 1, 1),
            new GnuRadio.Blocks.Head<Complex32>(1_000_000),
            new FirFilterCcf(4, Firdes.LowPass(1, 1, 0.1, 0.05)),
            new GnuRadio.Blocks.NullSink<Complex32>());
        fg.Run();
    }
}
