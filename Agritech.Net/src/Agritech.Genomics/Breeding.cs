using Agritech.Core.Data;
using Agritech.Core.Hosting;

namespace Agritech.Genomics;

/// <summary>Bi-allelic SNP marker.</summary>
public sealed record SnpMarker(string Id, string Chromosome, long Position, char Ref, char Alt);

/// <summary>
/// Genotype matrix coded as allele dosage (0 = hom ref, 1 = het, 2 = hom alt, -1 = missing). Rows = individuals, columns = markers.
/// </summary>
public sealed class GenotypeMatrix
{
    public GenotypeMatrix(IReadOnlyList<string> individuals, IReadOnlyList<SnpMarker> markers, sbyte[,] dosage)
    {
        if (dosage.GetLength(0) != individuals.Count || dosage.GetLength(1) != markers.Count) throw new ArgumentException("Dimension mismatch.");
        Individuals = individuals; Markers = markers; Dosage = dosage;
    }

    public IReadOnlyList<string> Individuals { get; }
    public IReadOnlyList<SnpMarker> Markers { get; }
    public sbyte[,] Dosage { get; }

    /// <summary>Alternate allele frequency per marker.</summary>
    public double AltFrequency(int m)
    {
        int sum = 0, n = 0;
        for (int i = 0; i < Individuals.Count; i++) if (Dosage[i, m] >= 0) { sum += Dosage[i, m]; n++; }
        return n == 0 ? double.NaN : sum / (2.0 * n);
    }

    public double MinorAlleleFrequency(int m) { double p = AltFrequency(m); return Math.Min(p, 1 - p); }

    public double ObservedHeterozygosity(int m)
    {
        int het = 0, n = 0;
        for (int i = 0; i < Individuals.Count; i++) if (Dosage[i, m] >= 0) { n++; if (Dosage[i, m] == 1) het++; }
        return n == 0 ? double.NaN : het / (double)n;
    }

    /// <summary>Hardy–Weinberg equilibrium χ² (1 df) for a marker.</summary>
    public double HardyWeinbergChiSquare(int m)
    {
        int aa = 0, ab = 0, bb = 0;
        for (int i = 0; i < Individuals.Count; i++)
            switch (Dosage[i, m]) { case 0: aa++; break; case 1: ab++; break; case 2: bb++; break; }
        int n = aa + ab + bb;
        if (n == 0) return 0;
        double p = (2 * aa + ab) / (2.0 * n), q = 1 - p;
        double[] exp = [p * p * n, 2 * p * q * n, q * q * n];
        int[] obs = [aa, ab, bb];
        return exp.Select((e, i) => e == 0 ? 0 : (obs[i] - e) * (obs[i] - e) / e).Sum();
    }

    /// <summary>Removes markers with MAF below threshold or missing rate above threshold (quality control).</summary>
    public GenotypeMatrix Filter(double minMaf = 0.05, double maxMissing = 0.2)
    {
        var keep = Enumerable.Range(0, Markers.Count).Where(m =>
        {
            int miss = 0;
            for (int i = 0; i < Individuals.Count; i++) if (Dosage[i, m] < 0) miss++;
            return MinorAlleleFrequency(m) >= minMaf && miss / (double)Individuals.Count <= maxMissing;
        }).ToArray();
        var d = new sbyte[Individuals.Count, keep.Length];
        for (int i = 0; i < Individuals.Count; i++) for (int k = 0; k < keep.Length; k++) d[i, k] = Dosage[i, keep[k]];
        return new GenotypeMatrix(Individuals, keep.Select(k => Markers[k]).ToList(), d);
    }

    /// <summary>Centered marker matrix Z (missing imputed to mean).</summary>
    internal double[][] Centered()
    {
        int n = Individuals.Count, p = Markers.Count;
        var z = new double[n][];
        var freq = Enumerable.Range(0, p).Select(AltFrequency).ToArray();
        for (int i = 0; i < n; i++)
        {
            z[i] = new double[p];
            for (int m = 0; m < p; m++) z[i][m] = (Dosage[i, m] < 0 ? 2 * freq[m] : Dosage[i, m]) - 2 * freq[m];
        }
        return z;
    }

    /// <summary>VanRaden (2008) genomic relationship matrix G = ZZ' / 2Σp(1−p).</summary>
    public double[,] GenomicRelationship()
    {
        var z = Centered();
        int n = z.Length;
        double denom = 2 * Enumerable.Range(0, Markers.Count).Select(AltFrequency).Sum(p => p * (1 - p));
        var g = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i; j < n; j++)
            {
                double s = 0;
                for (int m = 0; m < z[i].Length; m++) s += z[i][m] * z[j][m];
                g[i, j] = g[j, i] = s / denom;
            }
        return g;
    }
}

/// <summary>Pedigree entry (unknown parents = null).</summary>
public sealed record PedigreeEntry(string Id, string? Sire, string? Dam);

public sealed record CrossRecommendation(string Parent1, string Parent2, double ExpectedProgenyValue, double Relationship, double Score);

/// <summary>Plant breeding advisor: response to selection, genomic prediction (rrBLUP), inbreeding and cross planning.</summary>
public static class BreedingAdvisor
{
    /// <summary>Breeder's equation R = i · h² · σp (per cycle) and per year given cycle length.</summary>
    public static (double ResponsePerCycle, double ResponsePerYear) ResponseToSelection(double heritability, double phenotypicSd, double selectedFraction, double yearsPerCycle = 1)
    {
        double i = SelectionIntensity(selectedFraction);
        double r = i * heritability * phenotypicSd;
        return (Math.Round(r, 4), Math.Round(r / yearsPerCycle, 4));
    }

    /// <summary>Standardised selection intensity i = φ(z)/p for truncation selection of top fraction p.</summary>
    public static double SelectionIntensity(double p)
    {
        if (p <= 0 || p >= 1) throw new ArgumentOutOfRangeException(nameof(p));
        double z = InverseNormal(1 - p);
        double phi = Math.Exp(-z * z / 2) / Math.Sqrt(2 * Math.PI);
        return phi / p;
    }

    private static double InverseNormal(double p)
    {
        // Acklam's rational approximation
        double[] a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        double[] b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        double[] c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        double[] d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        double q, r;
        if (p < 0.02425) { q = Math.Sqrt(-2 * Math.Log(p)); return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        if (p > 1 - 0.02425) { q = Math.Sqrt(-2 * Math.Log(1 - p)); return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1); }
        q = p - 0.5; r = q * q;
        return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q / (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
    }

    /// <summary>
    /// Ridge-regression BLUP marker effects (rrBLUP): solves (Z'Z + λI)u = Z'(y − ȳ), λ = σe²/σu² ≈ p(1−h²)/h² scaled.
    /// Returns GEBVs for all individuals in <paramref name="geno"/>.
    /// </summary>
    public static (double[] Gebv, double[] MarkerEffects) GenomicPrediction(GenotypeMatrix training, double[] phenotypes, double heritability, GenotypeMatrix? geno = null)
    {
        var z = training.Centered();
        int n = z.Length, p = z[0].Length;
        double mean = Stats.Mean(phenotypes);
        double sumPq = 2 * Enumerable.Range(0, p).Select(training.AltFrequency).Sum(f => f * (1 - f));
        double lambda = sumPq * (1 - heritability) / Math.Max(heritability, 1e-3);
        var a = new Matrix(p, p);
        var rhs = new double[p];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < p; j++)
            {
                double zij = z[i][j];
                if (zij == 0) continue;
                rhs[j] += zij * (phenotypes[i] - mean);
                for (int k = 0; k < p; k++) a[j, k] += zij * z[i][k];
            }
        for (int j = 0; j < p; j++) a[j, j] += lambda;
        var u = Matrix.Solve(a, rhs);
        var target = (geno ?? training).Centered();
        var gebv = target.Select(row => { double s = 0; for (int j = 0; j < p; j++) s += row[j] * u[j]; return Math.Round(s, 4); }).ToArray();
        return (gebv, u);
    }

    /// <summary>Additive relationship matrix A (tabular method); pedigree must be ordered parents-before-offspring.</summary>
    public static (IReadOnlyList<string> Ids, double[,] A) RelationshipMatrix(IReadOnlyList<PedigreeEntry> pedigree)
    {
        var idx = pedigree.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        int n = pedigree.Count;
        var a = new double[n, n];
        for (int i = 0; i < n; i++)
        {
            int s = pedigree[i].Sire is { } si && idx.TryGetValue(si, out var sx) ? sx : -1;
            int d = pedigree[i].Dam is { } di && idx.TryGetValue(di, out var dx) ? dx : -1;
            if ((s >= i) || (d >= i)) throw new ArgumentException($"Pedigree not ordered: parent of '{pedigree[i].Id}' appears later.");
            for (int j = 0; j < i; j++)
            {
                double v = 0.5 * ((s >= 0 ? a[j, s] : 0) + (d >= 0 ? a[j, d] : 0));
                a[i, j] = a[j, i] = v;
            }
            a[i, i] = 1 + (s >= 0 && d >= 0 ? 0.5 * a[s, d] : 0);
        }
        return (pedigree.Select(p => p.Id).ToList(), a);
    }

    /// <summary>Wright's inbreeding coefficient F = A[i,i] − 1.</summary>
    public static IReadOnlyDictionary<string, double> InbreedingCoefficients(IReadOnlyList<PedigreeEntry> pedigree)
    {
        var (ids, a) = RelationshipMatrix(pedigree);
        return ids.Select((id, i) => (id, f: Math.Round(a[i, i] - 1, 4))).ToDictionary(x => x.id, x => x.f);
    }

    /// <summary>
    /// Ranks crosses by expected progeny value (mid-parent GEBV) penalised by genomic relationship (to manage inbreeding/diversity).
    /// </summary>
    public static IReadOnlyList<CrossRecommendation> PlanCrosses(IReadOnlyList<string> candidates, double[] gebv, double[,] relationship, int top = 10, double diversityWeight = 0.5)
    {
        var list = new List<CrossRecommendation>();
        double sd = Stats.StdDev(gebv);
        for (int i = 0; i < candidates.Count; i++)
            for (int j = i + 1; j < candidates.Count; j++)
            {
                double mid = (gebv[i] + gebv[j]) / 2;
                double rel = relationship[i, j];
                list.Add(new CrossRecommendation(candidates[i], candidates[j], Math.Round(mid, 4), Math.Round(rel, 4), Math.Round(mid - diversityWeight * sd * rel, 4)));
            }
        return list.OrderByDescending(c => c.Score).Take(top).ToList();
    }

    /// <summary>Synthetic breeding population (genotypes + phenotypes with known QTL) for demos/tests.</summary>
    public static (GenotypeMatrix Geno, double[] Phenotypes, double[] TrueBv) SyntheticPopulation(int individuals = 120, int markers = 200, int qtl = 15, double h2 = 0.5, int seed = 5)
    {
        var rng = new Random(seed);
        var mk = Enumerable.Range(0, markers).Select(m => new SnpMarker($"snp{m + 1}", $"chr{m * 12 / markers + 1}", 1000L * (m + 1), 'A', 'G')).ToList();
        var freqs = Enumerable.Range(0, markers).Select(_ => 0.1 + rng.NextDouble() * 0.8).ToArray();
        var d = new sbyte[individuals, markers];
        for (int i = 0; i < individuals; i++)
            for (int m = 0; m < markers; m++)
                d[i, m] = (sbyte)((rng.NextDouble() < freqs[m] ? 1 : 0) + (rng.NextDouble() < freqs[m] ? 1 : 0));
        var effects = new double[markers];
        foreach (var q in Enumerable.Range(0, markers).OrderBy(_ => rng.Next()).Take(qtl)) effects[q] = rng.NextGaussian(0, 1);
        var bv = Enumerable.Range(0, individuals).Select(i => Enumerable.Range(0, markers).Sum(m => d[i, m] * effects[m])).ToArray();
        double varG = Stats.Variance(bv);
        double sdE = Math.Sqrt(varG * (1 - h2) / h2);
        var y = bv.Select(g => Math.Round(10 + g + rng.NextGaussian(0, sdE), 3)).ToArray();
        return (new GenotypeMatrix(Enumerable.Range(1, individuals).Select(i => $"L{i:000}").ToList(), mk, d), y, bv);
    }
}

public static class GenomicsBuilderExtensions
{
    public static AgritechBuilder AddGenomics(this AgritechBuilder builder) { builder.TryAddModule("Genomics"); return builder; }
}
