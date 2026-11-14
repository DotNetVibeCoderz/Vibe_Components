using System.Diagnostics;
using Electrical.AI;
using Electrical.AI.Learning;
using Electrical.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class VoltVarDeepRlTests(ITestOutputHelper output)
{
    [Fact]
    public void RlEnvironment_WrapsVoltVarEnvironment()
    {
        var env = new VoltVarRlEnvironment();
        Assert.Equal(env.Inner.ObservationSize + 2, env.ObservationSpace.FlatSize);
        Assert.Equal(7, env.ActionSpace.Count);
        env.Reset(1);
        int steps = 0;
        double total = 0;
        while (true)
        {
            var r = env.Step(0);
            total += env.LastStep!.Reward;
            Assert.Equal(env.LastStep.Reward * env.RewardScale, r.Reward, 4);
            steps++;
            if (r.Done) { Assert.True(r.Terminated); break; }
        }
        Assert.Equal(24, steps);
        var reference = VoltVarPolicies.Evaluate(new VoltVarEnvironment(), VoltVarPolicies.DoNothing);
        Assert.Equal(reference.TotalReward, total, 9);
    }

    [Fact]
    public void Dqn_BeatsDoNothing_Deterministically_AndCheckpoints()
    {
        var sw = Stopwatch.StartNew();
        var cmp = VoltVarAgentTrainer.Compare(options: new VoltVarRlOptions { Steps = 6_000 });
        output.WriteLine(cmp + $" — total {sw.Elapsed.TotalSeconds:0.0} s");
        // ≈ 4 s on an idle machine; generous bound because xUnit runs test classes in parallel.
        Assert.True(cmp.DeepRlTrainingSeconds < 30);
        Assert.True(cmp.DeepRl > cmp.DoNothing, cmp.ToString());
        Assert.True(cmp.DeepRl > cmp.VoltageRule, cmp.ToString());

        // Same seed → same weights → same greedy actions (on the same machine; RLNet's SIMD width may differ across CPUs).
        var small = new VoltVarRlOptions { Steps = 1_500 };
        var again = VoltVarAgentTrainer.Train(options: small);
        Assert.Equal(VoltVarAgentTrainer.Train(options: small).Evaluate(1000).Actions, again.Evaluate(1000).Actions);

        string path = Path.Combine(Path.GetTempPath(), $"eln-dqn-{Environment.ProcessId}.rlnet");
        try
        {
            again.Save(path);
            var loaded = VoltVarRlAgent.Load(path, again.Options);
            Assert.Equal(again.Evaluate(1000).Actions, loaded.Evaluate(1000).Actions);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Ppo_TrainsWithinBudget()
    {
        var agent = VoltVarAgentTrainer.Train(options: new VoltVarRlOptions { Algorithm = VoltVarRlAlgorithm.Ppo, Steps = 4_800 });
        var ep = agent.Evaluate();
        output.WriteLine($"PPO: {ep.TotalReward:0.00} ({agent.Report})");
        var none = VoltVarPolicies.Evaluate(new VoltVarEnvironment(), VoltVarPolicies.DoNothing);
        Assert.Equal(24, ep.Actions.Count);
        Assert.True(ep.TotalReward >= none.TotalReward - 1, $"{ep.TotalReward} vs {none.TotalReward}");
    }

    [Fact]
    public void AddLearning_RegistersService()
    {
        var sp = new ServiceCollection().AddElectrical(b => b.AddLearning()).BuildServiceProvider();
        var svc = sp.GetRequiredService<ILearningService>();
        Assert.Contains("Learning", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        var (_, _, r) = svc.TrainTopologyIdentifier(new TopologySnapshotOptions { SnapshotsPerConfiguration = 10 },
            new ClassificationTrainingOptions { Trees = 10 });
        Assert.Equal(8, r.Configurations);
    }
}
