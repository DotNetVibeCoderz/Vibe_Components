using System.Numerics;
using Electrical.Core.Units;

namespace Electrical.Distribution;

/// <summary>Length units used by line codes and segments.</summary>
public enum LengthUnit { None, Meter, Kilometer, Foot, Kft, Mile }

/// <summary>Length conversions.</summary>
public static class Lengths
{
    public const double KmPerMile = 1.609344;
    public const double FeetPerMile = 5280;
    public const double MetersPerFoot = 0.3048;

    /// <summary>Converts <paramref name="value"/> in <paramref name="unit"/> to km (<see cref="LengthUnit.None"/> is treated as km).</summary>
    public static double ToKm(double value, LengthUnit unit) => unit switch
    {
        LengthUnit.Meter => value / 1000,
        LengthUnit.Foot => value * MetersPerFoot / 1000,
        LengthUnit.Kft => value * MetersPerFoot,
        LengthUnit.Mile => value * KmPerMile,
        _ => value,
    };

    public static double FromKm(double km, LengthUnit unit) => km / ToKm(1, unit);

    /// <summary>Parses OpenDSS unit names (mi, kft, km, m, ft, none).</summary>
    public static LengthUnit Parse(string s) => s.Trim().ToLowerInvariant() switch
    {
        "mi" or "mile" or "miles" => LengthUnit.Mile,
        "kft" => LengthUnit.Kft,
        "km" => LengthUnit.Kilometer,
        "m" or "meter" or "meters" => LengthUnit.Meter,
        "ft" or "feet" or "foot" => LengthUnit.Foot,
        "none" or "" => LengthUnit.None,
        _ => throw new FormatException($"Unknown length unit '{s}'."),
    };
}

/// <summary>
/// Bare conductor data (US customary units as in Kersting, Appendix A): AC resistance [Ω/mile] at 50 °C, geometric mean radius [ft],
/// outside diameter [in] and ampacity [A].
/// </summary>
public sealed record ConductorData(string Name, double ResistanceOhmPerMile, double GmrFeet, double DiameterInches, double AmpacityA = 0)
{
    /// <summary>Resistance [Ω/km].</summary>
    public double ResistanceOhmPerKm => ResistanceOhmPerMile / Lengths.KmPerMile;
    /// <summary>GMR [mm].</summary>
    public double GmrMm => GmrFeet * 304.8;
    /// <summary>Outside radius [ft].</summary>
    public double RadiusFeet => DiameterInches / 24.0;

    /// <summary>Creates a conductor from metric data: resistance [Ω/km], GMR [mm], diameter [mm].</summary>
    public static ConductorData FromMetric(string name, double resistanceOhmPerKm, double gmrMm, double diameterMm, double ampacityA = 0) =>
        new(name, resistanceOhmPerKm * Lengths.KmPerMile, gmrMm / 304.8, diameterMm / 25.4, ampacityA);
}

/// <summary>Standard overhead conductors (Kersting, "Distribution System Modeling and Analysis", Appendix A).</summary>
public static class ConductorLibrary
{
    public static readonly ConductorData Acsr556_5_26_7 = new("ACSR 556,500 26/7 (Dove)", 0.1859, 0.0313, 0.927, 730);
    public static readonly ConductorData Acsr336_4_26_7 = new("ACSR 336,400 26/7 (Linnet)", 0.306, 0.0244, 0.721, 530);
    public static readonly ConductorData Acsr4_0_6_1 = new("ACSR 4/0 6/1 (Penguin)", 0.592, 0.00814, 0.563, 340);
    public static readonly ConductorData Acsr1_0_6_1 = new("ACSR 1/0 6/1 (Raven)", 1.12, 0.00446, 0.398, 230);
    public static readonly ConductorData Acsr2_6_1 = new("ACSR #2 6/1 (Sparrow)", 1.69, 0.00418, 0.316, 180);
    public static readonly ConductorData Aac500 = new("AAC 500 kcmil", 0.206, 0.0260, 0.813, 483);
    public static readonly ConductorData Aa250 = new("AA 250 kcmil", 0.41, 0.0171, 0.567, 329);
    public static readonly ConductorData Aa1_0 = new("AA 1/0", 0.97, 0.0111, 0.368, 202);
    public static readonly ConductorData Cu1_0 = new("Cu 1/0", 0.607, 0.01113, 0.368, 310);
    /// <summary>#14 copper strand used for concentric neutrals.</summary>
    public static readonly ConductorData Cu14 = new("Cu #14", 14.8722, 0.00208, 0.0641, 20);

    public static IReadOnlyList<ConductorData> All { get; } =
        [Acsr556_5_26_7, Acsr336_4_26_7, Acsr4_0_6_1, Acsr1_0_6_1, Acsr2_6_1, Aac500, Aa250, Aa1_0, Cu1_0, Cu14];

    /// <summary>Finds a conductor by (case-insensitive) substring of its name, e.g. "Linnet" or "4/0".</summary>
    public static ConductorData? Find(string text) =>
        All.FirstOrDefault(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Role of a conductor in a pole configuration.</summary>
public enum ConductorRole { PhaseA, PhaseB, PhaseC, Neutral }

/// <summary>Conductor placed at (x, y) [ft], y = height above ground.</summary>
public sealed record ConductorPosition(ConductorRole Role, double XFeet, double YFeet, ConductorData Conductor)
{
    /// <summary>Creates a position from metres.</summary>
    public static ConductorPosition Metric(ConductorRole role, double xMeters, double yMeters, ConductorData conductor) =>
        new(role, xMeters / Lengths.MetersPerFoot, yMeters / Lengths.MetersPerFoot, conductor);
}

/// <summary>Overhead pole configuration (phase conductors plus zero or more neutrals).</summary>
public sealed class OverheadConfiguration(string name, IEnumerable<ConductorPosition> conductors)
{
    public string Name { get; } = name;
    public IReadOnlyList<ConductorPosition> Conductors { get; } = conductors.ToList();

    /// <summary>Kersting Example 4.1 / IEEE spacing ID 500: phases at x = 0, 2.5, 7 ft (28 ft high), neutral at x = 4 ft, 24 ft high,
    /// with the given phasing of the three positions (left to right).</summary>
    public static OverheadConfiguration Spacing500(ConductorData phase, ConductorData neutral,
        ConductorRole left = ConductorRole.PhaseA, ConductorRole middle = ConductorRole.PhaseB, ConductorRole right = ConductorRole.PhaseC) =>
        new("ID-500", [new(left, 0, 28, phase), new(middle, 2.5, 28, phase), new(right, 7, 28, phase), new(ConductorRole.Neutral, 4, 24, neutral)]);
}

/// <summary>Concentric-neutral cable: phase conductor, <paramref name="StrandCount"/> neutral strands and the outside diameter over the
/// neutral strands <paramref name="OutsideDiameterInches"/> (d_od).</summary>
public sealed record ConcentricNeutralCable(ConductorData Phase, ConductorData Strand, int StrandCount, double OutsideDiameterInches)
{
    /// <summary>Radius of the circle through the strand centres R = (d_od − d_s)/24 [ft].</summary>
    public double NeutralRadiusFeet => (OutsideDiameterInches - Strand.DiameterInches) / 24.0;
    /// <summary>Equivalent neutral GMR = (GMR_s·k·R^(k−1))^(1/k) [ft].</summary>
    public double NeutralGmrFeet => Math.Pow(Strand.GmrFeet * StrandCount * Math.Pow(NeutralRadiusFeet, StrandCount - 1), 1.0 / StrandCount);
    /// <summary>Equivalent neutral resistance r_s/k [Ω/mile].</summary>
    public double NeutralResistanceOhmPerMile => Strand.ResistanceOhmPerMile / StrandCount;
}

/// <summary>Tape-shielded cable: phase conductor, outside diameter of the tape shield d_s [in] and tape thickness T [mils] (copper tape).</summary>
public sealed record TapeShieldedCable(ConductorData Phase, double ShieldOutsideDiameterInches, double TapeThicknessMils)
{
    /// <summary>Shield resistance 7.9385·10⁸·ρ/(d_s·T) with ρ = 2.3715·10⁻⁸ Ω·m (copper at 50 °C) [Ω/mile].</summary>
    public double ShieldResistanceOhmPerMile => 18.826 / (ShieldOutsideDiameterInches * TapeThicknessMils);
    /// <summary>Shield GMR = (d_s/2 − T/2000)/12 [ft].</summary>
    public double ShieldGmrFeet => (ShieldOutsideDiameterInches / 2 - TapeThicknessMils / 2000) / 12;
}

/// <summary>Position of a cable centre (x, y) [ft] and the phase it carries.</summary>
public sealed record CablePosition(ConductorRole Phase, double XFeet, double YFeet);

/// <summary>Phase impedance and shunt admittance per unit length after Kron reduction.</summary>
public sealed class LineImpedance
{
    internal LineImpedance(int[] phaseOrder, Complex[,] zPerMile, Complex[,] yPerMile, Complex[,] primitive)
    {
        PhaseOrder = phaseOrder;
        _z = zPerMile;
        _y = yPerMile;
        _prim = primitive;
    }

    private readonly Complex[,] _z, _y, _prim;

    /// <summary>Phase indices (0 = A …) of the matrix rows, ascending.</summary>
    public int[] PhaseOrder { get; }
    public PhaseSet Phases => PhaseSetExtensions.FromIndices(PhaseOrder);
    public int PhaseCount => PhaseOrder.Length;

    /// <summary>Phase impedance matrix [Ω/mile].</summary>
    public Complex[,] ZOhmPerMile => (Complex[,])_z.Clone();
    /// <summary>Phase impedance matrix [Ω/km].</summary>
    public Complex[,] ZOhmPerKm => CMat.Scale(_z, 1 / Lengths.KmPerMile);
    /// <summary>Shunt admittance matrix j·ω·C [S/mile].</summary>
    public Complex[,] YSiemensPerMile => (Complex[,])_y.Clone();
    /// <summary>Shunt admittance matrix [S/km].</summary>
    public Complex[,] YSiemensPerKm => CMat.Scale(_y, 1 / Lengths.KmPerMile);
    /// <summary>Primitive impedance matrix before Kron reduction [Ω/mile] (phases first, then neutrals).</summary>
    public Complex[,] PrimitiveOhmPerMile => (Complex[,])_prim.Clone();

    /// <summary>Element of the impedance matrix by phase letters, e.g. Z('a','b') [Ω/mile].</summary>
    public Complex Z(int phaseRow, int phaseCol) =>
        _z[Array.IndexOf(PhaseOrder, phaseRow), Array.IndexOf(PhaseOrder, phaseCol)];

    /// <summary>3×3 phase impedance [Ω/mile] with zero rows/columns for missing phases (Kersting's lateral convention).</summary>
    public Complex[,] ZFull3x3OhmPerMile()
    {
        var r = new Complex[3, 3];
        for (int i = 0; i < PhaseOrder.Length; i++)
            for (int j = 0; j < PhaseOrder.Length; j++) r[PhaseOrder[i], PhaseOrder[j]] = _z[i, j];
        return r;
    }

    /// <summary>Sequence impedances [Ω/mile] (three-phase lines only).</summary>
    public SequenceImpedance Sequence => SequenceImpedance.FromPhaseMatrix(_z);

    /// <summary>Converts to a <see cref="LineCode"/> (per km).</summary>
    public LineCode ToLineCode(string name, double normalAmps = 0) =>
        new(name, ZOhmPerKm, YSiemensPerKm) { NormalAmps = normalAmps };

    public override string ToString() => $"Z [Ω/mile], phases {Phases.ToLabel()}:\n{CMat.Format(_z)}";
}

/// <summary>Sequence impedances from a 3×3 phase matrix: Z012 = A⁻¹·Zabc·A.</summary>
public sealed class SequenceImpedance
{
    private SequenceImpedance(Complex[,] z012) => _z012 = z012;
    private readonly Complex[,] _z012;

    /// <summary>Full 3×3 sequence matrix (rows/cols 0, 1, 2); off-diagonals show the coupling of an untransposed line.</summary>
    public Complex[,] Z012 => (Complex[,])_z012.Clone();
    public Complex Zero => _z012[0, 0];
    public Complex Positive => _z012[1, 1];
    public Complex Negative => _z012[2, 2];

    public static SequenceImpedance FromPhaseMatrix(Complex[,] zabc)
    {
        if (zabc.GetLength(0) != 3) throw new ArgumentException("Sequence impedances need a 3×3 phase matrix.", nameof(zabc));
        Complex a = SymmetricalComponents.A, a2 = SymmetricalComponents.A2;
        var As = new Complex[,] { { 1, 1, 1 }, { 1, a2, a }, { 1, a, a2 } };
        var Ainv = new Complex[,] { { 1, 1, 1 }, { 1, a, a2 }, { 1, a2, a } };
        Ainv = CMat.Scale(Ainv, 1.0 / 3);
        return new(CMat.Mul(CMat.Mul(Ainv, zabc), As));
    }
}

/// <summary>
/// Line constants: modified Carson's equations, Kron reduction and potential coefficients (Kersting, chapters 4 and 5).
/// Impedances in Ω/mile, admittances in S/mile, distances in ft.
/// </summary>
public static class LineConstants
{
    /// <summary>Earth resistivity used when not specified [Ω·m].</summary>
    public const double DefaultEarthResistivity = 100;

    /// <summary>Modified Carson self impedance z_ii = r_i + 0.00158836·f + j0.00202237·f·(ln(1/GMR_i) + 7.6786 + ½·ln(ρ/f)) [Ω/mile].</summary>
    public static Complex SelfImpedance(double rOhmPerMile, double gmrFeet, double frequencyHz = 60, double earthResistivity = DefaultEarthResistivity) =>
        new(rOhmPerMile + 0.00158836 * frequencyHz,
            0.00202237 * frequencyHz * (Math.Log(1 / gmrFeet) + 7.6786 + 0.5 * Math.Log(earthResistivity / frequencyHz)));

    /// <summary>Modified Carson mutual impedance z_ij [Ω/mile] for spacing D_ij [ft].</summary>
    public static Complex MutualImpedance(double distanceFeet, double frequencyHz = 60, double earthResistivity = DefaultEarthResistivity) =>
        new(0.00158836 * frequencyHz,
            0.00202237 * frequencyHz * (Math.Log(1 / distanceFeet) + 7.6786 + 0.5 * Math.Log(earthResistivity / frequencyHz)));

    /// <summary>Phase impedance and shunt admittance of an overhead line (neutrals Kron-reduced).</summary>
    public static LineImpedance Overhead(OverheadConfiguration configuration, double frequencyHz = 60, double earthResistivity = DefaultEarthResistivity)
    {
        var conds = Order(configuration.Conductors, c => c.Role);
        int n = conds.Count, np = conds.Count(c => c.Role != ConductorRole.Neutral);
        if (np == 0) throw new ArgumentException("Configuration has no phase conductors.", nameof(configuration));
        var prim = new Complex[n, n];
        var p = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var ci = conds[i]; var cj = conds[j];
                if (i == j)
                {
                    prim[i, i] = SelfImpedance(ci.Conductor.ResistanceOhmPerMile, ci.Conductor.GmrFeet, frequencyHz, earthResistivity);
                    p[i, i] = 11.17689 * Math.Log(2 * ci.YFeet / ci.Conductor.RadiusFeet);
                }
                else
                {
                    double dx = ci.XFeet - cj.XFeet;
                    double d = Math.Sqrt(dx * dx + Math.Pow(ci.YFeet - cj.YFeet, 2));
                    double s = Math.Sqrt(dx * dx + Math.Pow(ci.YFeet + cj.YFeet, 2));
                    prim[i, j] = MutualImpedance(d, frequencyHz, earthResistivity);
                    p[i, j] = 11.17689 * Math.Log(s / d);
                }
            }
        var z = CMat.Kron(prim, np);
        var pabc = CMat.Kron(p, np);                       // mile/µF
        var c = CMat.Inverse(pabc);                        // µF/mile
        var y = CMat.Scale(c, new Complex(0, 2 * Math.PI * frequencyHz * 1e-6)); // S/mile
        return new(PhaseIndices(conds.Take(np).Select(c => c.Role)), z, y, prim);
    }

    /// <summary>Concentric-neutral cables (Kersting Example 4.2 / 5.2), optional extra bare neutral conductor.</summary>
    public static LineImpedance ConcentricNeutral(ConcentricNeutralCable cable, IReadOnlyList<CablePosition> positions,
        ConductorPosition? extraNeutral = null, double frequencyHz = 60, double earthResistivity = DefaultEarthResistivity)
    {
        var pos = Order(positions, x => x.Phase);
        double R = cable.NeutralRadiusFeet;
        int k = cable.StrandCount;
        Func<int, int, double> dist = (i, j) => Math.Sqrt(Math.Pow(pos[i].XFeet - pos[j].XFeet, 2) + Math.Pow(pos[i].YFeet - pos[j].YFeet, 2));
        var z = ShieldedCables(pos.Count, cable.Phase, cable.NeutralResistanceOhmPerMile, cable.NeutralGmrFeet,
            (i, j) => i == j ? R : Math.Pow(Math.Pow(dist(i, j), k) - Math.Pow(R, k), 1.0 / k), dist, pos, extraNeutral,
            frequencyHz, earthResistivity, out var prim);
        double rdc = cable.Phase.RadiusFeet, rds = cable.Strand.RadiusFeet;
        double yag = 77.3619 * frequencyHz / 60 / (Math.Log(R / rdc) - Math.Log(k * rds / R) / k) * 1e-6;
        return new(PhaseIndices(pos.Select(p => p.Phase)), z, DiagonalY(pos.Count, yag), prim);
    }

    /// <summary>Tape-shielded cables (Kersting Example 4.3 / 5.3), optional extra bare neutral conductor.</summary>
    public static LineImpedance TapeShielded(TapeShieldedCable cable, IReadOnlyList<CablePosition> positions,
        ConductorPosition? extraNeutral = null, double frequencyHz = 60, double earthResistivity = DefaultEarthResistivity)
    {
        var pos = Order(positions, x => x.Phase);
        double gmr = cable.ShieldGmrFeet;
        Func<int, int, double> dist = (i, j) => Math.Sqrt(Math.Pow(pos[i].XFeet - pos[j].XFeet, 2) + Math.Pow(pos[i].YFeet - pos[j].YFeet, 2));
        var z = ShieldedCables(pos.Count, cable.Phase, cable.ShieldResistanceOhmPerMile, gmr,
            (i, j) => i == j ? gmr : dist(i, j), dist, pos, extraNeutral, frequencyHz, earthResistivity, out var prim);
        double yag = 77.3619 * frequencyHz / 60 / Math.Log(gmr / cable.Phase.RadiusFeet) * 1e-6;
        return new(PhaseIndices(pos.Select(p => p.Phase)), z, DiagonalY(pos.Count, yag), prim);
    }

    /// <summary>Builds a 3×3 (or n×n) matrix from sequence impedances: Zs = (2Z1 + Z0)/3 on the diagonal, Zm = (Z0 − Z1)/3 elsewhere.</summary>
    public static Complex[,] FromSequence(int phases, Complex z1, Complex z0)
    {
        var r = new Complex[phases, phases];
        Complex zs = (2 * z1 + z0) / 3, zm = (z0 - z1) / 3;
        if (phases == 1) zs = z1; // single-phase line: positive-sequence value (OpenDSS convention)
        for (int i = 0; i < phases; i++)
            for (int j = 0; j < phases; j++) r[i, j] = i == j ? zs : zm;
        return r;
    }

    private static Complex[,] ShieldedCables(int nc, ConductorData phase, double rShield, double gmrShield,
        Func<int, int, double> phaseToShield, Func<int, int, double> centre, IReadOnlyList<CablePosition> pos,
        ConductorPosition? extra, double f, double rho, out Complex[,] prim)
    {
        int n = 2 * nc + (extra is null ? 0 : 1);
        prim = new Complex[n, n];
        // indices: 0..nc-1 phase conductors, nc..2nc-1 shields/neutrals, 2nc extra neutral
        double ExtraDist(int i) => Math.Sqrt(Math.Pow(pos[i].XFeet - extra!.XFeet, 2) + Math.Pow(pos[i].YFeet - extra.YFeet, 2));
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                bool iP = i < nc, jP = j < nc, iX = i == 2 * nc, jX = j == 2 * nc;
                int ci = iP ? i : i - nc, cj = jP ? j : j - nc;
                if (i == j)
                {
                    prim[i, j] = iX ? SelfImpedance(extra!.Conductor.ResistanceOhmPerMile, extra.Conductor.GmrFeet, f, rho)
                        : iP ? SelfImpedance(phase.ResistanceOhmPerMile, phase.GmrFeet, f, rho)
                        : SelfImpedance(rShield, gmrShield, f, rho);
                    continue;
                }
                double d;
                if (iX || jX) d = ExtraDist(iX ? cj : ci);
                else if (iP && jP) d = centre(ci, cj);
                else if (!iP && !jP) d = centre(ci, cj);
                else d = iP ? phaseToShield(ci, cj) : phaseToShield(cj, ci);
                prim[i, j] = MutualImpedance(d, f, rho);
            }
        return CMat.Kron(prim, nc);
    }

    private static Complex[,] DiagonalY(int n, double b)
    {
        var y = new Complex[n, n];
        for (int i = 0; i < n; i++) y[i, i] = new Complex(0, b);
        return y;
    }

    private static List<T> Order<T>(IEnumerable<T> items, Func<T, ConductorRole> role)
    {
        var list = items.ToList();
        var phases = list.Where(c => role(c) != ConductorRole.Neutral).ToList();
        if (phases.Select(role).Distinct().Count() != phases.Count)
            throw new ArgumentException("Each phase may appear only once in a configuration.");
        return [.. phases.OrderBy(c => (int)role(c)), .. list.Where(c => role(c) == ConductorRole.Neutral)];
    }

    private static int[] PhaseIndices(IEnumerable<ConductorRole> roles) => roles.Select(r => (int)r).ToArray();
}

/// <summary>
/// Per-length phase impedance/admittance description of a line (like an OpenDSS LineCode). Matrices are n×n in the order of the
/// conductors of the segment that uses the code.
/// </summary>
public sealed class LineCode
{
    /// <summary>Creates a line code from per-km matrices: series impedance [Ω/km] and shunt admittance [S/km] (may be null).</summary>
    public LineCode(string name, Complex[,] zOhmPerKm, Complex[,]? ySiemensPerKm = null)
    {
        int n = zOhmPerKm.GetLength(0);
        if (zOhmPerKm.GetLength(1) != n) throw new ArgumentException("Impedance matrix must be square.", nameof(zOhmPerKm));
        if (ySiemensPerKm is not null && (ySiemensPerKm.GetLength(0) != n || ySiemensPerKm.GetLength(1) != n))
            throw new ArgumentException("Admittance matrix size must match the impedance matrix.", nameof(ySiemensPerKm));
        Name = name;
        ZOhmPerKm = (Complex[,])zOhmPerKm.Clone();
        YSiemensPerKm = ySiemensPerKm is null ? new Complex[n, n] : (Complex[,])ySiemensPerKm.Clone();
    }

    public string Name { get; }
    public int PhaseCount => ZOhmPerKm.GetLength(0);
    /// <summary>Series phase impedance [Ω/km].</summary>
    public Complex[,] ZOhmPerKm { get; }
    /// <summary>Shunt admittance j·ω·C [S/km].</summary>
    public Complex[,] YSiemensPerKm { get; }
    /// <summary>Normal ampacity [A] (0 = unknown).</summary>
    public double NormalAmps { get; set; }

    /// <summary>Impedance [Ω/mile].</summary>
    public Complex[,] ZOhmPerMile => CMat.Scale(ZOhmPerKm, Lengths.KmPerMile);

    /// <summary>
    /// From matrices in Ω/mile and shunt susceptance in µS/mile (the format of the IEEE test feeder data sheets).
    /// </summary>
    public static LineCode FromOhmPerMile(string name, Complex[,] zOhmPerMile, double[,]? bMicroSiemensPerMile = null)
    {
        int n = zOhmPerMile.GetLength(0);
        var y = new Complex[n, n];
        if (bMicroSiemensPerMile is not null)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++) y[i, j] = new Complex(0, bMicroSiemensPerMile[i, j] * 1e-6 / Lengths.KmPerMile);
        return new(name, CMat.Scale(zOhmPerMile, 1 / Lengths.KmPerMile), y);
    }

    /// <summary>
    /// From resistance/reactance matrices [Ω/unit] and capacitance [nF/unit] (OpenDSS rmatrix/xmatrix/cmatrix) at <paramref name="frequencyHz"/>.
    /// </summary>
    public static LineCode FromRxc(string name, double[,] r, double[,] x, double[,]? cNanoFarad, LengthUnit unit, double frequencyHz = 60)
    {
        int n = r.GetLength(0);
        double perKm = 1 / Lengths.ToKm(1, unit == LengthUnit.None ? LengthUnit.Kilometer : unit);
        var z = new Complex[n, n];
        var y = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                z[i, j] = new Complex(r[i, j], x[i, j]) * perKm;
                if (cNanoFarad is not null) y[i, j] = new Complex(0, 2 * Math.PI * frequencyHz * cNanoFarad[i, j] * 1e-9) * perKm;
            }
        return new(name, z, y);
    }

    /// <summary>From sequence data: R1, X1, R0, X0 [Ω/unit], C1, C0 [nF/unit].</summary>
    public static LineCode FromSequence(string name, int phases, double r1, double x1, double r0, double x0, double c1NanoFarad, double c0NanoFarad,
        LengthUnit unit, double frequencyHz = 60)
    {
        double perKm = 1 / Lengths.ToKm(1, unit == LengthUnit.None ? LengthUnit.Kilometer : unit);
        var z = LineConstants.FromSequence(phases, new Complex(r1, x1) * perKm, new Complex(r0, x0) * perKm);
        double w = 2 * Math.PI * frequencyHz * 1e-9 * perKm;
        var y = LineConstants.FromSequence(phases, new Complex(0, w * c1NanoFarad), new Complex(0, w * c0NanoFarad));
        return new(name, z, y);
    }

    public override string ToString() => $"LineCode {Name} ({PhaseCount} ph)";
}
