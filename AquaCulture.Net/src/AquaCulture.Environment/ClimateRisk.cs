using AquaCulture.Core;
using AquaCulture.Core.Farming;

namespace AquaCulture.Environment;

/// <summary>A climate hazard with likelihood, impact and adaptation.</summary>
public sealed record ClimateHazard(string Code, Text2 Name, double EventsPerYear, int Likelihood, int Impact, Text2 Adaptation)
{
    /// <summary>Risk score = likelihood × impact (1–25).</summary>
    public int Score => Likelihood * Impact;
    public Text2 Level => Score >= 15 ? new("Very high", "Sangat tinggi") : Score >= 10 ? new("High", "Tinggi") : Score >= 5 ? new("Medium", "Sedang") : new("Low", "Rendah");
}

public sealed record ClimateRiskReport(int Years, IReadOnlyList<ClimateHazard> Hazards)
{
    public ClimateHazard? Top => Hazards.Count == 0 ? null : Hazards[0];
}

/// <summary>
/// Climate risk analysis from daily weather: heatwaves, heavy/extreme rain, dry spells, storms and overcast spells,
/// scored as likelihood × impact with an impact matrix by culture system.
/// </summary>
public static class ClimateRiskAnalysis
{
    public static ClimateRiskReport Analyze(IReadOnlyList<DailyWeather> daily, CultureSystem system)
    {
        double years = Math.Max(1.0 / 12, daily.Count / 365.25);
        int heatwaves = CountRuns(daily, d => d.TmaxC >= 34, 3);
        int heavy = daily.Count(d => d.RainMm >= 50 && d.RainMm < 100);
        int extreme = daily.Count(d => d.RainMm >= 100);
        int dry = CountRuns(daily, d => d.RainMm < 1, 14);
        int storms = daily.Count(d => d.MaxWindMs >= 12);
        int overcast = CountRuns(daily, d => d.SolarMjM2 < 10, 3);

        int Lik(double perYear) => perYear >= 12 ? 5 : perYear >= 4 ? 4 : perYear >= 1 ? 3 : perYear >= 0.2 ? 2 : 1;
        bool cage = system == CultureSystem.FloatingNetCage, indoor = system is CultureSystem.Ras or CultureSystem.Hatchery;
        bool earthen = system is CultureSystem.EarthenPond;

        var list = new List<ClimateHazard>
        {
            new("heatwave", new("Heatwave (≥3 days ≥ 34 °C)", "Gelombang panas (≥3 hari ≥ 34 °C)"), heatwaves / years, Lik(heatwaves / years), indoor ? 2 : cage ? 3 : 4,
                new("Deeper water, shading nets, night aeration, heat-tolerant strains", "Air lebih dalam, paranet, aerasi malam, strain tahan panas")),
            new("heavy-rain", new("Heavy rain (50–100 mm/day)", "Hujan lebat (50–100 mm/hari)"), heavy / years, Lik(heavy / years), indoor ? 1 : earthen ? 4 : 3,
                new("Overflow outlets, dike liming, salinity/pH checks after rain", "Pintu limpas, pengapuran pematang, cek salinitas/pH setelah hujan")),
            new("flood", new("Extreme rain / flood (≥ 100 mm/day)", "Hujan ekstrem / banjir (≥ 100 mm/hari)"), extreme / years, Lik(extreme / years), indoor ? 2 : 5,
                new("Raise dikes, escape nets, emergency harvest plan, insurance", "Tinggikan pematang, jaring pengaman, rencana panen darurat, asuransi")),
            new("drought", new("Dry spell (≥ 14 days without rain)", "Kemarau (≥ 14 hari tanpa hujan)"), dry / years, Lik(dry / years), indoor ? 1 : cage ? 3 : 4,
                new("Reservoir ponds, water recycling, lower density in the dry season", "Kolam tandon, daur ulang air, kurangi padat tebar saat kemarau")),
            new("storm", new("Storm winds (≥ 12 m/s)", "Angin badai (≥ 12 m/s)"), storms / years, Lik(storms / years), cage ? 5 : indoor ? 1 : 2,
                new("Reinforced moorings and HDPE cages, early-warning alerts", "Tambat diperkuat dan KJA HDPE, peringatan dini")),
            new("overcast", new("Overcast spell (≥ 3 dull days)", "Mendung berkepanjangan (≥ 3 hari)"), overcast / years, Lik(overcast / years), indoor ? 1 : 3,
                new("Backup aeration and generator, reduce feed on dull days", "Aerasi dan genset cadangan, kurangi pakan saat mendung")),
        };
        return new ClimateRiskReport((int)Math.Ceiling(years), list.OrderByDescending(h => h.Score).ThenByDescending(h => h.EventsPerYear).ToList());
    }

    private static int CountRuns(IReadOnlyList<DailyWeather> d, Func<DailyWeather, bool> cond, int minLength)
    {
        int runs = 0, len = 0;
        foreach (var x in d)
        {
            if (cond(x)) len++;
            else { if (len >= minLength) runs++; len = 0; }
        }
        if (len >= minLength) runs++;
        return runs;
    }
}
