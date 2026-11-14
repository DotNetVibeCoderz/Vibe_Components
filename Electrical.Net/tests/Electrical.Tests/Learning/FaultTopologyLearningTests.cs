using Electrical.AI.Learning;
using Electrical.Core.Network;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class FaultTopologyLearningTests(ITestOutputHelper output)
{
    [Fact]
    public void FaultDataset_IsDeterministic_AndCoversAllCodes()
    {
        var o = new FaultDatasetOptions { Samples = 300 };
        var a = FaultDatasetGenerator.Generate(o);
        var b = FaultDatasetGenerator.Generate(o);
        Assert.Equal(300, a.Samples.Count);
        Assert.Equal(a.FeatureNames.Length, a.Samples[0].Features.Length);
        for (int i = 0; i < a.Samples.Count; i++) Assert.Equal(a.Samples[i].Features, b.Samples[i].Features);
        Assert.Equal(FaultDatasetGenerator.Codes.Order(), a.Samples.Select(s => s.Code).Distinct().Order());
        Assert.Contains(a.Samples, s => s.BranchIndex is not null);
        Assert.Contains(a.Samples, s => s.BranchIndex is null);
    }

    [Fact]
    public void FaultCode_RotationAndTypeMapping()
    {
        Assert.Equal("AG", FaultDatasetGenerator.Code(FaultType.SingleLineToGround, 0));
        Assert.Equal("BG", FaultDatasetGenerator.Code(FaultType.SingleLineToGround, 1));
        Assert.Equal("BC", FaultDatasetGenerator.Code(FaultType.LineToLine, 0));
        Assert.Equal("AC", FaultDatasetGenerator.Code(FaultType.LineToLine, 1));
        Assert.Equal("ABG", FaultDatasetGenerator.Code(FaultType.DoubleLineToGround, 2));
        Assert.Equal("ABC", FaultDatasetGenerator.Code(FaultType.ThreePhase, 1));
        foreach (var c in FaultDatasetGenerator.Codes)
            Assert.Contains(c, Enumerable.Range(0, 3).Select(r => FaultDatasetGenerator.Code(FaultDatasetGenerator.TypeOf(c), r)));
    }

    [Fact]
    public void SplitLine_PreservesSeriesImpedance()
    {
        var net = TestCases.Case33BW();
        var (split, mid) = FaultDatasetGenerator.SplitLine(net, 5, 0.25);
        Assert.Equal(net.BusCount + 1, split.BusCount);
        var halves = split.Branches.Where(b => b.From == mid || b.To == mid).ToArray();
        Assert.Equal(2, halves.Length);
        Assert.Equal(net.Branches[5].X, halves.Sum(b => b.X), 12);
        Assert.Equal(net.Branches[5].R * 0.25, split.Branches[5].R, 12);
        // Bolted fault at the split point lies between the faults at both ends.
        var sc = new ShortCircuitOptions { ExternalGrids = [new ExternalGrid { Bus = 1, SkMva = 250 }] };
        double ik(PowerNetwork n, int bus) => new ShortCircuitStudy(n, sc).Calculate(bus, FaultType.ThreePhase).IkKa;
        double iMid = ik(split, mid);
        Assert.InRange(iMid, ik(net, net.Branches[5].To), ik(net, net.Branches[5].From));
    }

    [Fact]
    public void FaultModels_Case33_ClassifyAndLocate()
    {
        var ds = FaultDatasetGenerator.Generate(new FaultDatasetOptions { Samples = 1600 });
        var (model, r) = FaultLearning.Train(ds, new ClassificationTrainingOptions { Trees = 50 });
        output.WriteLine("Case33 RF: " + r);
        Assert.True(r.TypeAccuracy > 0.95, r.ToString());
        Assert.True(r.CodeAccuracy > 0.9, r.ToString());
        Assert.True(r.LocationAccuracy > 0.5, r.ToString());
        Assert.True(r.LocationWithinOneHop > 0.85, r.ToString());

        string dir = Path.Combine(Path.GetTempPath(), $"eln-fault-{Environment.ProcessId}");
        try
        {
            model.Save(dir);
            var loaded = FaultLearningModel.Load(dir);
            var (_, test) = ds.Split();
            var r2 = FaultLearning.Evaluate(loaded, ds.Network, test, r.TrainSamples);
            Assert.Equal(r, r2);
            var p = loaded.Predict(test[0].Features);
            Assert.Contains(p.Code, FaultDatasetGenerator.Codes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FaultModels_Case14_MlNetClassifier()
    {
        var ds = FaultDatasetGenerator.Generate(FaultDatasetOptions.Case14() with { Samples = 700 });
        var (model, r) = FaultLearning.Train(ds, new ClassificationTrainingOptions { Algorithm = ClassificationAlgorithm.MlNetFastTree, Trees = 20 });
        output.WriteLine("Case14 ML.NET: " + r);
        Assert.IsType<MlNetClassifier>(model.Classifier);
        Assert.Equal(FaultDatasetGenerator.Codes.Order(), model.Classifier.Labels.Order());
        Assert.True(r.TypeAccuracy > 0.9, r.ToString());
        Assert.True(r.LocationWithinOneHop > 0.7, r.ToString());

        string dir = Path.Combine(Path.GetTempPath(), $"eln-fault14-{Environment.ProcessId}");
        try
        {
            model.Save(dir);
            Assert.True(File.Exists(Path.Combine(dir, "classifier.zip")));
            var loaded = FaultLearningModel.Load(dir);
            var (_, test) = ds.Split();
            Assert.Equal(r.CodeAccuracy, FaultLearning.Evaluate(loaded, ds.Network, test).CodeAccuracy, 9);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Case33Ties_OptimalConfiguration_MatchesLiteratureLosses()
    {
        var net = ReconfigurableFeeders.Case33WithTies();
        Assert.Equal(37, net.Branches.Count);
        var cfg = ReconfigurableFeeders.Case33Configurations;
        foreach (var c in cfg)
        {
            var n = c.Apply(net);
            Assert.True(Topology.IsRadial(n), c.Name);
            Assert.Single(Topology.Islands(n));
        }
        // Base case 202.7 kW (Baran & Wu 1989); minimum-loss configuration 139.5 kW (Goswami & Basu 1992).
        Assert.Equal(202.7, PowerFlowSolver.Solve(cfg[0].Apply(net)).LossesMw * 1000, 0);
        Assert.Equal(139.5, PowerFlowSolver.Solve(cfg[1].Apply(net)).LossesMw * 1000, 0);
    }

    [Fact]
    public void TopologyIdentification_Case33_FromVoltageSnapshots()
    {
        var o = new TopologySnapshotOptions { SnapshotsPerConfiguration = 50 };
        var snaps = TopologyIdentification.GenerateSnapshots(o);
        Assert.Equal(8 * 50, snaps.Count);
        var (clf, corr, r) = TopologyIdentification.Train(snaps, o, new ClassificationTrainingOptions { Trees = 60 });
        output.WriteLine(r.ToString());
        Assert.True(r.ClassifierAccuracy > 0.95, r.ToString());
        Assert.True(r.CorrelationAccuracy > 0.95, r.ToString());
        Assert.Equal(8, corr.Configurations.Count);
        Assert.Equal(8, clf.Labels.Count);
    }
}
