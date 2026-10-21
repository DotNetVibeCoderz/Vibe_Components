using System.Numerics.Tensors;

namespace Blazor.AI.Core.Vectors;

/// <summary>SIMD helpers over <see cref="TensorPrimitives"/>.</summary>
public static class VectorMath
{
    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b) =>
        a.Length == 0 || a.Length != b.Length ? 0f : TensorPrimitives.CosineSimilarity(a, b);

    /// <summary>Returns a unit-length copy of <paramref name="vector"/> (or zeros for a zero vector).</summary>
    public static float[] Normalize(ReadOnlySpan<float> vector)
    {
        var result = vector.ToArray();
        var norm = TensorPrimitives.Norm(result);
        if (norm > 0)
        {
            TensorPrimitives.Divide(result, norm, result);
        }

        return result;
    }

    /// <summary>Full cosine similarity matrix for a set of vectors (used by embedding studios).</summary>
    public static double[,] SimilarityMatrix(IReadOnlyList<ReadOnlyMemory<float>> vectors)
    {
        var n = vectors.Count;
        var matrix = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            matrix[i, i] = 1;
            for (var j = i + 1; j < n; j++)
            {
                var value = Cosine(vectors[i].Span, vectors[j].Span);
                matrix[i, j] = value;
                matrix[j, i] = value;
            }
        }

        return matrix;
    }
}
