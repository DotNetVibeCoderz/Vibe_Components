using AquaCulture.AI.Rl;

namespace AquaCulture.Tests;

public class RlPolicyTests
{
    [Fact]
    public void Aeration_environment_ranks_the_farmer_baselines_sensibly()
    {
        var always = AerationEnvironment.Evaluate("always", AerationEnvironment.AlwaysOn, episodes: 8);
        var setpoint = AerationEnvironment.Evaluate("sp5", AerationEnvironment.SetPoint(5), episodes: 8);
        var off = AerationEnvironment.Evaluate("off", _ => 0, episodes: 8);
        Assert.Equal(0, always.HoursBelowCritical);
        Assert.True(off.HoursBelowCritical > 20, "a stocked shrimp pond without aeration must crash at night");
        Assert.True(setpoint.Kwh < always.Kwh * 0.5);
        Assert.True(setpoint.CostIdr < always.CostIdr && always.CostIdr < off.CostIdr);
        Assert.Equal(setpoint, AerationEnvironment.Evaluate("sp5", AerationEnvironment.SetPoint(5), episodes: 8));   // deterministic
    }

    [Fact]
    public void Sac_aeration_policy_protects_dawn_do_at_set_point_cost()
    {
        var policy = AerationPolicy.Train(steps: 8_000);
        var sac = AerationEnvironment.Evaluate("sac", policy.Decide, episodes: 10);
        var always = AerationEnvironment.Evaluate("always", AerationEnvironment.AlwaysOn, episodes: 10);
        var timer = AerationEnvironment.Evaluate("timer", AerationEnvironment.NightTimer, episodes: 10);
        var sp5 = AerationEnvironment.Evaluate("sp5", AerationEnvironment.SetPoint(5), episodes: 10);
        Assert.True(sac.HoursBelowCritical <= 0.5, $"SAC {sac}");
        Assert.True(sac.CostIdr < always.CostIdr * 0.6, $"SAC {sac.CostIdr:N0} vs always-on {always.CostIdr:N0}");
        Assert.True(sac.CostIdr < timer.CostIdr, $"SAC {sac.CostIdr:N0} vs night timer {timer.CostIdr:N0}");
        Assert.True(sac.CostIdr < sp5.CostIdr * 1.2, $"SAC {sac.CostIdr:N0} vs tuned set-point {sp5.CostIdr:N0}");
        Assert.NotEmpty(policy.ExportParameters());
    }

    [Fact]
    public void Feeding_environment_penalises_over_and_under_feeding()
    {
        var under = FeedingEnvironment.Evaluate("0.7", FeedingEnvironment.Fixed(0.7), episodes: 8);
        var table = FeedingEnvironment.Evaluate("adj", FeedingEnvironment.AdjustedTable, episodes: 8);
        var over = FeedingEnvironment.Evaluate("1.3", FeedingEnvironment.Fixed(1.3), episodes: 8);
        Assert.True(table.ProfitIdr > under.ProfitIdr && table.ProfitIdr > over.ProfitIdr);
        Assert.True(over.WasteKg > 10 * table.WasteKg, "feeding 30 % above the table must waste feed");
        Assert.True(over.Survival < table.Survival, "waste lowers dawn DO and survival");
    }

    [Fact]
    public void Sac_feeding_policy_earns_more_than_the_adjusted_feeding_table()
    {
        var policy = FeedingRlPolicy.Train(steps: 9_000);
        var sac = FeedingEnvironment.Evaluate("sac", policy.Decide, episodes: 10);
        var table = FeedingEnvironment.Evaluate("adj", FeedingEnvironment.AdjustedTable, episodes: 10);
        Assert.True(sac.ProfitIdr > table.ProfitIdr, $"SAC {sac.ProfitIdr:N0} vs adjusted table {table.ProfitIdr:N0}");
        Assert.True(sac.WasteKg < 2 * table.WasteKg + 5, $"SAC waste {sac.WasteKg} kg vs table {table.WasteKg} kg");
    }
}
