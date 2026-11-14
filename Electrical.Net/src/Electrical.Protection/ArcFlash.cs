namespace Electrical.Protection;

/// <summary>Equipment class for IEEE 1584-2002 (selects the enclosure, typical gap and distance exponent of Table 4).</summary>
public enum ArcEquipment
{
    /// <summary>Open air (no enclosure).</summary>
    OpenAir,
    /// <summary>Metal-clad / metal-enclosed switchgear.</summary>
    Switchgear,
    /// <summary>Motor control centre (low voltage).</summary>
    MotorControlCenter,
    /// <summary>Panelboard (low voltage).</summary>
    Panelboard,
    /// <summary>Cable (termination box).</summary>
    Cable,
}

/// <summary>Input for an arc-flash incident-energy calculation.</summary>
/// <param name="SystemKv">Nominal system voltage (line-to-line) [kV], 0.208–15 kV for the empirical model (above 15 kV the Lee method is used).</param>
/// <param name="BoltedFaultKa">Three-phase bolted fault current I_bf [kA] (0.7–106 kA range of the empirical model).</param>
/// <param name="ArcDurationS">Arc duration (protective device clearing time) [s].</param>
/// <param name="WorkingDistanceMm">Working distance D [mm] (typical: 910 MV switchgear, 610 LV switchgear, 455 MCC/panel/cable).</param>
/// <param name="Equipment">Equipment class.</param>
/// <param name="GapMm">Conductor gap G [mm]; null = IEEE 1584-2002 Table 4 typical value.</param>
/// <param name="SolidlyGrounded">true = grounded system (K2 = −0.113), false = ungrounded / high-resistance grounded (K2 = 0).</param>
public sealed record ArcFlashInput(
    double SystemKv, double BoltedFaultKa, double ArcDurationS, double WorkingDistanceMm, ArcEquipment Equipment = ArcEquipment.Switchgear,
    double? GapMm = null, bool SolidlyGrounded = true);

/// <summary>Arc-flash result. Energies in cal/cm² (and J/cm²), distances in mm.</summary>
public sealed record ArcFlashResult(
    string Model, double ArcingCurrentKa, double ArcDurationS, double IncidentEnergyCalCm2, double ArcFlashBoundaryMm, int PpeCategory,
    string PpeDescription, double GapMm, double DistanceExponent)
{
    public double IncidentEnergyJCm2 => IncidentEnergyCalCm2 * 4.184;
}

/// <summary>
/// Arc-flash hazard calculation per the <b>IEEE 1584-2002</b> empirical model (0.208–15 kV) with the Lee method above 15 kV.
/// This is the 2002 edition model — not the IEEE 1584-2018 model (which uses electrode configurations VCB/VCBB/HCB/VOA/HOA and
/// enclosure-size correction). Results are suitable for screening and teaching; use a validated 2018 tool for labelling.
/// PPE categories follow NFPA 70E (1.2 / 4 / 8 / 25 / 40 cal/cm²).
/// </summary>
public static class ArcFlash
{
    /// <summary>Incident energy threshold for a second-degree burn (onset), 1.2 cal/cm² = 5.0 J/cm².</summary>
    public const double BoundaryEnergyCalCm2 = 1.2;

    /// <summary>Typical gap [mm] and distance exponent x (IEEE 1584-2002 Table 4).</summary>
    public static (double GapMm, double X) Table4(double kv, ArcEquipment eq)
    {
        if (kv <= 1.0)
            return eq switch
            {
                ArcEquipment.OpenAir => (32, 2.000),
                ArcEquipment.Switchgear => (32, 1.473),
                ArcEquipment.MotorControlCenter or ArcEquipment.Panelboard => (25, 1.641),
                _ => (13, 2.000),
            };
        if (kv <= 5.0)
            return eq switch
            {
                ArcEquipment.OpenAir => (102, 2.000),
                ArcEquipment.Cable => (13, 2.000),
                _ => (102, 0.973),
            };
        return eq switch
        {
            ArcEquipment.OpenAir => (153, 2.000),
            ArcEquipment.Cable => (13, 2.000),
            _ => (153, 0.973),
        };
    }

    /// <summary>Arcing current I_a [kA] (IEEE 1584-2002 eqs. 1 and 2).</summary>
    public static double ArcingCurrentKa(double kv, double boltedKa, ArcEquipment eq, double? gapMm = null)
    {
        double g = gapMm ?? Table4(kv, eq).GapMm;
        double lgIbf = Math.Log10(boltedKa);
        double lgIa = kv < 1.0
            ? (eq == ArcEquipment.OpenAir ? -0.153 : -0.097) + 0.662 * lgIbf + 0.0966 * kv + 0.000526 * g + 0.5588 * kv * lgIbf - 0.00304 * g * lgIbf
            : 0.00402 + 0.983 * lgIbf;
        return Math.Pow(10, lgIa);
    }

    /// <summary>Normalised incident energy E_n [J/cm²] for 0.2 s at 610 mm (IEEE 1584-2002 eq. 3).</summary>
    public static double NormalizedEnergyJCm2(double arcingKa, ArcEquipment eq, double gapMm, bool grounded)
    {
        double k1 = eq == ArcEquipment.OpenAir ? -0.792 : -0.555;
        double k2 = grounded ? -0.113 : 0.0;
        return Math.Pow(10, k1 + k2 + 1.081 * Math.Log10(arcingKa) + 0.0011 * gapMm);
    }

    /// <summary>Incident energy and arc-flash boundary for a fixed arc duration.</summary>
    public static ArcFlashResult Calculate(ArcFlashInput input)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.BoltedFaultKa);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.WorkingDistanceMm);
        double t = input.ArcDurationS;
        if (input.SystemKv > 15)
        {
            // Lee method: E [J/cm²] = 2.142e6 · V[kV] · Ibf[kA] · t / D²[mm²]
            double ej = 2.142e6 * input.SystemKv * input.BoltedFaultKa * t / (input.WorkingDistanceMm * input.WorkingDistanceMm);
            double db = Math.Sqrt(2.142e6 * input.SystemKv * input.BoltedFaultKa * t / (BoundaryEnergyCalCm2 * 4.184));
            double cal = ej / 4.184;
            return new ArcFlashResult("Lee (theoretical, > 15 kV)", input.BoltedFaultKa, t, cal, db, PpeCategory(cal), PpeDescription(cal), 0, 2.0);
        }
        var (gTyp, x) = Table4(input.SystemKv, input.Equipment);
        double g = input.GapMm ?? gTyp;
        double ia = ArcingCurrentKa(input.SystemKv, input.BoltedFaultKa, input.Equipment, g);
        double en = NormalizedEnergyJCm2(ia, input.Equipment, g, input.SolidlyGrounded);
        double cf = input.SystemKv <= 1.0 ? 1.5 : 1.0;
        double common = 4.184 * cf * en * (t / 0.2) * Math.Pow(610, x);           // E·D^x [J/cm²·mm^x]
        double eJ = common / Math.Pow(input.WorkingDistanceMm, x);
        double dbMm = Math.Pow(common / (BoundaryEnergyCalCm2 * 4.184), 1 / x);
        double eCal = eJ / 4.184;
        return new ArcFlashResult("IEEE 1584-2002", ia, t, eCal, dbMm, PpeCategory(eCal), PpeDescription(eCal), g, x);
    }

    /// <summary>
    /// IEEE 1584-2002 procedure with a protective device: the arc duration is the device clearing time at the arcing current
    /// (plus <paramref name="breakerOpeningS"/>); the calculation is repeated at 85 % of the arcing current and the higher energy is returned.
    /// </summary>
    public static ArcFlashResult Calculate(ArcFlashInput input, IProtectiveDevice device, double breakerOpeningS = 0, double maximumDurationS = 2.0)
    {
        ArcFlashResult Run(double factor)
        {
            double ia = input.SystemKv > 15 ? input.BoltedFaultKa : ArcingCurrentKa(input.SystemKv, input.BoltedFaultKa, input.Equipment, input.GapMm);
            double t = Math.Min(device.OperatingTime(ia * factor * 1000) + breakerOpeningS, maximumDurationS);
            var r = Calculate(input with { ArcDurationS = t });
            if (factor == 1.0) return r;
            // energy scales with arcing current^1.081 at constant duration
            double scale = Math.Pow(factor, 1.081);
            double e = r.IncidentEnergyCalCm2 * scale;
            double db = r.ArcFlashBoundaryMm * Math.Pow(scale, 1 / r.DistanceExponent);
            return r with { ArcingCurrentKa = r.ArcingCurrentKa * factor, IncidentEnergyCalCm2 = e, ArcFlashBoundaryMm = db, PpeCategory = PpeCategory(e), PpeDescription = PpeDescription(e) };
        }
        var full = Run(1.0);
        if (input.SystemKv > 15) return full;
        var reduced = Run(0.85);
        return reduced.IncidentEnergyCalCm2 > full.IncidentEnergyCalCm2 ? reduced : full;
    }

    /// <summary>NFPA 70E PPE category for incident energy [cal/cm²]: 0 (&lt; 1.2), 1 (≤ 4), 2 (≤ 8), 3 (≤ 25), 4 (≤ 40), −1 (&gt; 40, no category).</summary>
    public static int PpeCategory(double energyCalCm2) => energyCalCm2 switch
    {
        < 1.2 => 0,
        <= 4 => 1,
        <= 8 => 2,
        <= 25 => 3,
        <= 40 => 4,
        _ => -1,
    };

    public static string PpeDescription(double energyCalCm2) => PpeCategory(energyCalCm2) switch
    {
        0 => "Below 1.2 cal/cm²: arc-rated PPE not required (non-melting clothing)",
        1 => "PPE category 1: arc rating ≥ 4 cal/cm²",
        2 => "PPE category 2: arc rating ≥ 8 cal/cm²",
        3 => "PPE category 3: arc rating ≥ 25 cal/cm²",
        4 => "PPE category 4: arc rating ≥ 40 cal/cm²",
        _ => "Above 40 cal/cm²: DANGER — no PPE category, de-energise before work",
    };
}
