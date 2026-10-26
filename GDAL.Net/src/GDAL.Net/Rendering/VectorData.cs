using System.Globalization;
using System.Text;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net.Rendering;

/// <summary>Kind of drawable primitive.</summary>
public enum ShapeKind
{
    Point,
    Line,
    Polygon,
}

/// <summary>
/// A drawable part of a feature in world coordinates: a point, a line, or a polygon (first ring exterior, others holes).
/// Coordinates are interleaved XY.
/// </summary>
public sealed record VectorShape(ShapeKind Kind, IReadOnlyList<double[]> Rings);

/// <summary>A feature loaded for display: attributes + shapes in display CRS.</summary>
public sealed record VectorFeature(long Id, IReadOnlyDictionary<string, object?> Attributes, IReadOnlyList<VectorShape> Shapes, Envelope Envelope)
{
    public object? this[string field] => Attributes.TryGetValue(field, out var v) ? v : null;
}

/// <summary>
/// An immutable, GDAL-independent snapshot of a vector layer ready for fast rendering and hit testing
/// (features are read once, optionally reprojected; the UI never touches native handles while drawing).
/// </summary>
public sealed class VectorData
{
    private VectorData(string name, IReadOnlyList<FieldInfo> fields, IReadOnlyList<VectorFeature> features, Envelope extent, string? crs)
    {
        Name = name;
        Fields = fields;
        Features = features;
        Extent = extent;
        Crs = crs;
    }

    public string Name { get; }
    public IReadOnlyList<FieldInfo> Fields { get; }
    public IReadOnlyList<VectorFeature> Features { get; }
    public Envelope Extent { get; }
    /// <summary>Display CRS description.</summary>
    public string? Crs { get; }

    /// <summary>Loads a layer (respecting its filters), optionally reprojecting to <paramref name="target"/>.</summary>
    public static VectorData Load(Layer layer, SpatialReference? target = null, int maxFeatures = 100_000, double simplifyTolerance = 0)
    {
        using var source = layer.SpatialReference;
        CoordinateTransformation? ct = null;
        if (target is not null && source is not null && !source.IsSame(target))
        {
            ct = CoordinateTransformation.Create(source, target);
        }
        try
        {
            var features = new List<VectorFeature>();
            var extent = Envelope.Empty;
            layer.ResetReading();
            while (features.Count < maxFeatures && layer.GetNextFeature() is { } f)
            {
                using (f)
                {
                    var shapes = new List<VectorShape>();
                    var env = Envelope.Empty;
                    if (f.Geometry is { } g)
                    {
                        Geometry? work = null;
                        try
                        {
                            if (ct is not null || simplifyTolerance > 0)
                            {
                                work = g.Clone();
                                if (ct is not null) work.Transform(ct);
                                if (simplifyTolerance > 0)
                                {
                                    var s = work.SimplifyPreserveTopology(simplifyTolerance);
                                    work.Dispose();
                                    work = s;
                                }
                            }
                            Collect(work ?? g, shapes);
                        }
                        finally
                        {
                            work?.Dispose();
                        }
                        foreach (var shape in shapes)
                        {
                            foreach (var ring in shape.Rings)
                            {
                                for (var i = 0; i + 1 < ring.Length; i += 2) env = env.Expand(ring[i], ring[i + 1]);
                            }
                        }
                    }
                    extent = extent.Union(env);
                    features.Add(new VectorFeature(f.Id, f.GetAttributes(), shapes, env));
                }
            }
            return new VectorData(layer.Name, layer.Fields, features, extent, target?.ToString() ?? source?.ToString());
        }
        finally
        {
            ct?.Dispose();
        }
    }

    /// <summary>Builds a snapshot from geometries (e.g. the result of a geoprocessing operation).</summary>
    public static VectorData FromGeometries(string name, IEnumerable<Geometry> geometries)
    {
        var features = new List<VectorFeature>();
        var extent = Envelope.Empty;
        long id = 0;
        foreach (var g in geometries)
        {
            var shapes = new List<VectorShape>();
            Collect(g, shapes);
            var env = g.Envelope;
            extent = extent.Union(env);
            features.Add(new VectorFeature(id++, new Dictionary<string, object?>(), shapes, env));
        }
        return new VectorData(name, [], features, extent, null);
    }

    private static void Collect(Geometry g, List<VectorShape> shapes)
    {
        switch (g.FlatType)
        {
            case GeometryType.Point:
                if (!g.IsEmpty) shapes.Add(new VectorShape(ShapeKind.Point, [[g.X, g.Y]]));
                break;
            case GeometryType.LineString:
            case GeometryType.LinearRing:
                shapes.Add(new VectorShape(ShapeKind.Line, [Points(g)]));
                break;
            case GeometryType.Polygon:
            {
                var rings = new List<double[]>();
                foreach (var ring in g.Parts) rings.Add(Points(ring));
                if (rings.Count > 0) shapes.Add(new VectorShape(ShapeKind.Polygon, rings));
                break;
            }
            case GeometryType.CircularString:
            case GeometryType.CompoundCurve:
            case GeometryType.CurvePolygon:
            case GeometryType.MultiCurve:
            case GeometryType.MultiSurface:
            {
                using var linear = g.ToLinear();
                Collect(linear, shapes);
                break;
            }
            default:
                foreach (var part in g.Parts) Collect(part, shapes);
                break;
        }
    }

    private static double[] Points(Geometry g)
    {
        var n = g.PointCount;
        var xy = new double[n * 2];
        g.CopyPoints(xy);
        return xy;
    }

    /// <summary>Features whose shapes contain/touch the world point (tolerance in world units for points/lines).</summary>
    public IEnumerable<VectorFeature> HitTest(double x, double y, double tolerance)
    {
        for (var i = Features.Count - 1; i >= 0; i--)
        {
            var f = Features[i];
            var e = f.Envelope;
            if (x < e.MinX - tolerance || x > e.MaxX + tolerance || y < e.MinY - tolerance || y > e.MaxY + tolerance) continue;
            foreach (var s in f.Shapes)
            {
                if (Hit(s, x, y, tolerance))
                {
                    yield return f;
                    break;
                }
            }
        }
    }

    private static bool Hit(VectorShape s, double x, double y, double tol)
    {
        switch (s.Kind)
        {
            case ShapeKind.Point:
                return Math.Abs(s.Rings[0][0] - x) <= tol && Math.Abs(s.Rings[0][1] - y) <= tol;
            case ShapeKind.Line:
                return NearPolyline(s.Rings[0], x, y, tol);
            default:
                var inside = PointInRing(s.Rings[0], x, y);
                for (var r = 1; r < s.Rings.Count && inside; r++)
                {
                    if (PointInRing(s.Rings[r], x, y)) inside = false;
                }
                return inside || NearPolyline(s.Rings[0], x, y, tol);
        }
    }

    private static bool PointInRing(double[] ring, double x, double y)
    {
        var inside = false;
        var n = ring.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = ring[2 * i], yi = ring[2 * i + 1], xj = ring[2 * j], yj = ring[2 * j + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    private static bool NearPolyline(double[] line, double x, double y, double tol)
    {
        var t2 = tol * tol;
        for (var i = 0; i + 3 < line.Length; i += 2)
        {
            double ax = line[i], ay = line[i + 1], bx = line[i + 2], by = line[i + 3];
            double dx = bx - ax, dy = by - ay;
            var len2 = dx * dx + dy * dy;
            var t = len2 == 0 ? 0 : Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0, 1);
            double px = ax + t * dx - x, py = ay + t * dy - y;
            if (px * px + py * py <= t2) return true;
        }
        return false;
    }
}

/// <summary>Maps world coordinates to screen pixels (north up, uniform scale, y down) and back.</summary>
public readonly record struct ScreenTransform(double OriginX, double OriginY, double Scale, double Width, double Height)
{
    /// <summary>Fits <paramref name="world"/> into a viewport, preserving aspect ratio, with optional padding (pixels).</summary>
    public static ScreenTransform Fit(Envelope world, double width, double height, double padding = 0)
    {
        if (world.IsEmpty) world = new Envelope(-1, -1, 1, 1);
        var ww = Math.Max(world.Width, 1e-12);
        var wh = Math.Max(world.Height, 1e-12);
        var scale = Math.Min((width - 2 * padding) / ww, (height - 2 * padding) / wh);
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        var cx = world.CenterX;
        var cy = world.CenterY;
        return new ScreenTransform(cx - width / 2 / scale, cy + height / 2 / scale, scale, width, height);
    }

    /// <summary>Visible world extent.</summary>
    public Envelope WorldExtent => new(OriginX, OriginY - Height / Scale, OriginX + Width / Scale, OriginY);

    public (double X, double Y) ToScreen(double x, double y) => ((x - OriginX) * Scale, (OriginY - y) * Scale);

    public (double X, double Y) ToWorld(double sx, double sy) => (OriginX + sx / Scale, OriginY - sy / Scale);

    /// <summary>Zooms by <paramref name="factor"/> keeping the screen point (sx, sy) fixed.</summary>
    public ScreenTransform ZoomAt(double sx, double sy, double factor)
    {
        var (wx, wy) = ToWorld(sx, sy);
        var s = Scale * factor;
        return this with { Scale = s, OriginX = wx - sx / s, OriginY = wy + sy / s };
    }

    /// <summary>Pans by a screen-space delta.</summary>
    public ScreenTransform Pan(double dx, double dy) => this with { OriginX = OriginX - dx / Scale, OriginY = OriginY + dy / Scale };

    /// <summary>Same view center, new viewport size.</summary>
    public ScreenTransform Resize(double width, double height)
    {
        var c = ToWorld(Width / 2, Height / 2);
        return new ScreenTransform(c.X - width / 2 / Scale, c.Y + height / 2 / Scale, Scale, width, height);
    }

    /// <summary>SVG path data ("M x y L ... Z") of a shape in screen space.</summary>
    public string ToSvgPath(VectorShape shape, int decimals = 1)
    {
        var sb = new StringBuilder();
        var fmt = "F" + decimals.ToString(CultureInfo.InvariantCulture);
        foreach (var ring in shape.Rings)
        {
            for (var i = 0; i + 1 < ring.Length; i += 2)
            {
                var (x, y) = ToScreen(ring[i], ring[i + 1]);
                sb.Append(i == 0 ? 'M' : 'L').Append(x.ToString(fmt, CultureInfo.InvariantCulture)).Append(' ').Append(y.ToString(fmt, CultureInfo.InvariantCulture)).Append(' ');
            }
            if (shape.Kind == ShapeKind.Polygon) sb.Append("Z ");
        }
        return sb.ToString();
    }
}
