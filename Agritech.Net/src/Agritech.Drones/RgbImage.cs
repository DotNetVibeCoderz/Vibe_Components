namespace Agritech.Drones;

/// <summary>Interleaved 8-bit RGB image buffer (row-major).</summary>
public sealed class RgbImage
{
    public RgbImage(int width, int height, byte[]? pixels = null)
    {
        Width = width; Height = height;
        Pixels = pixels ?? new byte[width * height * 3];
        if (Pixels.Length != width * height * 3) throw new ArgumentException("Pixel buffer must be width*height*3 bytes.");
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public (byte R, byte G, byte B) this[int x, int y]
    {
        get { int i = (y * Width + x) * 3; return (Pixels[i], Pixels[i + 1], Pixels[i + 2]); }
        set { int i = (y * Width + x) * 3; Pixels[i] = value.R; Pixels[i + 1] = value.G; Pixels[i + 2] = value.B; }
    }

    /// <summary>Writes a binary PPM (P6) — viewable in most image tools without extra dependencies.</summary>
    public void SavePpm(string path)
    {
        using var fs = File.Create(path);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{Width} {Height}\n255\n");
        fs.Write(header);
        fs.Write(Pixels);
    }

    /// <summary>Synthetic top-down row-crop image with weeds, used for demos and tests.</summary>
    public static RgbImage SyntheticRowCrop(int width = 320, int height = 240, int rows = 5, int weeds = 25, int seed = 1)
    {
        var img = new RgbImage(width, height);
        var rng = new Random(seed);
        double rowSpacing = width / (double)rows;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int n = rng.Next(-12, 12);
                img[x, y] = ((byte)(120 + n), (byte)(92 + n), (byte)(66 + n)); // soil
            }
        for (int r = 0; r < rows; r++)
        {
            double cx = rowSpacing * (r + 0.5);
            for (int y = 0; y < height; y++)
                for (int x = (int)(cx - rowSpacing * 0.12); x <= (int)(cx + rowSpacing * 0.12); x++)
                    if (x >= 0 && x < width && rng.NextDouble() < 0.85)
                        img[x, y] = ((byte)rng.Next(40, 70), (byte)rng.Next(130, 175), (byte)rng.Next(35, 60));
        }
        for (int w = 0; w < weeds; w++)
        {
            // weeds between rows
            int r = rng.Next(rows - 1);
            int cx = (int)(rowSpacing * (r + 1) + rng.Next(-(int)(rowSpacing * 0.2), (int)(rowSpacing * 0.2)));
            int cy = rng.Next(height), rad = rng.Next(3, 7);
            for (int y = cy - rad; y <= cy + rad; y++)
                for (int x = cx - rad; x <= cx + rad; x++)
                    if (x >= 0 && y >= 0 && x < width && y < height && (x - cx) * (x - cx) + (y - cy) * (y - cy) <= rad * rad)
                        img[x, y] = ((byte)rng.Next(70, 100), (byte)rng.Next(150, 190), (byte)rng.Next(40, 70));
        }
        return img;
    }
}
