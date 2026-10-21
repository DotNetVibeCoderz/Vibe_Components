namespace Blazor.AI.MediaPipe;

/// <summary>A labelled image example (embedding plus an optional thumbnail).</summary>
public sealed record LabelledExample(string Label, float[] Embedding, string? Thumbnail = null);

/// <summary>A kNN prediction from <see cref="ExampleGallery"/>.</summary>
public sealed record GalleryMatch(string Label, double Similarity, double Confidence, int Votes);

/// <summary>
/// Few-shot classifier: users teach labels by example; prediction is a similarity-weighted kNN vote over image
/// embeddings (MobileNetV3, L2-normalised). Thread-safe for one writer and concurrent readers.
/// </summary>
public sealed class ExampleGallery
{
    private volatile IReadOnlyList<LabelledExample> _examples = [];

    public IReadOnlyList<LabelledExample> Examples => _examples;

    public IReadOnlyList<string> Labels => _examples.Select(e => e.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void Add(LabelledExample example) => _examples = [.. _examples, example];

    public void Remove(string label) => _examples = _examples.Where(e => !string.Equals(e.Label, label, StringComparison.OrdinalIgnoreCase)).ToList();

    public void Clear() => _examples = [];

    /// <summary>Best label by weighted vote of the <paramref name="k"/> most similar examples; null when the gallery is empty.</summary>
    public GalleryMatch? Predict(float[] embedding, int k = 5)
    {
        var examples = _examples;
        if (examples.Count == 0)
        {
            return null;
        }

        var nearest = examples.Select(e => (e, s: FaceGallery.Cosine(embedding, e.Embedding))).OrderByDescending(x => x.s).Take(k).ToList();
        var votes = nearest.GroupBy(x => x.e.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Label: g.Key, Weight: g.Sum(x => Math.Max(0, x.s)), Best: g.Max(x => x.s), Count: g.Count()))
            .OrderByDescending(v => v.Weight).ToList();
        var total = votes.Sum(v => v.Weight);
        var top = votes[0];
        return new GalleryMatch(top.Label, top.Best, total <= 0 ? 0 : top.Weight / total, top.Count);
    }
}
