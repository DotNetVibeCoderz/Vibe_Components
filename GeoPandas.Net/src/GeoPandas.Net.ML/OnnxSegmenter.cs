using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RasterGrid = GeoPandas.Net.Raster.Raster;

namespace GeoPandas.Net.ML;

/// <summary>
/// Semantic segmentation of rasters with an ONNX model (U-Net, DeepLab, SegFormer… exported to ONNX): bands are selected and normalised,
/// the image is cut into overlapping tiles (NCHW float32), and each tile's centre is written to a mosaic of class probabilities plus an
/// arg-max class raster. Works with dynamic (H, W) and fixed-size models (edge tiles are zero-padded).
/// </summary>
public sealed class OnnxSegmenter : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _input, _output;
    private readonly int? _fixedH, _fixedW;

    public OnnxSegmenter(string modelPath, SessionOptions? options = null)
    {
        _session = options is null ? new InferenceSession(modelPath) : new InferenceSession(modelPath, options);
        _input = _session.InputMetadata.Keys.First();
        _output = _session.OutputMetadata.Keys.First();
        var dims = _session.InputMetadata[_input].Dimensions;
        if (dims.Length == 4 && dims[2] > 0 && dims[3] > 0) { _fixedH = dims[2]; _fixedW = dims[3]; }
    }

    /// <summary>Band indexes fed to the model, in order (default: the first N bands the model expects).</summary>
    public int[]? Bands { get; init; }
    /// <summary>Per-band normalisation: (value − mean) / std.</summary>
    public float[]? Mean { get; init; }
    public float[]? Std { get; init; }
    /// <summary>Tile size for dynamic models (fixed-size models use their own).</summary>
    public int TileSize { get; init; } = 512;
    /// <summary>Overlap between tiles; only the central part of each tile is kept.</summary>
    public int Overlap { get; init; } = 32;
    /// <summary>Apply softmax to the output (when the model returns logits).</summary>
    public bool Softmax { get; init; }
    /// <summary>Class names (optional) for the probability bands.</summary>
    public IReadOnlyList<string>? ClassNames { get; init; }

    public string InputName => _input;
    public string OutputName => _output;

    /// <summary>Segments a raster: returns the class raster (arg-max) and the probability raster (one band per class).</summary>
    public (RasterGrid Classes, RasterGrid Probabilities) Predict(RasterGrid image)
    {
        int inChannels = _session.InputMetadata[_input].Dimensions is { Length: 4 } d && d[1] > 0 ? d[1] : (Bands?.Length ?? image.BandCount);
        var bands = Bands ?? Enumerable.Range(0, inChannels).ToArray();
        int th = _fixedH ?? Math.Min(TileSize, image.Height), tw = _fixedW ?? Math.Min(TileSize, image.Width);
        int stepY = Math.Max(1, th - 2 * Overlap), stepX = Math.Max(1, tw - 2 * Overlap);
        int W = image.Width, H = image.Height;
        float[][]? probs = null;
        int classes = 0;
        for (int y0 = 0; ; y0 += stepY)
        {
            int ty = Math.Min(y0, Math.Max(0, H - th));
            for (int x0 = 0; ; x0 += stepX)
            {
                int tx = Math.Min(x0, Math.Max(0, W - tw));
                var input = new DenseTensor<float>([1, bands.Length, th, tw]);
                for (int b = 0; b < bands.Length; b++)
                {
                    var src = image.Bands[bands[b]];
                    float mean = Mean is null ? 0 : Mean[b], std = Std is null ? 1 : Std[b];
                    for (int r = 0; r < th; r++)
                        for (int c = 0; c < tw; c++)
                        {
                            int rr = ty + r, cc = tx + c;
                            float v = rr < H && cc < W ? src[rr * W + cc] : 0;
                            input[0, b, r, c] = float.IsNaN(v) || image.IsNoData(v) ? 0 : (v - mean) / std;
                        }
                }
                using var results = _session.Run([NamedOnnxValue.CreateFromTensor(_input, input)]);
                var output = results.First(v => v.Name == _output).AsTensor<float>();
                classes = output.Dimensions[1];
                probs ??= Enumerable.Range(0, classes).Select(_ => new float[W * H]).ToArray();
                // keep the tile centre (full tile at the image edges)
                int r0 = ty == 0 ? 0 : Overlap, c0 = tx == 0 ? 0 : Overlap;
                int r1 = ty + th >= H ? th : th - Overlap, c1 = tx + tw >= W ? tw : tw - Overlap;
                for (int r = r0; r < r1 && ty + r < H; r++)
                    for (int c = c0; c < c1 && tx + c < W; c++)
                    {
                        int i = (ty + r) * W + tx + c;
                        if (Softmax)
                        {
                            float max = float.MinValue;
                            for (int k = 0; k < classes; k++) max = Math.Max(max, output[0, k, r, c]);
                            double sum = 0;
                            for (int k = 0; k < classes; k++) sum += Math.Exp(output[0, k, r, c] - max);
                            for (int k = 0; k < classes; k++) probs[k][i] = (float)(Math.Exp(output[0, k, r, c] - max) / sum);
                        }
                        else for (int k = 0; k < classes; k++) probs[k][i] = output[0, k, r, c];
                    }
                if (tx + tw >= W) break;
            }
            if (ty + th >= H) break;
        }
        var cls = new float[W * H];
        for (int i = 0; i < cls.Length; i++)
        {
            int best = 0;
            for (int k = 1; k < classes; k++) if (probs![k][i] > probs[best][i]) best = k;
            cls[i] = best;
        }
        var prob = new RasterGrid(W, H, probs!, image.Transform, image.Crs, float.NaN)
        {
            BandNames = ClassNames?.Take(classes).ToArray() ?? Enumerable.Range(0, classes).Select(k => $"class_{k}").ToArray()
        };
        return (new RasterGrid(W, H, [cls], image.Transform, image.Crs) { BandNames = ["class"] }, prob);
    }

    public void Dispose() => _session.Dispose();
}
