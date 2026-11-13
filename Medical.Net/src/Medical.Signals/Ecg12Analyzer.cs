using Medical.Core;
using Medical.Native;

namespace Medical.Signals;

/// <summary>Per-lead measurements on the median beat (mV, relative to the PR-segment baseline).</summary>
public sealed record LeadMeasurement(EcgLead Lead, double RMv, double SMv, double NetQrsMv, double StJMv, double StJ60Mv, double TPeakMv, double? TEndMs);

/// <summary>Patient context for sex- and age-specific criteria.</summary>
public sealed record EcgPatient(Gender Sex = Gender.Unknown, int? AgeYears = null);

public enum QtcCategory { Short, Normal, Borderline, Prolonged, MarkedlyProlonged }

/// <summary>How the end of the T wave is located on each median beat.</summary>
public enum TEndMethod
{
    /// <summary>Area-curve indicator (Zhang et al., IEEE TBME 2006) over a 96 ms window — robust to noise. Default.</summary>
    AreaCurve,
    /// <summary>Tangent method: steepest descending slope extrapolated to the PR baseline (classic manual technique).</summary>
    Tangent,
}

/// <summary>Fiducial points on the median beat, as sample indices into <see cref="Ecg12Analysis.MedianBeats"/>.</summary>
public sealed record Fiducials(int? POnset, int QrsOnset, int RPeak, int QrsOffset, int TEnd);

/// <summary>Intervals from <see cref="Ecg12Analyzer.MeasureIntervals"/> (any number of leads).</summary>
public sealed record EcgIntervals(double HeartRateBpm, double RrMs, double? PrMs, double QrsMs, double QtMs, double QtcBazettMs, double QtcFridericiaMs,
    int[] RPeaks, Fiducials Fiducials, float[][] MedianBeats)
{
    /// <summary>Per-lead T end (ms from QRS onset; null when the lead has no measurable T wave).</summary>
    public double?[] TEndMs { get; init; } = [];
    /// <summary>Per-lead T amplitude (mV, relative to the PR baseline).</summary>
    public double[] TPeakMv { get; init; } = [];
}

/// <summary>Result of <see cref="Ecg12Analyzer.Analyze"/>.</summary>
public sealed record Ecg12Analysis(
    double HeartRateBpm,
    double RrMs,
    double? PrMs,
    double QrsMs,
    double QtMs,
    double QtcBazettMs,
    double QtcFridericiaMs,
    double QtcFraminghamMs,
    double QtcHodgesMs,
    double QrsAxisDeg,
    Localized Axis,
    QtcCategory Qtc,
    IReadOnlyList<LeadMeasurement> Leads,
    IReadOnlyList<string> StemiTerritories,
    IReadOnlyList<EcgLead> StDepressionLeads,
    Rhythm Rhythm,
    Localized Interpretation,
    float[][] MedianBeats,
    Fiducials Fiducials,
    int[] RPeaks,
    double SampleRate)
{
    public bool MeetsStemiCriteria => StemiTerritories.Count > 0;
    public LeadMeasurement this[EcgLead lead] => Leads[(int)lead];
}

/// <summary>
/// Resting 12-lead ECG measurement and interpretation support.
/// <list type="bullet">
/// <item>R peaks: Pan-Tompkins (Rust kernel) on the spatial QRS magnitude of the eight independent leads.</item>
/// <item>Median beat per lead; QRS onset/offset from the spatial velocity (15 % of its QRS maximum); P onset and
/// T end by the tangent method (steepest slope intersecting the PR baseline); global QT = QRS onset → latest T end
/// among leads within 40 ms of the median T end.</item>
/// <item>QTc: Bazett, Fridericia, Framingham (Sagie 1992), Hodges. Categories use Fridericia (as in ICH E14 / FDA
/// practice, since Bazett is rate-biased) with the AHA/ACCF/HRS 2009 limits (Rautaharju et al.): prolonged ≥ 450 ms (men) /
/// ≥ 460 ms (women), ≥ 500 ms markedly prolonged; short ≤ 340 ms (ESC 2015 short-QT syndrome).</item>
/// <item>ST deviation at J and J+60 ms vs the PR segment. STEMI voltage criteria of the Fourth Universal Definition
/// of Myocardial Infarction (Thygesen et al., 2018): new ST elevation at J in two contiguous leads ≥ 1 mm, except
/// V2–V3 ≥ 2 mm (men ≥ 40 y), ≥ 2.5 mm (men &lt; 40 y), ≥ 1.5 mm (women); ST depression ≥ 0.5 mm in two contiguous leads.</item>
/// </list>
/// Measurements support — never replace — physician over-reading. "New" changes need a prior ECG for comparison.
/// </summary>
public static class Ecg12Analyzer
{
    private static readonly EcgLead[] Independent = [EcgLead.I, EcgLead.II, EcgLead.V1, EcgLead.V2, EcgLead.V3, EcgLead.V4, EcgLead.V5, EcgLead.V6];

    /// <summary>Anatomically contiguous lead pairs per territory (two-contiguous-lead rule).</summary>
    public static readonly IReadOnlyList<(string Territory, (EcgLead A, EcgLead B)[] Pairs)> Territories =
    [
        ("Inferior", [(EcgLead.II, EcgLead.III), (EcgLead.III, EcgLead.aVF), (EcgLead.II, EcgLead.aVF)]),
        ("High lateral", [(EcgLead.I, EcgLead.aVL)]),
        ("Septal", [(EcgLead.V1, EcgLead.V2)]),
        ("Anterior", [(EcgLead.V2, EcgLead.V3), (EcgLead.V3, EcgLead.V4)]),
        ("Lateral", [(EcgLead.V4, EcgLead.V5), (EcgLead.V5, EcgLead.V6)]),
    ];

    /// <summary>Intermediate results of the interval measurement (median-beat coordinates in samples).</summary>
    internal sealed record Core(int[] Peaks, double[] Rr, double RrMedian, float[][] Median, int R, int Onset, int Offset, double[] Baseline, double PAmplitudeMv,
        double?[] TEnd, double[] TPeakMv, double GlobalTEnd, double? POnset, int H);

    internal static Core Measure(float[][] leads, int[] independent, int[] pLeads, double fs, TEndMethod method = TEndMethod.AreaCurve)
    {
        int n = leads[0].Length, nl = leads.Length;

        // 1) R peaks on the spatial magnitude of baseline-corrected independent leads.
        var clean = independent.Select(l => Filters.RemoveBaseline(new Signal(leads[l], fs))).ToArray();
        var mag = new float[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            foreach (var c in clean) s += c[i] * (double)c[i];
            mag[i] = (float)Math.Sqrt(s);
        }
        // Clip artefacts so one spike cannot raise the detection threshold above every real QRS.
        var sortedMag = mag.Order().ToArray();
        float clip = sortedMag[(int)(0.99 * (sortedMag.Length - 1))];
        for (int i = 0; i < n; i++) if (mag[i] > clip) mag[i] = clip;
        var peaks = MedicalKernels.DetectRPeaks(mag, fs, 0.3);
        if (peaks.Length < 3) throw new InvalidOperationException("Fewer than three beats detected; cannot build a median beat.");
        var rr = peaks.Zip(peaks.Skip(1), (a, b) => (b - a) * 1000.0 / fs).ToArray();
        double rrMedian = Median(rr);

        // 2) Median beats (two passes: the second removes each beat's own PR-segment level, i.e. baseline wander).
        int pre = (int)Math.Round(Math.Min(0.40, 0.6 * rrMedian / 1000) * fs);
        int post = (int)Math.Round(Math.Max(0.45, Math.Min(0.75, 0.92 * rrMedian / 1000)) * fs);
        var usable = peaks.Where(p => p - pre >= 0 && p + post < n).ToArray();
        if (usable.Length < 3) throw new InvalidOperationException("Fewer than three complete beats; cannot build a median beat.");
        int len = pre + post;
        int r = pre;
        float[][] MedianOf(Func<int, int, float> offsetOf)
        {
            var med = new float[nl][];
            var buf = new float[usable.Length];
            for (int l = 0; l < nl; l++)
            {
                med[l] = new float[len];
                var x = leads[l];
                for (int t = 0; t < len; t++)
                {
                    for (int b = 0; b < usable.Length; b++) buf[b] = x[usable[b] - pre + t] - offsetOf(l, b);
                    Array.Sort(buf);
                    med[l][t] = usable.Length % 2 == 1 ? buf[usable.Length / 2] : (buf[usable.Length / 2 - 1] + buf[usable.Length / 2]) / 2;
                }
            }
            return med;
        }

        // 3) QRS onset / offset from the spatial velocity, searched outward from its steepest points either side of R
        //    (at the R peak itself the velocity can be near zero when few leads are available).
        (int Onset, int Offset) Qrs(float[][] med)
        {
            var vel = new double[len];
            for (int t = 1; t < len - 1; t++)
                foreach (int l in independent) vel[t] += Math.Abs(med[l][t + 1] - med[l][t - 1]) / 2;
            int win = (int)(0.06 * fs);
            int upPeak = r, downPeak = r;
            for (int t = Math.Max(1, r - win); t <= r; t++) if (vel[t] > vel[upPeak]) upPeak = t;
            for (int t = r; t < Math.Min(len - 1, r + win); t++) if (vel[t] > vel[downPeak]) downPeak = t;
            double vmax = Math.Max(vel[upPeak], vel[downPeak]);
            double thr = 0.15 * vmax;
            int hold = Math.Max(2, (int)(0.006 * fs));
            int on = upPeak, off = downPeak;
            for (int t = upPeak, below = 0; t > Math.Max(1, upPeak - (int)(0.10 * fs)); t--)
            {
                below = vel[t] < thr ? below + 1 : 0;
                on = t;
                if (below >= hold) { on = t + hold - 1; break; }
            }
            for (int t = downPeak, below = 0; t < Math.Min(len - 2, downPeak + (int)(0.12 * fs)); t++)
            {
                below = vel[t] < thr ? below + 1 : 0;
                off = t;
                if (below >= hold) { off = t - hold + 1; break; }
            }
            return (on, off);
        }

        var median = MedianOf((_, _) => 0f);
        var (onset0, _) = Qrs(median);
        int w0 = Math.Max(0, onset0 - (int)(0.030 * fs)), w1 = Math.Max(w0 + 1, onset0 - (int)(0.010 * fs));
        var level = new float[nl, usable.Length];
        for (int l = 0; l < nl; l++)
            for (int b = 0; b < usable.Length; b++)
            {
                double acc = 0;
                for (int t = w0; t < w1; t++) acc += leads[l][usable[b] - pre + t];
                level[l, b] = (float)(acc / (w1 - w0));
            }
        median = MedianOf((l, b) => level[l, b]);
        var (onset, offset) = Qrs(median);

        // 4) Baselines from the PR segment (or just before QRS onset).
        int bFrom = Math.Max(0, onset - (int)(0.030 * fs)), bTo = Math.Max(bFrom + 1, onset - (int)(0.010 * fs));
        var baseline = new double[nl];
        for (int l = 0; l < nl; l++) { double s = 0; for (int t = bFrom; t < bTo; t++) s += median[l][t]; baseline[l] = s / (bTo - bFrom); }

        // 5) T end per lead (area-curve indicator or tangent method), then a consensus across leads.
        int h = Math.Max(1, (int)Math.Round(0.010 * fs));                  // derivative half-span (10 ms)
        // Stop before the next beat's P wave (next R − 220 ms) so a P wave is never taken for the end of T.
        int tFrom = Math.Min(len - 2, offset + (int)(0.08 * fs));
        int tTo = Math.Max(tFrom + 2, Math.Min(len - h - 1, r + (int)((rrMedian / 1000 - 0.22) * fs)));
        int stRef = Math.Min(len - 1, offset + (int)(0.06 * fs));
        int areaW = Math.Max(4, (int)Math.Round(0.096 * fs));   // chosen on half of QTDB, see docs
        var tEnd = new double?[nl];
        var tPeakMv = new double[nl];
        var tStrength = new double[nl];
        for (int l = 0; l < nl; l++)
        {
            var m = median[l];
            // T peak = largest deviation from the line joining the ST level to the end-of-beat baseline, so that ST
            // elevation or depression neither hides the T wave nor turns the return to baseline into a "peak".
            double Ref(int t) => m[stRef] + (baseline[l] - m[stRef]) * (t - stRef) / (double)(tTo - stRef);
            int pk = tFrom; double best = 0;
            for (int t = tFrom; t < tTo; t++) { double d = Math.Abs(m[t] - Ref(t)); if (d > best) { best = d; pk = t; } }
            double sign = Math.Sign(m[pk] - Ref(pk));
            tPeakMv[l] = Math.Round(m[pk] - baseline[l], 3);
            // Strength must hold against both references, so baseline drift (which tilts the reference line) cannot make
            // a flat lead look like the strongest T wave.
            tStrength[l] = Math.Min(best, Math.Abs(m[pk] - baseline[l]));
            if (tStrength[l] < 0.05) continue;
            int last = Math.Min(tTo, pk + (int)(0.30 * fs));
            if (method == TEndMethod.AreaCurve)
            {
                // Zhang et al. 2006: A(k) = Σ_{j=k−W+1..k} (s(j) − s(k)); the T end maximises A on the descending limb.
                int bestK = -1; double bestA = double.NegativeInfinity;
                for (int k = pk + h; k < last; k++)
                {
                    double area = 0;
                    for (int j = Math.Max(pk, k - areaW + 1); j <= k; j++) area += (m[j] - m[k]) * sign;
                    if (area > bestA) { bestA = area; bestK = k; }
                }
                if (bestK > pk) tEnd[l] = bestK;
            }
            else
            {
                int steep = -1; double slope = 0;
                for (int t = pk + 1; t < last; t++)
                {
                    double d = (m[t + h] - m[t - h]) / (2.0 * h) * sign;  // descending limb → negative
                    if (d < slope) { slope = d; steep = t; }
                }
                if (steep < 0 || slope >= 0) continue;
                double end = steep + (baseline[l] - m[steep]) / (slope * sign);
                if (end > pk && end < len) tEnd[l] = end;
            }
        }
        // Consensus: the reference is the lead with the strongest T wave; the global T end is the latest T end among
        // leads with a T at least 35 % as strong that lie within 40 ms of the reference — so small, noisy or reciprocal
        // T waves can neither stretch nor shorten the QT.
        double globalTEnd = offset + 0.3 * fs;
        int refLead = -1;
        for (int l = 0; l < nl; l++) if (tEnd[l].HasValue && (refLead < 0 || tStrength[l] > tStrength[refLead])) refLead = l;
        if (refLead >= 0)
        {
            double reference = tEnd[refLead]!.Value, strongest = tStrength[refLead];
            globalTEnd = Enumerable.Range(0, nl)
                .Where(l => tEnd[l].HasValue && tStrength[l] >= 0.35 * strongest && Math.Abs(tEnd[l]!.Value - reference) <= 0.040 * fs)
                .Max(l => tEnd[l]!.Value);
        }

        // 6) P onset (tangent) on the lead with the largest P wave among the candidates.
        double? pOnset = null;
        double pAmp = 0;
        int prevTEnd = (int)Math.Round(globalTEnd - rrMedian / 1000 * fs);   // previous beat's T end in median coordinates
        int pFrom = Math.Max(Math.Max(h + 1, onset - (int)(0.30 * fs)), prevTEnd + (int)(0.02 * fs));
        int pTo = Math.Max(pFrom + 1, onset - (int)(0.04 * fs));
        foreach (int lead in pLeads.OrderByDescending(l => PAmp(median[l], baseline[l], pFrom, pTo)))
        {
            var m = median[lead]; double b0 = baseline[lead];
            int pk = pFrom; double best = 0;
            for (int t = pFrom; t < pTo; t++) { double d = Math.Abs(m[t] - b0); if (d > best) { best = d; pk = t; } }
            pAmp = best;
            if (best < 0.05) break;
            double sign = Math.Sign(m[pk] - b0);
            int steep = -1; double slope = 0;
            for (int t = Math.Max(h, pk - (int)(0.12 * fs)); t < pk; t++)
            {
                double d = (m[t + h] - m[t - h]) / (2.0 * h) * sign;
                if (d > slope) { slope = d; steep = t; }
            }
            if (steep > 0 && slope > 0)
            {
                double candidate = steep + (b0 - m[steep]) / (slope * sign);
                double pr = (onset - candidate) * 1000 / fs;
                if (pr is >= 80 and <= 400) pOnset = candidate;       // otherwise not a credible P wave
            }
            break;
        }

        return new Core(peaks, rr, rrMedian, median, r, onset, offset, baseline, pAmp, tEnd, tPeakMv, globalTEnd, pOnset, h);
    }

    /// <summary>
    /// Lead-agnostic interval measurement (Holter, 2- or 3-lead recordings): the same median-beat, spatial-velocity and
    /// tangent-method algorithms as <see cref="Analyze"/>, using every given lead.
    /// </summary>
    public static EcgIntervals MeasureIntervals(IReadOnlyList<float[]> leads, double sampleRate, TEndMethod method = TEndMethod.AreaCurve)
    {
        var arr = leads.ToArray();
        var all = Enumerable.Range(0, arr.Length).ToArray();
        var c = Measure(arr, all, all, sampleRate, method);
        double ms(double samples) => samples * 1000 / sampleRate;
        double qt = Math.Round(ms(c.GlobalTEnd - c.Onset)), rrS = c.RrMedian / 1000;
        return new EcgIntervals(Math.Round(60 / rrS, 1), Math.Round(c.RrMedian), c.POnset is { } p ? Math.Round(ms(c.Onset - p)) : null,
            Math.Round(ms(c.Offset - c.Onset)), qt, Math.Round(qt / Math.Sqrt(rrS)), Math.Round(qt / Math.Cbrt(rrS)), c.Peaks,
            new Fiducials(c.POnset is { } q ? (int)Math.Round(q) : null, c.Onset, c.R, c.Offset, (int)Math.Round(c.GlobalTEnd)), c.Median)
        {
            TEndMs = c.TEnd.Select(t => t is { } v ? Math.Round(ms(v - c.Onset)) : (double?)null).ToArray(),
            TPeakMv = c.TPeakMv,
        };
    }

    public static Ecg12Analysis Analyze(Ecg12 ecg, EcgPatient? patient = null, TEndMethod method = TEndMethod.AreaCurve)
    {
        patient ??= new EcgPatient();
        double fs = ecg.SampleRate;
        var c = Measure(ecg.Leads, Independent.Select(l => (int)l).ToArray(), [(int)EcgLead.II, (int)EcgLead.I, (int)EcgLead.V1], fs, method);
        var (peaks, rr, rrMedian, median, r, onset, offset, baseline) = (c.Peaks, c.Rr, c.RrMedian, c.Median, c.R, c.Onset, c.Offset, c.Baseline);
        var (tEnd, tPeakMv, globalTEnd, pOnset) = (c.TEnd, c.TPeakMv, c.GlobalTEnd, c.POnset);
        int len = median[0].Length;

        // 7) Intervals.
        double ms(double samples) => samples * 1000 / fs;
        double qrsMs = Math.Round(ms(offset - onset));
        double qtMs = Math.Round(ms(globalTEnd - onset));
        double? prMs = pOnset is { } po ? Math.Round(ms(onset - po)) : null;
        double rrS = rrMedian / 1000, hr = 60 / rrS;
        double bazett = Math.Round(qtMs / Math.Sqrt(rrS));
        double frid = Math.Round(qtMs / Math.Cbrt(rrS));
        double fram = Math.Round(qtMs + 154 * (1 - rrS));
        double hodges = Math.Round(qtMs + 1.75 * (hr - 60));

        // 8) Per-lead amplitudes and ST.
        int j = offset, j60 = Math.Min(len - 1, offset + (int)Math.Round(0.06 * fs));
        var leads = new List<LeadMeasurement>(12);
        for (int l = 0; l < 12; l++)
        {
            var m = median[l];
            double rMax = 0, sMin = 0;
            for (int t = onset; t <= offset; t++) { double d = m[t] - baseline[l]; rMax = Math.Max(rMax, d); sMin = Math.Min(sMin, d); }
            leads.Add(new LeadMeasurement((EcgLead)l, Math.Round(rMax, 3), Math.Round(sMin, 3), Math.Round(rMax + sMin, 3),
                Math.Round(m[j] - baseline[l], 3), Math.Round(m[j60] - baseline[l], 3), tPeakMv[l], tEnd[l] is { } te ? Math.Round(ms(te - onset)) : null));
        }
        double Area(EcgLead lead) { double a = 0; for (int t = onset; t <= offset; t++) a += median[(int)lead][t] - baseline[(int)lead]; return a; }
        double axis = Math.Round(Math.Atan2(Area(EcgLead.aVF), Area(EcgLead.I)) * 180 / Math.PI);     // mean QRS vector (net area)
        var axisText = axis switch
        {
            >= -30 and <= 90 => new Localized($"Normal axis ({axis:0}°)", $"Aksis normal ({axis:0}°)"),
            < -30 and >= -90 => new Localized($"Left axis deviation ({axis:0}°)", $"Deviasi aksis kiri ({axis:0}°)"),
            > 90 and <= 180 => new Localized($"Right axis deviation ({axis:0}°)", $"Deviasi aksis kanan ({axis:0}°)"),
            _ => new Localized($"Extreme axis ({axis:0}°)", $"Aksis ekstrem ({axis:0}°)"),
        };

        // 9) ST criteria (UDMI 2018).
        double V23Threshold() => patient.Sex == Gender.Female ? 0.15 : patient.AgeYears is < 40 ? 0.25 : 0.20;
        bool Elevated(LeadMeasurement m) => m.StJMv >= (m.Lead is EcgLead.V2 or EcgLead.V3 ? V23Threshold() : 0.10);
        var stemi = new List<string>();
        foreach (var (name, pairs) in Territories)
            if (pairs.Any(p => Elevated(leads[(int)p.A]) && Elevated(leads[(int)p.B]))) stemi.Add(name);
        var depressed = leads.Where(m => m.StJMv <= -0.05 && m.Lead != EcgLead.aVR).Select(m => m.Lead).ToList();
        bool depression = Territories.Any(t => t.Pairs.Any(p => depressed.Contains(p.A) && depressed.Contains(p.B)));
        bool aVrElevation = leads[(int)EcgLead.aVR].StJMv >= 0.10;

        // 10) QTc category on Fridericia (Bazett over-corrects at high and under-corrects at low rates).
        var qtc = frid switch
        {
            >= 500 => QtcCategory.MarkedlyProlonged,
            _ when frid >= (patient.Sex == Gender.Female ? 460 : 450) => QtcCategory.Prolonged,
            _ when frid >= (patient.Sex == Gender.Female ? 450 : 440) => QtcCategory.Borderline,
            <= 340 => QtcCategory.Short,
            _ => QtcCategory.Normal,
        };

        double rrMean = rr.Average(), rrCv = Math.Sqrt(rr.Sum(v => (v - rrMean) * (v - rrMean)) / Math.Max(1, rr.Length - 1)) / rrMean;
        // Irregular RR without a convincing P wave (≥ 0.08 mV on the median beat; random f-waves leave a small residue) = AF.
        var rhythm = rrCv > 0.10 && (prMs is null || c.PAmplitudeMv < 0.08) ? Rhythm.AtrialFibrillation
            : EcgAnalyzer.Analyze(ecg.Signal(EcgLead.II)).Rhythm switch
            {
                Rhythm.AtrialFibrillation when prMs is not null => Rhythm.Ectopy,         // irregular but P waves present
                var r0 => r0,
            };
        if (rhythm == Rhythm.AtrialFibrillation) prMs = null;
        var en = new List<string>(); var id = new List<string>();
        en.Add($"{RhythmText(rhythm).En}, {hr:0} bpm"); id.Add($"{RhythmText(rhythm).Id}, {hr:0} bpm");
        en.Add(axisText.En); id.Add(axisText.Id);
        en.Add($"PR {(prMs is { } pr1 ? $"{pr1:0} ms" : "—")}, QRS {qrsMs:0} ms, QT/QTc {qtMs:0}/{bazett:0} ms (Fridericia {frid:0})");
        id.Add($"PR {(prMs is { } pr2 ? $"{pr2:0} md" : "—")}, QRS {qrsMs:0} md, QT/QTc {qtMs:0}/{bazett:0} md (Fridericia {frid:0})");
        if (prMs > 200) { en.Add("first-degree AV block"); id.Add("blok AV derajat satu"); }
        if (qrsMs >= 120) { en.Add("wide QRS — consider bundle-branch block"); id.Add("QRS lebar — pertimbangkan blok cabang berkas"); }
        if (qtc >= QtcCategory.Prolonged) { en.Add(qtc == QtcCategory.MarkedlyProlonged ? "markedly prolonged QTc (≥ 500 ms): torsades risk — review QT-prolonging drugs and electrolytes" : "prolonged QTc"); id.Add(qtc == QtcCategory.MarkedlyProlonged ? "QTc sangat memanjang (≥ 500 md): risiko torsades — tinjau obat pemanjang QT dan elektrolit" : "QTc memanjang"); }
        if (stemi.Count > 0)
        {
            var leadsUp = leads.Where(Elevated).Select(m => m.Lead.ToString());
            en.Add($"ST elevation meeting STEMI criteria ({string.Join(", ", stemi)}: {string.Join(", ", leadsUp)}) — activate the reperfusion pathway if new and clinically consistent");
            id.Add($"elevasi ST memenuhi kriteria STEMI ({string.Join(", ", stemi)}: {string.Join(", ", leadsUp)}) — aktifkan jalur reperfusi bila baru dan sesuai klinis");
        }
        if (depression && stemi.Count == 0)
        {
            en.Add($"ST depression in {string.Join(", ", depressed)}{(aVrElevation ? " with ST elevation in aVR (diffuse subendocardial ischaemia / left-main pattern)" : "")}");
            id.Add($"depresi ST di {string.Join(", ", depressed)}{(aVrElevation ? " dengan elevasi ST di aVR (iskemia subendokardial difus / pola left main)" : "")}");
        }
        else if (depression) { en.Add($"reciprocal ST depression in {string.Join(", ", depressed)}"); id.Add($"depresi ST resiprokal di {string.Join(", ", depressed)}"); }

        return new Ecg12Analysis(Math.Round(hr, 1), Math.Round(rrMedian), prMs, qrsMs, qtMs, bazett, frid, fram, hodges, axis, axisText, qtc, leads,
            stemi, depression ? depressed : [], rhythm, new Localized(string.Join("; ", en) + ".", string.Join("; ", id) + "."),
            median, new Fiducials(pOnset is { } p ? (int)Math.Round(p) : null, onset, r, offset, (int)Math.Round(globalTEnd)), peaks, fs);
    }

    private static Localized RhythmText(Rhythm r) => r switch
    {
        Rhythm.AtrialFibrillation => new("Atrial fibrillation", "Fibrilasi atrium"),
        Rhythm.SinusTachycardia => new("Sinus tachycardia", "Takikardia sinus"),
        Rhythm.SinusBradycardia => new("Sinus bradycardia", "Bradikardia sinus"),
        Rhythm.Ectopy => new("Sinus rhythm with premature beats", "Irama sinus dengan denyut prematur"),
        _ => new("Sinus rhythm", "Irama sinus"),
    };

    private static double PAmp(float[] m, double b, int from, int to)
    {
        double best = 0;
        for (int t = from; t < to; t++) best = Math.Max(best, Math.Abs(m[t] - b));
        return best;
    }

    private static double Median(double[] v)
    {
        var s = v.Order().ToArray();
        return s.Length == 0 ? double.NaN : s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}
