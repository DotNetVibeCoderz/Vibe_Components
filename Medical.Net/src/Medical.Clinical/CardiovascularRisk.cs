using Medical.Core;

namespace Medical.Clinical;

/// <summary>Race group of the Pooled Cohort Equations (the equations were derived for these two groups only).</summary>
public enum PceRace { WhiteOrOther, AfricanAmerican }

/// <summary>Inputs shared by the 10-year cardiovascular risk equations (cholesterol in mg/dL, blood pressure in mmHg).</summary>
public sealed record CvRiskInputs(int Age, Gender Sex, double TotalCholesterol, double Hdl, double SystolicBp, bool TreatedForHypertension, bool Smoker, bool Diabetes);

/// <summary>A 10-year risk estimate with its category and the equation that produced it.</summary>
public sealed record CvRiskResult(string Equation, double TenYearRiskPercent, RiskLevel Risk, Localized Category, IReadOnlyList<string> Warnings);

/// <summary>
/// 10-year cardiovascular risk equations. Coefficients were cross-checked against an independent implementation
/// (CVrisk, R) and the published worked examples.
/// </summary>
public static class CardiovascularRisk
{
    private sealed record Pce(double LnAge, double LnAge2, double LnTc, double LnAgeTc, double LnHdl, double LnAgeHdl, double LnTreated, double LnAgeTreated,
        double LnUntreated, double LnAgeUntreated, double Smoker, double LnAgeSmoker, double Diabetes, double Mean, double S10);

    // Goff et al., 2013 ACC/AHA Guideline on the Assessment of Cardiovascular Risk, Table A.
    private static readonly Pce WhiteFemale = new(-29.799, 4.884, 13.540, -3.114, -13.578, 3.149, 2.019, 0, 1.957, 0, 7.574, -1.665, 0.661, -29.18, 0.9665);
    private static readonly Pce AaFemale = new(17.114, 0, 0.940, 0, -18.920, 4.475, 29.291, -6.432, 27.820, -6.087, 0.691, 0, 0.874, 86.61, 0.9533);
    private static readonly Pce WhiteMale = new(12.344, 0, 11.853, -2.664, -7.990, 1.769, 1.797, 0, 1.764, 0, 7.837, -1.795, 0.658, 61.18, 0.9144);
    private static readonly Pce AaMale = new(2.469, 0, 0.302, 0, -0.307, 0, 1.916, 0, 1.809, 0, 0.549, 0, 0.645, 19.54, 0.8954);

    /// <summary>
    /// ACC/AHA Pooled Cohort Equations (Goff et al., Circulation 2014): 10-year risk of a first hard ASCVD event, for ages
    /// 40–79 without clinical ASCVD. Categories per the 2018 / 2019 guidelines: &lt; 5 % low, 5–7.4 % borderline,
    /// 7.5–19.9 % intermediate, ≥ 20 % high. The equations may over- or under-estimate risk in Asian populations.
    /// </summary>
    public static CvRiskResult Ascvd(CvRiskInputs x, PceRace race = PceRace.WhiteOrOther)
    {
        var warnings = Validate(x, 40, 79);
        var p = (x.Sex == Gender.Female, race) switch
        {
            (true, PceRace.AfricanAmerican) => AaFemale,
            (true, _) => WhiteFemale,
            (false, PceRace.AfricanAmerican) => AaMale,
            _ => WhiteMale,
        };
        double la = Math.Log(x.Age), ltc = Math.Log(x.TotalCholesterol), lh = Math.Log(x.Hdl), lsbp = Math.Log(x.SystolicBp);
        double s = p.LnAge * la + p.LnAge2 * la * la + p.LnTc * ltc + p.LnAgeTc * la * ltc + p.LnHdl * lh + p.LnAgeHdl * la * lh
                   + (x.TreatedForHypertension ? p.LnTreated * lsbp + p.LnAgeTreated * la * lsbp : p.LnUntreated * lsbp + p.LnAgeUntreated * la * lsbp)
                   + (x.Smoker ? p.Smoker + p.LnAgeSmoker * la : 0) + (x.Diabetes ? p.Diabetes : 0);
        double risk = 100 * (1 - Math.Pow(p.S10, Math.Exp(s - p.Mean)));
        if (race == PceRace.WhiteOrOther) warnings.Add("Uses the white equations for other groups; consider local recalibration (e.g. for Indonesian / Asian patients).");
        return Result("Pooled Cohort Equations (ACC/AHA 2013)", risk, warnings);
    }

    /// <summary>
    /// Framingham general cardiovascular disease 10-year risk (D'Agostino et al., Circulation 2008), ages 30–74: coronary,
    /// cerebrovascular, peripheral arterial disease and heart failure. Uses the same categories as <see cref="Ascvd"/> for display.
    /// </summary>
    public static CvRiskResult Framingham(CvRiskInputs x)
    {
        var warnings = Validate(x, 30, 74);
        bool f = x.Sex == Gender.Female;
        double s = (f ? 2.32888 : 3.06117) * Math.Log(x.Age)
                   + (f ? 1.20904 : 1.12370) * Math.Log(x.TotalCholesterol)
                   + (f ? -0.70833 : -0.93263) * Math.Log(x.Hdl)
                   + (x.TreatedForHypertension ? (f ? 2.82263 : 1.99881) : (f ? 2.76157 : 1.93303)) * Math.Log(x.SystolicBp)
                   + (x.Smoker ? (f ? 0.52873 : 0.65451) : 0) + (x.Diabetes ? (f ? 0.69154 : 0.57367) : 0);
        double risk = 100 * (1 - Math.Pow(f ? 0.95012 : 0.88936, Math.Exp(s - (f ? 26.1931 : 23.9802))));
        return Result("Framingham general CVD (2008)", risk, warnings);
    }

    private static List<string> Validate(CvRiskInputs x, int minAge, int maxAge)
    {
        if (x.Sex is not (Gender.Female or Gender.Male)) throw new ArgumentException("The equations are sex-specific (female or male).", nameof(x));
        Guard.Positive(x.TotalCholesterol, nameof(x.TotalCholesterol));
        Guard.Positive(x.Hdl, nameof(x.Hdl));
        Guard.Positive(x.SystolicBp, nameof(x.SystolicBp));
        var w = new List<string>();
        if (x.Age < minAge || x.Age > maxAge) w.Add($"Age {x.Age} is outside the validated range {minAge}–{maxAge}; the estimate is an extrapolation.");
        if (x.TotalCholesterol is < 130 or > 320) w.Add("Total cholesterol outside 130–320 mg/dL (derivation range).");
        if (x.Hdl is < 20 or > 100) w.Add("HDL outside 20–100 mg/dL (derivation range).");
        if (x.SystolicBp is < 90 or > 200) w.Add("Systolic BP outside 90–200 mmHg (derivation range).");
        return w;
    }

    private static CvRiskResult Result(string equation, double risk, List<string> warnings)
    {
        risk = Math.Round(Math.Clamp(risk, 0, 100), 1);
        var (level, text) = risk switch
        {
            < 5 => (RiskLevel.Low, new Localized("Low risk (< 5 %) — lifestyle advice.", "Risiko rendah (< 5 %) — anjuran gaya hidup.")),
            < 7.5 => (RiskLevel.LowMedium, new Localized("Borderline risk (5–7.4 %) — discuss statin if risk enhancers are present.", "Risiko ambang (5–7,4 %) — diskusikan statin bila ada faktor penambah risiko.")),
            < 20 => (RiskLevel.Medium, new Localized("Intermediate risk (7.5–19.9 %) — moderate-intensity statin after shared decision; consider coronary calcium if uncertain.",
                                                     "Risiko menengah (7,5–19,9 %) — statin intensitas sedang setelah diskusi bersama; pertimbangkan skor kalsium koroner bila ragu.")),
            _ => (RiskLevel.High, new Localized("High risk (≥ 20 %) — high-intensity statin.", "Risiko tinggi (≥ 20 %) — statin intensitas tinggi.")),
        };
        return new CvRiskResult(equation, risk, level, text, warnings);
    }
}
