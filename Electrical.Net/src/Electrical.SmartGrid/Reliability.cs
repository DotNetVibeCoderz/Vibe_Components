using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.Renewable;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.SmartGrid;

/// <summary>Reliability data of one series component (a branch / feeder section).</summary>
/// <param name="FailureRatePerYear">λ [failures/yr].</param>
/// <param name="RepairHours">Mean time to repair r [h].</param>
/// <param name="ProtectedByFuse">A fuse/recloser at the upstream end isolates faults on this section and below from the rest of the feeder.</param>
public sealed record ComponentReliability(double FailureRatePerYear, double RepairHours, bool ProtectedByFuse = false);

/// <summary>Load-point indices: λ [f/yr], U [h/yr], r = U/λ [h].</summary>
public sealed record LoadPointReliability(int BusId, double Lambda, double UnavailabilityHours, double AverageOutageHours, int Customers, double LoadMw);

/// <summary>IEEE 1366 customer-oriented indices and energy not supplied.</summary>
/// <param name="Saifi">Σλ_j·N_j / ΣN [interruptions/customer·yr].</param>
/// <param name="Saidi">ΣU_j·N_j / ΣN [h/customer·yr].</param>
/// <param name="Caidi">SAIDI / SAIFI [h/interruption].</param>
/// <param name="Asai">Average service availability index (0–1).</param>
/// <param name="EnsMwh">Energy not supplied ΣL_j·U_j [MWh/yr].</param>
public sealed record ReliabilityIndices(double Saifi, double Saidi, double Caidi, double Asai, double EnsMwh, IReadOnlyList<LoadPointReliability> LoadPoints)
{
    /// <summary>Average energy not supplied per customer [MWh/customer·yr].</summary>
    public double Aens => LoadPoints.Sum(l => l.Customers) is int n && n > 0 ? EnsMwh / n : 0;
}

/// <summary>
/// Analytical reliability of radial feeders (Billinton &amp; Allan, "Reliability Evaluation of Power Systems", ch. 7). A failure of a section
/// interrupts every load point downstream of it for the repair time. Load points elsewhere are interrupted too unless a fuse on the faulted
/// section's path isolates it; when sectionalizing switches exist they are restored after <c>switchingHours</c>, otherwise after repair.
/// </summary>
public static class RadialReliability
{
    /// <param name="components">Reliability data per branch index (branches without data are treated as perfectly reliable).</param>
    /// <param name="customers">Customers per bus id (default: 1 per bus with load).</param>
    /// <param name="switchingHours">Isolation/restoration time with sectionalizing switches; NaN = no switches (unaffected-by-fuse load points wait for repair).</param>
    public static ReliabilityIndices Evaluate(PowerNetwork net, IReadOnlyDictionary<int, ComponentReliability> components,
        IReadOnlyDictionary<int, int>? customers = null, double switchingHours = double.NaN)
    {
        if (!Topology.IsRadial(net)) throw new InvalidOperationException("Network must be radial.");
        int n = net.BusCount;
        int root = net.SlackBus is { } s ? net.IndexOf(s.Id) : 0;
        // tree: parent branch of each bus
        var parentBranch = new int[n]; var parent = new int[n];
        Array.Fill(parentBranch, -1); Array.Fill(parent, -1);
        var adj = new List<(int To, int Branch)>[n];
        for (int i = 0; i < n; i++) adj[i] = [];
        for (int k = 0; k < net.Branches.Count; k++)
        {
            var b = net.Branches[k];
            if (!b.InService) continue;
            int f = net.IndexOf(b.From), t = net.IndexOf(b.To);
            adj[f].Add((t, k)); adj[t].Add((f, k));
        }
        var seen = new bool[n];
        var q = new Queue<int>(); q.Enqueue(root); seen[root] = true;
        while (q.Count > 0)
        {
            int u = q.Dequeue();
            foreach (var (v, k) in adj[u]) if (!seen[v]) { seen[v] = true; parent[v] = u; parentBranch[v] = k; q.Enqueue(v); }
        }
        // downstream bus of each branch and set of branches on the path from root to each bus
        var childOf = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) if (parentBranch[i] >= 0) childOf[parentBranch[i]] = i;
        bool IsDownstream(int bus, int branch)
        {
            if (!childOf.TryGetValue(branch, out int c)) return false;
            for (int v = bus; v >= 0; v = parent[v]) if (v == c) return true;
            return false;
        }
        // nearest fuse at or above a branch (walking up the tree)
        int FuseAbove(int branch)
        {
            if (!childOf.TryGetValue(branch, out int c)) return -1;
            for (int v = c; v >= 0 && parentBranch[v] >= 0; v = parent[v])
            {
                int k = parentBranch[v];
                if (components.TryGetValue(k, out var cr) && cr.ProtectedByFuse) return k;
            }
            return -1;
        }

        var loadMw = new Dictionary<int, double>();
        foreach (var l in net.Loads) if (l.InService) loadMw[l.Bus] = loadMw.GetValueOrDefault(l.Bus) + l.P;
        var points = new List<LoadPointReliability>();
        foreach (var bus in net.Buses)
        {
            int nc = customers is not null ? customers.GetValueOrDefault(bus.Id) : (loadMw.ContainsKey(bus.Id) ? 1 : 0);
            if (nc == 0 && !loadMw.ContainsKey(bus.Id)) continue;
            int j = net.IndexOf(bus.Id);
            double lam = 0, u = 0;
            foreach (var (k, c) in components)
            {
                if (k < 0 || k >= net.Branches.Count || !net.Branches[k].InService) continue;
                if (IsDownstream(j, k)) { lam += c.FailureRatePerYear; u += c.FailureRatePerYear * c.RepairHours; continue; }
                int fuse = FuseAbove(k);
                if (fuse >= 0 && !IsDownstream(j, fuse)) continue; // isolated by the fuse
                double dur = double.IsNaN(switchingHours) ? c.RepairHours : Math.Min(switchingHours, c.RepairHours);
                lam += c.FailureRatePerYear; u += c.FailureRatePerYear * dur;
            }
            points.Add(new LoadPointReliability(bus.Id, lam, u, lam > 0 ? u / lam : 0, nc, loadMw.GetValueOrDefault(bus.Id)));
        }
        double ntot = points.Sum(p => p.Customers);
        double saifi = ntot > 0 ? points.Sum(p => p.Lambda * p.Customers) / ntot : 0;
        double saidi = ntot > 0 ? points.Sum(p => p.UnavailabilityHours * p.Customers) / ntot : 0;
        double asai = ntot > 0 ? 1 - saidi / 8760 : 1;
        double ens = points.Sum(p => p.LoadMw * p.UnavailabilityHours);
        return new ReliabilityIndices(saifi, saidi, saifi > 0 ? saidi / saifi : 0, asai, ens, points);
    }

    /// <summary>Uniform component data for every in-service branch (e.g. λ = 0.1 f/yr, r = 4 h).</summary>
    public static Dictionary<int, ComponentReliability> Uniform(PowerNetwork net, double failureRatePerYear, double repairHours, bool fused = false) =>
        Enumerable.Range(0, net.Branches.Count).Where(k => net.Branches[k].InService)
            .ToDictionary(k => k, _ => new ComponentReliability(failureRatePerYear, repairHours, fused));
}

/// <summary>DI facade for smart-grid studies.</summary>
public interface ISmartGridService
{
    ReconfigurationResult Reconfigure(PowerNetwork net);
    VoltVarResult VoltVar(PowerNetwork net, VoltVarControls controls);
    MicrogridDispatch MicrogridOptimal(MicrogridConfig config);
    ReliabilityIndices Reliability(PowerNetwork net, IReadOnlyDictionary<int, ComponentReliability> components, IReadOnlyDictionary<int, int>? customers = null);
    PeakShavingResult PeakShaving(IReadOnlyList<double> loadMw, BatteryStorage battery, double stepHours = 1);
}

internal sealed class SmartGridService : ISmartGridService
{
    public ReconfigurationResult Reconfigure(PowerNetwork net) => FeederReconfiguration.Optimize(net);
    public VoltVarResult VoltVar(PowerNetwork net, VoltVarControls controls) => VoltVarOptimization.Optimize(net, controls);
    public MicrogridDispatch MicrogridOptimal(MicrogridConfig config) => MicrogridEms.Optimal(config);
    public ReliabilityIndices Reliability(PowerNetwork net, IReadOnlyDictionary<int, ComponentReliability> components, IReadOnlyDictionary<int, int>? customers = null) =>
        RadialReliability.Evaluate(net, components, customers);
    public PeakShavingResult PeakShaving(IReadOnlyList<double> loadMw, BatteryStorage battery, double stepHours = 1) =>
        DemandResponse.OptimalPeakShaving(loadMw, battery, stepHours);
}

public static class SmartGridBuilderExtensions
{
    /// <summary>Registers <see cref="ISmartGridService"/> (and the Renewable/PowerFlow modules).</summary>
    public static ElectricalBuilder AddSmartGrid(this ElectricalBuilder b)
    {
        b.AddRenewable();
        if (b.TryAddModule("SmartGrid")) b.Services.TryAddSingleton<ISmartGridService, SmartGridService>();
        return b;
    }
}
