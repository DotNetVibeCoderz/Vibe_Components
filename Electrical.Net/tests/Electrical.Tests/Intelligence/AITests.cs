using System.Net;
using System.Numerics;
using System.Text.Json.Nodes;
using Electrical.AI;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Core.Units;
using Electrical.DigitalTwin;
using Electrical.Forecasting;
using Electrical.ShortCircuit;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

/// <summary>Runs only when ELECTRICAL_LIVE_LLM=1 and an LLM is configured through the ELECTRICAL_LLM_* variables.</summary>
public sealed class LiveLlmFactAttribute : FactAttribute
{
    public LiveLlmFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ELECTRICAL_LIVE_LLM") != "1" || LlmOptions.FromEnvironment() is null)
            Skip = "Live LLM test: set ELECTRICAL_LIVE_LLM=1 and ELECTRICAL_LLM_PROVIDER/API_KEY/MODEL.";
    }
}

public class AITests
{
    [Fact]
    public void LlmOptions_FromEnvironmentVariables()
    {
        Assert.Null(LlmOptions.FromVariables(_ => null));
        var vars = new Dictionary<string, string>
        {
            ["ELECTRICAL_LLM_PROVIDER"] = "azure", ["ELECTRICAL_LLM_ENDPOINT"] = "https://res.openai.azure.com/",
            ["ELECTRICAL_LLM_API_KEY"] = "secret-key", ["ELECTRICAL_LLM_MODEL"] = "gpt-5-mini",
        };
        var az = LlmOptions.FromVariables(k => vars.GetValueOrDefault(k))!;
        Assert.Equal(LlmProvider.AzureOpenAI, az.Provider);
        Assert.Equal("https://res.openai.azure.com/openai/v1/chat/completions", az.ChatCompletionsUrl);
        Assert.DoesNotContain("secret-key", az.ToString());
        var hf = LlmOptions.FromVariables(k => k == "HF_TOKEN" ? "hf_x" : null)!;
        Assert.Equal(LlmProvider.HuggingFace, hf.Provider);
        Assert.Equal("https://router.huggingface.co/v1/chat/completions", hf.ChatCompletionsUrl);
        var ol = LlmOptions.FromVariables(k => k == "ELECTRICAL_LLM_PROVIDER" ? "ollama" : null)!;
        Assert.Equal("http://localhost:11434/v1/chat/completions", ol.ChatCompletionsUrl);
        Assert.Null(ol.ApiKey);
    }

    [Fact]
    public async Task Agent_ToolCallingLoopWithOpenAiCompatibleClient()
    {
        // fake endpoint: first asks for short_circuit at bus 4, then answers using the tool output
        var handler = new FakeHandler();
        var model = new OpenAiCompatibleChatModel(LlmOptions.OpenAI("test-key", "gpt-test"), new HttpClient(handler));
        var agent = new GridAgent(new GridAgentContext { CaseName = "case14" }, model);
        var ans = await agent.AskAsync("What is the SLG fault current at bus 4?");
        Assert.Equal(["short_circuit"], ans.ToolsUsed);
        Assert.StartsWith("Final:", ans.Text);
        Assert.Contains("kA", ans.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Bearer test-key", handler.Authorization[0]);
        var first = JsonNode.Parse(handler.Requests[0])!;
        Assert.Equal("gpt-test", first["model"]!.GetValue<string>());
        Assert.Equal(10, first["tools"]!.AsArray().Count);
        var second = JsonNode.Parse(handler.Requests[1])!["messages"]!.AsArray();
        Assert.Equal("tool", second[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", second[^1]!["tool_call_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task RuleBasedAgent_AnswersBilingualQuestionsWithTools()
    {
        var agent = new GridAgent(new GridAgentContext { CaseName = "case14" }, new RuleBasedChatModel());
        var en = (await agent.AskAsync("What is the three-phase short circuit current at bus 4 of case 14?"));
        Assert.Equal("en", en.Language);
        Assert.Equal(["short_circuit"], en.ToolsUsed);
        Assert.Contains("Short circuit (ThreePhase) at bus 4", en.Text);

        var id = (await agent.AskAsync("Berapa arus hubung singkat satu fasa ke tanah di bus 4?"));
        Assert.Equal("id", id.Language);
        Assert.Contains("Hubung singkat (SingleLineToGround) di bus 4", id.Text);
        Assert.Contains("arus tanah", id.Text);

        var pf = (await agent.AskAsync("Run a power flow on case 33 with load increased by 20%"));
        Assert.Contains("run_power_flow", pf.ToolsUsed);
        Assert.Contains("load ×1.20", pf.Text);

        var why = (await agent.AskAsync("Kenapa tegangan rendah di kasus 33?"));
        Assert.Equal(["explain_alarms"], why.ToolsUsed);
        Assert.Contains("karena", why.Text);

        Assert.Equal("forecast_load", (await agent.AskAsync("Prakiraan beban besok?")).ToolsUsed[0]);
        Assert.Equal("grid_overview", (await agent.AskAsync("hello")).ToolsUsed[0]);
    }

    [Fact]
    public async Task AllTools_RunWithoutErrors()
    {
        var sim = new TwinSimulator(TestCases.Case14(), new TwinSimulatorOptions { Steps = 3 });
        sim.Run();
        var agent = new GridAgent(new GridAgentContext { CaseName = "case14", Twin = sim.Twin }, new RuleBasedChatModel());
        foreach (var t in agent.Tools)
        {
            string args = t.Name switch
            {
                "hosting_capacity" => """{"case":"case33bw","bus":18}""",
                "short_circuit" => """{"bus":9,"fault_type":"dlg"}""",
                "economic_dispatch" => """{"with_losses":true}""",
                _ => "{}",
            };
            string r = await agent.InvokeToolAsync(new ToolCall("x", t.Name, args));
            Assert.False(r.StartsWith("Tool error"), $"{t.Name}: {r}");
            Assert.True(r.Length > 20, t.Name);
        }
        Assert.Contains("Cycle 2", await agent.InvokeToolAsync(new ToolCall("x", "twin_status", "{}")));
        Assert.Contains("Unknown tool", await agent.InvokeToolAsync(new ToolCall("x", "nope", "{}")));
    }

    [Fact]
    public void AnomalyDetectors_FindSpikesDriftAndShifts()
    {
        var load = SyntheticProfiles.Load(LoadProfileKind.Commercial, new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7)), 5, TimeSpan.FromHours(1), 50, noise: 0.01, seed: 5); // Mon–Fri
        var x = (double[])load.Values.Clone();
        x[30] *= 1.6; x[100] *= 0.3;
        var spikes = RobustZScore.DetectSeasonal(x, period: 24, threshold: 5);
        Assert.Contains(spikes, a => a.Index == 30 && a.Direction == 1);
        Assert.Contains(spikes, a => a.Index == 100 && a.Direction == -1);
        Assert.True(spikes.Count <= 3, $"{spikes.Count} detections");
        // non-seasonal variant on a series with a single spike
        var flat = Enumerable.Range(0, 50).Select(i => 5.0 + 0.01 * (i % 7)).ToArray();
        flat[20] = 9;
        Assert.Equal(20, Assert.Single(RobustZScore.Detect(flat)).Index);

        // sensor with a small (1.5σ) persistent offset from sample 100
        var rng = new Random(3);
        var y = Enumerable.Range(0, 200).Select(i => 10 + 0.1 * Electrical.StateEstimation.MeasurementGenerator.Gaussian(rng) + (i >= 100 ? 0.15 : 0)).ToArray();
        var ewma = new EwmaDetector(0.2, 3, warmup: 50).Detect(y);
        Assert.NotEmpty(ewma);
        Assert.All(ewma, a => Assert.True(a.Index >= 100));
        var cusum = new CusumDetector(0.5, 5, warmup: 50).Detect(y);
        Assert.NotEmpty(cusum);
        Assert.InRange(cusum[0].Index, 100, 130);
        Assert.Equal(1, cusum[0].Direction);
        Assert.Equal(3.0, RobustZScore.Median([5, 1, 3]));
    }

    [Theory]
    [InlineData(FaultType.ThreePhase, "ABC")]
    [InlineData(FaultType.SingleLineToGround, "A")]
    [InlineData(FaultType.LineToLine, "BC")]
    [InlineData(FaultType.DoubleLineToGround, "BC")]
    public void FaultClassifier_RecognizesIec60909Faults(FaultType type, string phases)
    {
        var r = ShortCircuitAnalysis.Calculate(TestCases.Case14(), 4, type);
        var c = FaultClassifier.Classify(r.PhaseCurrentsKa, pickup: 1.0);
        Assert.Equal(type, c.Type);
        Assert.Equal(phases, c.Phases);
        Assert.Equal(type is FaultType.SingleLineToGround or FaultType.DoubleLineToGround, c.Grounded);
        // balanced load current below pick-up → no fault
        Assert.False(FaultClassifier.Classify(ThreePhase.Balanced(0.4), pickup: 1.0).IsFault);
    }

    [Fact]
    public void FaultLocator_ReactanceMethodRecoversDistance()
    {
        Complex z1 = new(0.1, 0.4), z0 = new(0.3, 1.2);
        const double d = 12.5;
        // SLG on phase A of a radial line: only phase A carries current
        Complex ia = Complex.FromPolarCoordinates(2.0, -1.2);
        var i = new ThreePhase(ia, 0, 0);
        var k0 = (z0 - z1) / (3 * z1);
        var v = new ThreePhase(d * z1 * (ia + k0 * ia), Phasor.FromPolarDeg(7.2, -120), Phasor.FromPolarDeg(7.2, 120));
        var c = FaultClassifier.Classify(i);
        Assert.Equal(FaultType.SingleLineToGround, c.Type);
        var loc = FaultLocator.Locate(v, i, c, z1, z0);
        Assert.Equal(d, loc.DistanceKm, 6);
        Assert.Equal("A-G", loc.Loop);

        // B-C fault at 30 km with 1 Ω arc resistance (in phase with the loop current: no reactance error)
        Complex ib = Complex.FromPolarCoordinates(3, -2.0), ic = -ib;
        var i2 = new ThreePhase(0.05, ib, ic);
        var vb = Phasor.FromPolarDeg(5, -120);
        var v2 = new ThreePhase(Phasor.FromPolarDeg(7.2, 0), vb, vb - (30 * z1 + 1.0) * (ib - ic));
        var c2 = FaultClassifier.Classify(i2);
        Assert.Equal("BC", c2.Phases);
        Assert.Equal(30, FaultLocator.Locate(v2, i2, c2, z1, z0).DistanceKm, 6);
    }

    [Fact]
    public void KMeans_SeparatesCustomerClasses()
    {
        var start = new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.FromHours(7));
        var kinds = new[] { LoadProfileKind.Residential, LoadProfileKind.Commercial, LoadProfileKind.Industrial };
        var profiles = new List<double[]>();
        var truth = new List<int>();
        for (int i = 0; i < 30; i++)
        {
            int k = i % 3;
            profiles.Add(SyntheticProfiles.Load(kinds[k], start, 1, TimeSpan.FromHours(1), 1 + i * 0.37, noise: 0.03, seed: 100 + i).Values);
            truth.Add(k);
        }
        var res = LoadProfileClustering.KMeans(profiles, 3);
        Assert.Equal([10, 10, 10], res.Sizes.Order().ToArray());
        for (int i = 0; i < 30; i++)
            for (int j = 0; j < 30; j++)
                Assert.Equal(truth[i] == truth[j], res.Assignments[i] == res.Assignments[j]);
        var fresh = SyntheticProfiles.Load(LoadProfileKind.Commercial, start, 1, TimeSpan.FromHours(1), 42, noise: 0.03, seed: 999).Values;
        Assert.Equal(res.Assignments[1], res.Predict(fresh));
    }

    [Fact]
    public void VoltVar_QLearningBeatsDoNothing()
    {
        var env = new VoltVarEnvironment();
        var obs = env.Reset();
        Assert.Equal(33 + 3 + 1, obs.Length);
        Assert.Equal(7, env.ActionCount);
        Assert.Equal(24 * 125, env.StateCount);
        var step = env.Step(1);
        Assert.Equal(1, env.Levels[0]);
        Assert.True(step.Reward < 0 && step.LossesKw > 0);

        var idle = VoltVarPolicies.Evaluate(env, VoltVarPolicies.DoNothing);
        var rule = VoltVarPolicies.Evaluate(env, VoltVarPolicies.VoltageRule);
        var q = new TabularQLearning(env.StateCount, env.ActionCount, seed: 7);
        var training = q.Train(env, 400);
        var learned = VoltVarPolicies.Evaluate(env, q.Policy);
        Assert.True(training.EpisodeRewards[^20..].Average() > training.EpisodeRewards[..20].Average());
        Assert.True(learned.TotalReward > idle.TotalReward + 1, $"learned {learned.TotalReward:0.00} vs idle {idle.TotalReward:0.00}");
        Assert.True(learned.EnergyLossesKwh < idle.EnergyLossesKwh);
        Assert.True(learned.ViolationBusHours < idle.ViolationBusHours);
        Assert.True(rule.TotalReward > idle.TotalReward);
        // deterministic
        var q2 = new TabularQLearning(env.StateCount, env.ActionCount, seed: 7);
        q2.Train(env, 400);
        Assert.Equal(learned.TotalReward, VoltVarPolicies.Evaluate(env, q2.Policy).TotalReward, 9);
    }

    [Fact]
    public void AddAI_RegistersAgentAndModules()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddAI()).BuildServiceProvider();
        var modules = sp.GetRequiredService<ElectricalModuleRegistry>().Modules;
        foreach (var m in new[] { "AI", "DigitalTwin", "PowerFlow", "ShortCircuit", "Optimization", "Stability", "Renewable", "Forecasting" })
            Assert.Contains(m, modules);
        var agent = sp.GetRequiredService<GridAgent>();
        Assert.Same(sp.GetRequiredService<GridAgentContext>(), agent.Context);
        Assert.Equal(10, agent.Tools.Count);
    }

    [LiveLlmFact]
    public async Task Live_AgentAnswersWithRealLlm()
    {
        var agent = new GridAgent(new GridAgentContext { CaseName = "case14" });
        Assert.IsType<OpenAiCompatibleChatModel>(agent.Model);
        var ans = await agent.AskAsync("Run a power flow on case14 and tell me the total losses in MW.");
        Assert.Contains("run_power_flow", ans.ToolsUsed);
        Assert.Matches(@"13[.,]\d", ans.Text); // MATPOWER case14 losses 13.393 MW
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<string?> Authorization { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(body);
            Authorization.Add(request.Headers.Authorization?.ToString());
            string json;
            if (Requests.Count == 1)
                json = """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"short_circuit","arguments":"{\"bus\":4,\"fault_type\":\"slg\"}"}}]}}]}""";
            else
            {
                var tool = JsonNode.Parse(body)!["messages"]!.AsArray()[^1]!["content"]!.GetValue<string>();
                json = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Final: " + tool } }) }.ToJsonString();
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
