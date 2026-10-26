using GDAL.Net.Native;

namespace GDAL.Net.Srs;

/// <summary>Options controlling how PROJ picks a transformation (datum shifts, pipelines, area of interest).</summary>
public sealed class TransformationOptions
{
    /// <summary>Explicit PROJ pipeline / coordinate operation (WKT, PROJ string or "urn:ogc:def:coordinateOperation:EPSG::xxxx").</summary>
    public string? Operation { get; set; }

    /// <summary>Whether <see cref="Operation"/> may also be used in reverse.</summary>
    public bool OperationReversible { get; set; }

    /// <summary>Area of interest in WGS84 degrees, helps PROJ pick the best datum transformation.</summary>
    public Envelope? AreaOfInterest { get; set; }

    /// <summary>Allow ballpark (approximate) transformations when no exact one is found (default true).</summary>
    public bool? BallparkAllowed { get; set; }

    /// <summary>Only use the best transformation, fail if its grids are missing.</summary>
    public bool? OnlyBest { get; set; }
}

/// <summary>Transforms coordinates between two CRSs (datum transformations included, powered by PROJ).</summary>
public sealed unsafe class CoordinateTransformation : GdalObject
{
    private CoordinateTransformation(nint handle, SpatialReference source, SpatialReference target)
        : base(new GdalSafeHandle(handle, &Release))
    {
        Source = source;
        Target = target;
    }

    private static void Release(nint h) => GdalNative.OCTDestroyCoordinateTransformation(h);

    public SpatialReference Source { get; }
    public SpatialReference Target { get; }

    /// <summary>Creates a transformation from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public static CoordinateTransformation Create(SpatialReference source, SpatialReference target, TransformationOptions? options = null)
    {
        nint h;
        if (options is null)
        {
            h = GdalNative.OCTNewCoordinateTransformation(source.Handle, target.Handle);
        }
        else
        {
            var opts = GdalNative.OCTNewCoordinateTransformationOptions();
            try
            {
                if (options.Operation is { } op) GdalNative.OCTCoordinateTransformationOptionsSetOperation(opts, op, options.OperationReversible ? 1 : 0);
                if (options.AreaOfInterest is { } aoi) GdalNative.OCTCoordinateTransformationOptionsSetAreaOfInterest(opts, aoi.MinX, aoi.MinY, aoi.MaxX, aoi.MaxY);
                if (options.BallparkAllowed is { } b) GdalNative.OCTCoordinateTransformationOptionsSetBallparkAllowed(opts, b ? 1 : 0);
                if (options.OnlyBest is { } ob) GdalNative.OCTCoordinateTransformationOptionsSetOnlyBest(opts, ob ? 1 : 0);
                h = GdalNative.OCTNewCoordinateTransformationEx(source.Handle, target.Handle, opts);
            }
            finally
            {
                GdalNative.OCTDestroyCoordinateTransformationOptions(opts);
            }
        }
        return h == 0 ? throw GdalNative.LastError($"No transformation from {source} to {target}") : new CoordinateTransformation(h, source, target);
    }

    /// <summary>Convenience: transformation between two EPSG codes.</summary>
    public static CoordinateTransformation Create(int sourceEpsg, int targetEpsg)
        => Create(SpatialReference.FromEpsg(sourceEpsg), SpatialReference.FromEpsg(targetEpsg));

    /// <summary>Transforms one 2D point.</summary>
    public (double X, double Y) Transform(double x, double y)
    {
        double z = 0;
        int ok;
        GdalNative.OCTTransformEx(Handle, 1, &x, &y, &z, &ok);
        if (ok == 0) throw GdalNative.LastError("Point transformation failed");
        return (x, y);
    }

    /// <summary>Transforms one 3D point.</summary>
    public (double X, double Y, double Z) Transform(double x, double y, double z)
    {
        int ok;
        GdalNative.OCTTransformEx(Handle, 1, &x, &y, &z, &ok);
        if (ok == 0) throw GdalNative.LastError("Point transformation failed");
        return (x, y, z);
    }

    /// <summary>
    /// Transforms coordinate arrays in place (bulk, no allocations). Returns the number of points successfully transformed;
    /// optionally reports per-point success.
    /// </summary>
    public int TransformInPlace(Span<double> x, Span<double> y, Span<double> z = default, Span<bool> success = default)
    {
        if (x.Length != y.Length) throw new ArgumentException("x and y must have the same length.");
        if (!z.IsEmpty && z.Length != x.Length) throw new ArgumentException("z must be empty or match x/y length.");
        var n = x.Length;
        if (n == 0) return 0;
        var flags = n <= 1024 ? stackalloc int[n] : new int[n];
        fixed (double* px = x)
        fixed (double* py = y)
        fixed (double* pz = z)
        fixed (int* pf = flags)
        {
            GdalNative.OCTTransformEx(Handle, n, px, py, z.IsEmpty ? null : pz, pf);
        }
        GdalNative.CPLErrorReset();
        var good = 0;
        for (var i = 0; i < n; i++)
        {
            var ok = flags[i] != 0;
            if (ok) good++;
            if (!success.IsEmpty) success[i] = ok;
        }
        return good;
    }

    /// <summary>Transforms a bounding box, densifying edges for an accurate result.</summary>
    public Envelope TransformBounds(Envelope bounds, int densifyPoints = 21)
    {
        double minX, minY, maxX, maxY;
        var ok = GdalNative.OCTTransformBounds(Handle, bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY, &minX, &minY, &maxX, &maxY, densifyPoints);
        if (ok == 0) throw GdalNative.LastError("Bounds transformation failed");
        return new Envelope(minX, minY, maxX, maxY);
    }
}
