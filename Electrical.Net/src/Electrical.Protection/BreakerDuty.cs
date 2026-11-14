using Electrical.ShortCircuit;

namespace Electrical.Protection;

/// <summary>Circuit-breaker short-circuit ratings (IEC 62271-100 style).</summary>
/// <param name="Name">Breaker name.</param>
/// <param name="RatedVoltageKv">Rated voltage [kV].</param>
/// <param name="BreakingCurrentKa">Rated short-circuit breaking current I_sc (symmetrical) [kA].</param>
/// <param name="MakingCurrentKaPeak">Rated short-circuit making current [kA peak]; null = 2.5·I_sc (50 Hz) / 2.6·I_sc (60 Hz).</param>
/// <param name="ShortTimeCurrentKa">Rated short-time withstand current I_k [kA]; null = I_sc.</param>
/// <param name="ShortTimeDurationS">Rated duration of short circuit t_k [s].</param>
public sealed record BreakerRating(
    string Name, double RatedVoltageKv, double BreakingCurrentKa, double? MakingCurrentKaPeak = null, double? ShortTimeCurrentKa = null,
    double ShortTimeDurationS = 1.0);

/// <summary>Duty of a breaker relative to its ratings (percent of rating; ≤ 100 % is adequate).</summary>
public sealed record BreakerDutyResult(
    string Breaker, int BusId, FaultType WorstFault, double RequiredBreakingKa, double BreakingDutyPercent, double RequiredMakingKaPeak,
    double MakingDutyPercent, double RequiredShortTimeKa, double ShortTimeDutyPercent, bool VoltageOk, double DcComponentPercent)
{
    public bool Adequate => VoltageOk && BreakingDutyPercent <= 100 && MakingDutyPercent <= 100 && ShortTimeDutyPercent <= 100;
}

/// <summary>Breaker duty evaluation against IEC 60909 short-circuit results.</summary>
public static class BreakerDuty
{
    /// <summary>Checks a breaker against one fault result.</summary>
    /// <param name="faultDurationS">Actual fault duration [s] for the thermal (I²t) comparison with the short-time rating.</param>
    public static BreakerDutyResult Check(BreakerRating rating, FaultResult fault, double faultDurationS = 1.0, double frequencyHz = 50)
    {
        double making = rating.MakingCurrentKaPeak ?? (frequencyHz >= 55 ? 2.6 : 2.5) * rating.BreakingCurrentKa;
        double ik = rating.ShortTimeCurrentKa ?? rating.BreakingCurrentKa;
        // I_th²·T_k ≤ I_k²·t_k  →  equivalent current over the rated duration
        double ithEq = fault.IthKa * Math.Sqrt(faultDurationS / rating.ShortTimeDurationS);
        double dcPct = fault.IbKa > 0 ? 100 * fault.IdcKa / (Math.Sqrt(2) * fault.IbKa) : 0;
        return new BreakerDutyResult(rating.Name, fault.BusId, fault.Type, fault.IbKa, 100 * fault.IbKa / rating.BreakingCurrentKa,
            fault.IpKa, 100 * fault.IpKa / making, ithEq, 100 * ithEq / ik, rating.RatedVoltageKv >= fault.BaseKv - 1e-9, dcPct);
    }

    /// <summary>
    /// Checks breakers at buses against the worst of the three-phase, single-line-to-ground and double-line-to-ground faults.
    /// </summary>
    public static IReadOnlyList<BreakerDutyResult> CheckAll(ShortCircuitStudy study, IEnumerable<(int BusId, BreakerRating Rating)> breakers,
        double faultDurationS = 1.0)
    {
        var list = new List<BreakerDutyResult>();
        foreach (var (bus, rating) in breakers)
        {
            var worst = new[] { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.DoubleLineToGround }
                .Select(t => study.Calculate(bus, t)).MaxBy(r => r.IkKa)!;
            list.Add(Check(rating, worst, faultDurationS, study.Network.FrequencyHz));
        }
        return list;
    }
}
