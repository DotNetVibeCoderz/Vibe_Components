using System.Numerics;

namespace Electrical.Core.Units;

/// <summary>
/// Per-unit base for one voltage zone. Three-phase convention: S in MVA (three-phase), V in kV line-to-line,
/// Z_base = V² / S [Ω], I_base = S / (√3·V) [kA].
/// </summary>
public readonly record struct PerUnitBase(double BaseMva, double BaseKv)
{
    public double BaseOhm => BaseKv * BaseKv / BaseMva;
    public double BaseSiemens => BaseMva / (BaseKv * BaseKv);
    public double BaseKa => BaseMva / (Math.Sqrt(3) * BaseKv);

    public double OhmToPu(double ohm) => ohm / BaseOhm;
    public Complex OhmToPu(Complex ohm) => ohm / BaseOhm;
    public double PuToOhm(double pu) => pu * BaseOhm;
    public double SiemensToPu(double s) => s / BaseSiemens;
    public double KaToPu(double ka) => ka / BaseKa;
    public double PuToKa(double pu) => pu * BaseKa;
    public double MwToPu(double mw) => mw / BaseMva;
    public double PuToMw(double pu) => pu * BaseMva;
    public double KvToPu(double kv) => kv / BaseKv;

    /// <summary>Converts an impedance in pu on another base (e.g. a transformer nameplate) to this base.</summary>
    public double ChangeBase(double zPu, double oldMva, double oldKv) =>
        zPu * (oldKv * oldKv / oldMva) / BaseOhm;
}

/// <summary>Common electrical formulas.</summary>
public static class ElectricalFormulas
{
    public static readonly double Sqrt3 = Math.Sqrt(3);

    /// <summary>Three-phase apparent power [MVA] from line voltage [kV] and current [kA].</summary>
    public static double ThreePhaseMva(double kvLL, double ka) => Sqrt3 * kvLL * ka;

    /// <summary>Line current [kA] from three-phase power [MVA] and line voltage [kV].</summary>
    public static double LineCurrentKa(double mva, double kvLL) => mva / (Sqrt3 * kvLL);

    /// <summary>Reactive power [Mvar] needed to correct a load of P [MW] from pf1 to pf2.</summary>
    public static double PowerFactorCorrectionMvar(double pMw, double pfFrom, double pfTo) =>
        pMw * (Math.Tan(Math.Acos(pfFrom)) - Math.Tan(Math.Acos(pfTo)));

    /// <summary>Voltage drop [%] of a feeder with R, X [Ω] (per phase) carrying P [MW], Q [Mvar] at V [kV] (approximate formula).</summary>
    public static double VoltageDropPercent(double rOhm, double xOhm, double pMw, double qMvar, double kvLL) =>
        (rOhm * pMw + xOhm * qMvar) / (kvLL * kvLL) * 100;

    /// <summary>Conductor resistance at temperature t [°C] given R20 and temperature coefficient α (copper 0.00393, aluminium 0.00403).</summary>
    public static double ResistanceAtTemperature(double r20, double tC, double alpha = 0.00393) => r20 * (1 + alpha * (tC - 20));

    /// <summary>Capacitive reactance [Ω] at frequency f [Hz] for capacitance c [F].</summary>
    public static double CapacitiveReactance(double c, double f) => 1 / (2 * Math.PI * f * c);

    /// <summary>Inductive reactance [Ω] at frequency f [Hz] for inductance l [H].</summary>
    public static double InductiveReactance(double l, double f) => 2 * Math.PI * f * l;

    /// <summary>Resonant frequency [Hz] of an LC circuit.</summary>
    public static double ResonantFrequency(double l, double c) => 1 / (2 * Math.PI * Math.Sqrt(l * c));
}
