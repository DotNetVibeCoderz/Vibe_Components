using System.Text.Json.Nodes;
using Electrical.Cloud;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.Tests;

public class CloudTests
{
    [Fact]
    public void NetworkJson_RoundTripPreservesPowerFlow()
    {
        var net = TestCases.Case33BW();
        var back = NetworkJson.Parse(NetworkJson.Serialize(net));
        Assert.Equal(PowerFlowSolver.Solve(net).LossesMw, PowerFlowSolver.Solve(back).LossesMw, 9);
    }

    [Fact]
    public async Task StudyRunner_ExecutesQueuedJobsPerTenant()
    {
        await using var runner = StudyCatalog.Default(workers: 2);
        var a = await runner.SubmitAsync("utility-a", "powerflow", TestCases.Case14());
        var b = await runner.SubmitAsync("utility-b", "contingency", TestCases.Case9());
        var done = await runner.WaitAsync(a.Id);
        await runner.WaitAsync(b.Id);
        Assert.Equal(StudyStatus.Completed, done.Status);
        Assert.Equal(13.393, (double)done.Result!["lossesMw"]!, 2);
        Assert.Single(runner.List("utility-a"));
        Assert.Equal(9, runner.Get(b.Id)!.Result!.AsArray().Count);
    }

    [Fact]
    public void ResolveNetwork_AcceptsCaseMatpowerAndJson()
    {
        Assert.Equal(14, StudyCatalog.ResolveNetwork(JsonNode.Parse("""{"case":"case14"}""")).BusCount);
        var mp = Matpower.Write(TestCases.Case9());
        Assert.Equal(9, StudyCatalog.ResolveNetwork(new JsonObject { ["matpower"] = mp }).BusCount);
        Assert.Equal(33, StudyCatalog.ResolveNetwork(NetworkJson.ToJson(TestCases.Case33BW())).BusCount);
    }

    [Fact]
    public void ScenarioStores_SaveLoadList()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eln-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (IScenarioStore s in new IScenarioStore[] { new InMemoryScenarioStore(), new FileScenarioStore(dir) })
            {
                s.Save("t1", "base", TestCases.Case9());
                Assert.Equal(["base"], s.List("t1"));
                Assert.Empty(s.List("t2"));
                Assert.Equal(315, s.Load("t1", "base")!.TotalLoadMw, 9);
                Assert.True(s.Delete("t1", "base"));
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
