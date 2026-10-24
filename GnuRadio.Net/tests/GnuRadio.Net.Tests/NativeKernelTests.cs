using GnuRadio.Fft;
using GnuRadio.Filter;
using GnuRadio.Kernels;

namespace GnuRadio.Tests;

// Volk.UseNative is process-wide, so these tests must not run alongside the others.
[Collection(nameof(NativeKernelTests))]
[CollectionDefinition(nameof(NativeKernelTests), DisableParallelization = true)]
public class NativeKernelTests
{
    [Fact]
    public void NativeKernelsMatchManagedWhenPresent()
    {
        if (!Volk.NativeAvailable) return; // optional component: run native/build.ps1 to enable
        var rng = new FastRandom(3);
        var x = Enumerable.Range(0, 4096).Select(_ => rng.NextComplexGaussian()).ToArray();
        var taps = Firdes.LowPass(1, 1, 0.1, 0.05);

        var managed = Run(false);
        var native = Run(true);
        for (var i = 0; i < managed.Fir.Length; i++) Assert.True((managed.Fir[i] - native.Fir[i]).Magnitude < 1e-4f);
        for (var i = 0; i < managed.Spectrum.Length; i++) Assert.True((managed.Spectrum[i] - native.Spectrum[i]).Magnitude < 1e-2f);
        for (var i = 0; i < managed.Product.Length; i++) Assert.True((managed.Product[i] - native.Product[i]).Magnitude < 1e-3f);

        (Complex32[] Fir, Complex32[] Spectrum, Complex32[] Product) Run(bool useNative)
        {
            Volk.UseNative = useNative;
            try
            {
                var fir = new Complex32[x.Length];
                new FirFilter<Complex32>(taps).Filter(x, fir);
                var spectrum = x.ToArray();
                new FftPlan(4096).Execute(spectrum);
                var product = new Complex32[x.Length];
                Volk.Multiply(x, spectrum, product);
                return (fir, spectrum, product);
            }
            finally
            {
                Volk.UseNative = false;
            }
        }
    }
}
