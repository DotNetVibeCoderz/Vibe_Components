namespace Agritech.Core.Geo;

/// <summary>WGS-84 coordinate in decimal degrees (altitude in metres, optional).</summary>
public readonly record struct GeoCoordinate
{
    public double Latitude { get; }
    public double Longitude { get; }
    public double Altitude { get; }

    public GeoCoordinate(double latitude, double longitude, double altitude = 0)
    {
        Latitude = Guard.InRange(latitude, -90, 90);
        Longitude = Guard.InRange(longitude, -180, 180);
        Altitude = altitude;
    }

    public override string ToString() => $"{Latitude:F6}, {Longitude:F6}";
}

/// <summary>Planar point (metres in a projected CRS or local frame).</summary>
public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Point2 operator *(Point2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Point2 o) => X * o.X + Y * o.Y;
    public double Cross(Point2 o) => X * o.Y - Y * o.X;
    public double DistanceTo(Point2 o) => (this - o).Length;
}

/// <summary>Axis-aligned bounding box in geographic degrees.</summary>
public readonly record struct GeoBounds(double MinLat, double MinLon, double MaxLat, double MaxLon)
{
    public bool Contains(GeoCoordinate c) => c.Latitude >= MinLat && c.Latitude <= MaxLat && c.Longitude >= MinLon && c.Longitude <= MaxLon;
    public bool Intersects(GeoBounds o) => !(o.MinLat > MaxLat || o.MaxLat < MinLat || o.MinLon > MaxLon || o.MaxLon < MinLon);
    public GeoCoordinate Center => new((MinLat + MaxLat) / 2, (MinLon + MaxLon) / 2);
}
