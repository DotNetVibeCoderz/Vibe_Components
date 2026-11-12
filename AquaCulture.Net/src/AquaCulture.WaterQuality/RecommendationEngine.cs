using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AquaCulture.Core.Hosting;

namespace AquaCulture.WaterQuality;

public enum RecommendationCategory { Aeration, Feeding, WaterExchange, Chemical, Biological, Sludge, Monitoring, Routine }

/// <summary>An action with its reason, priority (1 = now) and an optional dose.</summary>
public sealed record Recommendation(int Priority, RecommendationCategory Category, Text2 Action, Text2 Rationale, double? Quantity = null, string? Unit = null);

/// <summary>Pond context that turns generic advice into doses.</summary>
public sealed record PondContext(double VolumeM3, double AreaM2, double BiomassKg = 0, double AerationKw = 0, CultureSystem System = CultureSystem.EarthenPond)
{
    public static PondContext From(Pond p, double biomassKg = 0) => new(p.VolumeM3, p.AreaM2, biomassKg, p.AerationKw, p.System);
}

/// <summary>
/// Turns a <see cref="WaterQualityAssessment"/> into prioritised, quantified actions: aeration sizing, feed cuts, water-exchange
/// fraction, bicarbonate/lime, salt for nitrite, molasses for biofloc, sludge removal and monitoring.
/// </summary>
public static class RecommendationEngine
{
    /// <summary>Aeration rule of thumb: kW per tonne of standing biomass (≈ 1 HP per 450 kg).</summary>
    public const double KwPerTonne = 1.65;

    public static IReadOnlyList<Recommendation> Recommend(WaterQualityAssessment a, PondContext pond)
    {
        var s = a.Sample; var sp = a.Species;
        var list = new List<Recommendation>();
        double v = pond.VolumeM3;

        if (s.DissolvedOxygen is { } dox)
        {
            if (dox < sp.DoCritical)
            {
                list.Add(new(1, RecommendationCategory.Aeration, new("Run all aerators now and add emergency aeration", "Nyalakan semua aerator sekarang dan tambah aerasi darurat"),
                    new($"DO {dox:0.0} mg/L is below the critical {sp.DoCritical} mg/L for {sp.Name.En}", $"DO {dox:0.0} mg/L di bawah batas kritis {sp.DoCritical} mg/L untuk {sp.Name.Id}")));
                list.Add(new(1, RecommendationCategory.Feeding, new("Stop feeding until DO recovers", "Hentikan pemberian pakan sampai DO pulih"),
                    new("Digestion raises oxygen demand by 30–50 %", "Pencernaan menaikkan kebutuhan oksigen 30–50 %")));
            }
            else if (dox < sp.DoOptimal)
            {
                list.Add(new(2, RecommendationCategory.Aeration, new("Increase aeration, especially 22:00–06:00", "Tambah aerasi, terutama pukul 22.00–06.00"),
                    new($"DO {dox:0.0} mg/L is under the growth optimum {sp.DoOptimal} mg/L", $"DO {dox:0.0} mg/L di bawah optimum pertumbuhan {sp.DoOptimal} mg/L")));
                list.Add(new(2, RecommendationCategory.Feeding, new("Reduce the next ration by 30 %", "Kurangi ransum berikutnya 30 %"),
                    new("Fish eat less and convert feed poorly at low DO", "Ikan makan lebih sedikit dan konversi pakan buruk saat DO rendah"), 30, "%"));
            }
            if (pond.BiomassKg > 0)
            {
                double needKw = pond.BiomassKg / 1000 * KwPerTonne;
                if (pond.AerationKw < needKw * 0.9)
                    list.Add(new(2, RecommendationCategory.Aeration, new($"Install about {needKw - pond.AerationKw:0.0} kW more aeration", $"Tambah sekitar {needKw - pond.AerationKw:0.0} kW aerasi"),
                        new($"{pond.BiomassKg:0} kg biomass needs ≈ {needKw:0.0} kW; installed {pond.AerationKw:0.0} kW", $"Biomassa {pond.BiomassKg:0} kg butuh ≈ {needKw:0.0} kW; terpasang {pond.AerationKw:0.0} kW"),
                        Math.Round(needKw - pond.AerationKw, 1), "kW"));
            }
        }

        if (a.UnionizedAmmonia is { } nh3 && nh3 > sp.Nh3Max && s.Ammonia is { } tan)
        {
            double targetTan = tan * sp.Nh3Max / nh3 * 0.8;
            double f = Math.Min(0.3, WaterChemistry.ExchangeFraction(tan, targetTan));
            list.Add(new(nh3 > 2 * sp.Nh3Max ? 1 : 2, RecommendationCategory.WaterExchange,
                new($"Exchange {f * 100:0} % of the water today (≈ {f * v:0} m³)", $"Ganti {f * 100:0} % air hari ini (≈ {f * v:0} m³)"),
                new($"Toxic NH₃ {nh3:0.000} mg/L exceeds {sp.Nh3Max} mg/L", $"NH₃ beracun {nh3:0.000} mg/L melebihi {sp.Nh3Max} mg/L"), Math.Round(f * v), "m³"));
            list.Add(new(2, RecommendationCategory.Feeding, new("Cut feed by 50 % for 1–2 days", "Kurangi pakan 50 % selama 1–2 hari"),
                new("Feed protein is the main ammonia source", "Protein pakan adalah sumber utama amonia"), 50, "%"));
            if (pond.System is CultureSystem.Biofloc or CultureSystem.LinedPond || sp.Group == SpeciesGroup.Shrimp)
            {
                double mol = WaterChemistry.MolassesForTanKg(tan - targetTan, v);
                list.Add(new(2, RecommendationCategory.Biological, new($"Add {mol:0.0} kg molasses (carbon source)", $"Tambahkan {mol:0.0} kg molase (sumber karbon)"),
                    new("Heterotrophic bacteria assimilate TAN at C:N ≈ 15–20", "Bakteri heterotrof menyerap TAN pada C:N ≈ 15–20"), Math.Round(mol, 1), "kg"));
            }
            if (s.Ph is > 8.5)
                list.Add(new(2, RecommendationCategory.Chemical, new("Avoid liming while pH is high", "Hindari pengapuran selama pH tinggi"),
                    new("High pH shifts ammonia to its toxic NH₃ form", "pH tinggi menggeser amonia ke bentuk NH₃ beracun")));
        }

        if (s.Ph is { } ph)
        {
            if (ph < sp.Ph.OptLow)
            {
                double target = sp.Alkalinity.OptLow + 20, cur = s.Alkalinity ?? Math.Max(10, sp.Alkalinity.OptLow - 40);
                double nahco3 = WaterChemistry.SodiumBicarbonateKg(target - cur, v), lime = WaterChemistry.AgriculturalLimeKg(target - cur, v);
                list.Add(new(2, RecommendationCategory.Chemical, new($"Raise pH/alkalinity: {nahco3:0} kg sodium bicarbonate (fast) or {lime:0} kg agricultural lime", $"Naikkan pH/alkalinitas: {nahco3:0} kg natrium bikarbonat (cepat) atau {lime:0} kg kapur pertanian"),
                    new($"pH {ph:0.0} is below {sp.Ph.OptLow}; buffering needs alkalinity ≥ {sp.Alkalinity.OptLow}", $"pH {ph:0.0} di bawah {sp.Ph.OptLow}; penyangga butuh alkalinitas ≥ {sp.Alkalinity.OptLow}"), Math.Round(nahco3), "kg"));
            }
            else if (ph > sp.Ph.OptHigh)
                list.Add(new(2, RecommendationCategory.WaterExchange, new("Exchange 10–20 % water and thin the plankton bloom", "Ganti 10–20 % air dan kurangi kepadatan plankton"),
                    new($"Afternoon pH {ph:0.0} above {sp.Ph.OptHigh} means a dense phytoplankton bloom", $"pH sore {ph:0.0} di atas {sp.Ph.OptHigh} menandakan blooming fitoplankton")));
        }

        if (s.Alkalinity is { } alkalinity && alkalinity < sp.Alkalinity.OptLow && !(s.Ph < sp.Ph.OptLow))
        {
            double kg = WaterChemistry.SodiumBicarbonateKg(sp.Alkalinity.OptLow - alkalinity + 10, v);
            list.Add(new(3, RecommendationCategory.Chemical, new($"Add {kg:0} kg sodium bicarbonate to stabilise pH", $"Tambahkan {kg:0} kg natrium bikarbonat untuk menstabilkan pH"),
                new($"Alkalinity {alkalinity:0} mg/L is below {sp.Alkalinity.OptLow} mg/L; pH will swing", $"Alkalinitas {alkalinity:0} mg/L di bawah {sp.Alkalinity.OptLow} mg/L; pH akan berfluktuasi"), Math.Round(kg), "kg"));
        }

        if (s.Nitrite is { } no2 && no2 > sp.NitriteMax)
        {
            if (sp.Group == SpeciesGroup.Fish)
            {
                double salt = WaterChemistry.SaltForNitriteKg(no2, WaterChemistry.ChlorideFromSalinity(s.SalinityPpt ?? 0), v);
                list.Add(new(2, RecommendationCategory.Chemical, new($"Add {salt:0} kg salt (NaCl) for a 20:1 chloride:nitrite ratio", $"Tambahkan {salt:0} kg garam (NaCl) untuk rasio klorida:nitrit 20:1"),
                    new($"Nitrite {no2:0.##} mg/L causes brown-blood disease; chloride blocks its uptake", $"Nitrit {no2:0.##} mg/L menyebabkan penyakit darah coklat; klorida menghambat penyerapannya"), Math.Round(salt), "kg"));
            }
            else
                list.Add(new(2, RecommendationCategory.Biological, new("Apply nitrifying probiotic and reduce feed 20 %", "Aplikasikan probiotik nitrifikasi dan kurangi pakan 20 %"),
                    new($"Nitrite {no2:0.##} mg/L above {sp.NitriteMax}", $"Nitrit {no2:0.##} mg/L di atas {sp.NitriteMax}")));
        }

        if (s.SalinityPpt is { } sal)
        {
            if (sal < sp.Salinity.OptLow && sp.Habitat != Habitat.Freshwater)
                list.Add(new(sal < sp.Salinity.Min ? 1 : 3, RecommendationCategory.WaterExchange, new("Add seawater or mineral salt and add Ca/Mg/K minerals", "Tambahkan air laut atau garam mineral serta mineral Ca/Mg/K"),
                    new($"Salinity {sal:0.#} ppt below {sp.Salinity.OptLow} ppt (often after heavy rain)", $"Salinitas {sal:0.#} ppt di bawah {sp.Salinity.OptLow} ppt (sering setelah hujan lebat)")));
            else if (sal > sp.Salinity.OptHigh)
            {
                double f = WaterChemistry.ExchangeFraction(sal, sp.Salinity.OptHigh, 0);
                list.Add(new(3, RecommendationCategory.WaterExchange, new($"Dilute with {f * v:0} m³ freshwater", $"Encerkan dengan {f * v:0} m³ air tawar"),
                    new($"Salinity {sal:0.#} ppt above {sp.Salinity.OptHigh} ppt", $"Salinitas {sal:0.#} ppt di atas {sp.Salinity.OptHigh} ppt"), Math.Round(f * v), "m³"));
            }
        }

        if (s.TemperatureC is { } t)
        {
            if (t > sp.Temperature.OptHigh)
                list.Add(new(t > sp.Temperature.Max - 1 ? 1 : 3, RecommendationCategory.Feeding, new("Feed in the cool hours and reduce ration 20 %; raise water level or shade", "Beri pakan saat sejuk dan kurangi ransum 20 %; naikkan air atau beri naungan"),
                    new($"Water {t:0.0} °C exceeds the optimum {sp.Temperature.OptHigh} °C; oxygen solubility drops", $"Air {t:0.0} °C melebihi optimum {sp.Temperature.OptHigh} °C; kelarutan oksigen turun")));
            else if (t < sp.Temperature.OptLow)
                list.Add(new(3, RecommendationCategory.Feeding, new("Reduce feeding; metabolism slows in cold water", "Kurangi pakan; metabolisme melambat di air dingin"),
                    new($"Water {t:0.0} °C is below {sp.Temperature.OptLow} °C", $"Air {t:0.0} °C di bawah {sp.Temperature.OptLow} °C")));
        }

        if (s.OrpMv is < 100 || s.Bod is > 15 || s.Cod is > 60)
            list.Add(new(2, RecommendationCategory.Sludge, new("Siphon bottom sludge and aerate near the bottom", "Sipon lumpur dasar dan beri aerasi dekat dasar"),
                new("Low ORP / high oxygen demand indicates organic sludge and possible H₂S", "ORP rendah / kebutuhan oksigen tinggi menandakan lumpur organik dan potensi H₂S")));
        if (s.TurbidityNtu is { } ntu && ntu > sp.TurbidityMax || s.Tss is > 100)
            list.Add(new(3, RecommendationCategory.Sludge, new("Let solids settle; check aerators are not scouring the dike", "Endapkan padatan; pastikan aerator tidak mengikis pematang"),
                new("High turbidity/TSS clogs gills and limits light", "Kekeruhan/TSS tinggi menyumbat insang dan membatasi cahaya")));
        if (s.Nitrate is > 100)
            list.Add(new(3, RecommendationCategory.WaterExchange, new("Increase daily exchange or add denitrification", "Tingkatkan pergantian harian atau tambah denitrifikasi"),
                new("Nitrate accumulates in RAS and biofloc", "Nitrat menumpuk di RAS dan bioflok")));
        if (s.Phosphate is > 1)
            list.Add(new(3, RecommendationCategory.Feeding, new("Use lower-phosphorus feed and avoid over-feeding", "Gunakan pakan rendah fosfor dan hindari pakan berlebih"),
                new("Phosphate above 1 mg/L fuels harmful algal blooms", "Fosfat di atas 1 mg/L memicu blooming alga berbahaya")));

        if (list.Count == 0)
            list.Add(new(3, RecommendationCategory.Routine, new("Water is within range: keep the routine and measure DO at dawn", "Air dalam batas aman: pertahankan rutinitas dan ukur DO saat subuh"),
                new($"WQI {a.Wqi:0} ({WaterQualityAssessment.ClassName(a.Class).En})", $"WQI {a.Wqi:0} ({WaterQualityAssessment.ClassName(a.Class).Id})")));
        if (s.DissolvedOxygen is null)
            list.Add(new(3, RecommendationCategory.Monitoring, new("Measure dissolved oxygen at dawn and dusk", "Ukur oksigen terlarut saat subuh dan senja"),
                new("DO is the parameter that kills fastest", "DO adalah parameter yang paling cepat mematikan")));
        return list.OrderBy(r => r.Priority).ToList();
    }
}

public static class WaterQualityBuilderExtensions
{
    /// <summary>Registers water-quality services (alert engine factory).</summary>
    public static AquaBuilder AddWaterQuality(this AquaBuilder b) => b.TryAddModule("WaterQuality", s =>
        s.TryAddSingleton<Func<SpeciesProfile, AlertEngine>>(sp => species => AlertEngine.ForSpecies(species, sp.GetService<IAquaEventBus>())));
}
