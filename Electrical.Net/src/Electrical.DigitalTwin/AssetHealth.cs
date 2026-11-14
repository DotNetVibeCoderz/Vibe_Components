using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.DigitalTwin;

/// <summary>
/// Steady-state transformer thermal parameters per IEEE C57.91 (defaults: 65 °C-rise insulation, rated hot spot 110 °C at 30 °C ambient).
/// </summary>
/// <param name="TopOilRiseRatedK">Top-oil rise over ambient at rated load Δθ_TO,R [K].</param>
/// <param name="HotSpotRiseRatedK">Hot-spot rise over top oil at rated load Δθ_H,R [K].</param>
/// <param name="LossRatio">Ratio R of load losses at rated load to no-load losses.</param>
/// <param name="OilExponent">Oil exponent n (0.8 for ONAN/ONAF).</param>
/// <param name="WindingExponent">Winding exponent m (0.8 for ONAN/ONAF).</param>
public sealed record TransformerThermalParameters(double TopOilRiseRatedK = 55, double HotSpotRiseRatedK = 25, double LossRatio = 4.5,
    double OilExponent = 0.8, double WindingExponent = 0.8)
{
    public static TransformerThermalParameters Default { get; } = new();

    /// <summary>Normal insulation life [h] at the 110 °C reference hot spot (IEEE C57.91 Table 2: 180 000 h).</summary>
    public const double NormalLifeHours = 180_000;

    /// <summary>
    /// Ultimate (steady-state) hot-spot temperature [°C] at per-unit load K:
    /// θ_H = θ_A + Δθ_TO,R·((K²R + 1)/(R + 1))ⁿ + Δθ_H,R·K^(2m).
    /// </summary>
    public double HotSpotC(double loadPu, double ambientC)
    {
        double k2 = loadPu * loadPu;
        return ambientC + TopOilRiseRatedK * Math.Pow((k2 * LossRatio + 1) / (LossRatio + 1), OilExponent) + HotSpotRiseRatedK * Math.Pow(k2, WindingExponent);
    }

    /// <summary>Aging acceleration factor F_AA = exp(15000/383 − 15000/(θ_H + 273)) (IEEE C57.91 eq. 2; 1 at 110 °C).</summary>
    public static double AgingAccelerationFactor(double hotSpotC) => Math.Exp(15000.0 / 383.0 - 15000.0 / (hotSpotC + 273.0));
}

/// <summary>Health of one transformer or line.</summary>
/// <param name="BranchIndex">Position in <see cref="PowerNetwork.Branches"/>.</param>
/// <param name="LoadingPercent">Latest loading [% of RateA].</param>
/// <param name="HotSpotC">Latest hot-spot (transformer) or conductor (line) temperature [°C].</param>
/// <param name="AgingFactor">Latest aging acceleration factor F_AA (1 = normal ageing).</param>
/// <param name="EquivalentAgingHours">Σ F_AA·Δt [h] since tracking started.</param>
/// <param name="ElapsedHours">Tracked time [h].</param>
/// <param name="LossOfLifePercent">100·EquivalentAgingHours/normal life [%].</param>
/// <param name="HealthIndex">0–100 (100 = as new and lightly stressed).</param>
public sealed record AssetHealth(int BranchIndex, string Name, bool IsTransformer, double LoadingPercent, double HotSpotC, double AgingFactor,
    double EquivalentAgingHours, double ElapsedHours, double LossOfLifePercent, double HealthIndex)
{
    /// <summary>F_EQA = equivalent aging hours / elapsed hours.</summary>
    public double EquivalentAgingFactor => ElapsedHours > 0 ? EquivalentAgingHours / ElapsedHours : 1;

    public string Status => HealthIndex >= 80 ? "Good" : HealthIndex >= 50 ? "Fair" : "Poor";
    public string StatusId => HealthIndex >= 80 ? "Baik" : HealthIndex >= 50 ? "Cukup" : "Buruk";

    public override string ToString() =>
        $"{Name}: {LoadingPercent:0.#}% load, {HotSpotC:0.#} °C, F_AA {AgingFactor:0.###}, F_EQA {EquivalentAgingFactor:0.###}, health {HealthIndex:0} ({Status})";
}

/// <summary>
/// Loading-based thermal ageing of transformers (IEEE C57.91 steady-state hot spot and aging acceleration factor) and lines
/// (simplified: conductor temperature θ_A + 45 K·K², same Arrhenius law referenced to the 75 °C design temperature).
/// Each update integrates F_AA over the elapsed interval.
/// </summary>
public sealed class AssetHealthTracker(TransformerThermalParameters? transformer = null)
{
    /// <summary>Conductor temperature rise over ambient at rated current [K] (75 °C design at 30 °C ambient ≈ 45 K).</summary>
    public const double LineRatedRiseK = 45;
    /// <summary>Reference conductor temperature [°C] for line ageing.</summary>
    public const double LineReferenceC = 75;

    private readonly TransformerThermalParameters _tx = transformer ?? TransformerThermalParameters.Default;
    private readonly Dictionary<int, (double Eq, double Elapsed, AssetHealth Last)> _state = [];

    public IReadOnlyList<AssetHealth> Assets => _state.Values.Select(v => v.Last).OrderBy(a => a.BranchIndex).ToList();

    public AssetHealth? Get(int branchIndex) => _state.TryGetValue(branchIndex, out var s) ? s.Last : null;

    /// <summary>Integrates ageing over <paramref name="hours"/> at the given branch loadings (rated branches only).</summary>
    public void Update(PowerNetwork net, IReadOnlyList<BranchResult> branches, double hours, double ambientC)
    {
        foreach (var r in branches)
        {
            if (r.RateA <= 0) continue;
            var br = net.Branches[r.Index];
            double k = r.LoadingPercent / 100;
            double theta, faa;
            if (br.IsTransformer)
            {
                theta = _tx.HotSpotC(k, ambientC);
                faa = TransformerThermalParameters.AgingAccelerationFactor(theta);
            }
            else
            {
                theta = ambientC + LineRatedRiseK * k * k;
                faa = Math.Exp(15000.0 / (LineReferenceC + 273.0) - 15000.0 / (theta + 273.0));
            }
            _state.TryGetValue(r.Index, out var s);
            double eq = s.Eq + faa * Math.Max(0, hours), el = s.Elapsed + Math.Max(0, hours);
            double lol = 100 * eq / TransformerThermalParameters.NormalLifeHours;
            double feqa = el > 0 ? eq / el : faa;
            double stress = Math.Max(faa, feqa);
            double health = Math.Clamp(100 - 15 * Math.Log2(Math.Max(1, stress)) - 10 * Math.Max(0, k - 1) * 10 - lol, 0, 100);
            string name = br.Name ?? BranchRatings.Label(br);
            _state[r.Index] = (eq, el, new AssetHealth(r.Index, name, br.IsTransformer, r.LoadingPercent, theta, faa, eq, el, lol, health));
        }
    }

    public void Reset() => _state.Clear();
}
