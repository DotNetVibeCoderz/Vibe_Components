using AquaCulture.Core.Geo;
using NetTopologySuite.Geometries;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace AquaCulture.GIS;

/// <summary>Interop with NetTopologySuite (geometry operations) and ProjNet (UTM projection).</summary>
public static class NtsInterop
{
    private static readonly GeometryFactory Wgs84Factory = new(new PrecisionModel(), 4326);
    private static readonly CoordinateTransformationFactory CtFactory = new();

    public static Polygon ToNts(GeoPolygon p) =>
        Wgs84Factory.CreatePolygon(p.Ring.Append(p.Ring[0]).Select(c => new Coordinate(c.Longitude, c.Latitude)).ToArray());

    public static GeoPolygon FromNts(Polygon p) => new(p.ExteriorRing.Coordinates.Select(c => new GeoCoordinate(c.Y, c.X)).ToArray());

    public static Point ToNts(GeoCoordinate c) => Wgs84Factory.CreatePoint(new Coordinate(c.Longitude, c.Latitude));

    /// <summary>UTM zone number and hemisphere for a coordinate.</summary>
    public static (int Zone, bool North) UtmZone(GeoCoordinate c) => ((int)Math.Floor((c.Longitude + 180) / 6) + 1, c.Latitude >= 0);

    private static (ICoordinateTransformation Fwd, ICoordinateTransformation Inv) Utm(int zone, bool north)
    {
        var utm = ProjectedCoordinateSystem.WGS84_UTM(zone, north);
        return (CtFactory.CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, utm), CtFactory.CreateFromCoordinateSystems(utm, GeographicCoordinateSystem.WGS84));
    }

    /// <summary>Projects to UTM (easting, northing in metres) with ProjNet.</summary>
    public static (double Easting, double Northing, int Zone, bool North) ToUtm(GeoCoordinate c)
    {
        var (zone, north) = UtmZone(c);
        var (fwd, _) = Utm(zone, north);
        var (x, y) = fwd.MathTransform.Transform(c.Longitude, c.Latitude);
        return (x, y, zone, north);
    }

    public static GeoCoordinate FromUtm(double easting, double northing, int zone, bool north)
    {
        var (_, inv) = Utm(zone, north);
        var (lon, lat) = inv.MathTransform.Transform(easting, northing);
        return new GeoCoordinate(lat, lon);
    }

    /// <summary>Buffers a polygon by a distance in metres (computed in UTM), e.g. a biosecurity or mangrove green-belt zone.</summary>
    public static GeoPolygon BufferMeters(GeoPolygon p, double meters)
    {
        var (zone, north) = UtmZone(p.Centroid);
        var (fwd, inv) = Utm(zone, north);
        var gf = new GeometryFactory();
        var projected = gf.CreatePolygon(p.Ring.Append(p.Ring[0]).Select(c => { var (x, y) = fwd.MathTransform.Transform(c.Longitude, c.Latitude); return new Coordinate(x, y); }).ToArray());
        var buffered = (Polygon)projected.Buffer(meters);
        return new GeoPolygon(buffered.ExteriorRing.Coordinates.Select(c => { var (lon, lat) = inv.MathTransform.Transform(c.X, c.Y); return new GeoCoordinate(lat, lon); }).ToArray());
    }

    /// <summary>Area (m²) of the union of polygons computed in UTM (handles overlaps exactly).</summary>
    public static double UnionAreaM2(IEnumerable<GeoPolygon> polygons)
    {
        var list = polygons.ToList();
        if (list.Count == 0) return 0;
        var (zone, north) = UtmZone(list[0].Centroid);
        var (fwd, _) = Utm(zone, north);
        var gf = new GeometryFactory();
        Geometry? union = null;
        foreach (var p in list)
        {
            var g = gf.CreatePolygon(p.Ring.Append(p.Ring[0]).Select(c => { var (x, y) = fwd.MathTransform.Transform(c.Longitude, c.Latitude); return new Coordinate(x, y); }).ToArray());
            union = union is null ? g : union.Union(g);
        }
        return union!.Area;
    }
}
