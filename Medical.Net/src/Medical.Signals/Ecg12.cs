using Medical.Core;

namespace Medical.Signals;

/// <summary>The 12 standard leads, in the conventional order.</summary>
public enum EcgLead { I, II, III, aVR, aVL, aVF, V1, V2, V3, V4, V5, V6 }

/// <summary>A 12-lead resting ECG in millivolts.</summary>
public sealed record Ecg12(float[][] Leads, double SampleRate)
{
    public static readonly EcgLead[] Order = Enum.GetValues<EcgLead>();

    public float[] this[EcgLead lead] => Leads[(int)lead];
    public int Samples => Leads[0].Length;
    public double DurationSeconds => Samples / SampleRate;
    public Signal Signal(EcgLead lead) => new(this[lead], SampleRate);

    /// <summary>Recomputes III, aVR, aVL and aVF from I and II (Einthoven / Goldberger), e.g. after filtering.</summary>
    public static void DeriveLimbLeads(float[] i, float[] ii, float[] iii, float[] avr, float[] avl, float[] avf)
    {
        for (int k = 0; k < i.Length; k++)
        {
            iii[k] = ii[k] - i[k];
            avr[k] = -(i[k] + ii[k]) / 2;
            avl[k] = i[k] - ii[k] / 2;
            avf[k] = ii[k] - i[k] / 2;
        }
    }
}

/// <summary>ST-segment / repolarisation pattern for the 12-lead simulator.</summary>
public enum StPattern
{
    None,
    /// <summary>Inferior STEMI: ST elevation II, III, aVF with reciprocal depression in I and aVL.</summary>
    InferiorStemi,
    /// <summary>Anterior STEMI: ST elevation V1–V4.</summary>
    AnteriorStemi,
    /// <summary>Lateral STEMI: ST elevation I, aVL, V5, V6.</summary>
    LateralStemi,
    /// <summary>Diffuse subendocardial ischaemia: widespread ST depression with ST elevation in aVR.</summary>
    DiffuseDepression,
}

/// <summary>Simulator parameters (all optional).</summary>
public sealed record Ecg12Options
{
    public double Seconds { get; init; } = 10;
    public double HeartRate { get; init; } = 72;
    public Rhythm Rhythm { get; init; } = Rhythm.NormalSinus;
    public StPattern StPattern { get; init; } = StPattern.None;
    /// <summary>ST offset magnitude at the J point in mV (projected onto each lead).</summary>
    public double StMagnitudeMv { get; init; } = 0.3;
    /// <summary>Target heart-rate corrected QT (Fridericia), ms; the simulated QT follows QTc·RR^(1/3).</summary>
    public double QtcMs { get; init; } = 400;
    public double PrMs { get; init; } = 160;
    /// <summary>Frontal QRS axis in degrees (−30 … +90 is normal).</summary>
    public double QrsAxisDeg { get; init; } = 60;
    public double SampleRate { get; init; } = 500;
    public double NoiseMv { get; init; } = 0.01;
    public ulong Seed { get; init; } = 7;
}

/// <summary>Ground truth of a simulated beat (times relative to the R peak, seconds).</summary>
public sealed record Ecg12Truth(double PrMs, double QrsMs, double QtMs, double RrMs, double QrsAxisDeg, IReadOnlyDictionary<EcgLead, double> StAtJMv);

/// <summary>
/// 12-lead ECG from a vectorcardiographic dipole model: every wave is a Gaussian-in-time heart vector, each lead is its
/// projection on the lead axis (frontal hexaxial system for the limb leads, horizontal plane for V1–V6), and the
/// augmented / Einthoven leads are derived exactly as an electrocardiograph does. ST injury adds a current of injury
/// vector from the J point; QT follows the Fridericia relation so QTc stays constant across rates.
/// </summary>
public static class Ecg12Simulator
{
    // Heart-vector axes: x = patient left, y = inferior (feet), z = anterior.
    private static readonly (double X, double Y, double Z)[] LeadAxis = BuildLeadAxes();

    private static (double, double, double)[] BuildLeadAxes()
    {
        (double, double, double) Frontal(double deg) => (Math.Cos(deg * Math.PI / 180), Math.Sin(deg * Math.PI / 180), 0);
        (double, double, double) Chest(double deg, double inferior)
        {
            double x = Math.Cos(deg * Math.PI / 180), z = Math.Sin(deg * Math.PI / 180), l = Math.Sqrt(x * x + z * z + inferior * inferior);
            return (x / l, inferior / l, z / l);
        }
        return
        [
            Frontal(0), Frontal(60), Frontal(120), Frontal(-150), Frontal(-30), Frontal(90),
            Chest(115, 0.1), Chest(94, 0.1), Chest(75, 0.15), Chest(55, 0.2), Chest(30, 0.2), Chest(0, 0.2),
        ];
    }

    private readonly record struct Dipole(double Time, double Width, double Amplitude, double X, double Y, double Z);

    /// <summary>Generates a 12-lead ECG and the ground truth of its intervals.</summary>
    public static (Ecg12 Ecg, Ecg12Truth Truth) Generate(Ecg12Options? options = null)
    {
        var o = options ?? new Ecg12Options();
        var rng = new SeededRandom(o.Seed);
        double fs = o.SampleRate, baseRr = 60 / o.HeartRate;
        int n = (int)(o.Seconds * fs);
        var vx = new double[n]; var vy = new double[n]; var vz = new double[n];

        // Frontal rotation of the depolarisation vectors to the requested axis (model axis is +60°).
        double rot = (o.QrsAxisDeg - 60) * Math.PI / 180;
        (double X, double Y, double Z) Rot(double x, double y, double z) => (x * Math.Cos(rot) - y * Math.Sin(rot), x * Math.Sin(rot) + y * Math.Cos(rot), z);

        double qtS = o.QtcMs / 1000 * Math.Cbrt(baseRr);     // Fridericia
        // Wave boundaries use the tangent convention for Gaussian waves: onset = centre − 2σ, end = centre + 2σ.
        const double qrsOnset = -0.026 - 2 * 0.008, qrsOffset = 0.024 + 2 * 0.009;
        double tSigma = 0.045 + 0.1 * (qtS - 0.36);         // broader T for longer QT
        double tPeak = qrsOnset + qtS - 2 * tSigma;          // QT = QRS onset → T end
        const double pSigma = 0.022;
        double pCenter = qrsOnset - o.PrMs / 1000 + 2 * pSigma; // PR = QRS onset − P onset

        Dipole[] Beat(bool af)
        {
            var list = new List<Dipole>();
            var (qx, qy, qz) = Rot(-0.5, 0.15, 0.85);
            var (r1x, r1y, r1z) = Rot(0.55, 0.45, 0.70);
            var (r2x, r2y, r2z) = Rot(0.62, 0.72, -0.30);
            var (sx, sy, sz) = Rot(-0.35, -0.45, -0.82);
            if (!af) list.Add(new(pCenter, pSigma, 0.15, 0.5, 0.85, 0.15));      // P (axis ≈ +60°)
            list.Add(new(-0.026, 0.008, 0.20, qx, qy, qz));      // septal q
            list.Add(new(-0.010, 0.009, 0.75, r1x, r1y, r1z));   // early R (anterior)
            list.Add(new(0.000, 0.010, 1.25, r2x, r2y, r2z));    // main R (left, inferior, posterior)
            list.Add(new(0.024, 0.009, 0.45, sx, sy, sz));       // terminal S
            list.Add(new(tPeak, tSigma, 0.34, 0.62, 0.62, 0.42));              // T, concordant with mean QRS
            return [.. list];
        }

        // Injury current direction per pattern.
        (double X, double Y, double Z) st = o.StPattern switch
        {
            StPattern.InferiorStemi => (-0.25, 1, 0.05),
            StPattern.AnteriorStemi => (0.15, -0.05, 1),
            StPattern.LateralStemi => (1, -0.35, 0.15),
            StPattern.DiffuseDepression => (0.55, 0.7, 0.45),   // away from aVR: depression in most leads, elevation in aVR
            _ => (0, 0, 0),
        };
        double stLen = Math.Sqrt(st.X * st.X + st.Y * st.Y + st.Z * st.Z);
        double stSign = o.StPattern == StPattern.DiffuseDepression ? -1 : 1;
        if (stLen > 0) st = (stSign * st.X / stLen * o.StMagnitudeMv, stSign * st.Y / stLen * o.StMagnitudeMv, stSign * st.Z / stLen * o.StMagnitudeMv);

        double t = 0.35;
        int k = 0;
        while (t < o.Seconds + baseRr)
        {
            bool af = o.Rhythm == Rhythm.AtrialFibrillation;
            double rr = af ? baseRr * rng.Uniform(0.65, 1.35) : baseRr * (1 + rng.Gaussian(0, 0.01));
            foreach (var d in Beat(af))
            {
                int from = Math.Max(0, (int)((t + d.Time - 5 * d.Width) * fs)), to = Math.Min(n, (int)((t + d.Time + 5 * d.Width) * fs) + 1);
                for (int i = from; i < to; i++)
                {
                    double z = (i / fs - t - d.Time) / d.Width;
                    double g = d.Amplitude * Math.Exp(-0.5 * z * z);
                    vx[i] += g * d.X; vy[i] += g * d.Y; vz[i] += g * d.Z;
                }
            }
            if (stLen > 0)
            {
                                // The injury current spans the ST segment and fades along the T downslope, gone by T end (peak + 2σ).
                double j = t + qrsOffset, end = t + tPeak + tSigma;
                int from = Math.Max(0, (int)((j - 0.04) * fs)), to = Math.Min(n, (int)((end + 0.15) * fs));
                for (int i = from; i < to; i++)
                {
                    double ti = i / fs;
                    double w = 1 / (1 + Math.Exp(-(ti - j + 0.012) / 0.004)) * (1 / (1 + Math.Exp((ti - end) / (0.25 * tSigma))));
                    vx[i] += w * st.X; vy[i] += w * st.Y; vz[i] += w * st.Z;
                }
            }
            t += rr;
            k++;
        }

        var leads = new float[12][];
        for (int l = 0; l < 12; l++) leads[l] = new float[n];
        var wanderPhase = rng.Uniform(0, 6.28);
        for (int i = 0; i < n; i++)
        {
            double time = i / fs;
            double fib = o.Rhythm == Rhythm.AtrialFibrillation ? 0.05 * Math.Sin(2 * Math.PI * 6.3 * time) + 0.03 * Math.Sin(2 * Math.PI * 7.9 * time + 1.1) : 0;
            // Limb electrodes: I and II carry independent noise; the others are derived from them, as in a real device.
            foreach (int l in (ReadOnlySpan<int>)[0, 1, 6, 7, 8, 9, 10, 11])
            {
                var a = LeadAxis[l];
                double v = vx[i] * a.X + vy[i] * a.Y + vz[i] * a.Z;
                double wander = 0.04 * Math.Sin(2 * Math.PI * 0.2 * time + wanderPhase + l);
                leads[l][i] = (float)(v + wander + fib * (l == 1 || l == 6 ? 1 : 0.4) + rng.Gaussian(0, o.NoiseMv));
            }
        }
        Ecg12.DeriveLimbLeads(leads[0], leads[1], leads[2], leads[3], leads[4], leads[5]);

        var stAtJ = new Dictionary<EcgLead, double>();
        for (int l = 0; l < 12; l++)
        {
            var a = LeadAxis[l];
            stAtJ[(EcgLead)l] = Math.Round(st.X * a.X + st.Y * a.Y + st.Z * a.Z, 3);
        }
        // Derived leads' ST values follow from I and II.
        stAtJ[EcgLead.III] = Math.Round(stAtJ[EcgLead.II] - stAtJ[EcgLead.I], 3);
        stAtJ[EcgLead.aVR] = Math.Round(-(stAtJ[EcgLead.I] + stAtJ[EcgLead.II]) / 2, 3);
        stAtJ[EcgLead.aVL] = Math.Round(stAtJ[EcgLead.I] - stAtJ[EcgLead.II] / 2, 3);
        stAtJ[EcgLead.aVF] = Math.Round(stAtJ[EcgLead.II] - stAtJ[EcgLead.I] / 2, 3);
        // Mean QRS vector (area of each depolarisation dipole) → frontal axis.
        double mx = 0, my = 0;
        foreach (var d in Beat(false).Skip(1).Take(4)) { mx += d.Amplitude * d.Width * d.X; my += d.Amplitude * d.Width * d.Y; }
        double meanAxis = Math.Round(Math.Atan2(my, mx) * 180 / Math.PI);
        var truth = new Ecg12Truth(o.Rhythm == Rhythm.AtrialFibrillation ? double.NaN : o.PrMs, Math.Round((qrsOffset - qrsOnset) * 1000), Math.Round(qtS * 1000), baseRr * 1000, meanAxis, stAtJ);
        return (new Ecg12(leads, fs), truth);
    }
}
