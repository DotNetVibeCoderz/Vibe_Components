namespace Blazor.AI.Analytics;

/// <summary>Item-based collaborative filtering with mean-centred cosine similarity (adjusted cosine).</summary>
public sealed class Recommender
{
    private readonly double[,] _ratings;
    private readonly double[,] _similarity;

    public Recommender(IReadOnlyList<string> users, IReadOnlyList<string> items, double[,] ratings)
    {
        Users = users;
        Items = items;
        _ratings = ratings;
        _similarity = ItemSimilarity(ratings);
    }

    public IReadOnlyList<string> Users { get; }

    public IReadOnlyList<string> Items { get; }

    public double Rating(int user, int item) => _ratings[user, item];

    public double Similarity(int a, int b) => _similarity[a, b];

    public double[,] SimilarityMatrix => _similarity;

    /// <summary>Predicted ratings for the user's unrated items, best first, with the items that drove each prediction.</summary>
    public IReadOnlyList<(int Item, double Score, IReadOnlyList<int> Because)> Recommend(int user, int top = 5)
    {
        var results = new List<(int, double, IReadOnlyList<int>)>();
        for (var item = 0; item < Items.Count; item++)
        {
            if (_ratings[user, item] > 0)
            {
                continue;
            }

            double numerator = 0, denominator = 0;
            var neighbours = new List<(int Item, double Weight)>();
            for (var rated = 0; rated < Items.Count; rated++)
            {
                var r = _ratings[user, rated];
                var s = _similarity[item, rated];
                if (r <= 0 || s <= 0)
                {
                    continue;
                }

                numerator += s * r;
                denominator += s;
                neighbours.Add((rated, s * r));
            }

            if (denominator > 0)
            {
                results.Add((item, numerator / denominator, neighbours.OrderByDescending(n => n.Weight).Take(2).Select(n => n.Item).ToArray()));
            }
        }

        return results.OrderByDescending(r => r.Item2).Take(top).ToArray();
    }

    public IReadOnlyList<(int Item, double Similarity)> SimilarItems(int item, int top = 4) =>
        Enumerable.Range(0, Items.Count).Where(i => i != item).Select(i => (i, _similarity[item, i])).OrderByDescending(x => x.Item2).Take(top).ToArray();

    private static double[,] ItemSimilarity(double[,] ratings)
    {
        int users = ratings.GetLength(0), items = ratings.GetLength(1);
        var means = new double[users];
        for (var u = 0; u < users; u++)
        {
            var rated = Enumerable.Range(0, items).Where(i => ratings[u, i] > 0).Select(i => ratings[u, i]).ToArray();
            means[u] = rated.Length == 0 ? 0 : rated.Average();
        }

        var similarity = new double[items, items];
        for (var a = 0; a < items; a++)
        {
            similarity[a, a] = 1;
            for (var b = a + 1; b < items; b++)
            {
                double dot = 0, na = 0, nb = 0;
                for (var u = 0; u < users; u++)
                {
                    if (ratings[u, a] <= 0 || ratings[u, b] <= 0)
                    {
                        continue;
                    }

                    var x = ratings[u, a] - means[u];
                    var y = ratings[u, b] - means[u];
                    dot += x * y;
                    na += x * x;
                    nb += y * y;
                }

                var s = na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
                similarity[a, b] = similarity[b, a] = s;
            }
        }

        return similarity;
    }
}
