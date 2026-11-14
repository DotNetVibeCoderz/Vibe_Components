using System.Numerics;
using Electrical.Distribution;

namespace Electrical.Tests;

public class DistributionLineImpedanceTests
{
    private static void Near(Complex expected, Complex actual, double tol, string what)
    {
        Assert.True((expected - actual).Magnitude <= tol, $"{what}: expected {expected}, got {actual}");
    }

    [Fact]
    public void KerstingExample4_1_KronReducedPhaseImpedance()
    {
        // Kersting, Distribution System Modeling and Analysis, Example 4.1: 336,400 26/7 ACSR phases, 4/0 6/1 ACSR neutral,
        // modified Carson's equations (60 Hz, 100 Ω·m), Kron reduction → z_abc [Ω/mile].
        var z = DistributionTestCases.Ieee4NodeLine();
        var zabc = z.ZOhmPerMile;
        Near(new(0.4576, 1.0780), zabc[0, 0], 1e-3, "z_aa");
        Near(new(0.1560, 0.5017), zabc[0, 1], 1e-3, "z_ab");
        Near(new(0.1535, 0.3849), zabc[0, 2], 1e-3, "z_ac");
        Near(new(0.4666, 1.0482), zabc[1, 1], 1e-3, "z_bb");
        Near(new(0.1580, 0.4236), zabc[1, 2], 1e-3, "z_bc");
        Near(new(0.4615, 1.0651), zabc[2, 2], 1e-3, "z_cc");
        Assert.Equal(4, z.PrimitiveOhmPerMile.GetLength(0));

        // Example 4.1 sequence impedances: z00 = 0.7735 + j1.9373, z11 = z22 = 0.3061 + j0.6270 Ω/mile
        var seq = z.Sequence;
        Near(new(0.7735, 1.9373), seq.Zero, 2e-3, "z0");
        Near(new(0.3061, 0.6270), seq.Positive, 2e-3, "z1");
        Near(seq.Positive, seq.Negative, 1e-9, "z2");
    }

    [Fact]
    public void KerstingExample5_1_ShuntAdmittanceFromPotentialCoefficients()
    {
        // Kersting Example 5.1 (same pole): y_abc = j[5.6711 −1.8362 −0.7033; ·  5.9774 −1.1690; · · 5.3911] µS/mile
        var y = DistributionTestCases.Ieee4NodeLine().YSiemensPerMile;
        double[,] expected = { { 5.6711, -1.8362, -0.7033 }, { -1.8362, 5.9774, -1.1690 }, { -0.7033, -1.1690, 5.3911 } };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Assert.Equal(0, y[i, j].Real, 12);
                // within 0.2 % of the diagonal (Kersting rounds the conductor radius)
                Assert.True(Math.Abs(expected[i, j] - y[i, j].Imaginary * 1e6) < 0.012, $"y[{i},{j}] = {y[i, j].Imaginary * 1e6:0.0000} µS/mile");
            }
    }

    [Fact]
    public void Ieee13Config601_FromGeometryMatchesDataSheet()
    {
        // IEEE 13-node configuration 601: 556,500 26/7 ACSR phases, 4/0 6/1 ACSR neutral, spacing ID 500 with phasing B A C N.
        var cfg = OverheadConfiguration.Spacing500(ConductorLibrary.Acsr556_5_26_7, ConductorLibrary.Acsr4_0_6_1,
            ConductorRole.PhaseB, ConductorRole.PhaseA, ConductorRole.PhaseC);
        var z = LineConstants.Overhead(cfg);
        var sheet = DistributionTestCases.Ieee13Config(601).ZOhmPerMile;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++) Near(sheet[i, j], z.ZOhmPerMile[i, j], 1.5e-3, $"601[{i},{j}]");
        // susceptance B_aa = 6.2998 µS/mile on the data sheet
        Assert.Equal(6.2998, z.YSiemensPerMile[0, 0].Imaginary * 1e6, 1);
    }

    [Fact]
    public void ConcentricNeutralCable_KerstingExample4_2And5_2()
    {
        // Kersting Example 4.2: three 250 kcmil AA, 1/3 neutral of 13 × #14 Cu, d_od = 1.29 in, 6 in spacing → IEEE configuration 606.
        var cable = new ConcentricNeutralCable(ConductorLibrary.Aa250, ConductorLibrary.Cu14, 13, 1.29);
        Assert.Equal(0.0511, cable.NeutralRadiusFeet, 4);
        Assert.Equal(0.0486, cable.NeutralGmrFeet, 4);
        var z = LineConstants.ConcentricNeutral(cable,
            [new(ConductorRole.PhaseA, 0, 0), new(ConductorRole.PhaseB, 0.5, 0), new(ConductorRole.PhaseC, 1.0, 0)]);
        var zabc = z.ZOhmPerMile;
        Near(new(0.7982, 0.4463), zabc[0, 0], 1.5e-3, "z_aa");
        Near(new(0.3192, 0.0328), zabc[0, 1], 1.5e-3, "z_ab");
        Near(new(0.2849, -0.0143), zabc[0, 2], 1.5e-3, "z_ac");
        Near(new(0.7891, 0.4041), zabc[1, 1], 1.5e-3, "z_bb");
        // Example 5.2: y_ag = j96.5569 µS/mile (IEEE data sheet 96.8897); within 0.5 %
        Assert.InRange(z.YSiemensPerMile[0, 0].Imaginary * 1e6, 96.0, 97.0);
        Assert.Equal(0, z.YSiemensPerMile[0, 1].Magnitude);
    }

    [Fact]
    public void TapeShieldedCable_KerstingExample4_3()
    {
        // Kersting Example 4.3: 1/0 AA tape-shielded cable (d_s = 0.88 in, T = 5 mils) with a 1/0 Cu neutral 3 in away:
        // r_shield = 4.2786 Ω/mile, GMR_shield = 0.0365 ft, z_aa = 1.3218 + j0.6744 Ω/mile.
        var cable = new TapeShieldedCable(ConductorLibrary.Aa1_0, 0.88, 5);
        Assert.Equal(4.2786, cable.ShieldResistanceOhmPerMile, 3);
        Assert.Equal(0.0365, cable.ShieldGmrFeet, 4);
        var z = LineConstants.TapeShielded(cable, [new(ConductorRole.PhaseA, 0, 0)],
            new ConductorPosition(ConductorRole.Neutral, 0.25, 0, ConductorLibrary.Cu1_0));
        Near(new(1.3218, 0.6744), z.ZOhmPerMile[0, 0], 2e-3, "z_aa");
        Assert.Equal(PhaseSet.A, z.Phases);
    }

    [Fact]
    public void GeneralizedMatrices_LineAndLadderConsistency()
    {
        // Kersting eq. 6.x: A = a⁻¹, B = a⁻¹·b, and the matrices derived from the nodal admittance give the same A and B.
        var net = DistributionTestCases.Ieee4Node();
        var line = net.Line("1-2");
        var g = GeneralizedMatrices.ForLine(line);
        var a = g.Sa!;
        var prod = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) prod[i, j] += a[i, k] * g.A[k, j];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) Near(i == j ? 1 : 0, prod[i, j], 1e-12, "a·A");

        // Grounded-wye transformer: A = (1/n_t)·U, B = Z_t (Kersting §8.x); n_t = 12.47/4.16
        var tg = GeneralizedMatrices.ForTransformer(net.Transformer("T1"));
        Assert.Equal(4.16 / 12.47, tg.A[0, 0].Real, 9);
        double zbase = 4.16 * 4.16 * 1000 / 6000;
        Near(new Complex(0.01, 0.06) * zbase, tg.B[1, 1], 1e-9, "Z_t");
        Assert.Equal(0, tg.Sc[0, 0].Magnitude);
        Assert.Equal(4.16 / 12.47, tg.Sd[2, 2].Real, 9);

        // Delta–grounded wye: A_t = (1/n_t)[[1,0,−1],[−1,1,0],[0,−1,1]] with n_t = kV_LL,high / kV_LN,low
        var dy = DistributionTestCases.Ieee4Node(Ieee4NodeConnection.DeltaGrY);
        var dg = GeneralizedMatrices.ForTransformer(dy.Transformer("T1"));
        double nt = 12.47 / (4.16 / Math.Sqrt(3));
        Assert.Equal(1 / nt, dg.A[0, 0].Real, 6);
        Assert.Equal(-1 / nt, dg.A[0, 2].Real, 6);
        Assert.Equal(0, dg.A[0, 1].Magnitude, 6);
        Assert.Null(dg.Sa);   // not unique for a delta primary
    }
}
