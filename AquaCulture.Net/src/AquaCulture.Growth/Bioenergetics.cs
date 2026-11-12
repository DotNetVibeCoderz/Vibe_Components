using AquaCulture.Core.Data;
using AquaCulture.Core.Species;

namespace AquaCulture.Growth;

/// <summary>
/// Parameters of a Wisconsin-style bioenergetics model (the structure of Fish Bioenergetics 4.0, Deslauriers et al. 2017):
/// consumption C = p · CA · W^CB · f_C(T); respiration R = RA · W^RB · f_R(T) · ACT; specific dynamic action, egestion and excretion as
/// fractions; temperature responses use Kitchell's "equation 2". Rates are per gram of body weight per day (g food / g / d, g O₂ / g / d).
/// </summary>
public sealed record BioenergeticsParameters
{
    public required string Name { get; init; }
    public double CA { get; init; }
    public double CB { get; init; } = -0.3;
    /// <summary>Optimum and maximum temperatures and Q-like slope of consumption (°C).</summary>
    public double CTO { get; init; }
    public double CTM { get; init; }
    public double CQ { get; init; } = 2.2;
    public double RA { get; init; }
    public double RB { get; init; } = -0.2;
    public double RTO { get; init; }
    public double RTM { get; init; }
    public double RQ { get; init; } = 2.1;
    /// <summary>Activity multiplier of standard metabolism.</summary>
    public double ACT { get; init; } = 1;
    /// <summary>Specific dynamic action (fraction of assimilated energy).</summary>
    public double SDA { get; init; } = 0.172;
    /// <summary>Egestion: indigestible fraction of consumed energy (1 − digestibility).</summary>
    public double FA { get; init; } = 0.15;
    /// <summary>Excretion: fraction of assimilated energy lost as nitrogenous waste.</summary>
    public double UA { get; init; } = 0.07;
    /// <summary>Energy density of the animal (J/g wet).</summary>
    public double PredatorEnergy { get; init; } = 5000;
    /// <summary>Energy density of the feed (J/g as fed; pelleted feeds 16 000–19 000).</summary>
    public double FeedEnergy { get; init; } = 17_000;
    /// <summary>Oxycalorific coefficient (J per g O₂).</summary>
    public double OxyCalorific { get; init; } = 13_560;
    /// <summary>Where the numbers come from; the built-in sets are indicative and should be calibrated with <see cref="Bioenergetics.FitP"/>.</summary>
    public string Source { get; init; } = "";

    /// <summary>
    /// Indicative Nile tilapia set: Cmax ≈ 3.5 % BW/d at 100 g and 30 °C, routine metabolism ≈ 300 mg O₂/kg/h at 30 °C with an activity
    /// multiplier of 2, digestibility 85 %, energy density 5 kJ/g. Produces ADG ≈ 1.6 g/d and FCR ≈ 1.4 (feed eaten) at 100 g, 28 °C, p = 0.7,
    /// and about 20 → 200 g in 120 days at 29 °C.
    /// </summary>
    public static BioenergeticsParameters Tilapia { get; } = new()
    {
        Name = "Oreochromis niloticus (indicative)", CA = 0.139, CB = -0.3, CTO = 31, CTM = 39, CQ = 2.2,
        RA = 0.0181, RB = -0.2, RTO = 33, RTM = 41, RQ = 2.1, ACT = 2.0, FA = 0.15, PredatorEnergy = 5000,
        Source = "Structure: Fish Bioenergetics 4.0; values indicative (literature ranges for tilapia), calibrate p and ACT locally.",
    };

    /// <summary>
    /// Indicative whiteleg shrimp set: Cmax ≈ 4.5 % BW/d at 10 g, routine metabolism ≈ 600 mg O₂/kg/h at 30 °C (the platform's oxygen-budget
    /// constant), digestibility 80 %, energy density 4.5 kJ/g. Produces ADG ≈ 0.3 g/d at 10 g, 30 °C, p = 0.7, with FCR ≈ 1.0 of feed eaten
    /// (farm FCR is higher because of uneaten feed).
    /// </summary>
    public static BioenergeticsParameters Vaname { get; } = new()
    {
        Name = "Litopenaeus vannamei (indicative)", CA = 0.090, CB = -0.3, CTO = 31, CTM = 38, CQ = 2.4,
        RA = 0.0228, RB = -0.2, RTO = 33, RTM = 40, RQ = 2.1, ACT = 1.0, FA = 0.20, PredatorEnergy = 4500,
        Source = "Structure: Fish Bioenergetics 4.0; values indicative (literature ranges for penaeids), calibrate p and ACT locally.",
    };

    public static BioenergeticsParameters For(SpeciesProfile s) => s.Group == SpeciesGroup.Shrimp ? Vaname with { Name = s.ScientificName + " (shrimp default)" }
        : Tilapia with { Name = s.ScientificName + " (fish default)" };
}

/// <summary>One day of a bioenergetics simulation (per animal).</summary>
public sealed record BioenergeticsDay(int Day, double TempC, double WeightG, double ConsumptionG, double RespirationJ, double GainG,
    double EnergyInJ, double EgestionJ, double ExcretionJ, double SdaJ)
{
    /// <summary>Feed conversion of the day (g feed / g gain); infinite when the animal loses weight.</summary>
    public double Fcr => GainG > 0 ? ConsumptionG / GainG : double.PositiveInfinity;
}

/// <summary>Wisconsin bioenergetics: daily energy budget, simulation, and fitting the proportion of maximum consumption (p) to observed growth.</summary>
public static class Bioenergetics
{
    /// <summary>Kitchell temperature function ("equation 2"): 1 at <paramref name="opt"/>, 0 at <paramref name="max"/>, rising like Q10 below.</summary>
    public static double TemperatureFactor(double t, double opt, double max, double q)
    {
        if (t >= max) return 0;
        double v = (max - t) / (max - opt);
        double z = Math.Log(q) * (max - opt), y = Math.Log(q) * (max - opt + 2);
        double x = z * z * Math.Pow(1 + Math.Sqrt(1 + 40 / y), 2) / 400;
        return Math.Pow(v, x) * Math.Exp(x * (1 - v));
    }

    /// <summary>Maximum consumption (g feed per g body weight per day) at a weight and temperature.</summary>
    public static double Cmax(BioenergeticsParameters b, double weightG, double tempC) =>
        b.CA * Math.Pow(weightG, b.CB) * TemperatureFactor(tempC, b.CTO, b.CTM, b.CQ);

    /// <summary>Respiration (g O₂ per g per day) including activity.</summary>
    public static double Respiration(BioenergeticsParameters b, double weightG, double tempC) =>
        b.RA * Math.Pow(weightG, b.RB) * TemperatureFactor(tempC, b.RTO, b.RTM, b.RQ) * b.ACT;

    /// <summary>The energy budget of one day for an animal of <paramref name="weightG"/> eating p · Cmax.</summary>
    public static BioenergeticsDay Day(BioenergeticsParameters b, int day, double weightG, double tempC, double p)
    {
        double c = Math.Max(0, p) * Cmax(b, weightG, tempC) * weightG;           // g feed
        double ein = c * b.FeedEnergy;
        double eg = ein * b.FA, assimilated = ein - eg;
        double ex = assimilated * b.UA, sda = assimilated * b.SDA;
        double resp = Respiration(b, weightG, tempC) * weightG * b.OxyCalorific;
        double net = assimilated - ex - sda - resp;
        double gain = net / b.PredatorEnergy;
        return new BioenergeticsDay(day, tempC, weightG, c, resp, gain, ein, eg, ex, sda);
    }

    /// <summary>Simulates daily growth from <paramref name="w0"/> over the temperatures given (one per day) at a constant p.</summary>
    public static IReadOnlyList<BioenergeticsDay> Simulate(BioenergeticsParameters b, double w0, IReadOnlyList<double> dailyTempC, double p)
    {
        var list = new List<BioenergeticsDay>(dailyTempC.Count);
        double w = w0;
        for (int d = 0; d < dailyTempC.Count; d++)
        {
            var day = Day(b, d, w, dailyTempC[d], p);
            list.Add(day);
            w = Math.Max(w * 0.5, w + day.GainG);
        }
        return list;
    }

    public static double FinalWeight(BioenergeticsParameters b, double w0, IReadOnlyList<double> temps, double p)
    {
        double w = w0;
        for (int d = 0; d < temps.Count; d++) w = Math.Max(w * 0.5, w + Day(b, d, w, temps[d], p).GainG);
        return w;
    }

    /// <summary>
    /// The proportion of maximum consumption that reproduces an observed growth from <paramref name="w0"/> to <paramref name="w1"/> over the
    /// temperature series (the standard way to apply a Wisconsin model to a farm or a wild population). Returns NaN when unreachable in 0–2.
    /// </summary>
    public static double FitP(BioenergeticsParameters b, double w0, double w1, IReadOnlyList<double> temps)
    {
        double F(double p) => FinalWeight(b, w0, temps, p) - w1;
        if (F(0) > 0 || F(2) < 0) return double.NaN;
        return Numerics.Brent(F, 0, 2, 1e-9);
    }

    /// <summary>Feed consumed over a simulation (g per animal) and its FCR: the mechanistic counterpart of the species FCR table.</summary>
    public static (double FeedG, double GainG, double Fcr) Totals(IReadOnlyList<BioenergeticsDay> days)
    {
        double feed = days.Sum(d => d.ConsumptionG), gain = days.Sum(d => d.GainG);
        return (feed, gain, gain > 0 ? feed / gain : double.PositiveInfinity);
    }
}

/// <summary>Parameters of the standard DEB model (Kooijman 2010), in the units of the Add-my-Pet collection (J, cm, d, at the reference temperature).</summary>
public sealed record DebParameters
{
    public required string Name { get; init; }
    /// <summary>Maximum surface-area-specific assimilation rate {p_Am}, J/d/cm².</summary>
    public required double PAm { get; init; }
    /// <summary>Energy conductance v, cm/d.</summary>
    public required double V { get; init; }
    /// <summary>Allocation fraction to soma κ.</summary>
    public required double Kappa { get; init; }
    /// <summary>Volume-specific somatic maintenance [p_M], J/d/cm³.</summary>
    public required double PM { get; init; }
    /// <summary>Specific cost for structure [E_G], J/cm³.</summary>
    public required double EG { get; init; }
    /// <summary>Shape coefficient δ_M: physical length = structural length / δ_M.</summary>
    public double ShapeCoefficient { get; init; } = 0.3;
    /// <summary>Arrhenius temperature T_A (K) and reference temperature (°C).</summary>
    public double ArrheniusK { get; init; } = 8000;
    public double ReferenceC { get; init; } = 20;
    /// <summary>Wet-weight conversion: weight (g) ≈ structural volume (cm³) × density + reserve energy / μ_E-based factor.</summary>
    public double WetDensity { get; init; } = 1.0;
    public double ReserveWeightPerJ { get; init; } = 1.0 / 23_000;
    public string Source { get; init; } = "";

    /// <summary>Maximum reserve density [E_m] = {p_Am} / v.</summary>
    public double Em => PAm / V;
    /// <summary>Maximum structural length L_m = κ {p_Am} / [p_M].</summary>
    public double Lm => Kappa * PAm / PM;
    /// <summary>Energy investment ratio g = [E_G] / (κ [E_m]).</summary>
    public double G => EG / (Kappa * Em);
    /// <summary>Somatic maintenance rate coefficient k_M = [p_M] / [E_G] (1/d).</summary>
    public double KM => PM / EG;

    /// <summary>Temperature correction factor relative to the reference temperature.</summary>
    public double TemperatureCorrection(double tempC) => Math.Exp(ArrheniusK / (ReferenceC + 273.15) - ArrheniusK / (tempC + 273.15));
}

/// <summary>State of a DEB individual.</summary>
public readonly record struct DebState(double Day, double ReserveJ, double StructureCm3)
{
    public double StructuralLength => Math.Cbrt(StructureCm3);
}

/// <summary>
/// The standard DEB model for the growth phase (assimilation, reserve dynamics, κ-rule, maintenance, growth; maturation and reproduction are
/// not tracked). Feeding level f (0–1, the scaled functional response) can change daily. At constant f the structural length follows von
/// Bertalanffy growth with L∞ = f·L_m and r_B = k_M·g / (3 (f + g)) — the property the tests check.
/// </summary>
public static class DebModel
{
    /// <summary>Starts an individual at a structural length with reserve density f·[E_m].</summary>
    public static DebState Start(DebParameters p, double structuralLengthCm, double f = 1)
    {
        double v = Math.Pow(structuralLengthCm, 3);
        return new DebState(0, f * p.Em * v, v);
    }

    /// <summary>Derivatives (dE/dt, dV/dt) at a state, feeding level and temperature.</summary>
    public static (double dE, double dV) Rates(DebParameters p, DebState s, double f, double tempC)
    {
        double tc = p.TemperatureCorrection(tempC);
        double pAm = p.PAm * tc, v = p.V * tc, pM = p.PM * tc;
        double V = Math.Max(1e-12, s.StructureCm3), L = Math.Cbrt(V), E = s.ReserveJ;
        double pA = f * pAm * L * L;
        double e = E / V;                                          // reserve density
        double pC = e * (p.EG * v / L + pM) / (p.EG + p.Kappa * e) * V;   // mobilisation
        double pS = pM * V;
        double growth = p.Kappa * pC - pS;
        double dV = growth / p.EG;                                  // negative = shrinking (starvation)
        double dE = pA - pC;
        return (dE, dV);
    }

    /// <summary>Integrates daily (RK4 with 4 sub-steps) over feeding levels and temperatures (one per day).</summary>
    public static IReadOnlyList<DebState> Simulate(DebParameters p, DebState start, IReadOnlyList<double> dailyF, IReadOnlyList<double> dailyTempC)
    {
        int n = Math.Min(dailyF.Count, dailyTempC.Count);
        var list = new List<DebState>(n + 1) { start };
        double[] y = [start.ReserveJ, start.StructureCm3];
        for (int d = 0; d < n; d++)
        {
            double f = dailyF[d], t = dailyTempC[d];
            for (int k = 0; k < 4; k++)
                y = Numerics.Rk4((_, s) => { var r = Rates(p, new DebState(0, s[0], Math.Max(1e-12, s[1])), f, t); return [r.dE, r.dV]; }, d + k / 4.0, y, 0.25);
            list.Add(new DebState(d + 1, Math.Max(0, y[0]), Math.Max(1e-12, y[1])));
        }
        return list;
    }

    /// <summary>Physical length (cm) and wet weight (g) of a state.</summary>
    public static (double LengthCm, double WeightG) Size(DebParameters p, DebState s) =>
        (s.StructuralLength / p.ShapeCoefficient, s.StructureCm3 * p.WetDensity + s.ReserveJ * p.ReserveWeightPerJ);

    /// <summary>von Bertalanffy growth rate r_B (1/d) at constant f and the reference temperature.</summary>
    public static double VonBertalanffyRate(DebParameters p, double f) => p.KM * p.G / (3 * (f + p.G));
}

/// <summary>Moult stage of a crustacean (Drach): A–B post-moult (soft, not feeding much), C intermoult, D pre-moult.</summary>
public enum MoultStage { A, B, C, D0, D1, D2, D3 }

/// <summary>One day in a shrimp's moult calendar.</summary>
public sealed record MoultDay(int Day, double WeightG, MoultStage Stage, bool Ecdysis, double FeedFactor);

/// <summary>
/// Stage-specific moult cycle of penaeid shrimp. The intermoult period grows with size and shortens with temperature
/// (IP = a·W^b at 28 °C, Q10-corrected); weight increases stepwise at ecdysis while the smooth curve of a growth model is the population mean.
/// Feeding drops around ecdysis (stages D2–B), which is why farmers cut rations and add minerals when a pond moults in synchrony
/// (after water exchange or around the new and full moon).
/// </summary>
public sealed class ShrimpMoultModel
{
    /// <summary>IP (days) = A · W(g)^B at 28 °C. Defaults give ≈ 4.4 d at 1 g, ≈ 10.6 d at 10 g, ≈ 13.7 d at 20 g.</summary>
    public double A { get; init; } = 4.4;
    public double B { get; init; } = 0.38;
    public double Q10 { get; init; } = 2.5;
    /// <summary>Relative feed intake per stage (C = 1).</summary>
    public IReadOnlyDictionary<MoultStage, double> Feeding { get; init; } = new Dictionary<MoultStage, double>
    {
        [MoultStage.A] = 0.2, [MoultStage.B] = 0.6, [MoultStage.C] = 1.0, [MoultStage.D0] = 1.0, [MoultStage.D1] = 0.9, [MoultStage.D2] = 0.7, [MoultStage.D3] = 0.4,
    };

    public double IntermoultDays(double weightG, double tempC) => A * Math.Pow(Math.Max(0.01, weightG), B) * Math.Pow(Q10, (28 - tempC) / 10);

    /// <summary>Stage at a fraction (0–1) of the intermoult period (Drach staging proportions for penaeids).</summary>
    public static MoultStage StageAt(double phase) => phase switch
    {
        < 0.05 => MoultStage.A,
        < 0.15 => MoultStage.B,
        < 0.50 => MoultStage.C,
        < 0.65 => MoultStage.D0,
        < 0.80 => MoultStage.D1,
        < 0.92 => MoultStage.D2,
        _ => MoultStage.D3,
    };

    /// <summary>
    /// Turns a smooth individual growth curve (one weight per day, e.g. from <see cref="GrowthSimulator"/>) into a stepwise moult calendar:
    /// the weight stays constant between moults and jumps to the smooth curve at each ecdysis.
    /// </summary>
    public IReadOnlyList<MoultDay> Calendar(IReadOnlyList<double> smoothWeightG, IReadOnlyList<double> dailyTempC, double startPhase = 0)
    {
        var list = new List<MoultDay>(smoothWeightG.Count);
        double phase = startPhase, w = smoothWeightG[0];
        for (int d = 0; d < smoothWeightG.Count; d++)
        {
            double ip = IntermoultDays(w, dailyTempC[Math.Min(d, dailyTempC.Count - 1)]);
            phase += 1 / ip;
            bool moult = phase >= 1;
            if (moult) { phase -= 1; w = smoothWeightG[d]; }
            var stage = moult ? MoultStage.A : StageAt(phase);     // the day of ecdysis is stage A
            list.Add(new MoultDay(d, w, stage, moult, Feeding[stage]));
        }
        return list;
    }

    /// <summary>
    /// Share of a pond moulting on each day, for a population whose moult phases are spread with a von Mises-like concentration
    /// <paramref name="synchrony"/> (0 = uniform, asynchronous; 1 = everybody together) and a synchronising event on <paramref name="triggerDay"/>.
    /// Also returns the pond-level feed factor (mean over the population's stages).
    /// </summary>
    public IReadOnlyList<(int Day, double MoultingShare, double FeedFactor)> PondMoulting(int days, double weightG, double tempC, double synchrony, int triggerDay = 0, int individuals = 400, ulong seed = 3)
    {
        var rng = new DeterministicRandom(seed);
        var phases = new double[individuals];
        for (int i = 0; i < individuals; i++)
            phases[i] = rng.NextDouble() < synchrony ? (0.98 + 0.04 * rng.NextDouble()) % 1 : rng.NextDouble();
        var result = new List<(int, double, double)>(days);
        double ip = IntermoultDays(weightG, tempC);
        for (int d = 0; d < days; d++)
        {
            int moulting = 0; double feed = 0;
            for (int i = 0; i < individuals; i++)
            {
                double before = phases[i];
                if (d >= triggerDay) phases[i] += 1 / ip;
                if (phases[i] >= 1) { phases[i] -= 1; moulting++; }
                else if (before > phases[i]) moulting++;
                feed += Feeding[StageAt(phases[i])];
            }
            result.Add((d, moulting / (double)individuals, feed / individuals));
        }
        return result;
    }
}
