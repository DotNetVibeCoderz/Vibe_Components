using Agritech.Crops;
using Agritech.Soil;

namespace Agritech.AI;

public sealed record FertilizerApplication(string Stage, int FromDap, int ToDap, IReadOnlyList<(FertilizerProduct Product, double KgPerHa, double TotalKg)> Products)
{
    public bool IsDue(int dap) => dap >= FromDap && dap <= ToDap;
}

public sealed record FertilizerAdvice(CropDefinition Crop, double TargetYieldTonHa, NutrientNeed Need, FertilizerPlan Plan,
    IReadOnlyList<FertilizerApplication> Schedule, double AreaHa)
{
    /// <summary>Applications whose window overlaps the 7-day period starting at <paramref name="dap"/>.</summary>
    public IReadOnlyList<FertilizerApplication> DueWithinWeek(int dap) =>
        Schedule.Where(a => a.FromDap <= dap + 6 && a.ToDap >= dap).ToList();
}

/// <summary>
/// Site-specific nutrient management advisor combining crop removal, soil test and target yield into a
/// split-application schedule (basal + top-dressings) aligned with crop phenology.
/// </summary>
public static class FertilizerAdvisor
{
    public static FertilizerAdvice Advise(CropDefinition crop, SoilSample soil, double areaHa, double? targetYieldTonHa = null, bool useCompound = true)
    {
        double target = targetYieldTonHa ?? crop.PotentialYieldTonHa * 0.75;
        var removal = new NutrientNeed(crop.Removal.N, crop.Removal.P2O5, crop.Removal.K2O);
        var need = FertilizerCalculator.Requirement(soil, target, removal);
        var plan = FertilizerCalculator.Plan(need, areaHa, useCompound);

        int ini = crop.StageDays[0], dev = crop.StageDays[1];
        int td1 = Math.Max(14, ini - 5), td2 = ini + dev / 2;
        double urea = plan.Lines.Where(l => l.Product.Id == "urea").Sum(l => l.KgPerHa);
        var basal = plan.Lines.Where(l => l.Product.Id != "urea").Select(l => (l.Product, l.KgPerHa, l.TotalKg)).ToList();
        if (urea > 0) basal.Add((FertilizerProducts.Urea, Math.Round(urea / 3, 1), Math.Round(urea / 3 * areaHa, 1)));
        var schedule = new List<FertilizerApplication> { new("Basal / Dasar", 0, 7, basal) };
        if (urea > 0)
        {
            schedule.Add(new("Top-dress 1 / Susulan 1", td1, td1 + 7, [(FertilizerProducts.Urea, Math.Round(urea / 3, 1), Math.Round(urea / 3 * areaHa, 1))]));
            schedule.Add(new("Top-dress 2 / Susulan 2", td2, td2 + 7, [(FertilizerProducts.Urea, Math.Round(urea / 3, 1), Math.Round(urea / 3 * areaHa, 1))]));
        }
        return new FertilizerAdvice(crop, Math.Round(target, 2), need, plan, schedule, areaHa);
    }
}
