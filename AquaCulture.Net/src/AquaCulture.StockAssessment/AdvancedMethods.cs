using AquaCulture.Core.Data;

namespace AquaCulture.StockAssessment;

// ─────────────────────────────── ELEFAN ───────────────────────────────

/// <summary>One length-frequency sample: date (decimal year) and counts per length bin (bin midpoints shared by all samples).</summary>
public sealed record LfqSample(double DecimalYear, double[] Counts);

/// <summary>Length-frequency data set.</summary>
public sealed record LfqData(double[] BinMidsCm, IReadOnlyList<LfqSample> Samples)
{
    public double BinWidth => BinMidsCm.Length > 1 ? BinMidsCm[1] - BinMidsCm[0] : 1;
}

/// <summary>Best growth curve and the score surface.</summary>
public sealed record ElefanResult(double LInfinity, double K, double TAnchor, double Rn, double[] LInfGrid, double[] KGrid, double[,] Score)
{
    /// <summary>Growth performance index φ' = log₁₀K + 2 log₁₀L∞ (comparable across studies of a species).</summary>
    public double Phi => Math.Log10(K) + 2 * Math.Log10(LInfinity);
}

/// <summary>
/// ELEFAN (Pauly &amp; David 1981, as in TropFishR): length-frequency samples are restructured into peaks and troughs (counts over a running
/// mean); a von Bertalanffy growth curve and its yearly cohorts are scored by the peaks they cross (ESP) relative to the available peaks
/// (ASP), Rn = 10^(ESP/ASP)/10; a response-surface search over L∞ and K (each with the best anchor time) gives the growth parameters.
/// </summary>
public static class Elefan
{
    /// <summary>Restructured scores per sample and bin, and the available sum of peaks (ASP).</summary>
    public static (double[][] Scores, double Asp) Restructure(LfqData data, int movingAverage = 5)
    {
        var scores = new double[data.Samples.Count][];
        double asp = 0;
        int half = movingAverage / 2;
        for (int s = 0; s < data.Samples.Count; s++)
        {
            var c = data.Samples[s].Counts;
            int n = c.Length;
            var q = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0; int m = 0;
                for (int j = i - half; j <= i + half; j++) { sum += j >= 0 && j < n ? c[j] : 0; m++; }
                double ma = sum / m;
                q[i] = ma > 0 ? c[i] / ma : 0;
            }
            double mean = q.Where((_, i) => c[i] > 0).DefaultIfEmpty(1).Average();
            var r = q.Select((v, i) => c[i] > 0 ? v / mean - 1 : 0).ToArray();
            // balance positives and negatives (sum of positive = |sum of negative|)
            double pos = r.Where(v => v > 0).Sum(), neg = -r.Where(v => v < 0).Sum();
            if (pos > 0 && neg > 0) for (int i = 0; i < n; i++) if (r[i] < 0) r[i] *= pos / neg;
            scores[s] = r;
            // ASP: the highest point of each run of positive values
            double runMax = 0;
            for (int i = 0; i <= n; i++)
            {
                if (i < n && r[i] > 0) runMax = Math.Max(runMax, r[i]);
                else { asp += runMax; runMax = 0; }
            }
        }
        return (scores, asp);
    }

    /// <summary>Explained sum of peaks of a growth curve: each sample adds the score of the bin every cohort's length falls in (each peak once).</summary>
    public static double Esp(LfqData data, double[][] scores, double lInf, double k, double tAnchor)
    {
        double esp = 0, w = data.BinWidth, l0 = data.BinMidsCm[0] - w / 2;
        int n = data.BinMidsCm.Length;
        for (int s = 0; s < data.Samples.Count; s++)
        {
            double t = data.Samples[s].DecimalYear;
            var used = new HashSet<int>();
            for (int cohort = (int)Math.Floor(t - tAnchor) - 15; cohort <= (int)Math.Floor(t - tAnchor); cohort++)
            {
                double age = t - (tAnchor + cohort);
                if (age <= 0) continue;
                double len = lInf * (1 - Math.Exp(-k * age));
                int bin = (int)Math.Floor((len - l0) / w);
                if (bin < 0 || bin >= n || !used.Add(bin)) continue;
                // a peak counts once: move to the top of the run of positives it hit
                esp += scores[s][bin];
            }
        }
        return esp;
    }

    public static double Rn(double esp, double asp) => asp <= 0 ? 0 : Math.Pow(10, esp / asp) / 10;

    /// <summary>Response-surface search; the anchor time is optimised on a 0–1 grid for each (L∞, K).</summary>
    public static ElefanResult Fit(LfqData data, double[] lInfGrid, double[] kGrid, int anchorSteps = 40, int movingAverage = 5)
    {
        var (scores, asp) = Restructure(data, movingAverage);
        var grid = new double[lInfGrid.Length, kGrid.Length];
        double best = double.NegativeInfinity, bl = 0, bk = 0, bt = 0;
        for (int a = 0; a < lInfGrid.Length; a++)
            for (int b = 0; b < kGrid.Length; b++)
            {
                double rowBest = double.NegativeInfinity, rowT = 0;
                for (int s = 0; s < anchorSteps; s++)
                {
                    double ta = s / (double)anchorSteps;
                    double rn = Rn(Esp(data, scores, lInfGrid[a], kGrid[b], ta), asp);
                    if (rn > rowBest) { rowBest = rn; rowT = ta; }
                }
                grid[a, b] = rowBest;
                if (rowBest > best) { best = rowBest; bl = lInfGrid[a]; bk = kGrid[b]; bt = rowT; }
            }
        return new ElefanResult(bl, bk, bt, Math.Round(best, 4), lInfGrid, kGrid, grid);
    }

    /// <summary>Synthetic monthly LFQ from a known VBGF with one recruitment pulse a year, mortality Z and sampling noise.</summary>
    public static LfqData Synthetic(double lInf, double k, double tAnchor, double z = 1.2, int months = 24, double binCm = 1, double maxCm = 0, int perSample = 800, ulong seed = 7)
    {
        var rng = new DeterministicRandom(seed);
        double top = maxCm > 0 ? maxCm : lInf * 1.05;
        var mids = Enumerable.Range(0, (int)Math.Ceiling(top / binCm)).Select(i => binCm * (i + 0.5)).ToArray();
        var samples = new List<LfqSample>();
        for (int mth = 0; mth < months; mth++)
        {
            double t = 2025 + mth / 12.0;
            var counts = new double[mids.Length];
            for (int f = 0; f < perSample; f++)
            {
                // age from the exponential survival curve, recruitment spread ±0.06 yr around the anchor
                double age = -Math.Log(1 - rng.NextDouble() * (1 - Math.Exp(-z * 6))) / z;
                double birth = Math.Floor(t - age - tAnchor) + tAnchor + 0.06 * rng.NextGaussian();
                double a = t - birth;
                if (a <= 0.05) continue;
                double len = lInf * (1 - Math.Exp(-k * a)) * Math.Exp(0.03 * rng.NextGaussian());
                int bin = (int)(len / binCm);
                if (bin >= 0 && bin < mids.Length && len > 3 * binCm) counts[bin]++;
            }
            samples.Add(new LfqSample(t, counts));
        }
        return new LfqData(mids, samples);
    }
}

// ─────────────────────────────── CMSY ───────────────────────────────

/// <summary>Resilience categories of FishBase and their prior ranges of r.</summary>
public enum Resilience { VeryLow, Low, Medium, High }

/// <summary>CMSY estimates with 95 % ranges.</summary>
public sealed record CmsyResult(double R, double K, double Msy, (double Lo, double Hi) MsyRange, (double Lo, double Hi) RRange, (double Lo, double Hi) KRange,
    double FinalBOverBmsy, int ViablePairs, int Trials, (double Lo, double Hi) StartDepletion, (double Lo, double Hi) EndDepletion);

/// <summary>
/// CMSY — catch-only MSY (Froese et al. 2017, Fish and Fisheries 18: 506–526): prior ranges for r (from resilience) and for relative
/// biomass at the start and end of the series (from the catch trend), Monte-Carlo (r, k) pairs projected with the Schaefer model, and the
/// viable pairs that keep the stock alive and end inside the depletion prior. As in CMSY, the estimate is taken from the upper half of the
/// viable r values (the "tip of the triangle"), which reduces the bias towards low r.
/// </summary>
public static class Cmsy
{
    public static (double Lo, double Hi) RPrior(Resilience r) => r switch
    {
        Resilience.VeryLow => (0.015, 0.1),
        Resilience.Low => (0.05, 0.5),
        Resilience.Medium => (0.2, 0.8),
        _ => (0.6, 1.5),
    };

    /// <summary>Default depletion priors from the catch series (Froese et al. 2017, table 1).</summary>
    public static ((double, double) Start, (double, double) End) DepletionPriors(IReadOnlyList<double> catches)
    {
        double max = catches.Max();
        var start = catches[0] / max < 0.5 ? (0.5, 0.9) : (0.3, 0.6);
        var end = catches[^1] / max > 0.5 ? (0.3, 0.7) : (0.01, 0.4);
        return (start, end);
    }

    public static CmsyResult Fit(IReadOnlyList<double> catches, Resilience resilience, int trials = 30_000, ulong seed = 11,
        (double, double)? startDepletion = null, (double, double)? endDepletion = null)
    {
        var rng = new DeterministicRandom(seed);
        var (rLo, rHi) = RPrior(resilience);
        var (sd, ed) = DepletionPriors(catches);
        var start = startDepletion ?? sd; var end = endDepletion ?? ed;
        double maxC = catches.Max();
        double kLo = maxC / rHi, kHi = 12 * maxC / rLo;
        var viable = new List<(double R, double K, double Final)>();
        for (int i = 0; i < trials; i++)
        {
            double r = Math.Exp(rng.Uniform(Math.Log(rLo), Math.Log(rHi)));
            double k = Math.Exp(rng.Uniform(Math.Log(kLo), Math.Log(kHi)));
            double b = k * rng.Uniform(start.Item1, start.Item2);
            bool ok = true;
            foreach (var c in catches)
            {
                b = b + r * b * (1 - b / k) - c;
                if (b < 0.01 * k) { ok = false; break; }
            }
            double d = b / k;
            if (ok && d >= end.Item1 && d <= end.Item2) viable.Add((r, k, d));
        }
        if (viable.Count < 10) throw new InvalidOperationException("Too few viable r–k pairs: widen the priors or check the catch series.");
        double medianR = Median(viable.Select(v => v.R));
        var top = viable.Where(v => v.R >= medianR).ToList();
        double rEst = Median(top.Select(v => v.R));
        // K: geometric mean of the K values of the upper-r pairs near the chosen r
        var nearR = top.OrderBy(v => Math.Abs(Math.Log(v.R / rEst))).Take(Math.Max(10, top.Count / 4)).ToList();
        double kEst = Math.Exp(nearR.Average(v => Math.Log(v.K)));
        var msys = top.Select(v => v.R * v.K / 4).OrderBy(x => x).ToList();
        double final = Median(top.Select(v => v.Final)) * 2;                        // B/Bmsy = (B/K) / 0.5
        return new CmsyResult(Math.Round(rEst, 4), Math.Round(kEst), Math.Round(rEst * kEst / 4, 1), (Q(msys, 0.025), Q(msys, 0.975)),
            (top.Min(v => v.R), top.Max(v => v.R)), (top.Min(v => v.K), top.Max(v => v.K)), Math.Round(final, 3), viable.Count, trials, start, end);
    }

    private static double Median(IEnumerable<double> v) { var a = v.OrderBy(x => x).ToList(); return a[a.Count / 2]; }
    private static double Q(List<double> sorted, double q) => sorted[(int)Math.Clamp(Math.Round(q * (sorted.Count - 1)), 0, sorted.Count - 1)];
}

// ─────────────────────────────── Bayesian surplus production ───────────────────────────────

/// <summary>Posterior summaries of a Bayesian Schaefer model.</summary>
public sealed record BayesianSpResult(
    (double Median, double Lo, double Hi) R, (double Median, double Lo, double Hi) K, (double Median, double Lo, double Hi) Msy,
    (double Median, double Lo, double Hi) FinalBOverBmsy, (double Median, double Lo, double Hi) FinalFOverFmsy,
    double ProbabilityOverfished, double ProbabilityOverfishing, double AcceptanceRate, int Samples, IReadOnlyList<(double R, double K, double Msy)> Draws);

/// <summary>
/// Bayesian Schaefer surplus-production model fitted to catch and a CPUE index (observation-error, log-normal; catchability q and the
/// observation variance integrated analytically) by Metropolis–Hastings on (log r, log K, initial depletion). Priors: log-normal r from
/// resilience, log-uniform K, uniform initial depletion. Reports posterior medians and 95 % credible intervals, MSY, and Kobe
/// probabilities — the uncertainty that a single MLE fit (<see cref="SurplusProduction.FitDynamic"/>) hides.
/// </summary>
public static class BayesianSurplusProduction
{
    public static BayesianSpResult Fit(IReadOnlyList<CatchEffort> data, Resilience resilience = Resilience.Medium, int iterations = 30_000, int burnIn = 8_000,
        int thin = 5, ulong seed = 21, (double Lo, double Hi)? initialDepletion = null)
    {
        var catches = data.Select(d => d.Catch).ToArray();
        var cpue = data.Select(d => d.Cpue).ToArray();
        var (rLo, rHi) = Cmsy.RPrior(resilience);
        double rMu = (Math.Log(rLo) + Math.Log(rHi)) / 2, rSd = (Math.Log(rHi) - Math.Log(rLo)) / 4;
        double maxC = catches.Max(), kLo = Math.Log(maxC), kHi = Math.Log(100 * maxC);
        var (dLo, dHi) = initialDepletion ?? (0.5, 1.0);

        double LogPost(double lr, double lk, double d0)
        {
            if (lk < kLo || lk > kHi || d0 < dLo || d0 > dHi) return double.NegativeInfinity;
            double r = Math.Exp(lr), k = Math.Exp(lk), b = k * d0;
            var bio = new double[catches.Length];
            for (int t = 0; t < catches.Length; t++)
            {
                bio[t] = b;
                b = b + r * b * (1 - b / k) - catches[t];
                if (b <= 1e-6 * k) return double.NegativeInfinity;
            }
            // concentrated likelihood: log q̂ = mean(log I − log B); σ² integrated (inverse-gamma(ε, ε) → Student-like)
            int n = cpue.Length;
            double lq = 0; for (int t = 0; t < n; t++) lq += Math.Log(cpue[t]) - Math.Log(bio[t]);
            lq /= n;
            double ss = 0; for (int t = 0; t < n; t++) { double e = Math.Log(cpue[t]) - lq - Math.Log(bio[t]); ss += e * e; }
            double logLik = -(n - 1) / 2.0 * Math.Log(ss + 1e-12);
            double logPrior = -0.5 * Math.Pow((lr - rMu) / rSd, 2);
            return logLik + logPrior;
        }

        var rng = new DeterministicRandom(seed);
        double cr = rMu, ck = Math.Log(4 * maxC / Math.Exp(rMu) * 2), cd = (dLo + dHi) / 2;
        if (double.IsNegativeInfinity(LogPost(cr, ck, cd)))
            for (int i = 0; i < 2000 && double.IsNegativeInfinity(LogPost(cr, ck, cd)); i++) { ck = rng.Uniform(kLo, kHi); cr = rMu + rSd * rng.NextGaussian(); }
        double cur = LogPost(cr, ck, cd);
        double sr = 0.15, sk = 0.15, sdp = 0.05;
        int accepted = 0, windowAccepted = 0;
        var draws = new List<(double R, double K, double D0)>();
        for (int it = 0; it < iterations; it++)
        {
            double pr = cr + sr * rng.NextGaussian(), pk = ck + sk * rng.NextGaussian(), pd = cd + sdp * rng.NextGaussian();
            double lp = LogPost(pr, pk, pd);
            if (Math.Log(rng.NextDouble()) < lp - cur)
            {
                cr = pr; ck = pk; cd = pd; cur = lp;
                if (it >= burnIn) accepted++; else windowAccepted++;
            }
            // adapt the proposal during burn-in toward 20–35 % acceptance (r and K are strongly correlated, so steps end up small)
            if (it < burnIn && it % 200 == 199)
            {
                double rate = windowAccepted / 200.0, f = rate < 0.2 ? 0.7 : rate > 0.35 ? 1.3 : 1.0;
                sr *= f; sk *= f; sdp *= f; windowAccepted = 0;
            }
            if (it >= burnIn && (it - burnIn) % thin == 0) draws.Add((Math.Exp(cr), Math.Exp(ck), cd));
        }

        var msy = draws.Select(d => d.R * d.K / 4).ToList();
        var bb = new List<double>(); var ff = new List<double>();
        foreach (var d in draws)
        {
            double b = d.K * d.D0;
            for (int t = 0; t < catches.Length - 1; t++) b = b + d.R * b * (1 - b / d.K) - catches[t];
            double bmsy = d.K / 2, fmsy = d.R / 2;
            bb.Add(b / bmsy); ff.Add(catches[^1] / b / fmsy);
        }
        static (double, double, double) S(IEnumerable<double> v)
        {
            var a = v.OrderBy(x => x).ToList();
            return (Math.Round(a[a.Count / 2], 4), Math.Round(a[(int)(0.025 * (a.Count - 1))], 4), Math.Round(a[(int)(0.975 * (a.Count - 1))], 4));
        }
        return new BayesianSpResult(S(draws.Select(d => d.R)), S(draws.Select(d => d.K)), S(msy), S(bb), S(ff),
            Math.Round(bb.Count(x => x < 1) / (double)bb.Count, 3), Math.Round(ff.Count(x => x > 1) / (double)ff.Count, 3),
            Math.Round(accepted / (double)(iterations - burnIn), 3), draws.Count, draws.Select(d => (d.R, d.K, d.R * d.K / 4)).ToList());
    }

    /// <summary>A synthetic fishery from a known Schaefer stock (development then management), with log-normal CPUE noise.</summary>
    public static IReadOnlyList<CatchEffort> Synthetic(double r = 0.5, double k = 10_000, double q = 0.0004, int years = 30, double sigma = 0.1, ulong seed = 3)
    {
        var rng = new DeterministicRandom(seed);
        double b = k * 0.95;
        var list = new List<CatchEffort>();
        for (int y = 0; y < years; y++)
        {
            double fRel = y < years * 0.6 ? 0.2 + 1.6 * y / (years * 0.6) : 1.8 - 1.0 * (y - years * 0.6) / (years * 0.4);   // ramps up to 1.8 Fmsy, then down
            double f = fRel * r / 2;
            double effort = f / q;
            double c = f * b;
            list.Add(new CatchEffort(2000 + y, Math.Round(c, 2), Math.Round(effort / Math.Exp(sigma * rng.NextGaussian()), 2)));
            b = Math.Max(0.01 * k, b + r * b * (1 - b / k) - c);
        }
        return list;
    }
}
