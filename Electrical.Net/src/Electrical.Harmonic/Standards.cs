namespace Electrical.Harmonic;

/// <summary>One harmonic order checked against its limit.</summary>
public sealed record HarmonicLimitRow(int Order, double Measured, double Limit)
{
    public bool Pass => Measured <= Limit + 1e-12;
}

/// <summary>Result of a harmonic compliance check. Values in percent unless the standard specifies amperes (IEC 61000-3-2).</summary>
public sealed record ComplianceResult(string Standard, string Unit, double TotalMeasured, double TotalLimit, IReadOnlyList<HarmonicLimitRow> Rows)
{
    public bool TotalPass => TotalMeasured <= TotalLimit + 1e-12;
    public bool Compliant => TotalPass && Rows.All(r => r.Pass);
    public IEnumerable<HarmonicLimitRow> Violations => Rows.Where(r => !r.Pass);
}

/// <summary>IEEE 519-2022 harmonic limits at the point of common coupling (PCC).</summary>
public static class Ieee519
{
    /// <summary>Voltage distortion limits (Table 1): individual harmonic [%] and THD [%] by bus voltage [kV].</summary>
    public static (double Individual, double Thd) VoltageLimits(double busKv) => busKv switch
    {
        <= 1.0 => (5.0, 8.0),
        <= 69 => (3.0, 5.0),
        <= 161 => (1.5, 2.5),
        _ => (1.0, 1.5),
    };

    // Odd-harmonic limits [%] for 2≤h<11, 11≤h<17, 17≤h<23, 23≤h<35, 35≤h≤50 and TDD, per Isc/IL row.
    private static readonly double[][] Table2 = // 120 V – 69 kV
    [
        [4.0, 2.0, 1.5, 0.6, 0.3, 5.0],
        [7.0, 3.5, 2.5, 1.0, 0.5, 8.0],
        [10.0, 4.5, 4.0, 1.5, 0.7, 12.0],
        [12.0, 5.5, 5.0, 2.0, 1.0, 15.0],
        [15.0, 7.0, 6.0, 2.5, 1.4, 20.0],
    ];
    private static readonly double[][] Table3 = // > 69 kV – 161 kV
    [
        [2.0, 1.0, 0.75, 0.3, 0.15, 2.5],
        [3.5, 1.75, 1.25, 0.5, 0.25, 4.0],
        [5.0, 2.25, 2.0, 0.75, 0.35, 6.0],
        [6.0, 2.75, 2.5, 1.0, 0.5, 7.5],
        [7.5, 3.5, 3.0, 1.25, 0.7, 10.0],
    ];
    private static readonly double[][] Table4 = // > 161 kV
    [
        [1.0, 0.5, 0.38, 0.15, 0.1, 1.5],
        [2.0, 1.0, 0.75, 0.3, 0.15, 2.5],
        [3.0, 1.5, 1.15, 0.45, 0.22, 3.75],
    ];

    private static double[] Row(double iscOverIl, double busKv)
    {
        if (busKv > 161) return Table4[iscOverIl < 25 ? 0 : iscOverIl < 50 ? 1 : 2];
        int r = iscOverIl < 20 ? 0 : iscOverIl < 50 ? 1 : iscOverIl < 100 ? 2 : iscOverIl < 1000 ? 3 : 4;
        return (busKv > 69 ? Table3 : Table2)[r];
    }

    /// <summary>Current distortion limit for order h [% of I_L] (Tables 2–4). Even harmonics are limited to 25 % of the odd limits.</summary>
    /// <param name="order">Harmonic order (2–50).</param>
    /// <param name="iscOverIl">Ratio of maximum short-circuit current to maximum demand load current at the PCC.</param>
    /// <param name="busKv">PCC voltage [kV].</param>
    public static double CurrentLimitPercent(int order, double iscOverIl, double busKv = 13.8)
    {
        if (order < 2 || order > 50) return double.PositiveInfinity;
        var row = Row(iscOverIl, busKv);
        double lim = order < 11 ? row[0] : order < 17 ? row[1] : order < 23 ? row[2] : order < 35 ? row[3] : row[4];
        return order % 2 == 0 ? 0.25 * lim : lim;
    }

    /// <summary>TDD limit [%] (Tables 2–4).</summary>
    public static double TddLimitPercent(double iscOverIl, double busKv = 13.8) => Row(iscOverIl, busKv)[5];

    /// <summary>Checks a voltage spectrum against Table 1.</summary>
    public static ComplianceResult CheckVoltage(HarmonicSpectrum voltage, double busKv)
    {
        var (ind, thd) = VoltageLimits(busKv);
        var rows = Enumerable.Range(2, Math.Min(voltage.MaxOrder, 50) - 1).Select(h => new HarmonicLimitRow(h, voltage.IndividualPercent(h), ind)).ToList();
        return new ComplianceResult("IEEE 519-2022 Table 1 (voltage)", "%", voltage.ThdPercent, thd, rows);
    }

    /// <summary>Checks a current spectrum against Tables 2–4.</summary>
    /// <param name="current">Current spectrum [A].</param>
    /// <param name="shortCircuitCurrentA">Maximum short-circuit current at the PCC [A].</param>
    /// <param name="maximumDemandCurrentA">Maximum demand load current I_L [A].</param>
    /// <param name="busKv">PCC voltage [kV].</param>
    public static ComplianceResult CheckCurrent(HarmonicSpectrum current, double shortCircuitCurrentA, double maximumDemandCurrentA, double busKv = 13.8)
    {
        double ratio = shortCircuitCurrentA / maximumDemandCurrentA;
        var rows = Enumerable.Range(2, Math.Min(current.MaxOrder, 50) - 1)
            .Select(h => new HarmonicLimitRow(h, 100 * current[h] / maximumDemandCurrentA, CurrentLimitPercent(h, ratio, busKv))).ToList();
        return new ComplianceResult($"IEEE 519-2022 current limits (Isc/IL = {ratio:0.#})", "% of IL", current.TddPercent(maximumDemandCurrentA),
            TddLimitPercent(ratio, busKv), rows);
    }
}

/// <summary>IEC 61000-2-2 (compatibility levels, LV public networks) and IEC 61000-3-2 (Class A equipment emission limits).</summary>
public static class Iec61000
{
    /// <summary>IEC 61000-2-2 Table 1 compatibility level for harmonic voltage of order h [% of fundamental].</summary>
    public static double VoltageCompatibilityLevelPercent(int h)
    {
        if (h < 2 || h > 50) return double.PositiveInfinity;
        if (h % 2 == 0)
            return h switch { 2 => 2.0, 4 => 1.0, 6 => 0.5, 8 => 0.5, _ => 0.25 * 10.0 / h + 0.25 };
        if (h % 3 == 0)
            return h switch { 3 => 5.0, 9 => 1.5, 15 => 0.4, 21 => 0.3, _ => 0.2 };
        return h switch
        {
            5 => 6.0, 7 => 5.0, 11 => 3.5, 13 => 3.0, 17 => 2.0, 19 => 1.5, 23 => 1.5, 25 => 1.5,
            _ => 0.2 + 1.3 * 25.0 / h,
        };
    }

    /// <summary>IEC 61000-2-2 compatibility level for THD (8 %).</summary>
    public const double VoltageThdCompatibilityPercent = 8.0;

    /// <summary>Checks a voltage spectrum against IEC 61000-2-2 compatibility levels.</summary>
    public static ComplianceResult CheckVoltage(HarmonicSpectrum voltage)
    {
        var rows = Enumerable.Range(2, Math.Min(voltage.MaxOrder, 50) - 1)
            .Select(h => new HarmonicLimitRow(h, voltage.IndividualPercent(h), VoltageCompatibilityLevelPercent(h))).ToList();
        return new ComplianceResult("IEC 61000-2-2 (LV compatibility levels)", "%", voltage.ThdPercent, VoltageThdCompatibilityPercent, rows);
    }

    /// <summary>IEC 61000-3-2 Class A maximum permissible harmonic current [A] for order h (equipment ≤ 16 A per phase, 230/400 V).</summary>
    public static double ClassACurrentLimitA(int h)
    {
        if (h < 2 || h > 40) return double.PositiveInfinity;
        if (h % 2 == 0) return h switch { 2 => 1.08, 4 => 0.43, 6 => 0.30, _ => 0.23 * 8.0 / h };
        return h switch { 3 => 2.30, 5 => 1.14, 7 => 0.77, 9 => 0.40, 11 => 0.33, 13 => 0.21, _ => 0.15 * 15.0 / h };
    }

    /// <summary>Checks an equipment input-current spectrum [A] against IEC 61000-3-2 Class A.</summary>
    public static ComplianceResult CheckClassA(HarmonicSpectrum currentA)
    {
        var rows = Enumerable.Range(2, Math.Min(currentA.MaxOrder, 40) - 1).Select(h => new HarmonicLimitRow(h, currentA[h], ClassACurrentLimitA(h))).ToList();
        return new ComplianceResult("IEC 61000-3-2 Class A", "A", 0, double.PositiveInfinity, rows);
    }
}
