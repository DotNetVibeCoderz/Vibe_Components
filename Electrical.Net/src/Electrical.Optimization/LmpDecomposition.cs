using Electrical.Core.Numerics;

namespace Electrical.Optimization;

/// <summary>
/// LMP components per bus position [$/MWh]: LMP = Energy + Loss + Congestion + Voltage. Energy is the price at the reference bus; the
/// voltage term (zero unless a bus voltage limit binds) collects the effect of binding |V| limits, which AC prices also carry.
/// </summary>
public sealed class LmpComponents
{
    public required int ReferenceBusId { get; init; }
    public required int[] BusIds { get; init; }
    public required double[] Lmp { get; init; }
    public required double[] Energy { get; init; }
    public required double[] Loss { get; init; }
    public required double[] Congestion { get; init; }
    public required double[] Voltage { get; init; }
    /// <summary>Marginal loss factors ∂P_loss/∂P_i with the reference bus as balancing bus (0 for DC).</summary>
    public required double[] LossFactors { get; init; }

    /// <summary>Delivery factors 1 − LF_i.</summary>
    public double[] DeliveryFactors => LossFactors.Select(l => 1 - l).ToArray();

    /// <summary>Largest |LMP − (energy + loss + congestion + voltage)| over all buses [$/MWh].</summary>
    public double MaxResidual => Enumerable.Range(0, Lmp.Length).Select(i => Math.Abs(Lmp[i] - Energy[i] - Loss[i] - Congestion[i] - Voltage[i])).DefaultIfEmpty(0).Max();

    public int PositionOf(int busId) => Array.IndexOf(BusIds, busId);
}

/// <summary>
/// Decomposition of locational marginal prices.
/// <para>
/// AC: the stationarity of the OPF Lagrangian with respect to the bus voltages, J_Vᵀ·λ + J_h,Vᵀ·μ + ν_V = 0 (J = power-flow Jacobian at the
/// solution, μ = branch-limit duals, ν = voltage-bound duals), is solved for λ with the reference-bus price λ_r as parameter. With
/// J_r the Jacobian without the reference P row and without the fixed angle column:
/// λ = λ_r·(1 − LF) − J_r⁻ᵀ·J_h,Vᵀ·μ − J_r⁻ᵀ·ν, where 1 − LF_i = −(J_r⁻ᵀ·∂P_r/∂V)_i are the delivery factors obtained from the AC Jacobian.
/// The three terms are the energy + loss, congestion and voltage components.
/// </para>
/// <para>DC: LMP = λ + congestion (losses are zero in the lossless DC model).</para>
/// </summary>
public static class LmpDecomposition
{
    /// <param name="referenceBusId">Bus whose price defines the energy component (default: the OPF reference bus).</param>
    public static LmpComponents Decompose(AcOpfResult result, int? referenceBusId = null)
    {
        var m = result.Model;
        var net = m.Net;
        int nb = m.Nb;
        int rb = referenceBusId is int id ? net.IndexOf(id) : m.Ref;
        var x = result.Solver.X;
        var (jg, jh) = m.Jacobians(x);

        // y variables: θ (except fixed reference angle), |V|; rows: P (except reference-price bus), Q
        var colPos = new int[m.Nx]; Array.Fill(colPos, -1);
        int ny = 0;
        for (int i = 0; i < nb; i++) if (i != m.Ref) colPos[m.IVa + i] = ny++;
        for (int i = 0; i < nb; i++) colPos[m.IVm + i] = ny++;
        var rowPos = new int[2 * nb]; Array.Fill(rowPos, -1);
        int nr = 0;
        for (int r = 0; r < 2 * nb; r++) if (r != rb) rowPos[r] = nr++;
        if (nr != ny) throw new InvalidOperationException("Inconsistent reduced Jacobian.");

        var mb = new SparseBuilder<double>(ny, ny); // M = J_rᵀ
        var ar = new double[ny];
        for (int r = 0; r < 2 * nb; r++)
        {
            var ci = jg.RowIndices(r); var cv = jg.RowValues(r);
            for (int q = 0; q < ci.Length; q++)
            {
                int c = colPos[ci[q]];
                if (c < 0) continue;
                if (r == rb) ar[c] += cv[q];
                else mb.Add(c, rowPos[r], cv[q]);
            }
        }
        for (int i = 0; i < ny; i++) mb.Add(i, i, 0);
        var qh = new double[ny];
        var mu = result.Solver.Mu;
        for (int k = 0; k < jh.Rows; k++)
        {
            if (mu[k] == 0) continue;
            var ci = jh.RowIndices(k); var cv = jh.RowValues(k);
            for (int q = 0; q < ci.Length; q++) { int c = colPos[ci[q]]; if (c >= 0) qh[c] += cv[q] * mu[k]; }
        }
        var qv = new double[ny];
        for (int i = 0; i < nb; i++) qv[colPos[m.IVm + i]] = result.Solver.MuUpper[m.IVm + i] - result.Solver.MuLower[m.IVm + i];

        var lu = SparseLU<double>.Factorize(mb.ToCsr());
        var s = lu.Solve(ar);
        var c1 = lu.Solve(qh);
        var c2 = lu.Solve(qv);

        double bmva = m.BaseMva;
        double lr = result.Solver.Lambda[rb];
        double energy = lr / bmva;
        var e = new double[nb]; var loss = new double[nb]; var cong = new double[nb]; var volt = new double[nb]; var lf = new double[nb];
        for (int i = 0; i < nb; i++)
        {
            e[i] = energy;
            if (i == rb) continue;
            int p = rowPos[i];
            lf[i] = 1 + s[p];          // delivery factor = −s
            loss[i] = -lf[i] * energy;
            cong[i] = -c1[p] / bmva;
            volt[i] = -c2[p] / bmva;
        }
        return new LmpComponents
        {
            ReferenceBusId = net.Buses[rb].Id, BusIds = result.BusIds, Lmp = result.Lmp, Energy = e, Loss = loss, Congestion = cong,
            Voltage = volt, LossFactors = lf,
        };
    }

    /// <summary>DC-OPF prices: energy = λ (price at the PTDF reference bus), congestion = LMP − λ, no losses.</summary>
    public static LmpComponents Decompose(DcOpfResult result)
    {
        int nb = result.Lmp.Length;
        var cong = result.Lmp.Select(l => l - result.Lambda).ToArray();
        int refPos = Array.FindIndex(cong, c => Math.Abs(c) < 1e-9);
        return new LmpComponents
        {
            ReferenceBusId = refPos >= 0 ? result.BusIds[refPos] : result.BusIds[0], BusIds = result.BusIds, Lmp = result.Lmp,
            Energy = Enumerable.Repeat(result.Lambda, nb).ToArray(), Loss = new double[nb], Congestion = cong, Voltage = new double[nb],
            LossFactors = new double[nb],
        };
    }
}
