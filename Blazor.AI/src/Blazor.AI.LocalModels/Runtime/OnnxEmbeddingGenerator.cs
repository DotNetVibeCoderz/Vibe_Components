using Gravicode.HFNet.GraviTokenizers;
using Microsoft.Extensions.AI;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Blazor.AI.LocalModels;

/// <summary>
/// Sentence embeddings from an ONNX model (e.g. an exported sentence-transformer) with ONNX Runtime, tokenised by HF.Net.
/// Mean-pools the last hidden state over the attention mask and L2-normalises.
/// </summary>
public sealed class OnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly InferenceSession _session;
    private readonly HfTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly string _modelId;
    private readonly Lock _gate = new();

    /// <param name="modelPath">Path to the .onnx file.</param>
    /// <param name="tokenizerPath">Path to tokenizer.json (or a directory containing it).</param>
    public OnnxEmbeddingGenerator(string modelPath, string tokenizerPath, int maxLength = 256, SessionOptions? sessionOptions = null)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
        _tokenizer = Directory.Exists(tokenizerPath) ? HfTokenizer.FromDirectory(tokenizerPath) : HfTokenizer.Load(tokenizerPath);
        _maxLength = maxLength;
        _modelId = Path.GetFileNameWithoutExtension(modelPath);
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var text in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new Embedding<float>(Embed(text)) { ModelId = _modelId });
        }

        return Task.FromResult(result);
    }

    public float[] Embed(string text)
    {
        var encoding = _tokenizer.Encode(text);
        var length = Math.Min(encoding.Length, _maxLength);
        var ids = new DenseTensor<long>([1, length]);
        var mask = new DenseTensor<long>([1, length]);
        var types = new DenseTensor<long>([1, length]);
        for (var i = 0; i < length; i++)
        {
            ids[0, i] = encoding.Ids[i];
            mask[0, i] = encoding.AttentionMask[i];
            types[0, i] = encoding.TypeIds.Count > i ? encoding.TypeIds[i] : 0;
        }

        var inputs = new List<NamedOnnxValue>();
        foreach (var name in _session.InputMetadata.Keys)
        {
            inputs.Add(name switch
            {
                _ when name.Contains("mask", StringComparison.OrdinalIgnoreCase) => NamedOnnxValue.CreateFromTensor(name, mask),
                _ when name.Contains("type", StringComparison.OrdinalIgnoreCase) => NamedOnnxValue.CreateFromTensor(name, types),
                _ => NamedOnnxValue.CreateFromTensor(name, ids),
            });
        }

        float[] pooled;
        lock (_gate)
        {
            using var outputs = _session.Run(inputs);
            var hidden = outputs.First().AsTensor<float>();
            var dims = hidden.Dimensions.ToArray();
            if (dims.Length == 2)
            {
                pooled = Enumerable.Range(0, dims[1]).Select(d => hidden[0, d]).ToArray();
            }
            else
            {
                pooled = new float[dims[2]];
                float count = 0;
                for (var t = 0; t < dims[1]; t++)
                {
                    if (mask[0, t] == 0) continue;
                    count++;
                    for (var d = 0; d < dims[2]; d++) pooled[d] += hidden[0, t, d];
                }

                for (var d = 0; d < pooled.Length; d++) pooled[d] /= Math.Max(1, count);
            }
        }

        var norm = MathF.Sqrt(pooled.Sum(v => v * v));
        return norm > 0 ? pooled.Select(v => v / norm).ToArray() : pooled;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(EmbeddingGeneratorMetadata) ? new EmbeddingGeneratorMetadata("onnxruntime", null, _modelId) : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() => _session.Dispose();
}
