using AquaCulture.Core;
using AquaCulture.Core.Species;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AquaCulture.ComputerVision;

/// <summary>Axis-aligned box in image pixels.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Iou(Box o)
    {
        double ix = Math.Max(0, Math.Min(X + Width, o.X + o.Width) - Math.Max(X, o.X)), iy = Math.Max(0, Math.Min(Y + Height, o.Y + o.Height) - Math.Max(Y, o.Y));
        double inter = ix * iy;
        return inter <= 0 ? 0 : inter / (Area + o.Area - inter);
    }
}

public sealed record Detection(string Label, int ClassId, double Confidence, Box Box);

/// <summary>Label sets for common aquaculture detectors.</summary>
public static class AquaLabels
{
    public static readonly string[] Animals = ["fish", "shrimp"];
    public static readonly string[] ShrimpDisease = ["healthy", "wssv", "ahpnd_ems", "ehp", "black_gill", "imnv"];
    public static readonly string[] FishDisease = ["healthy", "gill_disease", "fin_rot", "ulcer"];

    public static Text2 Describe(string label) => label switch
    {
        "healthy" => new("Healthy", "Sehat"),
        "wssv" => new("White spot (WSSV)", "Bercak putih (WSSV)"),
        "ahpnd_ems" => new("AHPND / EMS", "AHPND / EMS"),
        "ehp" => new("EHP", "EHP"),
        "black_gill" => new("Black gill", "Insang hitam"),
        "imnv" => new("Myonecrosis (IMNV)", "Mionekrosis (IMNV)"),
        "gill_disease" => new("Gill disease", "Penyakit insang"),
        "fin_rot" => new("Fin rot", "Busuk sirip"),
        "ulcer" => new("Ulcer", "Borok"),
        "red_body" => new("Red discoloration", "Tubuh kemerahan"),
        _ => new(label, label),
    };
}

/// <summary>YOLO output decoding and non-maximum suppression.</summary>
public static class YoloDecoder
{
    /// <summary>
    /// Decodes YOLOv8/v11 output [1, 4 + classes, anchors] (or YOLOv5 [1, anchors, 5 + classes] when <paramref name="v5"/>) into detections
    /// in original-image pixels, undoing the letterbox.
    /// </summary>
    public static IReadOnlyList<Detection> Decode(ReadOnlySpan<float> output, int anchors, int classes, string[] labels, double scale, int padX, int padY,
        double confThreshold = 0.25, double iouThreshold = 0.45, bool v5 = false)
    {
        var dets = new List<Detection>();
        for (int a = 0; a < anchors; a++)
        {
            float cx, cy, w, h, obj = 1;
            int stride = v5 ? 5 + classes : 4 + classes;
            int Ix(int k) => v5 ? a * stride + k : k * anchors + a;
            cx = output[Ix(0)]; cy = output[Ix(1)]; w = output[Ix(2)]; h = output[Ix(3)];
            int off = 4;
            if (v5) { obj = output[Ix(4)]; off = 5; }
            int best = 0; float bestScore = 0;
            for (int c = 0; c < classes; c++) { float sc = output[Ix(off + c)] * obj; if (sc > bestScore) { bestScore = sc; best = c; } }
            if (bestScore < confThreshold) continue;
            var box = new Box((cx - w / 2 - padX) / scale, (cy - h / 2 - padY) / scale, w / scale, h / scale);
            dets.Add(new Detection(best < labels.Length ? labels[best] : $"class{best}", best, bestScore, box));
        }
        return Nms(dets, iouThreshold);
    }

    /// <summary>Class-wise greedy NMS.</summary>
    public static IReadOnlyList<Detection> Nms(IEnumerable<Detection> dets, double iou)
    {
        var keep = new List<Detection>();
        foreach (var g in dets.GroupBy(d => d.ClassId))
        {
            var list = g.OrderByDescending(d => d.Confidence).ToList();
            while (list.Count > 0)
            {
                var top = list[0]; keep.Add(top); list.RemoveAt(0);
                list.RemoveAll(d => d.Box.Iou(top.Box) > iou);
            }
        }
        return keep.OrderByDescending(d => d.Confidence).ToList();
    }
}

/// <summary>
/// ONNX Runtime YOLO detector (fish/shrimp counting, disease classes). Export your model with
/// <c>yolo export model=best.pt format=onnx</c> and pass its class names.
/// </summary>
public sealed class OnnxDetector : IDisposable
{
    private readonly InferenceSession _session;

    public OnnxDetector(string modelPath, string[] labels, int inputSize = 640, SessionOptions? options = null)
    {
        _session = new InferenceSession(modelPath, options ?? new SessionOptions());
        Labels = labels; InputSize = inputSize;
    }

    public string[] Labels { get; }
    public int InputSize { get; }
    public double Confidence { get; init; } = 0.25;
    public double Iou { get; init; } = 0.45;

    public IReadOnlyList<Detection> Detect(RgbImage image)
    {
        var (lb, scale, px, py) = ImageOps.Letterbox(image, InputSize);
        var input = new DenseTensor<float>(ImageOps.ToChw(lb), [1, 3, InputSize, InputSize]);
        using var res = _session.Run([NamedOnnxValue.CreateFromTensor(_session.InputMetadata.Keys.First(), input)]);
        var t = res.First().AsTensor<float>();
        var dims = t.Dimensions.ToArray();
        var data = t.ToArray();
        bool v5 = dims[2] > dims[1];
        int anchors = v5 ? dims[1] : dims[2], classes = (v5 ? dims[2] - 5 : dims[1] - 4);
        return YoloDecoder.Decode(data, anchors, classes, Labels, scale, px, py, Confidence, Iou, v5);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>A connected foreground region.</summary>
public sealed record Blob(int Id, int Area, Box Box, double CentroidX, double CentroidY, double MajorAxisPx, double MinorAxisPx, int[] Pixels);

/// <summary>Per-animal analysis from the model-free pipeline.</summary>
public sealed record AnimalAnalysis(Blob Blob, double LengthCm, double WeightG, double WhiteSpotRatio, double RedRatio, double DarkHeadRatio, string Finding, double Score);

/// <summary>Counting, sizing and health screening result.</summary>
public sealed record CountResult(int Count, double MeanLengthCm, double MeanWeightG, double BiomassG, double CvLengthPct, IReadOnlyList<AnimalAnalysis> Animals)
{
    public int Suspicious => Animals.Count(a => a.Finding != "healthy");
}

/// <summary>
/// Model-free, explainable vision pipeline: background separation (colour distance to the water), 4-connected components,
/// moment-based length, weight from the species length–weight relationship and colour screening for white spots (WSSV),
/// red discoloration/ulcers and dark gills. Use it where no trained model is available or to pre-label images for training.
/// </summary>
public static class AnimalAnalyzer
{
    public static (bool[] Mask, (double R, double G, double B) Background) Segment(RgbImage img, double threshold = 60)
    {
        // background = median colour of the border
        var border = new List<(byte, byte, byte)>();
        for (int x = 0; x < img.Width; x += 2) { border.Add(img[x, 0]); border.Add(img[x, img.Height - 1]); }
        for (int y = 0; y < img.Height; y += 2) { border.Add(img[0, y]); border.Add(img[img.Width - 1, y]); }
        double Med(IEnumerable<double> v) { var a = v.OrderBy(z => z).ToArray(); return a[a.Length / 2]; }
        var bg = (Med(border.Select(b => (double)b.Item1)), Med(border.Select(b => (double)b.Item2)), Med(border.Select(b => (double)b.Item3)));
        var mask = new bool[img.Width * img.Height];
        for (int i = 0; i < mask.Length; i++)
        {
            double dr = img.Data[i * 3] - bg.Item1, dg = img.Data[i * 3 + 1] - bg.Item2, db = img.Data[i * 3 + 2] - bg.Item3;
            mask[i] = Math.Sqrt(dr * dr + dg * dg + db * db) > threshold;
        }
        return (mask, bg);
    }

    public static IReadOnlyList<Blob> Components(bool[] mask, int width, int height, int minArea = 80)
    {
        var label = new int[mask.Length];
        var blobs = new List<Blob>();
        var stack = new Stack<int>();
        int id = 0;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || label[start] != 0) continue;
            id++;
            var pix = new List<int>();
            stack.Push(start); label[start] = id;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); pix.Add(k);
                int x = k % width, y = k / width;
                if (x > 0 && mask[k - 1] && label[k - 1] == 0) { label[k - 1] = id; stack.Push(k - 1); }
                if (x < width - 1 && mask[k + 1] && label[k + 1] == 0) { label[k + 1] = id; stack.Push(k + 1); }
                if (y > 0 && mask[k - width] && label[k - width] == 0) { label[k - width] = id; stack.Push(k - width); }
                if (y < height - 1 && mask[k + width] && label[k + width] == 0) { label[k + width] = id; stack.Push(k + width); }
            }
            if (pix.Count < minArea) continue;
            double mx = pix.Average(p => p % width), my = pix.Average(p => p / width);
            double sxx = 0, syy = 0, sxy = 0;
            foreach (int p in pix) { double dx = p % width - mx, dy = p / width - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
            sxx /= pix.Count; syy /= pix.Count; sxy /= pix.Count;
            double tr = sxx + syy, det = sxx * syy - sxy * sxy, disc = Math.Sqrt(Math.Max(0, tr * tr / 4 - det));
            double l1 = tr / 2 + disc, l2 = Math.Max(0, tr / 2 - disc);
            int minX = pix.Min(p => p % width), maxX = pix.Max(p => p % width), minY = pix.Min(p => p / width), maxY = pix.Max(p => p / width);
            blobs.Add(new Blob(blobs.Count + 1, pix.Count, new Box(minX, minY, maxX - minX + 1, maxY - minY + 1), mx, my, 4 * Math.Sqrt(l1), 4 * Math.Sqrt(l2), pix.ToArray()));
        }
        return blobs;
    }

    /// <summary>Counts, sizes (cm via <paramref name="cmPerPixel"/>) and screens animals in an image.</summary>
    public static CountResult Analyze(RgbImage img, SpeciesProfile species, double cmPerPixel, int minArea = 80)
    {
        var (mask, _) = Segment(img);
        var blobs = Components(mask, img.Width, img.Height, minArea);
        var list = new List<AnimalAnalysis>();
        foreach (var b in blobs)
        {
            int white = 0, red = 0, darkHead = 0, head = 0;
            double ax = 0, ay = 0;
            foreach (int p in b.Pixels) { ax += p % img.Width; ay += p / img.Width; }
            foreach (int p in b.Pixels)
            {
                byte r = img.Data[p * 3], g = img.Data[p * 3 + 1], bl = img.Data[p * 3 + 2];
                var (h, s, v) = ImageOps.ToHsv(r, g, bl);
                if (v > 0.93 && s < 0.08) white++;
                if ((h < 15 || h > 345) && s > 0.55 && v > 0.45) red++;
                if (v < 0.22) darkHead++;
            }
            head = Math.Max(1, b.Pixels.Length / 5);
            double lengthCm = b.MajorAxisPx * cmPerPixel;
            double weight = species.WeightFromLength(lengthCm);
            double ws = (double)white / b.Area, rr = (double)red / b.Area, dk = (double)darkHead / b.Area;
            string finding = "healthy"; double score = 0;
            if (ws > 0.015) { finding = species.Group == SpeciesGroup.Shrimp ? "wssv" : "white_spots"; score = Math.Min(1, ws * 20); }
            if (rr > 0.03 && rr * 10 > score) { finding = species.Group == SpeciesGroup.Shrimp ? "red_body" : "ulcer"; score = Math.Min(1, rr * 10); }
            if (dk > 0.04 && dk * 8 > score) { finding = species.Group == SpeciesGroup.Shrimp ? "black_gill" : "gill_disease"; score = Math.Min(1, dk * 8); }
            list.Add(new AnimalAnalysis(b, Math.Round(lengthCm, 2), Math.Round(weight, 2), Math.Round(ws, 4), Math.Round(rr, 4), Math.Round(dk, 4), finding, Math.Round(score, 2)));
        }
        if (list.Count == 0) return new CountResult(0, 0, 0, 0, 0, list);
        double meanL = list.Average(a => a.LengthCm);
        double sdL = Math.Sqrt(list.Sum(a => (a.LengthCm - meanL) * (a.LengthCm - meanL)) / Math.Max(1, list.Count - 1));
        return new CountResult(list.Count, Math.Round(meanL, 2), Math.Round(list.Average(a => a.WeightG), 2), Math.Round(list.Sum(a => a.WeightG), 1), Math.Round(100 * sdL / meanL, 1), list);
    }

    /// <summary>Draws boxes coloured by finding (green healthy, coral suspicious).</summary>
    public static RgbImage Annotate(RgbImage img, CountResult r)
    {
        var o = img.Clone();
        foreach (var a in r.Animals) ImageOps.DrawBox(o, a.Blob.Box.X - 2, a.Blob.Box.Y - 2, a.Blob.Box.Width + 4, a.Blob.Box.Height + 4, a.Finding == "healthy" ? ((byte)90, (byte)230, (byte)140) : ((byte)255, (byte)96, (byte)72));
        return o;
    }
}
