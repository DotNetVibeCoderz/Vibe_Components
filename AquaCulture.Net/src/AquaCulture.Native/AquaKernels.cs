namespace AquaCulture.Native;

/// <summary>Which implementation executes the kernels.</summary>
public enum KernelBackend { Native, Managed }

/// <summary>Summary of a float field (NaN skipped).</summary>
public readonly record struct FieldStats(long Count, double Min, double Max, double Mean, double StdDev);

/// <summary>
/// High-performance kernels. Uses the Rust library <c>aqua_native</c> when it can be loaded and falls back to managed code
/// with the same formulas otherwise. Set <c>AQUA_NATIVE=0</c> (or <see cref="ForceManaged"/>) to disable the native path.
/// </summary>
public static class AquaKernels
{
    private static readonly Lazy<bool> Available = new(() =>
    {
        if (Environment.GetEnvironmentVariable("AQUA_NATIVE") == "0") return false;
        try { return NativeMethods.Version() >= 10000; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    });

    /// <summary>Forces the managed implementation (for tests and benchmarks).</summary>
    public static bool ForceManaged { get; set; }

    public static KernelBackend Backend => !ForceManaged && Available.Value ? KernelBackend.Native : KernelBackend.Managed;

    public static uint? NativeVersion => Available.Value ? NativeMethods.Version() : null;

    // ───────────── chemistry ─────────────

    /// <summary>Fraction of total ammonia as un-ionised NH₃ (Emerson; Bower &amp; Bidwell salinity correction).</summary>
    public static double Nh3Fraction(double tempC, double ph, double salinityPpt = 0)
    {
        double tk = tempC + 273.15, pka = 0.09018 + 2729.92 / tk;
        if (salinityPpt > 0) pka += (0.1552 - 0.000314 * tk) * (19.9273 * salinityPpt / (1000 - 1.005109 * salinityPpt));
        return 1 / (1 + Math.Pow(10, pka - ph));
    }

    /// <summary>Oxygen solubility (mg/L), Benson &amp; Krause / APHA 4500-O with pressure correction.</summary>
    public static double DoSaturation(double tempC, double salinityPpt = 0, double pressureKPa = 101.325)
    {
        double t = tempC + 273.15, t2 = t * t, t3 = t2 * t, t4 = t3 * t;
        double lnC = -139.34411 + 1.575701e5 / t - 6.642308e7 / t2 + 1.2438e10 / t3 - 8.621949e11 / t4
                     - salinityPpt * (1.7674e-2 - 10.754 / t + 2140.7 / t2);
        double c = Math.Exp(lnC), p = pressureKPa / 101.325;
        if (Math.Abs(p - 1) < 1e-12) return c;
        double pwv = Math.Exp(11.8571 - 3840.70 / t - 216961 / t2);
        double theta = 0.000975 - 1.426e-5 * tempC + 6.436e-8 * tempC * tempC;
        return c * p * ((1 - pwv / p) * (1 - theta * p)) / ((1 - pwv) * (1 - theta));
    }

    public static unsafe void Nh3FractionBatch(ReadOnlySpan<double> temp, ReadOnlySpan<double> ph, ReadOnlySpan<double> sal, Span<double> output)
    {
        int n = Check(temp.Length, ph.Length, sal.Length, output.Length);
        if (Backend == KernelBackend.Native)
        {
            fixed (double* t = temp, p = ph, s = sal, o = output)
                if (NativeMethods.Nh3FractionBatch(t, p, s, o, (nuint)n) == 0) return;
        }
        for (int i = 0; i < n; i++) output[i] = Nh3Fraction(temp[i], ph[i], sal[i]);
    }

    public static unsafe void DoSaturationBatch(ReadOnlySpan<double> temp, ReadOnlySpan<double> sal, double pressureKPa, Span<double> output)
    {
        int n = Check(temp.Length, sal.Length, output.Length, output.Length);
        if (Backend == KernelBackend.Native)
        {
            fixed (double* t = temp, s = sal, o = output)
                if (NativeMethods.DoSaturationBatch(t, s, pressureKPa, o, (nuint)n) == 0) return;
        }
        for (int i = 0; i < n; i++) output[i] = DoSaturation(temp[i], sal[i], pressureKPa);
    }

    // ───────────── statistics ─────────────

    public static unsafe FieldStats Stats(ReadOnlySpan<float> values)
    {
        if (Backend == KernelBackend.Native)
        {
            AqStats s;
            fixed (float* v = values)
                if (NativeMethods.Stats(v, (nuint)values.Length, &s) == 0)
                    return new((long)s.Count, s.Min, s.Max, s.Mean, s.Count > 1 ? Math.Sqrt(s.M2 / (s.Count - 1)) : 0);
        }
        long n = 0; double mean = 0, m2 = 0, min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (float f in values)
        {
            if (float.IsNaN(f)) continue;
            double v = f; n++;
            double d = v - mean; mean += d / n; m2 += d * (v - mean);
            if (v < min) min = v;
            if (v > max) max = v;
        }
        return n == 0 ? new(0, double.NaN, double.NaN, double.NaN, double.NaN) : new(n, min, max, mean, n > 1 ? Math.Sqrt(m2 / (n - 1)) : 0);
    }

    // ───────────── growth Monte-Carlo ─────────────

    /// <summary>
    /// Monte-Carlo thermal-unit growth. For each simulation k (seed + k) the cube root of weight grows by TGC/1000 · T per day,
    /// with daily temperature T = temps[d] + tempSd · N(0, 1). Returns the final weight (g) of each simulation.
    /// </summary>
    public static unsafe double[] TgcMonteCarlo(double w0, double tgc, ReadOnlySpan<double> dailyTemps, int simulations, double tempSd, ulong seed)
    {
        var output = new double[simulations];
        if (Backend == KernelBackend.Native)
        {
            fixed (double* t = dailyTemps, o = output)
                if (NativeMethods.TgcMonteCarlo(w0, tgc, t, (nuint)dailyTemps.Length, (nuint)simulations, tempSd, seed, o) == 0) return output;
        }
        for (int k = 0; k < simulations; k++)
        {
            var rng = new Core.Data.DeterministicRandom(seed + (ulong)k);
            double cube = Math.Cbrt(w0);
            foreach (double t in dailyTemps)
            {
                double tt = t + tempSd * rng.NextGaussian();
                if (tt > 0) cube += tgc / 1000 * tt;
            }
            output[k] = cube * cube * cube;
        }
        return output;
    }

    // ───────────── hydrodynamics ─────────────

    /// <summary>
    /// One explicit advection–diffusion–reaction step on an nx × ny grid (row-major, j·nx + i):
    /// dc/dt = −u ∂c/∂x − v ∂c/∂y + D ∇²c − k c + s, upwind advection, zero flux at dry cells and borders, clamped at 0.
    /// </summary>
    public static unsafe void AdvectDiffuseStep(ReadOnlySpan<float> c, Span<float> output, ReadOnlySpan<float> u, ReadOnlySpan<float> v,
        ReadOnlySpan<byte> wet, ReadOnlySpan<float> source, int nx, int ny, float dx, float dt, float diffusivity, float decay)
    {
        int n = nx * ny;
        if (c.Length < n || output.Length < n || u.Length < n || v.Length < n || wet.Length < n || source.Length < n)
            throw new ArgumentException("All fields must have nx·ny elements.");
        if (Backend == KernelBackend.Native)
        {
            fixed (float* pc = c, po = output, pu = u, pv = v, ps = source)
            fixed (byte* pw = wet)
                if (NativeMethods.AdvectDiffuseStep(pc, po, pu, pv, pw, ps, (nuint)nx, (nuint)ny, dx, dt, diffusivity, decay) == 0) return;
        }
        float invDx = 1f / dx, invDx2 = invDx * invDx;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (wet[k] == 0) { output[k] = c[k]; continue; }
                float ck = c[k];
                float cw = Nb(c, wet, nx, ny, i - 1, j, ck), ce = Nb(c, wet, nx, ny, i + 1, j, ck);
                float cs = Nb(c, wet, nx, ny, i, j - 1, ck), cn = Nb(c, wet, nx, ny, i, j + 1, ck);
                float uk = u[k], vk = v[k];
                float dcdx = (uk > 0 ? ck - cw : ce - ck) * invDx;
                float dcdy = (vk > 0 ? ck - cs : cn - ck) * invDx;
                float lap = (cw + ce + cs + cn - 4f * ck) * invDx2;
                float rate = -uk * dcdx - vk * dcdy + diffusivity * lap - decay * ck + source[k];
                float next = ck + dt * rate;
                output[k] = next < 0 ? 0 : next;
            }
    }

    private static float Nb(ReadOnlySpan<float> c, ReadOnlySpan<byte> wet, int nx, int ny, int i, int j, float own)
    {
        if (i < 0 || j < 0 || i >= nx || j >= ny) return own;
        int k = j * nx + i;
        return wet[k] == 0 ? own : c[k];
    }

    private static int Check(int a, int b, int c, int d)
    {
        if (a != b || a != c || a != d) throw new ArgumentException("Input and output spans must have the same length.");
        return a;
    }
}
