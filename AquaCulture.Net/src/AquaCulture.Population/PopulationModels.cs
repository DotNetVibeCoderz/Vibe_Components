using AquaCulture.Core.Data;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Population;

/// <summary>Spawner–recruit relationship.</summary>
public abstract record RecruitmentModel
{
    public abstract double Recruits(double spawningBiomass);

    public sealed record BevertonHolt(double A, double B) : RecruitmentModel
    {
        public override double Recruits(double s) => A * s / (1 + B * s);
    }

    public sealed record Ricker(double A, double B) : RecruitmentModel
    {
        public override double Recruits(double s) => A * s * Math.Exp(-B * s);
    }

    public sealed record Constant(double R) : RecruitmentModel
    {
        public override double Recruits(double s) => R;
    }

    /// <summary>Beverton–Holt from steepness h, unfished recruitment R0 and unfished spawning biomass per recruit SPR0.</summary>
    public static BevertonHolt FromSteepness(double h, double r0, double spr0)
    {
        // R = 4hR0·S / (S0(1−h) + S(5h−1))  ⇒  A = 4hR0 / (S0(1−h)),  B = (5h−1) / (S0(1−h))
        double s0 = r0 * spr0;
        double a = 4 * h * r0 / (s0 * (1 - h));
        double b = (5 * h - 1) / (s0 * (1 - h));
        return new BevertonHolt(a, b);
    }
}

/// <summary>Harvest rule applied each time step.</summary>
public abstract record HarvestPolicy
{
    /// <summary>Returns the fishing mortality rate (per step) given exploitable biomass.</summary>
    public abstract double FishingMortality(double exploitableBiomass, int step);

    public sealed record None : HarvestPolicy { public override double FishingMortality(double b, int step) => 0; }

    public sealed record ConstantF(double F) : HarvestPolicy { public override double FishingMortality(double b, int step) => F; }

    /// <summary>Hockey-stick control rule: F ramps linearly from 0 at B_lim to F_target at B_trigger.</summary>
    public sealed record HockeyStick(double FTarget, double BTrigger, double BLim) : HarvestPolicy
    {
        public override double FishingMortality(double b, int step) =>
            b <= BLim ? 0 : b >= BTrigger ? FTarget : FTarget * (b - BLim) / (BTrigger - BLim);
    }
}

/// <summary>Age-structured population parameters (annual or seasonal steps).</summary>
public sealed record AgeStructuredParameters
{
    /// <summary>Weight (kg) at age.</summary>
    public required double[] WeightAtAge { get; init; }
    /// <summary>Proportion mature at age (0–1).</summary>
    public required double[] MaturityAtAge { get; init; }
    /// <summary>Fishery selectivity at age (0–1).</summary>
    public double[]? SelectivityAtAge { get; init; }
    /// <summary>Natural mortality per step.</summary>
    public double M { get; init; } = 0.3;
    public required RecruitmentModel Recruitment { get; init; }
    /// <summary>Lognormal recruitment variability (CV).</summary>
    public double RecruitmentCv { get; init; }
    /// <summary>Spawning seasonality multiplier per step (cycled), e.g. monsoon peaks.</summary>
    public double[]? SpawningSeason { get; init; }
    /// <summary>Immigrants per step entering age 1.</summary>
    public double Immigration { get; init; }
    /// <summary>Emigration rate per step (all ages).</summary>
    public double EmigrationRate { get; init; }
    /// <summary>Optional predator coupling (Holling type II on total prey biomass).</summary>
    public PredatorParameters? Predator { get; init; }
}

/// <summary>Predator with a Holling type-II functional response.</summary>
/// <param name="Attack">Attack rate a.</param>
/// <param name="HandlingTime">Handling time h.</param>
/// <param name="Conversion">Prey biomass to predator conversion efficiency.</param>
/// <param name="PredatorMortality">Predator death rate per step.</param>
/// <param name="InitialPredators">Starting predator abundance.</param>
public sealed record PredatorParameters(double Attack, double HandlingTime, double Conversion, double PredatorMortality, double InitialPredators);

/// <summary>One step of the population model.</summary>
public sealed record PopulationState(int Step, double Abundance, double Biomass, double SpawningBiomass, double Recruits, double Catch,
    double Predated, double Emigrants, double Immigrants, double Predators, double[] NumbersAtAge);

/// <summary>
/// Age-structured population dynamics with recruitment, spawning seasonality, natural mortality, fishing/harvest,
/// migration and predation. Deterministic for a given seed.
/// </summary>
public sealed class AgeStructuredModel(AgeStructuredParameters p)
{
    public AgeStructuredParameters Parameters { get; } = p;
    public int Ages => Parameters.WeightAtAge.Length;

    /// <summary>Unfished equilibrium numbers-at-age for a recruitment level.</summary>
    public double[] Equilibrium(double recruits)
    {
        var n = new double[Ages];
        n[0] = recruits;
        for (int a = 1; a < Ages; a++) n[a] = n[a - 1] * Math.Exp(-Parameters.M);
        n[^1] /= 1 - Math.Exp(-Parameters.M);
        return n;
    }

    public IReadOnlyList<PopulationState> Run(double[] initial, int steps, HarvestPolicy? policy = null, ulong seed = 1)
    {
        policy ??= new HarvestPolicy.None();
        var rng = new DeterministicRandom(seed);
        var p = Parameters;
        var sel = p.SelectivityAtAge ?? Enumerable.Repeat(1.0, Ages).ToArray();
        var n = (double[])initial.Clone();
        double predators = p.Predator?.InitialPredators ?? 0;
        var result = new List<PopulationState>(steps);
        for (int t = 0; t < steps; t++)
        {
            double ssb = 0, biomass = 0, exploitable = 0;
            for (int a = 0; a < Ages; a++) { ssb += n[a] * p.WeightAtAge[a] * p.MaturityAtAge[a]; biomass += n[a] * p.WeightAtAge[a]; exploitable += n[a] * p.WeightAtAge[a] * sel[a]; }

            double season = p.SpawningSeason is { Length: > 0 } ss ? ss[t % ss.Length] : 1;
            double rec = p.Recruitment.Recruits(ssb) * season;
            if (p.RecruitmentCv > 0) rec = rng.LogNormal(rec, p.RecruitmentCv);

            double f = policy.FishingMortality(exploitable, t);
            // predation removes biomass with a Holling II response, converted to an extra mortality rate on all ages
            double predated = 0, mPred = 0;
            if (p.Predator is { } pr && biomass > 0)
            {
                double consumption = pr.Attack * biomass / (1 + pr.Attack * pr.HandlingTime * biomass) * predators;
                predated = Math.Min(consumption, biomass * 0.9);
                mPred = -Math.Log(1 - predated / biomass);
                predators = Math.Max(0, predators + pr.Conversion * predated - pr.PredatorMortality * predators);
            }

            double catchB = 0, emig = 0;
            var next = new double[Ages];
            for (int a = 0; a < Ages; a++)
            {
                double fa = f * sel[a], z = p.M + fa + mPred + p.EmigrationRate;
                double dead = n[a] * (1 - Math.Exp(-z));
                catchB += z > 0 ? dead * fa / z * p.WeightAtAge[a] : 0;
                emig += z > 0 ? dead * p.EmigrationRate / z : 0;
                double surv = n[a] - dead;
                if (a + 1 < Ages) next[a + 1] += surv; else next[a] += surv;
            }
            next[0] = rec;
            if (Ages > 1) next[1] += p.Immigration;
            n = next;
            double abundance = n.Sum();
            result.Add(new PopulationState(t, abundance, n.Select((x, a) => x * p.WeightAtAge[a]).Sum(), ssb, rec, catchB, predated, emig, p.Immigration, predators, (double[])n.Clone()));
        }
        return result;
    }
}

/// <summary>Leslie matrix projection.</summary>
public sealed class LeslieMatrix
{
    private readonly double[,] _m;

    /// <param name="fecundity">Offspring (surviving to age 0 at the next step) per individual of each age.</param>
    /// <param name="survival">Survival from age i to i+1 (length = ages − 1).</param>
    public LeslieMatrix(double[] fecundity, double[] survival)
    {
        int n = fecundity.Length;
        if (survival.Length != n - 1) throw new ArgumentException("survival must have ages − 1 elements");
        _m = new double[n, n];
        for (int j = 0; j < n; j++) _m[0, j] = fecundity[j];
        for (int i = 1; i < n; i++) _m[i, i - 1] = survival[i - 1];
        Size = n;
    }

    public int Size { get; }

    public double[] Project(double[] n)
    {
        var r = new double[Size];
        for (int i = 0; i < Size; i++) for (int j = 0; j < Size; j++) r[i] += _m[i, j] * n[j];
        return r;
    }

    /// <summary>Dominant eigenvalue λ (population growth rate) and the stable age distribution, by power iteration.</summary>
    public (double Lambda, double[] StableAgeDistribution) Dominant(int maxIter = 10_000, double tol = 1e-12)
    {
        var v = Enumerable.Repeat(1.0 / Size, Size).ToArray();
        double lambda = 0;
        for (int it = 0; it < maxIter; it++)
        {
            var w = Project(v);
            double s = w.Sum();
            for (int i = 0; i < Size; i++) w[i] /= s;
            double diff = w.Zip(v, (a, b) => Math.Abs(a - b)).Sum();
            v = w; lambda = s;
            if (diff < tol) break;
        }
        return (lambda, v);
    }
}

/// <summary>Continuous predator–prey model with logistic prey and Holling type-II predation (Rosenzweig–MacArthur).</summary>
public static class PredatorPrey
{
    /// <summary>Integrates with RK4 and returns (time, prey, predator).</summary>
    public static IReadOnlyList<(double T, double Prey, double Predator)> Simulate(double prey0, double pred0, double r, double k, double a, double h,
        double e, double d, double tEnd, double dt = 0.05)
    {
        var y = new[] { prey0, pred0 };
        var list = new List<(double, double, double)> { (0, prey0, pred0) };
        double[] F(double t, double[] s)
        {
            double fr = a * s[0] / (1 + a * h * s[0]);
            return [r * s[0] * (1 - s[0] / k) - fr * s[1], e * fr * s[1] - d * s[1]];
        }
        for (double t = 0; t < tEnd - 1e-12; t += dt)
        {
            y = Numerics.Rk4(F, t, y, dt);
            y[0] = Math.Max(0, y[0]); y[1] = Math.Max(0, y[1]);
            list.Add((t + dt, y[0], y[1]));
        }
        return list;
    }
}

public static class PopulationBuilderExtensions
{
    public static AquaBuilder AddPopulation(this AquaBuilder b) => b.TryAddModule("Population", _ => { });
}
