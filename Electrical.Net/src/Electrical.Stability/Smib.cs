namespace Electrical.Stability;

/// <summary>
/// Single-machine infinite-bus (SMIB) classical system: P_e = P_max·sin δ with P_max = E·V/X for the pre-fault, fault-on and post-fault
/// reactances. Quantities in pu; H in s; frequency in Hz.
/// </summary>
/// <param name="H">Inertia constant [s].</param>
/// <param name="Pm">Mechanical power [pu].</param>
/// <param name="E">Internal EMF |E′| [pu].</param>
/// <param name="V">Infinite-bus voltage [pu].</param>
/// <param name="XPre">Transfer reactance before the fault [pu].</param>
/// <param name="XFault">Transfer reactance during the fault [pu] (∞ for a bolted fault at the machine terminals: P_max = 0).</param>
/// <param name="XPost">Transfer reactance after clearing [pu].</param>
/// <param name="D">Damping [pu power / pu speed].</param>
/// <param name="FrequencyHz">System frequency [Hz].</param>
public sealed record SmibSystem(double H, double Pm, double E, double V, double XPre, double XFault, double XPost, double D = 0, double FrequencyHz = 60)
{
    public double PmaxPre => E * V / XPre;
    public double PmaxFault => double.IsInfinity(XFault) ? 0 : E * V / XFault;
    public double PmaxPost => E * V / XPost;
    public double OmegaS => 2 * Math.PI * FrequencyHz;
    /// <summary>Initial rotor angle δ0 = asin(P_m/P_max,pre) [rad].</summary>
    public double Delta0 => Math.Asin(Pm / PmaxPre);
    /// <summary>Synchronising power coefficient at δ0, K_s = P_max·cos δ0 [pu/rad].</summary>
    public double SynchronizingCoefficient => PmaxPre * Math.Cos(Delta0);
    /// <summary>Undamped natural frequency ω_n = √(ω_s·K_s/(2H)) [rad/s].</summary>
    public double NaturalFrequencyRadS => Math.Sqrt(OmegaS * SynchronizingCoefficient / (2 * H));
}

/// <summary>Equal-area-criterion result (angles in degrees, time in s).</summary>
public sealed record EqualAreaResult(double Delta0Deg, double CriticalClearingAngleDeg, double MaxAngleDeg, double? CriticalClearingTimeS);

/// <summary>SMIB time-domain trajectory.</summary>
public sealed record SmibTrajectory(double[] Time, double[] DeltaDeg, double[] SpeedPu, bool Stable);

/// <summary>Equal-area criterion and SMIB simulation.</summary>
public static class Smib
{
    /// <summary>
    /// Critical clearing angle: cos δ_cr = [P_m(δ_max − δ0) + P_max3·cos δ_max − P_max2·cos δ0] / (P_max3 − P_max2), δ_max = π − asin(P_m/P_max3).
    /// For a bolted fault (P_max2 = 0) the clearing time is closed-form: t_cr = √(4H(δ_cr − δ0)/(ω_s·P_m)).
    /// </summary>
    public static EqualAreaResult EqualArea(SmibSystem s)
    {
        double d0 = s.Delta0;
        double p2 = s.PmaxFault, p3 = s.PmaxPost;
        if (s.Pm >= p3) throw new InvalidOperationException("Post-fault system cannot transmit Pm (no equilibrium).");
        double dmax = Math.PI - Math.Asin(s.Pm / p3);
        double c = (s.Pm * (dmax - d0) + p3 * Math.Cos(dmax) - p2 * Math.Cos(d0)) / (p3 - p2);
        double dcr = Math.Acos(Math.Clamp(c, -1, 1));
        double? tcr = p2 == 0 ? Math.Sqrt(4 * s.H * (dcr - d0) / (s.OmegaS * s.Pm)) : null;
        return new EqualAreaResult(d0 * 180 / Math.PI, dcr * 180 / Math.PI, dmax * 180 / Math.PI, tcr);
    }

    /// <summary>RK4 simulation with a fault from t = 0 to <paramref name="clearingTimeS"/>. Unstable when δ exceeds 180° (pole slip).</summary>
    public static SmibTrajectory Simulate(SmibSystem s, double clearingTimeS, double endTimeS = 3, double stepS = 0.0005)
    {
        double d = s.Delta0, w = 1;
        var t = new List<double> { 0 }; var dd = new List<double> { d * 180 / Math.PI }; var ww = new List<double> { 1.0 };
        double now = 0;
        bool stable = true;
        (double, double) F(double delta, double omega, double pmax) =>
            (s.OmegaS * (omega - 1), (s.Pm - pmax * Math.Sin(delta) - s.D * (omega - 1)) / (2 * s.H));
        while (now < endTimeS - 1e-12)
        {
            double h = Math.Min(stepS, endTimeS - now);
            if (now < clearingTimeS && now + h > clearingTimeS) h = clearingTimeS - now;
            double pmax = now < clearingTimeS - 1e-12 ? s.PmaxFault : s.PmaxPost;
            var (a1, b1) = F(d, w, pmax);
            var (a2, b2) = F(d + h / 2 * a1, w + h / 2 * b1, pmax);
            var (a3, b3) = F(d + h / 2 * a2, w + h / 2 * b2, pmax);
            var (a4, b4) = F(d + h * a3, w + h * b3, pmax);
            d += h / 6 * (a1 + 2 * a2 + 2 * a3 + a4);
            w += h / 6 * (b1 + 2 * b2 + 2 * b3 + b4);
            now += h;
            t.Add(now); dd.Add(d * 180 / Math.PI); ww.Add(w);
            if (d > Math.PI) { stable = false; break; }
        }
        return new SmibTrajectory(t.ToArray(), dd.ToArray(), ww.ToArray(), stable);
    }

    /// <summary>Critical clearing time by bisection on time-domain simulations [s].</summary>
    public static double CriticalClearingTime(SmibSystem s, double maxS = 2.0, double toleranceS = 1e-4, double endTimeS = 3)
    {
        double lo = 0, hi = maxS;
        if (Simulate(s, hi, endTimeS).Stable) return hi;
        while (hi - lo > toleranceS)
        {
            double mid = 0.5 * (lo + hi);
            if (Simulate(s, mid, endTimeS).Stable) lo = mid; else hi = mid;
        }
        return lo;
    }
}
