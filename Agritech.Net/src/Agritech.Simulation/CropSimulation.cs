using Agritech.Crops;
using Agritech.Soil;
using Agritech.Weather;

namespace Agritech.Simulation;

public enum IrrigationStrategy { Rainfed, AutoAtRaw, FixedInterval }

/// <summary>Crop management for a simulated season.</summary>
public sealed record Management
{
    public required DateOnly PlantingDate { get; init; }
    public IrrigationStrategy Irrigation { get; init; } = IrrigationStrategy.Rainfed;
    public int IrrigationIntervalDays { get; init; } = 7;
    public double IrrigationDepthMm { get; init; } = 30;
    /// <summary>Nitrogen applications: (days after planting, kg N/ha).</summary>
    public IReadOnlyList<(int Dap, double KgN)> NitrogenApplications { get; init; } = [];
    public double InitialMineralNKgHa { get; init; } = 40;
    public double InitialWaterFraction { get; init; } = 0.7;
    /// <summary>SCS curve number for runoff (≈ 75 for row crops on loam).</summary>
    public double CurveNumber { get; init; } = 75;
    /// <summary>Atmospheric CO2 (ppm) for the CO2 fertilisation effect on RUE.</summary>
    public double Co2Ppm { get; init; } = 420;
}

/// <summary>Daily model state output.</summary>
public sealed record SimulationDay(
    DateOnly Date, int Dap, double CumGdd, double DevStage, double Lai, double BiomassKgHa, double RootDepthM,
    double SoilWaterMm, double WaterStress, double NitrogenStress, double MineralNKgHa, double CropNKgHa,
    double Transpiration, double SoilEvaporation, double Runoff, double Drainage, double Irrigation, double NLeached, double Rain);

public sealed record SimulationResult(
    CropDefinition Crop, Management Management, IReadOnlyList<SimulationDay> Days, double YieldTonHa, double BiomassTonHa,
    DateOnly? FloweringDate, DateOnly? MaturityDate, double TotalIrrigationMm, double TotalTranspirationMm, double NLeachedKgHa,
    double WaterProductivityKgM3, double MeanWaterStress, double MeanNitrogenStress);

/// <summary>
/// Daily process-based crop growth model inspired by APSIM and DSSAT-CERES:
/// thermal-time phenology, LAI expansion/senescence, Beer's-law light interception with RUE (CO2-adjusted),
/// a 5-layer tipping-bucket soil water balance (SCS runoff, drainage, root extraction), linear root front advance,
/// and a soil mineral-N pool with mineralisation, leaching and critical-N-dilution crop demand.
/// </summary>
public sealed class CropSimulator(CropDefinition crop, SoilSample soil, WeatherStation station)
{
    private const int Layers = 5;
    private const double LayerThicknessMm = 200; // 5 × 20 cm = 1 m

    public SimulationResult Run(Management mgmt, IReadOnlyList<DailyWeather> weather)
    {
        var hyd = soil.Hydraulics();
        double ll = hyd.WiltingPoint * LayerThicknessMm, dul = hyd.FieldCapacity * LayerThicknessMm, sat = hyd.Saturation * LayerThicknessMm;
        var sw = Enumerable.Repeat(ll + (dul - ll) * mgmt.InitialWaterFraction, Layers).ToArray();
        double minN = mgmt.InitialMineralNKgHa, cropN = 0, leachedTotal = 0;
        double cumGdd = 0, lai = 0.01, biomass = 0, root = 0.05, maxLaiReached = 0;
        double irrTotal = 0, transTotal = 0, wsSum = 0, nsSum = 0;
        int nDays = 0, lastIrr = -999;
        DateOnly? flowering = null, maturity = null;
        double co2Factor = 1 + 0.08 * Math.Log(mgmt.Co2Ppm / 350) / Math.Log(2) * (crop.Id == "maize" || crop.Id == "sugarcane" ? 0.25 : 1); // C4 less responsive
        double s = 25.4 * (1000 / mgmt.CurveNumber - 10);
        var days = new List<SimulationDay>();
        var nApps = mgmt.NitrogenApplications.ToDictionary(a => a.Dap, a => a.KgN);
        double floweringFrac = crop.Stages.FirstOrDefault(st => st.Code is "R1" or "FLW")?.StartFraction ?? 0.5;
        double stressAtFlowering = 1;

        foreach (var w in weather)
        {
            int dap = w.Date.DayNumber - mgmt.PlantingDate.DayNumber;
            if (dap < 0) continue;
            if (maturity is not null) break;

            // --- Phenology
            double gdd = crop.GrowingDegreeDays(w.TminC, w.TmaxC);
            cumGdd += gdd;
            double ds = Math.Min(1, cumGdd / crop.GddToMaturity);
            if (flowering is null && ds >= floweringFrac) flowering = w.Date;
            if (ds >= 1) maturity = w.Date;

            // --- Management inputs
            double irr = 0;
            double rootedTaw = 0, rootedDepl = 0;
            for (int l = 0; l < Layers; l++)
            {
                double frac = Math.Clamp((root * 1000 - l * LayerThicknessMm) / LayerThicknessMm, 0, 1);
                rootedTaw += (dul - ll) * frac; rootedDepl += (dul - sw[l]) * frac;
            }
            if (mgmt.Irrigation == IrrigationStrategy.AutoAtRaw && rootedDepl > crop.DepletionFraction * rootedTaw) irr = Math.Max(0, rootedDepl);
            if (mgmt.Irrigation == IrrigationStrategy.FixedInterval && dap - lastIrr >= mgmt.IrrigationIntervalDays) irr = mgmt.IrrigationDepthMm;
            if (irr > 0) lastIrr = dap;
            irrTotal += irr;
            if (nApps.TryGetValue(dap, out var nAdd)) minN += nAdd;

            // --- Runoff (SCS) and infiltration
            double p = w.RainMm;
            double runoff = p > 0.2 * s ? (p - 0.2 * s) * (p - 0.2 * s) / (p + 0.8 * s) : 0;
            double infil = p - runoff + irr;

            // --- Tipping-bucket redistribution
            double drainage = 0;
            for (int l = 0; l < Layers; l++)
            {
                sw[l] += infil;
                infil = 0;
                if (sw[l] > dul)
                {
                    double excess = Math.Min(sw[l] - dul, (sw[l] - dul) * 0.5 + Math.Max(0, sw[l] - sat));
                    sw[l] -= excess;
                    if (l + 1 < Layers) infil = excess; else drainage = excess;
                }
            }

            // --- Evapotranspiration
            double et0 = w.Et0Mm ?? Evapotranspiration.PenmanMonteith(w, station);
            double fi = 1 - Math.Exp(-crop.ExtinctionCoefficient * lai);
            double potT = Math.Max(0, crop.KcMid * et0 * fi);        // canopy transpiration ∝ intercepted light
            double potE = 1.1 * et0 * (1 - fi);                       // bare-soil evaporation under the canopy gaps
            double es = Math.Min(potE, Math.Max(0, sw[0] - ll * 0.5) * 0.3);
            sw[0] -= es;

            // root water uptake (proportional to available water in rooted layers)
            double avail = 0;
            for (int l = 0; l < Layers; l++)
            {
                double frac = Math.Clamp((root * 1000 - l * LayerThicknessMm) / LayerThicknessMm, 0, 1);
                avail += Math.Max(0, sw[l] - ll) * frac * 0.07; // ~7%/day max extraction rate (APSIM kl)
            }
            double transp = Math.Min(potT, avail);
            if (avail > 0)
                for (int l = 0; l < Layers; l++)
                {
                    double frac = Math.Clamp((root * 1000 - l * LayerThicknessMm) / LayerThicknessMm, 0, 1);
                    sw[l] -= transp * Math.Max(0, sw[l] - ll) * frac * 0.07 / avail;
                }
            transTotal += transp;
            double waterStress = potT <= 0.01 ? 1 : Math.Clamp(transp / potT, 0, 1);
            double expansionStress = Math.Clamp(waterStress * 1.4 - 0.4, 0, 1); // leaf expansion more sensitive

            // --- Nitrogen: mineralisation, leaching, uptake
            double tFactor = Math.Max(0, (w.TmeanC - 5) / 25);
            double mFactor = Math.Clamp((sw[0] - ll) / (dul - ll), 0, 1);
            minN += soil.OrganicCarbonPct * 0.35 * tFactor * mFactor; // kg N/ha/day from humus turnover
            double totalWater = sw.Sum();
            double leached = drainage > 0 ? minN * Math.Min(0.5, drainage / Math.Max(1, totalWater) * 1.5) : 0;
            minN -= leached; leachedTotal += leached;

            // --- Growth
            double par = 0.5 * (w.SolarRadMjM2 ?? Evapotranspiration.SolarRadiationFromTemperature(w.TminC, w.TmaxC, Evapotranspiration.ExtraterrestrialRadiation(station.LatitudeDeg, w.DayOfYear)));
            double tempEff = CropRecommender.Trapezoid(w.TmeanC, crop.AbsoluteTempMinC, crop.OptimalTempMinC, crop.OptimalTempMaxC, crop.AbsoluteTempMaxC);
            double wMt = biomass / 1000; // t/ha
            double critNPct = wMt < 1 ? (crop.Category == Agritech.Crops.CropCategory.Legume ? 5.0 : 4.4) : (crop.Category == Agritech.Crops.CropCategory.Legume ? 5.0 : 3.4) * Math.Pow(wMt, -0.37);
            double nConc = biomass > 0 ? cropN / biomass * 100 : critNPct;
            double nStress = crop.Category == Agritech.Crops.CropCategory.Legume ? 1 : Math.Clamp((nConc - 0.5 * critNPct) / (0.5 * critNPct), 0.2, 1);
            double dBiomass = crop.RueGPerMj * co2Factor * par * fi * tempEff * Math.Min(waterStress, nStress) * 10; // g/m2 → kg/ha
            if (ds >= 1) dBiomass = 0;
            biomass += dBiomass;
            double nDemand = Math.Max(0, biomass * critNPct / 100 - cropN);
            double nUptake = Math.Min(nDemand, Math.Max(0, minN) * 0.25 + 0.5);
            if (crop.Category == Agritech.Crops.CropCategory.Legume) nUptake = nDemand; // fixation covers the gap
            nUptake = Math.Min(nUptake, Math.Max(0, minN) + (crop.Category == Agritech.Crops.CropCategory.Legume ? nDemand : 0));
            cropN += nUptake;
            if (crop.Category != Agritech.Crops.CropCategory.Legume) minN -= nUptake;

            // LAI dynamics
            double laiMaxStage = floweringFrac;
            if (ds < laiMaxStage)
            {
                double target = crop.MaxLai / (1 + Math.Exp(-12 * (ds / laiMaxStage - 0.5)));
                lai += Math.Max(0, target - lai) * expansionStress * Math.Min(1, nStress + 0.2);
                maxLaiReached = Math.Max(maxLaiReached, lai);
            }
            else
            {
                double senescence = (ds - laiMaxStage) / (1 - laiMaxStage);
                lai = Math.Max(0.05, maxLaiReached * (1 - senescence * senescence) * (0.7 + 0.3 * waterStress));
            }
            if (flowering == w.Date) stressAtFlowering = Math.Min(waterStress, nStress);

            // Root front advance (≈ 2.2 cm/day until 70% of season, limited by drought)
            if (ds < 0.7) root = Math.Min(crop.MaxRootDepthM, root + 0.022 * Math.Min(1, gdd / 12) * (0.5 + 0.5 * waterStress));

            wsSum += waterStress; nsSum += nStress; nDays++;
            days.Add(new SimulationDay(w.Date, dap, Math.Round(cumGdd, 1), Math.Round(ds, 4), Math.Round(lai, 3), Math.Round(biomass, 1), Math.Round(root, 3),
                Math.Round(sw.Sum(), 1), Math.Round(waterStress, 3), Math.Round(nStress, 3), Math.Round(minN, 2), Math.Round(cropN, 2),
                Math.Round(transp, 2), Math.Round(es, 2), Math.Round(runoff, 2), Math.Round(drainage, 2), Math.Round(irr, 1), Math.Round(leached, 3), w.RainMm));
        }

        // Harvest index reduced by stress around flowering (grain set).
        double hi = crop.HarvestIndex * (0.6 + 0.4 * stressAtFlowering);
        double yieldT = maturity is null ? 0 : biomass * hi / 1000;
        double totalWaterUsed = transTotal + days.Sum(d => d.SoilEvaporation);
        return new SimulationResult(crop, mgmt, days, Math.Round(yieldT, 3), Math.Round(biomass / 1000, 3), flowering, maturity, Math.Round(irrTotal, 1),
            Math.Round(transTotal, 1), Math.Round(leachedTotal, 2),
            totalWaterUsed <= 0 ? 0 : Math.Round(yieldT * 1000 / (totalWaterUsed * 10), 3),
            nDays == 0 ? 1 : Math.Round(wsSum / nDays, 3), nDays == 0 ? 1 : Math.Round(nsSum / nDays, 3));
    }
}
