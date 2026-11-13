using Medical.Core;

namespace Medical.Clinical;

public enum BmiStandard { Who, AsiaPacific }

public sealed record BmiResult(double Bmi, string Category, Localized Label);

public enum CkdStage { G1, G2, G3a, G3b, G4, G5 }

public sealed record EgfrResult(double Egfr, CkdStage Stage, Localized Label);

/// <summary>Anthropometric and renal-function calculators.</summary>
public static class Calculators
{
    /// <summary>Body-mass index with WHO or Asia-Pacific (WPRO 2000, used in Indonesia) categories.</summary>
    public static BmiResult Bmi(double weightKg, double heightCm, BmiStandard standard = BmiStandard.Who)
    {
        Guard.Positive(weightKg, nameof(weightKg)); Guard.Positive(heightCm, nameof(heightCm));
        double m = heightCm / 100, bmi = Math.Round(weightKg / (m * m), 1);
        (double over, double obese1, double obese2) = standard == BmiStandard.AsiaPacific ? (23.0, 25.0, 30.0) : (25.0, 30.0, 35.0);
        var (cat, label) = bmi switch
        {
            < 18.5 => ("Underweight", new Localized("Underweight", "Berat badan kurang")),
            _ when bmi < over => ("Normal", new Localized("Normal weight", "Berat badan normal")),
            _ when bmi < obese1 => ("Overweight", new Localized("Overweight", "Berat badan lebih")),
            _ when bmi < obese2 => ("Obese I", new Localized("Obesity class I", "Obesitas I")),
            _ => ("Obese II+", new Localized("Obesity class II or higher", "Obesitas II atau lebih")),
        };
        return new BmiResult(bmi, cat, label);
    }

    /// <summary>Body-surface area, Mosteller formula, m².</summary>
    public static double BsaMosteller(double weightKg, double heightCm) => Math.Round(Math.Sqrt(heightCm * weightKg / 3600), 2);

    /// <summary>Ideal body weight (Devine), kg.</summary>
    public static double IdealBodyWeight(double heightCm, Gender gender) =>
        Math.Round((gender == Gender.Female ? 45.5 : 50) + 2.3 * (heightCm / 2.54 - 60), 1);

    /// <summary>
    /// eGFR by the race-free CKD-EPI 2021 creatinine equation (Inker et al. NEJM 2021), mL/min/1.73 m².
    /// <paramref name="creatinineMgDl"/> in mg/dL (divide µmol/L by 88.4).
    /// </summary>
    public static EgfrResult EgfrCkdEpi2021(double creatinineMgDl, int age, Gender gender)
    {
        Guard.Positive(creatinineMgDl, nameof(creatinineMgDl));
        bool female = gender == Gender.Female;
        double k = female ? 0.7 : 0.9, a = female ? -0.241 : -0.302;
        double r = creatinineMgDl / k;
        double egfr = 142 * Math.Pow(Math.Min(r, 1), a) * Math.Pow(Math.Max(r, 1), -1.200) * Math.Pow(0.9938, age) * (female ? 1.012 : 1);
        egfr = Math.Round(egfr, 0);
        var stage = egfr switch { >= 90 => CkdStage.G1, >= 60 => CkdStage.G2, >= 45 => CkdStage.G3a, >= 30 => CkdStage.G3b, >= 15 => CkdStage.G4, _ => CkdStage.G5 };
        var label = stage switch
        {
            CkdStage.G1 => new Localized("Normal or high", "Normal atau tinggi"),
            CkdStage.G2 => new Localized("Mildly decreased", "Penurunan ringan"),
            CkdStage.G3a => new Localized("Mildly to moderately decreased", "Penurunan ringan-sedang"),
            CkdStage.G3b => new Localized("Moderately to severely decreased", "Penurunan sedang-berat"),
            CkdStage.G4 => new Localized("Severely decreased", "Penurunan berat"),
            _ => new Localized("Kidney failure", "Gagal ginjal"),
        };
        return new EgfrResult(egfr, stage, label);
    }

    /// <summary>Creatinine clearance by Cockcroft-Gault, mL/min (used for drug dosing).</summary>
    public static double CockcroftGault(double creatinineMgDl, int age, double weightKg, Gender gender) =>
        Math.Round((140 - age) * weightKg / (72 * creatinineMgDl) * (gender == Gender.Female ? 0.85 : 1), 1);

    /// <summary>Serum anion gap, mmol/L.</summary>
    public static double AnionGap(double sodium, double chloride, double bicarbonate) => sodium - (chloride + bicarbonate);

    /// <summary>Calcium corrected for albumin (Payne), mg/dL.</summary>
    public static double CorrectedCalcium(double calciumMgDl, double albuminGdl) => Math.Round(calciumMgDl + 0.8 * (4.0 - albuminGdl), 2);

    /// <summary>Maintenance IV fluid by Holliday-Segar (4-2-1 rule), mL/h.</summary>
    public static double MaintenanceFluidMlPerHour(double weightKg)
    {
        Guard.Positive(weightKg, nameof(weightKg));
        double first = Math.Min(weightKg, 10) * 4, second = Math.Clamp(weightKg - 10, 0, 10) * 2, rest = Math.Max(weightKg - 20, 0) * 1;
        return first + second + rest;
    }

    /// <summary>Mean arterial pressure, mmHg.</summary>
    public static double MeanArterialPressure(double systolic, double diastolic) => Math.Round((systolic + 2 * diastolic) / 3, 1);

    /// <summary>Shock index (HR / SBP); &gt; 0.9 suggests haemodynamic compromise.</summary>
    public static double ShockIndex(double heartRate, double systolicBp) => Math.Round(heartRate / systolicBp, 2);

    /// <summary>Estimated date of delivery by Naegele's rule from the last menstrual period.</summary>
    public static DateOnly DueDateNaegele(DateOnly lastMenstrualPeriod) => lastMenstrualPeriod.AddDays(280);

    /// <summary>Gestational age in weeks + days on <paramref name="on"/>.</summary>
    public static (int Weeks, int Days) GestationalAge(DateOnly lastMenstrualPeriod, DateOnly on)
    {
        int d = on.DayNumber - lastMenstrualPeriod.DayNumber;
        return (d / 7, d % 7);
    }
}
