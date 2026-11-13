namespace Medical.AI.Learning;

/// <summary>
/// Deterministic synthetic cohorts for demonstrating federated learning: three sites with different case mix whose
/// outcome follows the same logistic mechanism (lactate, age, systolic BP → 48-h deterioration).
/// </summary>
public static class SyntheticFederation
{
    public static readonly string[] FeatureNames = ["lactate", "age", "sbp"];

    /// <summary>A cohort; <paramref name="shift"/> moves lactate up, age up and blood pressure down (sicker population).</summary>
    public static List<(double[] X, bool Y)> Cohort(int n, int seed, double shift)
    {
        var r = new Random(seed);
        double G() => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
        var rows = new List<(double[], bool)>(n);
        for (int i = 0; i < n; i++)
        {
            double lac = Math.Max(0.3, 2 + shift + G()), age = Math.Clamp(55 + 10 * shift + 15 * G(), 18, 100), sbp = Math.Clamp(120 - 10 * shift + 18 * G(), 60, 220);
            double z = -1 + 1.3 * (lac - 2) + 0.04 * (age - 55) - 0.03 * (sbp - 120);
            rows.Add((new[] { Math.Round(lac, 2), Math.Round(age), Math.Round(sbp) }, r.NextDouble() < 1 / (1 + Math.Exp(-z))));
        }
        return rows;
    }

    /// <summary>Three sites and a held-out test cohort.</summary>
    public static (string[] Names, FederatedSite[] Sites, List<(double[] X, bool Y)> Test) Create() =>
        (FeatureNames,
         [new("RS Jakarta", Cohort(900, 1, 0.3)), new("RS Surabaya", Cohort(500, 2, -0.2)), new("Puskesmas Bandung", Cohort(250, 3, 0.0))],
         Cohort(2000, 99, 0.1));

    /// <summary>A site's rows — for simulation and baselines only; a real deployment never exposes them.</summary>
    public static IReadOnlyList<(double[] X, bool Y)> Rows(FederatedSite site) => site.Data;
}
