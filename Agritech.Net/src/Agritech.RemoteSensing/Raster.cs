using Agritech.Core.Geo;
using Agritech.Native;

namespace Agritech.RemoteSensing;

/// <summary>Affine geotransform for a north-up raster in geographic degrees (GDAL convention, no rotation).</summary>
public readonly record struct GeoTransform(double OriginLon, double OriginLat, double PixelWidthDeg, double PixelHeightDeg)
{
    public GeoCoordinate PixelCenter(int col, int row) =>
        new(OriginLat - (row + 0.5) * PixelHeightDeg, OriginLon + (col + 0.5) * PixelWidthDeg);

    public (int Col, int Row) ToPixel(GeoCoordinate c) =>
        ((int)Math.Floor((c.Longitude - OriginLon) / PixelWidthDeg), (int)Math.Floor((OriginLat - c.Latitude) / PixelHeightDeg));

    /// <summary>Builds a transform covering bounds with the given pixel size in metres.</summary>
    public static GeoTransform FromBounds(GeoBounds b, double pixelSizeM, out int width, out int height)
    {
        double pxLat = pixelSizeM / GeoMath.EarthRadiusM * GeoMath.RadToDeg;
        double pxLon = pxLat / Math.Cos(b.Center.Latitude * GeoMath.DegToRad);
        width = Math.Max(1, (int)Math.Ceiling((b.MaxLon - b.MinLon) / pxLon));
        height = Math.Max(1, (int)Math.Ceiling((b.MaxLat - b.MinLat) / pxLat));
        return new GeoTransform(b.MinLon, b.MaxLat, pxLon, pxLat);
    }
}

/// <summary>Single-band float raster (row-major). NaN marks no-data.</summary>
public sealed class RasterBand
{
    public RasterBand(int width, int height, GeoTransform transform, float[]? data = null, string name = "band")
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height; Transform = transform; Name = name;
        Data = data ?? new float[width * height];
        if (Data.Length != width * height) throw new ArgumentException("Data length must equal width × height.");
    }

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public GeoTransform Transform { get; }
    public float[] Data { get; }

    public float this[int col, int row]
    {
        get => Data[row * Width + col];
        set => Data[row * Width + col] = value;
    }

    public float Sample(GeoCoordinate c)
    {
        var (col, row) = Transform.ToPixel(c);
        return col < 0 || row < 0 || col >= Width || row >= Height ? float.NaN : this[col, row];
    }

    public RasterStats Statistics() => AgriKernels.Statistics(Data);

    public RasterBand Like(string name) => new(Width, Height, Transform, null, name);

    /// <summary>Applies a validity mask (false → NaN) in place.</summary>
    public void Mask(ReadOnlySpan<bool> valid)
    {
        for (int i = 0; i < Data.Length; i++) if (!valid[i]) Data[i] = float.NaN;
    }
}

/// <summary>Co-registered multispectral image (all bands share size and transform).</summary>
public sealed class MultispectralImage
{
    private readonly Dictionary<SpectralBand, RasterBand> _bands = [];

    public MultispectralImage(SatelliteSensor sensor, DateTimeOffset acquired, string? sceneId = null)
    {
        Sensor = sensor; Acquired = acquired; SceneId = sceneId;
    }

    public SatelliteSensor Sensor { get; }
    public DateTimeOffset Acquired { get; }
    public string? SceneId { get; }
    public IReadOnlyDictionary<SpectralBand, RasterBand> Bands => _bands;

    public MultispectralImage With(SpectralBand band, RasterBand raster)
    {
        if (_bands.Count > 0)
        {
            var first = _bands.Values.First();
            if (first.Width != raster.Width || first.Height != raster.Height)
                throw new ArgumentException("All bands must have identical dimensions (resample first).");
        }
        _bands[band] = raster;
        return this;
    }

    public RasterBand this[SpectralBand b] =>
        _bands.TryGetValue(b, out var r) ? r : throw new KeyNotFoundException($"Band {b} not present in image.");

    public bool Has(SpectralBand b) => _bands.ContainsKey(b);
}
