using AquaCulture.Core.Data;

namespace AquaCulture.ComputerVision;

/// <summary>8-bit RGB image (row-major, 3 bytes per pixel).</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? data = null)
    {
        Width = width; Height = height;
        Data = data ?? new byte[width * height * 3];
        if (Data.Length != width * height * 3) throw new ArgumentException("Data size mismatch.");
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get { int i = (y * Width + x) * 3; return (Data[i], Data[i + 1], Data[i + 2]); }
        set { int i = (y * Width + x) * 3; Data[i] = value.R; Data[i + 1] = value.G; Data[i + 2] = value.B; }
    }

    public RgbImage Clone() => new(Width, Height, (byte[])Data.Clone());

    /// <summary>From interleaved BGR bytes (OpenCV order).</summary>
    public static RgbImage FromBgr(int width, int height, ReadOnlySpan<byte> bgr)
    {
        var img = new RgbImage(width, height);
        for (int i = 0; i < width * height; i++) { img.Data[i * 3] = bgr[i * 3 + 2]; img.Data[i * 3 + 1] = bgr[i * 3 + 1]; img.Data[i * 3 + 2] = bgr[i * 3]; }
        return img;
    }

    /// <summary>Binary PPM (P6) for dependency-free debugging output.</summary>
    public byte[] ToPpm()
    {
        var head = System.Text.Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n");
        return [.. head, .. Data];
    }

    /// <summary>RGBA bytes (opaque) for PNG encoders and UI bitmaps.</summary>
    public byte[] ToRgba()
    {
        var o = new byte[Width * Height * 4];
        for (int i = 0; i < Width * Height; i++) { o[i * 4] = Data[i * 3]; o[i * 4 + 1] = Data[i * 3 + 1]; o[i * 4 + 2] = Data[i * 3 + 2]; o[i * 4 + 3] = 255; }
        return o;
    }
}

/// <summary>Image operations for model input and analysis.</summary>
public static class ImageOps
{
    /// <summary>Bilinear resize.</summary>
    public static RgbImage Resize(RgbImage src, int width, int height)
    {
        var dst = new RgbImage(width, height);
        double sx = (double)src.Width / width, sy = (double)src.Height / height;
        for (int y = 0; y < height; y++)
        {
            double fy = (y + 0.5) * sy - 0.5; int y0 = Math.Clamp((int)Math.Floor(fy), 0, src.Height - 1), y1 = Math.Min(y0 + 1, src.Height - 1); double wy = Math.Clamp(fy - y0, 0, 1);
            for (int x = 0; x < width; x++)
            {
                double fx = (x + 0.5) * sx - 0.5; int x0 = Math.Clamp((int)Math.Floor(fx), 0, src.Width - 1), x1 = Math.Min(x0 + 1, src.Width - 1); double wx = Math.Clamp(fx - x0, 0, 1);
                for (int c = 0; c < 3; c++)
                {
                    double v = (1 - wy) * ((1 - wx) * src.Data[(y0 * src.Width + x0) * 3 + c] + wx * src.Data[(y0 * src.Width + x1) * 3 + c])
                             + wy * ((1 - wx) * src.Data[(y1 * src.Width + x0) * 3 + c] + wx * src.Data[(y1 * src.Width + x1) * 3 + c]);
                    dst.Data[(y * width + x) * 3 + c] = (byte)Math.Round(v);
                }
            }
        }
        return dst;
    }

    /// <summary>Letterbox into a square of <paramref name="size"/> (YOLO input) with grey padding; returns scale and offsets.</summary>
    public static (RgbImage Image, double Scale, int PadX, int PadY) Letterbox(RgbImage src, int size = 640)
    {
        double scale = Math.Min((double)size / src.Width, (double)size / src.Height);
        int w = (int)Math.Round(src.Width * scale), h = (int)Math.Round(src.Height * scale);
        var resized = Resize(src, w, h);
        var dst = new RgbImage(size, size);
        Array.Fill(dst.Data, (byte)114);
        int px = (size - w) / 2, py = (size - h) / 2;
        for (int y = 0; y < h; y++) Buffer.BlockCopy(resized.Data, y * w * 3, dst.Data, ((y + py) * size + px) * 3, w * 3);
        return (dst, scale, px, py);
    }

    /// <summary>CHW float tensor normalised to 0–1 (optionally with per-channel mean/std).</summary>
    public static float[] ToChw(RgbImage img, float[]? mean = null, float[]? std = null)
    {
        int n = img.Width * img.Height;
        var t = new float[3 * n];
        for (int i = 0; i < n; i++)
            for (int c = 0; c < 3; c++)
            {
                float v = img.Data[i * 3 + c] / 255f;
                if (mean is not null && std is not null) v = (v - mean[c]) / std[c];
                t[c * n + i] = v;
            }
        return t;
    }

    /// <summary>RGB → HSV (H 0–360, S and V 0–1).</summary>
    public static (double H, double S, double V) ToHsv(byte r, byte g, byte b)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0, max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
        double h = d == 0 ? 0 : max == rf ? 60 * ((gf - bf) / d % 6) : max == gf ? 60 * ((bf - rf) / d + 2) : 60 * ((rf - gf) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    /// <summary>Draws a rectangle outline (for annotated output).</summary>
    public static void DrawBox(RgbImage img, double x, double y, double w, double h, (byte R, byte G, byte B) color, int thickness = 2)
    {
        int x0 = (int)x, y0 = (int)y, x1 = (int)(x + w), y1 = (int)(y + h);
        for (int t = 0; t < thickness; t++)
        {
            for (int i = x0; i <= x1; i++) { Set(img, i, y0 + t, color); Set(img, i, y1 - t, color); }
            for (int j = y0; j <= y1; j++) { Set(img, x0 + t, j, color); Set(img, x1 - t, j, color); }
        }
    }

    private static void Set(RgbImage img, int x, int y, (byte, byte, byte) c) { if (x >= 0 && y >= 0 && x < img.Width && y < img.Height) img[x, y] = c; }
}

/// <summary>Deterministic synthetic tank/tray images (top view) for demos and tests.</summary>
public static class SyntheticAquaImage
{
    public sealed record Animal(double X, double Y, double LengthPx, double WidthPx, double AngleRad, bool WhiteSpots, bool RedLesion, bool BlackGill);

    /// <summary>Renders animals on a turquoise background with soft caustics and noise.</summary>
    public static (RgbImage Image, IReadOnlyList<Animal> Animals) Create(int width, int height, int count, bool shrimp, ulong seed = 1, double diseasedFraction = 0.3)
    {
        var rng = new DeterministicRandom(seed);
        var img = new RgbImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double caustic = 0.5 + 0.5 * Math.Sin(x * 0.07 + Math.Sin(y * 0.05) * 2) * Math.Sin(y * 0.06 + Math.Cos(x * 0.04) * 2);
                img[x, y] = ((byte)(28 + 18 * caustic), (byte)(92 + 22 * caustic), (byte)(104 + 20 * caustic));
            }
        var animals = new List<Animal>();
        int tries = 0;
        while (animals.Count < count && tries++ < count * 50)
        {
            double len = shrimp ? rng.Uniform(38, 62) : rng.Uniform(55, 95), wid = len * (shrimp ? 0.24 : 0.32);
            double cx = rng.Uniform(len, width - len), cy = rng.Uniform(len, height - len);
            if (animals.Any(a => Math.Sqrt((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy)) < (a.LengthPx + len) / 2 + 6)) continue;
            bool sick = rng.NextDouble() < diseasedFraction;
            int kind = rng.Next(3);
            animals.Add(new Animal(cx, cy, len, wid, rng.Uniform(0, Math.PI), sick && kind == 0, sick && kind == 1, sick && kind == 2 && shrimp));
        }
        foreach (var a in animals)
        {
            double c = Math.Cos(a.AngleRad), s = Math.Sin(a.AngleRad);
            int r = (int)a.LengthPx;
            for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    double u = (dx * c + dy * s) / (a.LengthPx / 2), v = (-dx * s + dy * c) / (a.WidthPx / 2);
                    double taper = shrimp ? 1 - 0.35 * Math.Max(0, u) : 1 - 0.25 * Math.Abs(u);
                    if (u * u + v * v / (taper * taper) > 1) continue;
                    int x = (int)(a.X + dx), y = (int)(a.Y + dy);
                    if (x < 0 || y < 0 || x >= img.Width || y >= img.Height) continue;
                    double shade = 0.85 + 0.15 * (1 - Math.Abs(v));
                    (byte R, byte G, byte B) col = shrimp ? ((byte)(196 * shade), (byte)(178 * shade), (byte)(150 * shade)) : ((byte)(170 * shade), (byte)(176 * shade), (byte)(168 * shade));
                    if (a.BlackGill && u < -0.6) col = (40, 34, 30);
                    if (a.RedLesion && Math.Abs(u - 0.1) < 0.18 && Math.Abs(v) < 0.6) col = (196, 52, 46);
                    if (a.WhiteSpots && ((int)(u * 9) + (int)(v * 5)) % 3 == 0 && Math.Abs(u) < 0.8 && (dx * 7 + dy * 3) % 5 == 0) col = (250, 250, 248);
                    img[x, y] = col;
                }
        }
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = (byte)Math.Clamp(img.Data[i] + rng.Normal(0, 3), 0, 255);
        return (img, animals);
    }
}
