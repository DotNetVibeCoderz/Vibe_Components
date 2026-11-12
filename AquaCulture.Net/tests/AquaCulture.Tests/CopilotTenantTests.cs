using AquaCulture.AI.Agent;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Species;
using AquaCulture.Data;
using AquaCulture.IoT;
using AquaCulture.WaterQuality;

namespace AquaCulture.Tests;

public class CopilotTests
{
    private static FarmSite Site(string id, string name, double fcr, double price, double dox)
    {
        var farm = new Farm { Id = id, Name = name };
        var contexts = new List<AquaContext>();
        for (int p = 1; p <= 2; p++)
        {
            string pond = $"P-0{p}";
            farm.Ponds.Add(new Pond { Id = pond, AreaM2 = 2000, DepthM = 1.3 });
            var c = new CultureCycle { Id = $"{id}-C{p}", PondId = pond, Species = SpeciesCatalog.Vaname, StockingDate = new DateOnly(2026, 1, 1), StockedCount = 200_000, StockingWeightG = 0.01, SeedPricePerUnit = 40 };
            c.LogSampling(new DateOnly(2026, 3, 20), 18).LogFeed(new DateOnly(2026, 2, 1), 3000 * fcr, "F", 16_000).LogHarvest(new DateOnly(2026, 4, 1), 3000, 160_000, price);
            farm.Cycles.Add(c);
            contexts.Add(new AquaContext
            {
                PondName = pond, Species = SpeciesCatalog.Vaname,
                Water = new WaterSample { TemperatureC = 29.5, DissolvedOxygen = p == 2 ? dox : 5.5, Ph = 8.0, SalinityPpt = 18, Ammonia = p == 2 && dox < 3 ? 2.5 : 0.3 },
            });
        }
        return new FarmSite(farm, contexts, new DateOnly(2026, 4, 2));
    }

    [Fact]
    public async Task Copilot_compares_farms_and_finds_problem_ponds_offline()
    {
        FarmSite a = Site("A", "Tambak Barat", 1.3, 72_000, 5.0), b = Site("B", "Tambak Timur", 1.8, 68_000, 2.4);
        var agent = FarmCopilot.Create([a, b], new RuleBasedChatModel());
        Assert.Equal(4, agent.Ponds.Count);
        Assert.Equal("A/P-01", agent.Ponds[0].PondName);

        var overview = await agent.AskAsync("Bagaimana kondisi semua tambak?");
        Assert.Contains("portfolio_overview", overview.ToolsUsed);
        Assert.True(overview.Text.IndexOf("A Tambak Barat", StringComparison.Ordinal) < overview.Text.IndexOf("B Tambak Timur", StringComparison.Ordinal), "more profitable farm first");

        var fcr = await agent.AskAsync("Bandingkan tambak, mana yang terbaik?");
        Assert.Contains("compare_farms", fcr.ToolsUsed);
        Assert.Contains("fcr", fcr.Text);
        Assert.True(fcr.Text.IndexOf("- A", StringComparison.Ordinal) < fcr.Text.IndexOf("- B", StringComparison.Ordinal));

        var profit = await agent.AskAsync("Bandingkan tambak menurut laba per kg");
        Assert.Contains("profit", profit.Text);

        var alerts = await agent.AskAsync("Kolam bermasalah di mana saja?");
        Assert.Contains("farm_alerts", alerts.ToolsUsed);
        Assert.Contains("B/P-02", alerts.Text);
        Assert.DoesNotContain("A/P-01", alerts.Text);
        Assert.Contains("Tambak Barat", agent.History[0].Content);                              // portfolio named in the system prompt

        Assert.True(FarmCopilot.Metric(FarmCopilot.Summaries([a])[0].Summary, "profit") > FarmCopilot.Metric(FarmCopilot.Summaries([b])[0].Summary, "profit"));
        Assert.Throws<ArgumentException>(() => FarmCopilot.Metric(FarmCopilot.Summaries([a])[0].Summary, "magic"));
    }
}

public class TenantTests
{
    [Fact]
    public async Task Tenants_get_isolated_databases_and_hashed_api_keys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aqua-tenants-{Guid.NewGuid():N}");
        try
        {
            using (var tenants = new AquaTenants(root))
            {
                var (ta, keyA) = tenants.Create("koperasi-a", "Koperasi Udang A");
                var (_, keyB) = tenants.Create("pt-b", "PT Tambak B", "enterprise");
                Assert.Equal(2, tenants.List().Count);
                Assert.DoesNotContain(keyA, File.ReadAllText(Path.Combine(root, "tenants.json")));          // only the hash is stored
                Assert.True(tenants.Authenticate("koperasi-a", keyA));
                Assert.False(tenants.Authenticate("koperasi-a", keyB));
                Assert.False(tenants.Authenticate("nobody", keyA));

                foreach (var bad in new[] { "../etc", "A", "a", "x y", "tenant/../../z" })
                    Assert.Throws<ArgumentException>(() => tenants.Create(bad, "x"));
                Assert.Throws<InvalidOperationException>(() => tenants.Create("pt-b", "again"));

                var t0 = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
                await tenants.Store("koperasi-a").AppendReadingsAsync([new SensorReading("d1", "P-01", SensorKind.DissolvedOxygen, 4.2, t0, "mg/L")]);
                Assert.Single(await tenants.Store("koperasi-a").QueryReadingsAsync("P-01"));
                Assert.Empty(await tenants.Store("pt-b").QueryReadingsAsync("P-01"));
                Assert.True(File.Exists(tenants.DatabasePath("koperasi-a")));

                string newKey = tenants.RotateKey("koperasi-a");
                Assert.False(tenants.Authenticate("koperasi-a", keyA));
                Assert.True(tenants.Authenticate("koperasi-a", newKey));
                tenants.SetDisabled("pt-b", true);
                Assert.False(tenants.Authenticate("pt-b", keyB));
                Assert.Equal("Koperasi Udang A", ta.Name);
            }
            using var reopened = new AquaTenants(root);                                  // the catalog survives a restart
            Assert.True(reopened.Find("pt-b")!.Disabled);
            Assert.Throws<InvalidOperationException>(() => reopened.Store("pt-b"));
            Assert.Single(await reopened.Store("koperasi-a").QueryReadingsAsync("P-01"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
