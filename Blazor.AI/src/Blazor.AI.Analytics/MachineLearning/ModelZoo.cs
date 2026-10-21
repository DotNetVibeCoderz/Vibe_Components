using Gravicode.Science.GraviLearn;
using Gravicode.Science.GraviLearn.Linear;
using Gravicode.Science.GraviLearn.Neighbors;
using Gravicode.Science.GraviLearn.Trees;

namespace Blazor.AI.Analytics;

/// <summary>A trainable GraviLearn algorithm with the hyperparameters the UI exposes.</summary>
public sealed record ModelSpec(string Name, MlTask Task, string Description, IReadOnlyList<HyperParameter> Parameters, Func<IReadOnlyDictionary<string, double>, IEstimator> Create)
{
    public IEstimator Build(IReadOnlyDictionary<string, double>? values = null) =>
        Create(Parameters.ToDictionary(p => p.Name, p => values?.GetValueOrDefault(p.Name, p.Default) ?? p.Default));
}

public sealed record HyperParameter(string Name, string Label, double Default, double Min, double Max, double Step = 1);

/// <summary>The GraviLearn algorithms available in the workbenches.</summary>
public static class ModelZoo
{
    public static IReadOnlyList<ModelSpec> Classifiers { get; } =
    [
        new("Logistic regression", MlTask.Classification, "Fast, interpretable linear baseline.",
            [new("l2", "L2 penalty", 0.01, 0, 1, 0.01), new("iterations", "Iterations", 500, 50, 3000, 50)],
            p => new LogisticRegression(learningRate: 0.1, maxIterations: (int)p["iterations"], l2Penalty: p["l2"])),
        new("Decision tree", MlTask.Classification, "Readable if/else rules; can overfit when deep.",
            [new("depth", "Max depth", 5, 1, 20)],
            p => new DecisionTree(maxDepth: (int)p["depth"], seed: 1)),
        new("Random forest", MlTask.Classification, "Many trees vote; strong default for tabular data.",
            [new("trees", "Trees", 80, 10, 400, 10), new("depth", "Max depth", 8, 2, 30)],
            p => new RandomForestClassifier(nTrees: (int)p["trees"], maxDepth: (int)p["depth"], seed: 1)),
        new("Gradient boosting", MlTask.Classification, "Trees that fix each other's errors; often the most accurate.",
            [new("trees", "Trees", 100, 10, 500, 10), new("rate", "Learning rate", 0.1, 0.01, 1, 0.01), new("depth", "Max depth", 3, 1, 8)],
            p => new GradientBoostingClassifier(nTrees: (int)p["trees"], learningRate: p["rate"], maxDepth: (int)p["depth"], seed: 1)),
        new("k-nearest neighbours", MlTask.Classification, "Classifies by the most similar examples.",
            [new("k", "k", 7, 1, 50)],
            p => new KNearestNeighborsClassifier(k: (int)p["k"], distanceWeighted: true)),
        new("Gaussian naive Bayes", MlTask.Classification, "Probabilistic baseline that trains instantly.",
            [],
            _ => new GaussianNaiveBayes()),
    ];

    public static IReadOnlyList<ModelSpec> Regressors { get; } =
    [
        new("Linear regression", MlTask.Regression, "Ordinary least squares; coefficients are directly interpretable.",
            [],
            _ => new LinearRegression()),
        new("Ridge regression", MlTask.Regression, "Linear with L2 shrinkage; stable with correlated features.",
            [new("alpha", "Alpha", 1, 0, 100, 0.5)],
            p => new RidgeRegression(alpha: p["alpha"])),
        new("Lasso regression", MlTask.Regression, "Linear with L1 penalty; drops weak features to zero.",
            [new("alpha", "Alpha", 0.1, 0.001, 10, 0.01)],
            p => new LassoRegression(alpha: p["alpha"])),
        new("Random forest", MlTask.Regression, "Averages many trees; captures non-linear effects.",
            [new("trees", "Trees", 80, 10, 400, 10), new("depth", "Max depth", 10, 2, 30)],
            p => new RandomForestRegressor(nTrees: (int)p["trees"], maxDepth: (int)p["depth"], seed: 1)),
        new("Gradient boosting", MlTask.Regression, "Boosted trees; usually best on tabular regression.",
            [new("trees", "Trees", 150, 10, 600, 10), new("rate", "Learning rate", 0.1, 0.01, 1, 0.01), new("depth", "Max depth", 3, 1, 8)],
            p => new GradientBoostingRegressor(nTrees: (int)p["trees"], learningRate: p["rate"], maxDepth: (int)p["depth"], seed: 1)),
        new("k-nearest neighbours", MlTask.Regression, "Averages the most similar examples.",
            [new("k", "k", 7, 1, 50)],
            p => new KNearestNeighborsRegressor(k: (int)p["k"], distanceWeighted: true)),
    ];

    public static IReadOnlyList<ModelSpec> For(MlTask task) => task == MlTask.Regression ? Regressors : Classifiers;
}
