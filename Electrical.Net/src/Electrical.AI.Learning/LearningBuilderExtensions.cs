using Electrical.AI;
using Electrical.Core.Data;
using Electrical.Core.Hosting;
using Electrical.Forecasting;
using Electrical.PowerFlow;
using Electrical.ShortCircuit;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Electrical.AI.Learning;

/// <summary>DI facade over the machine-learning workflows of Electrical.AI.Learning.</summary>
public interface ILearningService
{
    MlLoadForecaster TrainLoadForecaster(TimeSeries load, TimeSeries? temperature = null, MlLoadForecastOptions? options = null);
    LoadForecastBenchmarkResult BenchmarkLoadForecast(TimeSeries load, TimeSeries? temperature = null, LoadForecastBenchmarkOptions? options = null);
    (FaultLearningModel Model, FaultLearningReport Report) TrainFaultModels(FaultDatasetOptions? dataset = null, ClassificationTrainingOptions? model = null);
    (IClassificationModel Classifier, CorrelationTopologyIdentifier Correlation, TopologyIdentificationReport Report) TrainTopologyIdentifier(
        TopologySnapshotOptions? options = null, ClassificationTrainingOptions? model = null);
    VoltVarRlAgent TrainVoltVarAgent(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null);
    VoltVarComparison CompareVoltVar(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null);
}

internal sealed class LearningService : ILearningService
{
    public MlLoadForecaster TrainLoadForecaster(TimeSeries load, TimeSeries? temperature = null, MlLoadForecastOptions? options = null) =>
        MlLoadForecaster.Train(load, temperature, options);

    public LoadForecastBenchmarkResult BenchmarkLoadForecast(TimeSeries load, TimeSeries? temperature = null, LoadForecastBenchmarkOptions? options = null) =>
        LoadForecastBenchmark.Run(load, temperature, options);

    public (FaultLearningModel Model, FaultLearningReport Report) TrainFaultModels(FaultDatasetOptions? dataset = null, ClassificationTrainingOptions? model = null) =>
        FaultLearning.Train(FaultDatasetGenerator.Generate(dataset), model);

    public (IClassificationModel Classifier, CorrelationTopologyIdentifier Correlation, TopologyIdentificationReport Report) TrainTopologyIdentifier(
        TopologySnapshotOptions? options = null, ClassificationTrainingOptions? model = null) =>
        TopologyIdentification.Train(TopologyIdentification.GenerateSnapshots(options), options, model);

    public VoltVarRlAgent TrainVoltVarAgent(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null) =>
        VoltVarAgentTrainer.Train(environment, options);

    public VoltVarComparison CompareVoltVar(VoltVarEnvironmentOptions? environment = null, VoltVarRlOptions? options = null) =>
        VoltVarAgentTrainer.Compare(environment, options);
}

public static class LearningBuilderExtensions
{
    /// <summary>Registers <see cref="ILearningService"/> plus the PowerFlow, ShortCircuit and Forecasting modules it builds on.</summary>
    public static ElectricalBuilder AddLearning(this ElectricalBuilder b)
    {
        b.AddPowerFlow().AddShortCircuit().AddForecasting();
        if (b.TryAddModule("Learning")) b.Services.TryAddSingleton<ILearningService, LearningService>();
        return b;
    }
}
