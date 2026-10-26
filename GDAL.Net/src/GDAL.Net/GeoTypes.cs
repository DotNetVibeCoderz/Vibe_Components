namespace GDAL.Net;

/// <summary>
/// Affine transform between pixel/line space and georeferenced space (GDAL geotransform):
/// X = OriginX + px*PixelWidth + ln*RotationX ; Y = OriginY + px*RotationY + ln*PixelHeight.
/// </summary>
public readonly record struct GeoTransform(double OriginX, double PixelWidth, double RotationX, double OriginY, double RotationY, double PixelHeight)
{
    /// <summary>The GDAL default (identity) transform.</summary>
    public static GeoTransform Identity => new(0, 1, 0, 0, 0, 1);

    /// <summary>North-up transform from an upper-left corner and a square/rectangular pixel size.</summary>
    public static GeoTransform FromOrigin(double upperLeftX, double upperLeftY, double pixelWidth, double pixelHeight)
        => new(upperLeftX, pixelWidth, 0, upperLeftY, 0, -Math.Abs(pixelHeight));

    /// <summary>North-up transform covering <paramref name="extent"/> with the given raster size.</summary>
    public static GeoTransform FromExtent(Envelope extent, int width, int height)
        => new(extent.MinX, extent.Width / width, 0, extent.MaxY, 0, -extent.Height / height);

    public static GeoTransform FromArray(ReadOnlySpan<double> gt) => new(gt[0], gt[1], gt[2], gt[3], gt[4], gt[5]);

    public double[] ToArray() => [OriginX, PixelWidth, RotationX, OriginY, RotationY, PixelHeight];

    public bool IsNorthUp => RotationX == 0 && RotationY == 0;

    /// <summary>Converts a pixel/line position to georeferenced coordinates.</summary>
    public (double X, double Y) PixelToWorld(double pixel, double line)
        => (OriginX + pixel * PixelWidth + line * RotationX, OriginY + pixel * RotationY + line * PixelHeight);

    /// <summary>Converts georeferenced coordinates to a (fractional) pixel/line position.</summary>
    public (double Pixel, double Line) WorldToPixel(double x, double y)
    {
        var inv = Invert();
        return inv.PixelToWorld(x, y);
    }

    /// <summary>Inverse transform (world → pixel).</summary>
    public GeoTransform Invert()
    {
        if (IsNorthUp)
        {
            return new GeoTransform(-OriginX / PixelWidth, 1.0 / PixelWidth, 0, -OriginY / PixelHeight, 0, 1.0 / PixelHeight);
        }
        var det = PixelWidth * PixelHeight - RotationX * RotationY;
        if (Math.Abs(det) < double.Epsilon)
        {
            throw new InvalidOperationException("Geotransform is not invertible.");
        }
        var inv = 1.0 / det;
        return new GeoTransform(
            (RotationX * OriginY - PixelHeight * OriginX) * inv,
            PixelHeight * inv,
            -RotationX * inv,
            (-PixelWidth * OriginY + RotationY * OriginX) * inv,
            -RotationY * inv,
            PixelWidth * inv);
    }

    /// <summary>Georeferenced bounding box of a raster of the given size.</summary>
    public Envelope GetExtent(int width, int height)
    {
        Span<(double X, double Y)> corners =
        [
            PixelToWorld(0, 0), PixelToWorld(width, 0), PixelToWorld(0, height), PixelToWorld(width, height),
        ];
        var env = Envelope.Empty;
        foreach (var (x, y) in corners) env = env.Expand(x, y);
        return env;
    }
}

/// <summary>Axis aligned bounding box.</summary>
public readonly record struct Envelope(double MinX, double MinY, double MaxX, double MaxY)
{
    public static Envelope Empty => new(double.PositiveInfinity, double.PositiveInfinity, double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;
    public double CenterX => (MinX + MaxX) * 0.5;
    public double CenterY => (MinY + MaxY) * 0.5;

    public Envelope Expand(double x, double y) => new(Math.Min(MinX, x), Math.Min(MinY, y), Math.Max(MaxX, x), Math.Max(MaxY, y));

    public Envelope Union(Envelope other) => IsEmpty ? other : other.IsEmpty ? this
        : new(Math.Min(MinX, other.MinX), Math.Min(MinY, other.MinY), Math.Max(MaxX, other.MaxX), Math.Max(MaxY, other.MaxY));

    public bool Intersects(Envelope other) => !(other.MinX > MaxX || other.MaxX < MinX || other.MinY > MaxY || other.MaxY < MinY);

    public bool Contains(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;

    /// <summary>Grows the envelope by a fraction of its size on each side.</summary>
    public Envelope Inflate(double fraction)
    {
        var dx = Width * fraction;
        var dy = Height * fraction;
        return new(MinX - dx, MinY - dy, MaxX + dx, MaxY + dy);
    }

    public override string ToString() => $"[{MinX:G10}, {MinY:G10}, {MaxX:G10}, {MaxY:G10}]";
}

/// <summary>A ground control point linking a pixel/line position to georeferenced coordinates.</summary>
public readonly record struct GroundControlPoint(double Pixel, double Line, double X, double Y, double Z = 0, string? Id = null, string? Info = null);

/// <summary>Band statistics.</summary>
public readonly record struct BandStatistics(double Minimum, double Maximum, double Mean, double StdDev);

/// <summary>A band histogram with equally sized buckets between <see cref="Minimum"/> and <see cref="Maximum"/>.</summary>
public sealed record Histogram(double Minimum, double Maximum, ulong[] Counts)
{
    public int BucketCount => Counts.Length;
    public double BucketWidth => (Maximum - Minimum) / Counts.Length;
    public double BucketCenter(int i) => Minimum + (i + 0.5) * BucketWidth;
    public ulong Total => Counts.Aggregate(0UL, (a, b) => a + b);
    public ulong MaxCount => Counts.Length == 0 ? 0 : Counts.Max();
}

/// <summary>A palette entry (RGBA).</summary>
public readonly record struct ColorEntry(short C1, short C2, short C3, short C4 = 255)
{
    public static ColorEntry FromRgb(byte r, byte g, byte b, byte a = 255) => new(r, g, b, a);
}
