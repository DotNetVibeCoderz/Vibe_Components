using TypeSafeSdk;

namespace Blazor.AI.Decision;

/// <summary>Options for the TypeSafe System One integration.</summary>
public sealed class TypeSafeDecisionOptions
{
    /// <summary>API key. When empty, the SDK's deterministic offline simulator is used.</summary>
    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public string? Endpoint { get; set; }

    public bool ForceSimulator { get; set; }
}

/// <summary>A labelled-choice decision with per-label probabilities.</summary>
public sealed record ChoiceDecision(string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities);

/// <summary>A score on the criteria scale (0 = first criterion, n-1 = last) with the nearest label.</summary>
public sealed record ScoreDecision(double Score, string? Label, double Confidence, IReadOnlyDictionary<string, double> Probabilities, int Levels)
{
    /// <summary>The score scaled to 0..1.</summary>
    public double Normalized => Levels <= 1 ? Score : Math.Clamp(Score / (Levels - 1), 0, 1);
}

/// <summary>
/// Strongly-typed decisions with TypeSafe System One (via Gravicode.TypeSafeSdk): choice, noul (graded yes/no)
/// and score questions over any state object. Falls back to the SDK simulator without an API key, so demos work offline.
/// </summary>
public sealed class TypeSafeDecisionService : IAsyncDisposable
{
    private readonly TypeSafeClient _client;

    public TypeSafeDecisionService(TypeSafeDecisionOptions options)
    {
        IsSimulated = options.ForceSimulator || string.IsNullOrWhiteSpace(options.ApiKey);
        var sdkOptions = new TypeSafeOptions { Simulator = IsSimulated, ApiKey = options.ApiKey ?? string.Empty };
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            sdkOptions.DefaultModel = options.Model;
        }

        if (!string.IsNullOrWhiteSpace(options.Endpoint))
        {
            sdkOptions.Endpoint = options.Endpoint;
        }

        _client = new TypeSafeClient(sdkOptions);
    }

    /// <summary>True when answers come from the offline simulator.</summary>
    public bool IsSimulated { get; }

    /// <summary>Picks one of <paramref name="labels"/> for the state, e.g. a ticket's category.</summary>
    public async Task<ChoiceDecision> ChooseAsync(object state, IReadOnlyList<string> labels, string? instructions = null, CancellationToken cancellationToken = default)
    {
        var schema = Choice.Create([.. labels]);
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            schema = schema.WithInstructions(instructions);
        }

        var response = await _client.SystemOneAsync(state, new Dictionary<string, object> { ["decision"] = schema }, model: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var answer = response.ChoiceAnswers.Values.FirstOrDefault();
        if (answer is not null)
        {
            return new ChoiceDecision(answer.Choice, answer.Confidence, answer.Probabilities);
        }

        var value = response.Choices.Values.FirstOrDefault() ?? throw new InvalidOperationException("TypeSafe returned no choice.");
        return new ChoiceDecision(value.Choice, value.Confidence, new Dictionary<string, double> { [value.Choice] = value.Confidence });
    }

    /// <summary>A graded yes/no ("noul") in 0..1, e.g. "Is this transaction fraudulent?".</summary>
    public async Task<double> AskAsync(object state, string question, string? trueMeaning = null, string? falseMeaning = null, CancellationToken cancellationToken = default)
    {
        var schema = Noul(question, trueMeaning, falseMeaning);
        var response = await _client.SystemOneAsync(state, new Dictionary<string, object> { ["answer"] = schema }, model: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Nouls.Values.FirstOrDefault()?.Noul ?? throw new InvalidOperationException("TypeSafe returned no answer.");
    }

    /// <summary>Several graded questions at once (one request), e.g. risk factors. Keys are question names.</summary>
    public async Task<IReadOnlyDictionary<string, double>> AskManyAsync(object state, IReadOnlyDictionary<string, string> questions, CancellationToken cancellationToken = default)
    {
        var payload = questions.ToDictionary(q => q.Key, q => (object)Noul(q.Value, null, null));
        var response = await _client.SystemOneAsync(state, payload, model: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        return response.Nouls.ToDictionary(n => n.Key, n => n.Value.Noul);
    }

    /// <summary>A score against ordered criteria (worst → best), e.g. credit quality.</summary>
    public async Task<ScoreDecision> ScoreAsync(object state, string instructions, IReadOnlyList<string> criteria, CancellationToken cancellationToken = default)
    {
        var schema = new ScoreSchema(instructions, criteria);
        var response = await _client.SystemOneAsync(state, new Dictionary<string, object> { ["score"] = schema }, model: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        var score = response.Scores.Values.FirstOrDefault() ?? throw new InvalidOperationException("TypeSafe returned no score.");
        return new ScoreDecision(score.Score, score.NearestLabel, score.Confidence, score.Probabilities, criteria.Count);
    }

    private static NoulSchema Noul(string question, string? whenTrue, string? whenFalse) => (whenTrue, whenFalse) switch
    {
        (null, null) => new NoulSchema(question),
        (not null, null) => new NoulSchema(question, True: whenTrue),
        (null, not null) => new NoulSchema(question, False: whenFalse),
        _ => new NoulSchema(question, whenTrue, whenFalse),
    };

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
