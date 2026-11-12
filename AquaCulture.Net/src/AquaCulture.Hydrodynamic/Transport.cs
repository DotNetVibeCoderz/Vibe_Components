using AquaCulture.Core.Hosting;
using AquaCulture.Native;

namespace AquaCulture.Hydrodynamic;

/// <summary>Water body types supported by the hydrodynamic module.</summary>
public enum WaterBodyType { Pond, River, Lake, Reservoir, CoastalArea, Ras }

/// <summary>Regular 2-D grid (row-major, index = j·Nx + i) with a wet/dry mask and a depth-averaged velocity field (m/s).</summary>
public sealed class Grid2D
{
    public Grid2D(int nx, int ny, double cellSizeM)
    {
        if (nx < 2 || ny < 2) throw new ArgumentOutOfRangeException(nameof(nx), "Grid must be at least 2 × 2.");
        Nx = nx; Ny = ny; Dx = cellSizeM;
        Wet = new byte[nx * ny]; Array.Fill(Wet, (byte)1);
        U = new float[nx * ny]; V = new float[nx * ny];
        Depth = new float[nx * ny]; Array.Fill(Depth, 1.2f);
    }

    public int Nx { get; }
    public int Ny { get; }
    public double Dx { get; }
    public byte[] Wet { get; }
    public float[] U { get; }
    public float[] V { get; }
    public float[] Depth { get; }
    public int Count => Nx * Ny;
    public int Index(int i, int j) => j * Nx + i;
    public (double X, double Y) Center(int i, int j) => ((i + 0.5) * Dx, (j + 0.5) * Dx);
    public double MaxSpeed { get { double m = 0; for (int k = 0; k < Count; k++) if (Wet[k] != 0) m = Math.Max(m, Math.Sqrt(U[k] * U[k] + V[k] * V[k])); return m; } }
    public int WetCount => Wet.Count(w => w != 0);

    /// <summary>Marks the outer ring of cells as dry (pond dikes).</summary>
    public Grid2D WithDikes(int width = 1)
    {
        for (int j = 0; j < Ny; j++) for (int i = 0; i < Nx; i++)
            if (i < width || j < width || i >= Nx - width || j >= Ny - width) Wet[Index(i, j)] = 0;
        return this;
    }
}

/// <summary>Velocity field generators.</summary>
public static class FlowFields
{
    /// <summary>Uniform current (river reach, lake drift).</summary>
    public static void Uniform(Grid2D g, double u, double v)
    {
        for (int k = 0; k < g.Count; k++) { g.U[k] = g.Wet[k] != 0 ? (float)u : 0; g.V[k] = g.Wet[k] != 0 ? (float)v : 0; }
    }

    /// <summary>River channel with a parabolic cross-section velocity profile along +x (max at mid-channel).</summary>
    public static void RiverChannel(Grid2D g, double maxVelocity)
    {
        for (int j = 0; j < g.Ny; j++)
        {
            double y = (j + 0.5) / g.Ny, prof = 4 * y * (1 - y);
            for (int i = 0; i < g.Nx; i++) { int k = g.Index(i, j); g.U[k] = g.Wet[k] != 0 ? (float)(maxVelocity * prof) : 0; g.V[k] = 0; }
        }
    }

    /// <summary>
    /// Rectangular-pond circulation driven by paddle wheels: a solid-body/irrotational vortex around the pond centre
    /// (clockwise when <paramref name="clockwise"/>) with speed peaking at mid-radius, plus jets near each wheel.
    /// </summary>
    public static void PaddleWheelCirculation(Grid2D g, double peakVelocity, IEnumerable<(double X, double Y, double Thrust)> wheels, bool clockwise = true)
    {
        double cx = g.Nx * g.Dx / 2, cy = g.Ny * g.Dx / 2, rmax = Math.Min(cx, cy);
        double sign = clockwise ? -1 : 1;
        var wl = wheels.ToList();
        for (int j = 0; j < g.Ny; j++)
            for (int i = 0; i < g.Nx; i++)
            {
                int k = g.Index(i, j);
                if (g.Wet[k] == 0) { g.U[k] = g.V[k] = 0; continue; }
                var (x, y) = g.Center(i, j);
                double dx = x - cx, dy = y - cy, r = Math.Sqrt(dx * dx + dy * dy) + 1e-6, rr = r / rmax;
                double speed = peakVelocity * Math.Min(1.4, 2 * rr * Math.Exp(1 - 2 * rr) * 1.0);
                double u = sign * -dy / r * speed, v = sign * dx / r * speed;
                foreach (var (wx, wy, thrust) in wl)
                {
                    double ddx = x - wx, ddy = y - wy, d2 = ddx * ddx + ddy * ddy;
                    double boost = thrust * Math.Exp(-d2 / (2 * Math.Pow(3 * g.Dx, 2)));
                    double tx = sign * -(wy - cy), ty = sign * (wx - cx), tn = Math.Sqrt(tx * tx + ty * ty) + 1e-6;
                    u += boost * tx / tn; v += boost * ty / tn;
                }
                g.U[k] = (float)u; g.V[k] = (float)v;
            }
    }

    /// <summary>Tidal coastal current: u(t) = U₀ sin(2πt/T) along the shore (+x), uniform over wet cells.</summary>
    public static void Tidal(Grid2D g, double amplitude, double timeHours, double periodHours = 12.42) =>
        Uniform(g, amplitude * Math.Sin(2 * Math.PI * timeHours / periodHours), 0);
}

/// <summary>Point or distributed source/sink of a scalar (mg/L per second, i.e. g m⁻³ s⁻¹).</summary>
public sealed record ScalarSource(int I, int J, double RatePerSecond, int Radius = 0);

/// <summary>Result of a transport simulation.</summary>
public sealed record TransportResult(float[] Field, double Mean, double Min, double Max, double SimulatedSeconds, int Steps, double TimeStep, KernelBackend Backend,
    IReadOnlyList<(double Seconds, double Mean, double Min)> History);

/// <summary>
/// Depth-averaged advection–diffusion–reaction transport of a scalar (DO, temperature, TAN, waste) on <see cref="Grid2D"/>,
/// solved explicitly with a CFL-limited time step using the Rust kernel (managed fallback).
/// Linear relaxation toward an equilibrium (e.g. surface reaeration toward DO saturation) is expressed as decay k and source k·C_eq.
/// </summary>
public sealed class ScalarTransport(Grid2D grid)
{
    public Grid2D Grid { get; } = grid;
    /// <summary>Horizontal eddy diffusivity (m²/s).</summary>
    public double Diffusivity { get; init; } = 0.05;
    /// <summary>First-order decay or relaxation rate (s⁻¹).</summary>
    public double Decay { get; init; }
    /// <summary>Equilibrium concentration for relaxation (adds Decay·Equilibrium to the source).</summary>
    public double Equilibrium { get; init; }
    /// <summary>Uniform background source (mg/L/s, negative for a sink such as respiration).</summary>
    public double Background { get; init; }
    public List<ScalarSource> Sources { get; } = [];

    /// <summary>Stable explicit time step (s).</summary>
    public double StableTimeStep()
    {
        double dx = Grid.Dx, umax = Math.Max(Grid.MaxSpeed, 1e-6);
        double dtAdv = 0.45 * dx / umax, dtDif = Diffusivity > 0 ? 0.2 * dx * dx / Diffusivity : double.MaxValue;
        double dtDec = Decay > 0 ? 0.5 / Decay : double.MaxValue;
        return Math.Min(Math.Min(dtAdv, dtDif), Math.Min(dtDec, 600));
    }

    public float[] SourceField()
    {
        var s = new float[Grid.Count];
        float bg = (float)(Background + Decay * Equilibrium);
        for (int k = 0; k < s.Length; k++) s[k] = Grid.Wet[k] != 0 ? bg : 0;
        foreach (var src in Sources)
        {
            var cells = new List<int>();
            for (int dj = -src.Radius; dj <= src.Radius; dj++)
                for (int di = -src.Radius; di <= src.Radius; di++)
                {
                    int i = src.I + di, j = src.J + dj;
                    if (i < 0 || j < 0 || i >= Grid.Nx || j >= Grid.Ny || di * di + dj * dj > src.Radius * src.Radius) continue;
                    int k = Grid.Index(i, j);
                    if (Grid.Wet[k] != 0) cells.Add(k);
                }
            foreach (int k in cells) s[k] += (float)(src.RatePerSecond / cells.Count);
        }
        return s;
    }

    /// <summary>Runs for <paramref name="seconds"/>, recording the mean/min every <paramref name="recordEverySeconds"/>.</summary>
    public TransportResult Run(float[] initial, double seconds, double recordEverySeconds = 600)
    {
        if (initial.Length != Grid.Count) throw new ArgumentException("Initial field size mismatch.");
        var c = (float[])initial.Clone();
        var next = new float[c.Length];
        var src = SourceField();
        double dt = StableTimeStep();
        int steps = (int)Math.Ceiling(seconds / dt);
        dt = seconds / steps;
        var history = new List<(double, double, double)>();
        double nextRecord = 0;
        for (int s = 0; s < steps; s++)
        {
            double t = s * dt;
            if (t >= nextRecord) { var st = AquaKernels.Stats(Masked(c)); history.Add((t, st.Mean, st.Min)); nextRecord += recordEverySeconds; }
            AquaKernels.AdvectDiffuseStep(c, next, Grid.U, Grid.V, Grid.Wet, src, Grid.Nx, Grid.Ny, (float)Grid.Dx, (float)dt, (float)Diffusivity, (float)Decay);
            (c, next) = (next, c);
        }
        var fs = AquaKernels.Stats(Masked(c));
        history.Add((seconds, fs.Mean, fs.Min));
        return new TransportResult(c, fs.Mean, fs.Min, fs.Max, seconds, steps, dt, AquaKernels.Backend, history);
    }

    private float[] Masked(float[] c)
    {
        var m = new float[c.Length];
        for (int k = 0; k < c.Length; k++) m[k] = Grid.Wet[k] != 0 ? c[k] : float.NaN;
        return m;
    }
}

/// <summary>Sedimentation and waste-dispersion closed forms.</summary>
public static class Sediment
{
    /// <summary>Stokes settling velocity (m/s) of a particle of diameter d (m).</summary>
    public static double StokesVelocity(double diameterM, double particleDensity = 1150, double waterDensity = 1000, double viscosity = 0.00089) =>
        9.81 * (particleDensity - waterDensity) * diameterM * diameterM / (18 * viscosity);

    /// <summary>First-order removal rate (s⁻¹) of suspended solids settling through a water column of depth h.</summary>
    public static double SettlingRate(double settlingVelocity, double depthM) => settlingVelocity / depthM;

    /// <summary>
    /// Steady depth-averaged plume from a continuous point source (e.g. a net cage) in a uniform current:
    /// C(x, y) = Q / (H √(4π D u x)) · exp(−u y² / (4 D x)) for x &gt; 0 (g/m³).
    /// </summary>
    public static double PlumeConcentration(double sourceGPerS, double depthM, double currentMs, double diffusivity, double xM, double yM)
    {
        if (xM <= 0 || currentMs <= 0) return 0;
        return sourceGPerS / (depthM * Math.Sqrt(4 * Math.PI * diffusivity * currentMs * xM)) * Math.Exp(-currentMs * yM * yM / (4 * diffusivity * xM));
    }

    /// <summary>Daily waste from a cage or pond: uneaten feed, faeces and dissolved nutrients (kg/day).</summary>
    public static (double SolidsKg, double NitrogenKg, double PhosphorusKg) WasteLoad(double feedKgPerDay, double feedProteinPct, double feedPhosphorusPct = 1.2, double fcr = 1.5)
    {
        double solids = feedKgPerDay * 0.28;
        double nFeed = feedKgPerDay * feedProteinPct / 100 / 6.25, nRetained = feedKgPerDay / fcr * 0.028;
        double pFeed = feedKgPerDay * feedPhosphorusPct / 100, pRetained = feedKgPerDay / fcr * 0.004;
        return (solids, Math.Max(0, nFeed - nRetained), Math.Max(0, pFeed - pRetained));
    }
}

public static class HydrodynamicBuilderExtensions
{
    public static AquaBuilder AddHydrodynamic(this AquaBuilder b) => b.TryAddModule("Hydrodynamic", _ => { });
}
