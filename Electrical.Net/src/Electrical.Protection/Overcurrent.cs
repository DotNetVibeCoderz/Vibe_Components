namespace Electrical.Protection;

/// <summary>A device with a time–current characteristic (relay, fuse, breaker trip unit).</summary>
public interface IProtectiveDevice
{
    string Name { get; }
    /// <summary>Operating (clearing) time [s] for a current [A, primary RMS]; <see cref="double.PositiveInfinity"/> when it does not operate.</summary>
    double OperatingTime(double currentA);
}

/// <summary>Standard inverse-time characteristics.</summary>
public enum CurveType
{
    /// <summary>IEC 60255-151 Standard Inverse (A = 0.14, P = 0.02).</summary>
    IecStandardInverse,
    /// <summary>IEC 60255-151 Very Inverse (A = 13.5, P = 1).</summary>
    IecVeryInverse,
    /// <summary>IEC 60255-151 Extremely Inverse (A = 80, P = 2).</summary>
    IecExtremelyInverse,
    /// <summary>IEC 60255-151 Long-Time Inverse (A = 120, P = 1).</summary>
    IecLongTimeInverse,
    /// <summary>IEEE C37.112 Moderately Inverse (A = 0.0515, B = 0.114, P = 0.02).</summary>
    IeeeModeratelyInverse,
    /// <summary>IEEE C37.112 Very Inverse (A = 19.61, B = 0.491, P = 2).</summary>
    IeeeVeryInverse,
    /// <summary>IEEE C37.112 Extremely Inverse (A = 28.2, B = 0.1217, P = 2).</summary>
    IeeeExtremelyInverse,
    /// <summary>Definite time: t = time multiplier [s] above pickup.</summary>
    DefiniteTime,
}

/// <summary>Inverse-time formulas t = TMS·(A / (M^P − 1) + B), M = I / I_s (IEC 60255-151 and IEEE C37.112).</summary>
public static class InverseTimeCurves
{
    /// <summary>Curve constants (A, B, P).</summary>
    public static (double A, double B, double P) Constants(CurveType curve) => curve switch
    {
        CurveType.IecStandardInverse => (0.14, 0, 0.02),
        CurveType.IecVeryInverse => (13.5, 0, 1),
        CurveType.IecExtremelyInverse => (80, 0, 2),
        CurveType.IecLongTimeInverse => (120, 0, 1),
        CurveType.IeeeModeratelyInverse => (0.0515, 0.114, 0.02),
        CurveType.IeeeVeryInverse => (19.61, 0.491, 2),
        CurveType.IeeeExtremelyInverse => (28.2, 0.1217, 2),
        CurveType.DefiniteTime => (0, 1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(curve)),
    };

    /// <summary>Operating time [s] at current multiple <paramref name="multiple"/> = I/I_s; ∞ for M ≤ 1.</summary>
    /// <param name="timeMultiplier">TMS (IEC) or time dial TD (IEEE); for definite time, the delay [s].</param>
    public static double OperatingTime(CurveType curve, double multiple, double timeMultiplier)
    {
        if (!(multiple > 1)) return double.PositiveInfinity;
        var (a, b, p) = Constants(curve);
        if (curve == CurveType.DefiniteTime) return timeMultiplier;
        return timeMultiplier * (a / (Math.Pow(multiple, p) - 1) + b);
    }

    /// <summary>Time multiplier giving operating time <paramref name="timeS"/> at multiple <paramref name="multiple"/>.</summary>
    public static double RequiredTimeMultiplier(CurveType curve, double multiple, double timeS)
    {
        double unit = OperatingTime(curve, multiple, 1.0);
        return double.IsInfinity(unit) ? double.PositiveInfinity : timeS / unit;
    }

    public static bool IsIec(CurveType c) => c is CurveType.IecStandardInverse or CurveType.IecVeryInverse or CurveType.IecExtremelyInverse or CurveType.IecLongTimeInverse;
}

/// <summary>Phase or earth overcurrent relay (ANSI 51/50) with an inverse-time stage and an optional instantaneous/high-set stage. Currents in primary A.</summary>
public sealed class OvercurrentRelay : IProtectiveDevice
{
    public OvercurrentRelay(string name, double pickupA, CurveType curve = CurveType.IecStandardInverse, double timeMultiplier = 0.1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pickupA);
        Name = name; PickupA = pickupA; Curve = curve; TimeMultiplier = timeMultiplier;
    }

    public string Name { get; set; }
    /// <summary>Inverse-time pickup (setting) current I_s [A primary].</summary>
    public double PickupA { get; set; }
    public CurveType Curve { get; set; }
    /// <summary>TMS (IEC) / time dial TD (IEEE) / delay [s] for definite time.</summary>
    public double TimeMultiplier { get; set; }
    /// <summary>High-set (instantaneous, ANSI 50) pickup [A]; null = disabled.</summary>
    public double? InstantaneousPickupA { get; set; }
    /// <summary>Delay of the high-set stage [s].</summary>
    public double InstantaneousDelayS { get; set; }
    /// <summary>Curve saturates above this multiple (e.g. 20 per IEC 60255-151 accuracy range); ∞ = no limit.</summary>
    public double MaxMultiple { get; set; } = double.PositiveInfinity;
    /// <summary>Additional fixed time [s] (breaker interrupting time) added to every trip.</summary>
    public double BreakerTimeS { get; set; }
    /// <summary>CT ratio (primary A per secondary A), informational / for secondary settings.</summary>
    public double CtRatio { get; set; } = 1;

    /// <summary>Pickup in secondary amperes (PickupA / CtRatio).</summary>
    public double PickupSecondaryA => PickupA / CtRatio;

    /// <inheritdoc />
    public double OperatingTime(double currentA)
    {
        double t = double.PositiveInfinity;
        double m = Math.Min(currentA / PickupA, MaxMultiple);
        t = Math.Min(t, InverseTimeCurves.OperatingTime(Curve, m, TimeMultiplier));
        if (InstantaneousPickupA is double hi && currentA >= hi) t = Math.Min(t, InstantaneousDelayS);
        return double.IsInfinity(t) ? t : t + BreakerTimeS;
    }

    public override string ToString() => $"{Name}: Is = {PickupA:0.##} A, {Curve}, TMS = {TimeMultiplier:0.###}" +
        (InstantaneousPickupA is double h ? $", I>> = {h:0.#} A / {InstantaneousDelayS:0.###} s" : "");
}

/// <summary>Fuse with a melting/clearing time–current characteristic given by points interpolated on log–log axes.</summary>
public sealed class Fuse : IProtectiveDevice
{
    private readonly double[] _logI, _logT;

    /// <param name="name">Fuse name.</param>
    /// <param name="ratedCurrentA">Rated current [A].</param>
    /// <param name="curve">Total clearing curve points (current [A], time [s]), any order; times must decrease with current.</param>
    public Fuse(string name, double ratedCurrentA, IEnumerable<(double CurrentA, double TimeS)> curve)
    {
        Name = name; RatedCurrentA = ratedCurrentA;
        var pts = curve.OrderBy(p => p.CurrentA).ToArray();
        if (pts.Length < 2) throw new ArgumentException("At least two curve points are required.", nameof(curve));
        Points = pts;
        _logI = pts.Select(p => Math.Log10(p.CurrentA)).ToArray();
        _logT = pts.Select(p => Math.Log10(p.TimeS)).ToArray();
    }

    /// <summary>
    /// Fuse described by a constant melting integral I²t [A²s] (adiabatic region), t = I²t / I², with a minimum melting current
    /// of <paramref name="minimumMeltingMultiple"/>·I_n (below which it never operates) and a minimum time of 0.004 s.
    /// </summary>
    public static Fuse FromI2t(string name, double ratedCurrentA, double meltingI2tA2s, double minimumMeltingMultiple = 1.6)
    {
        double iMin = minimumMeltingMultiple * ratedCurrentA;
        double iMax = Math.Sqrt(meltingI2tA2s / 0.004);
        if (iMax <= iMin) iMax = iMin * 100;
        var pts = new List<(double, double)>();
        for (int k = 0; k <= 20; k++)
        {
            double i = iMin * Math.Pow(iMax / iMin, k / 20.0);
            pts.Add((i, meltingI2tA2s / (i * i)));
        }
        return new Fuse(name, ratedCurrentA, pts);
    }

    public string Name { get; }
    public double RatedCurrentA { get; }
    public IReadOnlyList<(double CurrentA, double TimeS)> Points { get; }

    /// <summary>Clearing time [s] (log–log interpolation; constant-I²t extrapolation above the last point; ∞ below the first point).</summary>
    public double OperatingTime(double currentA)
    {
        if (currentA < Points[0].CurrentA) return double.PositiveInfinity;
        double li = Math.Log10(currentA);
        int n = _logI.Length;
        if (li >= _logI[n - 1]) return Points[n - 1].TimeS * Math.Pow(Points[n - 1].CurrentA / currentA, 2);
        int k = Array.BinarySearch(_logI, li);
        if (k >= 0) return Points[k].TimeS;
        k = ~k - 1;
        double w = (li - _logI[k]) / (_logI[k + 1] - _logI[k]);
        return Math.Pow(10, _logT[k] + w * (_logT[k + 1] - _logT[k]));
    }

    /// <summary>Let-through/melting I²t [A²s] at current <paramref name="currentA"/>.</summary>
    public double I2t(double currentA) => currentA * currentA * OperatingTime(currentA);
}

/// <summary>One point of a time–current characteristic.</summary>
public readonly record struct TccPoint(double CurrentA, double TimeS);

/// <summary>Time–current characteristic (TCC) data for log–log plots.</summary>
public static class Tcc
{
    /// <summary>Logarithmically spaced values between <paramref name="min"/> and <paramref name="max"/>.</summary>
    public static double[] LogSpace(double min, double max, int count)
    {
        if (min <= 0 || max <= min || count < 2) throw new ArgumentOutOfRangeException(nameof(count));
        var r = new double[count];
        double a = Math.Log10(min), b = Math.Log10(max);
        for (int i = 0; i < count; i++) r[i] = Math.Pow(10, a + (b - a) * i / (count - 1));
        return r;
    }

    /// <summary>TCC points of a device between <paramref name="minCurrentA"/> and <paramref name="maxCurrentA"/> (non-operating points omitted).</summary>
    public static IReadOnlyList<TccPoint> Curve(IProtectiveDevice device, double minCurrentA, double maxCurrentA, int points = 100) =>
        LogSpace(minCurrentA, maxCurrentA, points)
            .Select(i => new TccPoint(i, device.OperatingTime(i)))
            .Where(p => double.IsFinite(p.TimeS))
            .ToList();
}
