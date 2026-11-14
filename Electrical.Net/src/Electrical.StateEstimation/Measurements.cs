using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.StateEstimation;

/// <summary>Measured quantity.</summary>
public enum MeasurementType
{
    /// <summary>Bus voltage magnitude [pu].</summary>
    VoltageMagnitude,
    /// <summary>Net active power injection (generation − load) at a bus [MW].</summary>
    ActiveInjection,
    /// <summary>Net reactive power injection at a bus [Mvar].</summary>
    ReactiveInjection,
    /// <summary>Active power flow at one end of a branch, leaving the bus [MW].</summary>
    ActiveFlow,
    /// <summary>Reactive power flow at one end of a branch, leaving the bus [Mvar].</summary>
    ReactiveFlow,
    /// <summary>PMU voltage angle relative to the reference (slack) bus [degrees].</summary>
    VoltageAngle,
}

/// <summary>Branch terminal at which a flow is metered.</summary>
public enum BranchEnd { From, To }

/// <summary>
/// One telemetered value. <paramref name="Location"/> is a bus id for voltage/injection/angle measurements and a branch index
/// (position in <see cref="PowerNetwork.Branches"/>) for flows. Value and standard deviation share the unit of the type
/// (pu, MW, Mvar, degrees).
/// </summary>
public sealed record Measurement(MeasurementType Type, int Location, double Value, double Sigma, BranchEnd End = BranchEnd.From)
{
    public bool IsFlow => Type is MeasurementType.ActiveFlow or MeasurementType.ReactiveFlow;

    public override string ToString() => Type switch
    {
        MeasurementType.ActiveFlow or MeasurementType.ReactiveFlow => $"{Type}[branch {Location}, {End}] = {Value:0.####} ± {Sigma:0.####}",
        _ => $"{Type}[bus {Location}] = {Value:0.####} ± {Sigma:0.####}",
    };
}

/// <summary>Which measurements to synthesize and their standard deviations.</summary>
public sealed record MeasurementPlan
{
    public bool VoltageMagnitudes { get; init; } = true;
    public bool Injections { get; init; } = true;
    public bool Flows { get; init; } = true;
    /// <summary>Meter both ends of every branch (otherwise only the from end).</summary>
    public bool BothEnds { get; init; } = true;
    /// <summary>Bus ids equipped with PMUs (voltage magnitude + angle).</summary>
    public IReadOnlyList<int> PmuBuses { get; init; } = [];
    /// <summary>σ of voltage magnitudes [pu].</summary>
    public double SigmaVoltage { get; init; } = 0.004;
    /// <summary>σ of injections [pu on the system base] (0.01 pu = 1 MW on 100 MVA).</summary>
    public double SigmaInjectionPu { get; init; } = 0.01;
    /// <summary>σ of branch flows [pu on the system base].</summary>
    public double SigmaFlowPu { get; init; } = 0.008;
    /// <summary>σ of PMU angles [degrees].</summary>
    public double SigmaAngleDeg { get; init; } = 0.05;
    /// <summary>σ of PMU magnitudes [pu].</summary>
    public double SigmaPmuVoltage { get; init; } = 0.001;

    public static MeasurementPlan Full { get; } = new();
}

/// <summary>Synthesizes measurements from a power-flow solution with deterministic Gaussian noise (Box–Muller, seeded).</summary>
public static class MeasurementGenerator
{
    /// <param name="noise">Scale of the added noise (1 = σ as in the plan, 0 = exact values).</param>
    public static List<Measurement> FromPowerFlow(PowerFlowResult pf, MeasurementPlan? plan = null, int seed = 2024, double noise = 1.0)
    {
        plan ??= MeasurementPlan.Full;
        var rng = new Random(seed);
        double baseMva = pf.Network.BaseMva;
        var list = new List<Measurement>();
        void Add(MeasurementType t, int loc, double exact, double sigma, BranchEnd end = BranchEnd.From) =>
            list.Add(new Measurement(t, loc, exact + noise * sigma * Gaussian(rng), sigma, end));
        foreach (var b in pf.Buses)
        {
            if (b.Type == BusType.Isolated) continue;
            if (plan.VoltageMagnitudes) Add(MeasurementType.VoltageMagnitude, b.BusId, b.Vm, plan.SigmaVoltage);
            if (plan.Injections)
            {
                Add(MeasurementType.ActiveInjection, b.BusId, b.PGen - b.PLoad, plan.SigmaInjectionPu * baseMva);
                Add(MeasurementType.ReactiveInjection, b.BusId, b.QGen - b.QLoad, plan.SigmaInjectionPu * baseMva);
            }
        }
        if (plan.Flows)
            foreach (var br in pf.Branches)
            {
                if (!pf.Network.Branches[br.Index].InService) continue;
                Add(MeasurementType.ActiveFlow, br.Index, br.PFrom, plan.SigmaFlowPu * baseMva, BranchEnd.From);
                Add(MeasurementType.ReactiveFlow, br.Index, br.QFrom, plan.SigmaFlowPu * baseMva, BranchEnd.From);
                if (plan.BothEnds)
                {
                    Add(MeasurementType.ActiveFlow, br.Index, br.PTo, plan.SigmaFlowPu * baseMva, BranchEnd.To);
                    Add(MeasurementType.ReactiveFlow, br.Index, br.QTo, plan.SigmaFlowPu * baseMva, BranchEnd.To);
                }
            }
        double refAngle = pf.Network.SlackBus is { } s ? pf.Bus(s.Id).VaDeg : 0;
        foreach (int id in plan.PmuBuses)
        {
            var b = pf.Bus(id);
            Add(MeasurementType.VoltageAngle, id, b.VaDeg - refAngle, plan.SigmaAngleDeg);
            Add(MeasurementType.VoltageMagnitude, id, b.Vm, plan.SigmaPmuVoltage);
        }
        return list;
    }

    /// <summary>Standard normal deviate (Box–Muller).</summary>
    public static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}

/// <summary>Normal and chi-square distribution helpers.</summary>
public static class Distributions
{
    /// <summary>Standard normal quantile (Acklam's rational approximation, |error| &lt; 1.15·10⁻⁹).</summary>
    public static double NormalInverseCdf(double p)
    {
        if (p <= 0) return double.NegativeInfinity;
        if (p >= 1) return double.PositiveInfinity;
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        const double pl = 0.02425;
        if (p < pl)
        {
            double q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - pl)
        {
            double q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        double r = p - 0.5, s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r /
               (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }

    /// <summary>Chi-square CDF P(χ²_k ≤ x) via the regularized lower incomplete gamma function.</summary>
    public static double ChiSquareCdf(double x, int dof) => x <= 0 ? 0 : RegularizedGammaP(dof / 2.0, x / 2);

    /// <summary>
    /// Chi-square quantile: Wilson–Hilferty start k·(1 − 2/(9k) + z·√(2/(9k)))³, refined by Newton steps on the exact CDF.
    /// </summary>
    public static double ChiSquareInverseCdf(double p, int dof)
    {
        if (dof <= 0) throw new ArgumentOutOfRangeException(nameof(dof));
        double z = NormalInverseCdf(p);
        double k = dof, h = 2 / (9 * k);
        double x = Math.Max(1e-8, k * Math.Pow(1 - h + z * Math.Sqrt(h), 3));
        for (int it = 0; it < 50; it++)
        {
            double f = ChiSquareCdf(x, dof) - p;
            double pdf = Math.Exp((k / 2 - 1) * Math.Log(x) - x / 2 - k / 2 * Math.Log(2) - LogGamma(k / 2));
            if (pdf <= 0) break;
            double step = f / pdf;
            double nx = x - step;
            if (nx <= 0) nx = x / 2;
            if (Math.Abs(nx - x) < 1e-12 * Math.Max(1, x)) { x = nx; break; }
            x = nx;
        }
        return x;
    }

    /// <summary>Regularized lower incomplete gamma P(a, x) (series for x &lt; a + 1, Lentz continued fraction otherwise).</summary>
    public static double RegularizedGammaP(double a, double x)
    {
        if (x <= 0) return 0;
        double lg = LogGamma(a);
        if (x < a + 1)
        {
            double sum = 1 / a, del = sum, ap = a;
            for (int n = 0; n < 1000; n++) { ap++; del *= x / ap; sum += del; if (Math.Abs(del) < Math.Abs(sum) * 1e-16) break; }
            return sum * Math.Exp(-x + a * Math.Log(x) - lg);
        }
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, hh = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            double del = d * c;
            hh *= del;
            if (Math.Abs(del - 1) < 1e-16) break;
        }
        return 1 - Math.Exp(-x + a * Math.Log(x) - lg) * hh;
    }

    /// <summary>ln Γ(x) (Lanczos).</summary>
    public static double LogGamma(double x)
    {
        double[] cof = [76.18009172947146, -86.50532032941677, 24.01409824083091, -1.231739572450155, 0.1208650973866179e-2, -0.5395239384953e-5];
        double y = x, tmp = x + 5.5;
        tmp -= (x + 0.5) * Math.Log(tmp);
        double ser = 1.000000000190015;
        for (int j = 0; j < 6; j++) ser += cof[j] / ++y;
        return -tmp + Math.Log(2.5066282746310005 * ser / x);
    }
}
