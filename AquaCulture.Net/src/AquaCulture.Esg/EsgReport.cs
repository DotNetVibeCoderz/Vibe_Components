using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Esg;

/// <summary>
/// Emission and footprint factors. Values marked indicative depend on the supplier, the grid and the site: replace them with your
/// utility's factor and your feed mill's declared footprint.
/// </summary>
public sealed record EsgFactors
{
    /// <summary>Grid electricity (kg CO₂e per kWh). Indicative for the Java–Madura–Bali grid; ask PLN / KESDM for the current factor.</summary>
    public double GridKgCo2PerKwh { get; init; } = 0.85;
    /// <summary>Diesel combustion, kg CO₂ per litre (IPCC 2006 default).</summary>
    public double DieselKgCo2PerLitre { get; init; } = 2.68;
    /// <summary>Cradle-to-gate footprint of compound feed (kg CO₂e per kg). Indicative; feed mills publish their own.</summary>
    public double FeedKgCo2PerKg { get; init; } = 1.8;
    /// <summary>Above- and below-ground carbon stock of mangrove forest (Mg C per ha; Indo-Pacific mean, Donato et al. 2011).</summary>
    public double MangroveCarbonMgCPerHa { get; init; } = 1023;
    /// <summary>Land-use-change emissions are spread over this many years (PAS 2050 / LCA convention).</summary>
    public double LandUseAmortisationYears { get; init; } = 20;
    /// <summary>Sequestration of restored mangrove (t CO₂ per ha per year). Indicative.</summary>
    public double MangroveRestorationTCo2PerHaYear { get; init; } = 7;
    /// <summary>Nitrogen and phosphorus contents: feed N = protein / 6.25; body N and P of the harvested animal (wet weight).</summary>
    public double BodyNitrogen { get; init; } = 0.029;
    public double BodyPhosphorus { get; init; } = 0.003;
    public double FeedPhosphorus { get; init; } = 0.012;
}

/// <summary>Inputs that farm records do not hold, per cycle.</summary>
public sealed record CycleResources
{
    public double ElectricityKwh { get; init; }
    public double DieselLitres { get; init; }
    public double FeedProteinFraction { get; init; } = 0.35;
    /// <summary>Water pumped in (exchange and top-ups) over the cycle, m³.</summary>
    public double WaterIntakeM3 { get; init; }
    /// <summary>Pond area converted from mangrove (ha) and the farm age (years) for land-use-change accounting.</summary>
    public double ConvertedMangroveHa { get; init; }
    public double FarmAgeYears { get; init; } = 10;
    /// <summary>Mangrove restored or protected by the farm (ha), credited as sequestration over the cycle length.</summary>
    public double RestoredMangroveHa { get; init; }
}

/// <summary>Footprint of one cycle, totals and per kg harvested.</summary>
public sealed record EsgCycleReport(string CycleId, string PondId, double HarvestKg, int Days,
    double EnergyKwh, double EnergyKwhPerKg,
    double FeedCo2Kg, double ElectricityCo2Kg, double DieselCo2Kg, double LandUseCo2Kg, double RestorationCreditCo2Kg,
    double TotalCo2eKg, double Co2ePerKg,
    double WaterM3, double WaterM3PerKg,
    double NitrogenDischargeKg, double PhosphorusDischargeKg, double NitrogenPerTonne, double PhosphorusPerTonne,
    double Fcr, double FeedProteinPerKg)
{
    public IReadOnlyList<(Text2 Name, double KgCo2e)> Breakdown =>
    [
        (new("Feed", "Pakan"), FeedCo2Kg), (new("Electricity", "Listrik"), ElectricityCo2Kg), (new("Diesel", "Solar"), DieselCo2Kg),
        (new("Mangrove conversion", "Konversi mangrove"), LandUseCo2Kg), (new("Mangrove restoration", "Restorasi mangrove"), -RestorationCreditCo2Kg),
    ];
}

/// <summary>Farm-level ESG summary.</summary>
public sealed record EsgFarmReport(string FarmId, IReadOnlyList<EsgCycleReport> Cycles, double HarvestKg, double TotalCo2eKg, double Co2ePerKg,
    double EnergyKwhPerKg, double WaterM3PerKg, double NitrogenPerTonne, double PhosphorusPerTonne, IReadOnlyList<Text2> Findings);

/// <summary>
/// ESG accounting of aquaculture cycles: greenhouse gases (feed, electricity, diesel, mangrove conversion amortised over 20 years, minus
/// restoration), energy and water intensity, and nitrogen/phosphorus discharge by mass balance (feed N/P minus harvested N/P) — the
/// indicators that ASC, BAP and buyers' scorecards ask for. Results are per kg of harvest.
/// </summary>
public static class EsgAccounting
{
    public static EsgCycleReport Cycle(CultureCycle c, CycleResources r, EsgFactors? factors = null)
    {
        var f = factors ?? new EsgFactors();
        double harvest = c.HarvestedKg;
        if (harvest <= 0) throw new InvalidOperationException($"Cycle {c.Id} has no harvest.");
        double feed = c.TotalFeedKg;
        var end = c.Harvests.Count == 0 ? c.StockingDate : c.Harvests.Max(h => h.Date);
        int days = Math.Max(1, end.DayNumber - c.StockingDate.DayNumber);
        double feedCo2 = feed * f.FeedKgCo2PerKg;
        double elecCo2 = r.ElectricityKwh * f.GridKgCo2PerKwh;
        double dieselCo2 = r.DieselLitres * f.DieselKgCo2PerLitre;
        // land-use change: carbon stock × 44/12, spread over the amortisation period while the farm is younger than it, prorated to the cycle
        double luc = r.ConvertedMangroveHa > 0 && r.FarmAgeYears < f.LandUseAmortisationYears
            ? r.ConvertedMangroveHa * f.MangroveCarbonMgCPerHa * 44.0 / 12 * 1000 / f.LandUseAmortisationYears * days / 365.0
            : 0;
        double restoration = r.RestoredMangroveHa * f.MangroveRestorationTCo2PerHaYear * 1000 * days / 365.0;
        double total = feedCo2 + elecCo2 + dieselCo2 + luc - restoration;
        double feedN = feed * r.FeedProteinFraction / 6.25, feedP = feed * f.FeedPhosphorus;
        // stocked biomass carries N/P in; dead animals are part of the discharge
        double nOut = Math.Max(0, feedN + c.InitialBiomassKg * f.BodyNitrogen - harvest * f.BodyNitrogen);
        double pOut = Math.Max(0, feedP + c.InitialBiomassKg * f.BodyPhosphorus - harvest * f.BodyPhosphorus);
        return new EsgCycleReport(c.Id, c.PondId, Math.Round(harvest, 1), days,
            Math.Round(r.ElectricityKwh, 1), Math.Round(r.ElectricityKwh / harvest, 3),
            Math.Round(feedCo2, 1), Math.Round(elecCo2, 1), Math.Round(dieselCo2, 1), Math.Round(luc, 1), Math.Round(restoration, 1),
            Math.Round(total, 1), Math.Round(total / harvest, 3),
            Math.Round(r.WaterIntakeM3, 1), Math.Round(r.WaterIntakeM3 / harvest, 3),
            Math.Round(nOut, 2), Math.Round(pOut, 2), Math.Round(nOut / harvest * 1000, 2), Math.Round(pOut / harvest * 1000, 2),
            Math.Round(feed / harvest, 3), Math.Round(feed * r.FeedProteinFraction / harvest, 3));
    }

    /// <summary>Aggregates cycles (harvest-weighted intensities) and flags the indicators that most need attention.</summary>
    public static EsgFarmReport Farm(Farm farm, Func<CultureCycle, CycleResources> resources, EsgFactors? factors = null)
    {
        var cycles = farm.Cycles.Where(c => c.HarvestedKg > 0).Select(c => Cycle(c, resources(c), factors)).ToList();
        if (cycles.Count == 0) throw new InvalidOperationException("No harvested cycles.");
        double kg = cycles.Sum(c => c.HarvestKg), co2 = cycles.Sum(c => c.TotalCo2eKg);
        double energy = cycles.Sum(c => c.EnergyKwh) / kg, water = cycles.Sum(c => c.WaterM3) / kg;
        double n = cycles.Sum(c => c.NitrogenDischargeKg) / kg * 1000, p = cycles.Sum(c => c.PhosphorusDischargeKg) / kg * 1000;
        var findings = new List<Text2>();
        var top = cycles.SelectMany(c => c.Breakdown).GroupBy(b => b.Name.En).Select(g => (Name: g.First().Name, Kg: g.Sum(x => x.KgCo2e))).OrderByDescending(x => x.Kg).First();
        findings.Add(new($"Largest emission source: {top.Name.En} ({top.Kg / co2:P0} of the total).", $"Sumber emisi terbesar: {top.Name.Id} ({top.Kg / co2:P0} dari total)."));
        double fcr = cycles.Sum(c => c.Fcr * c.HarvestKg) / kg;
        if (fcr > 1.5) findings.Add(new($"FCR {fcr:0.00}: every 0.1 FCR saved cuts about {0.1 * new EsgFactors().FeedKgCo2PerKg:0.00} kg CO₂e per kg harvested.",
                                        $"FCR {fcr:0.00}: setiap penghematan 0,1 FCR memangkas sekitar {0.1 * new EsgFactors().FeedKgCo2PerKg:0.00} kg CO₂e per kg panen."));
        if (energy > 2) findings.Add(new($"Energy {energy:0.0} kWh/kg: DO-controlled aeration or variable-speed drives usually save 20–40 %.",
                                          $"Energi {energy:0.0} kWh/kg: aerasi berbasis DO atau inverter biasanya menghemat 20–40 %."));
        if (cycles.Any(c => c.LandUseCo2Kg > 0)) findings.Add(new("Mangrove conversion is counted for 20 years after clearing; restoration credits offset it.",
                                                                  "Konversi mangrove dihitung 20 tahun sejak pembukaan; restorasi mengimbanginya."));
        return new EsgFarmReport(farm.Id, cycles, Math.Round(kg, 1), Math.Round(co2, 1), Math.Round(co2 / kg, 3), Math.Round(energy, 3), Math.Round(water, 3),
            Math.Round(n, 2), Math.Round(p, 2), findings);
    }

    /// <summary>Water used by a pond over a cycle: daily exchange plus evaporation and seepage top-ups (m³).</summary>
    public static double PondWaterUse(double areaM2, double depthM, int days, double exchangePctPerDay, double evaporationMmPerDay = 5, double seepageMmPerDay = 3) =>
        areaM2 * depthM + days * (areaM2 * depthM * exchangePctPerDay / 100 + areaM2 * (evaporationMmPerDay + seepageMmPerDay) / 1000);
}

public static class EsgBuilderExtensions
{
    public static AquaBuilder AddEsg(this AquaBuilder b) => b.TryAddModule("Esg", _ => { });
}
