using System.Runtime.CompilerServices;

namespace Electrical.Dynamics;

/// <summary>
/// Elementary control blocks shared by the model library (IEEE 421.5 / PSS/E conventions).
/// Time constants are in seconds; a time constant ≤ <see cref="MinTimeConstant"/> means "bypassed" (the block becomes algebraic and owns no state).
/// </summary>
public static class Blocks
{
    /// <summary>Time constants at or below this value [s] are treated as zero (block bypassed).</summary>
    public const double MinTimeConstant = 1e-6;

    /// <summary>True when <paramref name="t"/> is an active (non-zero) time constant.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Active(double t) => t > MinTimeConstant;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double Clamp(double x, double min, double max) => x < min ? min : x > max ? max : x;

    /// <summary>
    /// Derivative of a non-windup (integrator-type) limited lag K/(1+sT): dx/dt = (K·u − x)/T, held at zero while the state sits on a
    /// limit and is pushed outward (IEEE 421.5 Annex E.5). The state itself is clamped by the caller after each step.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NonWindupLag(double x, double u, double k, double t, double min, double max)
    {
        double d = (k * u - x) / t;
        if ((x >= max && d > 0) || (x <= min && d < 0)) return 0;
        return d;
    }

    /// <summary>Derivative of a non-windup integrator dx/dt = u limited to [min, max].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double NonWindupIntegrator(double x, double u, double min, double max)
    {
        if ((x >= max && u > 0) || (x <= min && u < 0)) return 0;
        return u;
    }

    /// <summary>Lead-lag output y = x + (T1/T2)·(u − x) with state dx/dt = (u − x)/T2.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double LeadLagOutput(double x, double u, double t1, double t2) => x + t1 / t2 * (u - x);

    /// <summary>Dead band: zero inside [lo, hi], shifted outside (non-step dead band as in WECC models).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double DeadBand(double x, double lo, double hi) => x > hi ? x - hi : x < lo ? x - lo : 0;

    /// <summary>Piece-wise linear interpolation in a table (x ascending); constant extrapolation.</summary>
    public static double Interpolate(ReadOnlySpan<double> xs, ReadOnlySpan<double> ys, double x)
    {
        if (xs.Length == 0) return 0;
        if (x <= xs[0]) return ys[0];
        for (int i = 1; i < xs.Length; i++)
            if (x <= xs[i])
            {
                double dx = xs[i] - xs[i - 1];
                return dx <= 0 ? ys[i] : ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / dx;
            }
        return ys[^1];
    }

    /// <summary>Wraps an angle to (−π, π].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double WrapAngle(double a)
    {
        a = Math.IEEERemainder(a, 2 * Math.PI);
        return a;
    }
}

/// <summary>
/// PSS/E quadratic saturation Sat(ψ) = B·(ψ − A)² for ψ &gt; A, fitted through S(1.0) and S(1.2) where S(x) = Sat(x)/x.
/// </summary>
public readonly struct QuadraticSaturation
{
    public double A { get; }
    public double B { get; }

    public QuadraticSaturation(double s10, double s12)
    {
        if (s10 <= 0 || s12 <= 0) { A = 0; B = 0; return; }
        double r = Math.Sqrt(1.2 * s12 / s10);
        if (Math.Abs(1 - r) < 1e-9) { A = 0; B = s10; return; }
        A = (1.2 - r) / (1 - r);
        B = s10 / ((1 - A) * (1 - A));
    }

    public bool IsZero => B == 0;

    /// <summary>Sat(ψ) [pu].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double psi) => B == 0 || psi <= A ? 0 : B * (psi - A) * (psi - A);
}

/// <summary>
/// IEEE exciter saturation SE(Efd) = B·(Efd − A)²/Efd through (E1, SE(E1)) and (E2, SE(E2)), returned as SE·Efd.
/// </summary>
public readonly struct ExciterSaturation
{
    public double A { get; }
    public double B { get; }

    public ExciterSaturation(double e1, double se1, double e2, double se2)
    {
        if (e1 <= 0 || e2 <= 0 || se1 <= 0 || se2 <= 0) { A = 0; B = 0; return; }
        double r = Math.Sqrt(se1 * e1 / (se2 * e2));
        A = (e1 - r * e2) / (1 - r);
        B = se1 * e1 / ((e1 - A) * (e1 - A));
        if (!double.IsFinite(A) || !double.IsFinite(B)) { A = 0; B = 0; }
    }

    /// <summary>SE(Efd)·Efd [pu].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Evaluate(double efd) => B == 0 || efd <= A ? 0 : B * (efd - A) * (efd - A);
}

/// <summary>
/// Proper rational transfer function N(s)/D(s) realised in controllable canonical form (coefficients in ascending powers of s).
/// Leading zero coefficients of D reduce the order; numerator terms above the denominator order are dropped (documented simplification
/// for improper data such as a lead-lag with zero lag time constant).
/// </summary>
public sealed class RationalBlock
{
    private readonly double[] _d; // monic denominator coefficients d0..d(n-1)
    private readonly double[] _c; // output weights
    private readonly double _dFeed;

    /// <param name="numerator">b0 + b1·s + … (ascending powers).</param>
    /// <param name="denominator">a0 + a1·s + … (ascending powers, a0 ≠ 0 unless the block is a pure differentiator washout).</param>
    public RationalBlock(double[] numerator, double[] denominator)
    {
        int n = denominator.Length - 1;
        while (n > 0 && Math.Abs(denominator[n]) <= Blocks.MinTimeConstant * 1e-3) n--;
        Order = n;
        double an = denominator[n];
        if (an == 0) throw new ArgumentException("Denominator is zero.");
        var b = new double[n + 1];
        for (int i = 0; i <= n && i < numerator.Length; i++) b[i] = numerator[i] / an;
        _d = new double[n];
        for (int i = 0; i < n; i++) _d[i] = denominator[i] / an;
        _dFeed = b[n];
        _c = new double[n];
        for (int i = 0; i < n; i++) _c[i] = b[i] - _dFeed * _d[i];
        DcGain = n == 0 ? b[0] : denominator[0] == 0 ? double.NaN : numerator.Length > 0 ? numerator[0] / denominator[0] : 0;
    }

    /// <summary>Number of states.</summary>
    public int Order { get; }
    /// <summary>Steady-state gain N(0)/D(0).</summary>
    public double DcGain { get; }

    public static RationalBlock LeadLag(double t1, double t2) => new([1, t1], [1, t2]);
    public static RationalBlock Washout(double k, double t) => Blocks.Active(t) ? new([0, k * t], [1, t]) : new([k], [1]);
    public static RationalBlock Lag(double k, double t) => new([k], [1, t]);

    /// <summary>Steady-state states for a constant input <paramref name="u"/>.</summary>
    public void Initialize(double u, Span<double> x)
    {
        for (int i = 0; i < Order; i++) x[i] = 0;
        if (Order > 0 && _d[0] != 0) x[0] = u / _d[0];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double Output(ReadOnlySpan<double> x, double u)
    {
        double y = _dFeed * u;
        for (int i = 0; i < Order; i++) y += _c[i] * x[i];
        return y;
    }

    public void Derivatives(ReadOnlySpan<double> x, double u, Span<double> dx)
    {
        int n = Order;
        if (n == 0) return;
        double last = u;
        for (int i = 0; i < n; i++) last -= _d[i] * x[i];
        for (int i = 0; i < n - 1; i++) dx[i] = x[i + 1];
        dx[n - 1] = last;
    }

    /// <summary>Polynomial product (ascending coefficients).</summary>
    public static double[] Multiply(params double[][] polys)
    {
        double[] r = [1];
        foreach (var p in polys)
        {
            var q = new double[r.Length + p.Length - 1];
            for (int i = 0; i < r.Length; i++)
                for (int j = 0; j < p.Length; j++) q[i + j] += r[i] * p[j];
            r = q;
        }
        return r;
    }

    public static double[] Power(double[] p, int k)
    {
        double[] r = [1];
        for (int i = 0; i < k; i++) r = Multiply(r, p);
        return r;
    }
}
