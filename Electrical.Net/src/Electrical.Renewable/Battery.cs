namespace Electrical.Renewable;

/// <summary>
/// Battery energy storage system (BESS) energy-reservoir model. Grid-side power convention: positive = discharge (export),
/// negative = charge. Stored energy changes by −P·Δt/η_d when discharging and by |P|·Δt·η_c when charging.
/// Power limits follow from the C-rate; the state of charge is kept in [SocMin, SocMax].
/// Degradation: half cycles are detected at SoC turning points and each contributes 0.5/N(DoD) damage with the Wöhler-type
/// cycle life N(DoD) = N₁₀₀·DoD^(−k); capacity fades linearly to <see cref="EndOfLifeCapacity"/> at damage = 1.
/// Units: MW, MWh, hours.
/// </summary>
public sealed class BatteryStorage
{
    private double _lastTurnSoc;
    private int _direction; // +1 charging, −1 discharging, 0 idle/unknown
    private double _soc;

    /// <param name="capacityMwh">Nominal usable energy capacity [MWh] (at 100 % SoC window).</param>
    /// <param name="maxPowerMw">Maximum charge/discharge power [MW].</param>
    public BatteryStorage(double capacityMwh, double maxPowerMw, double initialSoc = 0.5)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityMwh);
        CapacityMwh = capacityMwh;
        MaxChargeMw = maxPowerMw;
        MaxDischargeMw = maxPowerMw;
        _soc = initialSoc;
        _lastTurnSoc = initialSoc;
    }

    /// <summary>Creates a battery whose power rating is <paramref name="cRate"/> × capacity (e.g. 0.5 C → 2 h battery).</summary>
    public static BatteryStorage FromCRate(double capacityMwh, double cRate, double initialSoc = 0.5) => new(capacityMwh, capacityMwh * cRate, initialSoc);

    public string? Name { get; set; }
    public double CapacityMwh { get; }
    public double MaxChargeMw { get; set; }
    public double MaxDischargeMw { get; set; }
    /// <summary>Charging efficiency (grid → cells).</summary>
    public double ChargeEfficiency { get; set; } = 0.95;
    /// <summary>Discharging efficiency (cells → grid).</summary>
    public double DischargeEfficiency { get; set; } = 0.95;
    public double SocMin { get; set; } = 0.1;
    public double SocMax { get; set; } = 0.95;
    /// <summary>Cycle life at 100 % depth of discharge.</summary>
    public double CycleLifeAt100Dod { get; set; } = 5000;
    /// <summary>Wöhler exponent k in N(DoD) = N₁₀₀·DoD^(−k).</summary>
    public double WohlerExponent { get; set; } = 1.3;
    /// <summary>Remaining capacity fraction at end of life (damage = 1).</summary>
    public double EndOfLifeCapacity { get; set; } = 0.8;

    /// <summary>State of charge (fraction of the nominal capacity).</summary>
    public double Soc { get => _soc; set { _soc = Math.Clamp(value, 0, 1); _lastTurnSoc = _soc; _direction = 0; } }
    /// <summary>Stored energy [MWh] (relative to effective capacity).</summary>
    public double EnergyMwh => _soc * EffectiveCapacityMwh;
    /// <summary>Round-trip efficiency η_c·η_d.</summary>
    public double RoundTripEfficiency => ChargeEfficiency * DischargeEfficiency;

    /// <summary>Total energy discharged to the grid [MWh].</summary>
    public double DischargedMwh { get; private set; }
    /// <summary>Total energy drawn from the grid for charging [MWh].</summary>
    public double ChargedMwh { get; private set; }
    /// <summary>Equivalent full cycles: cell-side throughput / (2 × capacity).</summary>
    public double EquivalentFullCycles { get; private set; }
    /// <summary>Accumulated cycle damage (Miner's rule, 1 = end of life).</summary>
    public double CycleDamage { get; private set; }
    /// <summary>State of health = remaining capacity fraction.</summary>
    public double StateOfHealth => 1 - (1 - EndOfLifeCapacity) * Math.Min(1, CycleDamage);
    /// <summary>Capacity after fade [MWh].</summary>
    public double EffectiveCapacityMwh => CapacityMwh * StateOfHealth;

    /// <summary>Cycle life for a depth of discharge in (0, 1].</summary>
    public double CycleLife(double depthOfDischarge) => CycleLifeAt100Dod * Math.Pow(Math.Clamp(depthOfDischarge, 1e-6, 1), -WohlerExponent);

    /// <summary>Largest discharge power [MW] sustainable for <paramref name="hours"/> within power and SoC limits.</summary>
    public double AvailableDischargeMw(double hours) =>
        Math.Max(0, Math.Min(MaxDischargeMw, (_soc - SocMin) * EffectiveCapacityMwh * DischargeEfficiency / hours));

    /// <summary>Largest charge power [MW] (grid side) acceptable for <paramref name="hours"/> within power and SoC limits.</summary>
    public double AvailableChargeMw(double hours) =>
        Math.Max(0, Math.Min(MaxChargeMw, (SocMax - _soc) * EffectiveCapacityMwh / ChargeEfficiency / hours));

    /// <summary>
    /// Applies a power request [MW] for <paramref name="hours"/> (positive = discharge, negative = charge), limited by power and SoC.
    /// Returns the grid-side power actually delivered (same sign convention).
    /// </summary>
    public double Step(double requestedMw, double hours)
    {
        if (hours <= 0) return 0;
        double p = requestedMw >= 0 ? Math.Min(requestedMw, AvailableDischargeMw(hours)) : -Math.Min(-requestedMw, AvailableChargeMw(hours));
        if (Math.Abs(p) < 1e-12) return 0;
        double cap = EffectiveCapacityMwh;
        double dE = p > 0 ? -p * hours / DischargeEfficiency : -p * hours * ChargeEfficiency;
        int dir = p > 0 ? -1 : 1;
        if (_direction != 0 && dir != _direction) AccumulateHalfCycle(_soc);
        if (_direction == 0 || dir != _direction) _direction = dir;
        _soc = Math.Clamp(_soc + dE / cap, 0, 1);
        if (p > 0) DischargedMwh += p * hours; else ChargedMwh += -p * hours;
        EquivalentFullCycles += Math.Abs(dE) / (2 * CapacityMwh);
        return p;
    }

    /// <summary>Closes the current half cycle (call at the end of a simulation to count the last excursion).</summary>
    public void FinalizeCycles()
    {
        if (_direction != 0) AccumulateHalfCycle(_soc);
        _direction = 0;
    }

    private void AccumulateHalfCycle(double turnSoc)
    {
        double depth = Math.Abs(turnSoc - _lastTurnSoc);
        if (depth > 1e-6) CycleDamage += 0.5 / CycleLife(depth);
        _lastTurnSoc = turnSoc;
    }

    /// <summary>Copy with the same parameters and state (counters included).</summary>
    public BatteryStorage Clone() => (BatteryStorage)MemberwiseClone();
}
