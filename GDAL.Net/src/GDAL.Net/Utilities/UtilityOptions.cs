using System.Globalization;

namespace GDAL.Net;

/// <summary>Shared helpers for typed utility options.</summary>
public abstract record UtilityOptionsBase
{
    /// <summary>Output format (driver short name), e.g. "GTiff", "COG", "MEM", "GPKG".</summary>
    public string? Format { get; init; }

    /// <summary>Driver creation options (-co KEY=VALUE).</summary>
    public IDictionary<string, string>? CreationOptions { get; init; }

    /// <summary>Raw extra command line switches appended verbatim.</summary>
    public IEnumerable<string>? ExtraArgs { get; init; }

    protected static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    protected void AddCommon(List<string> a)
    {
        if (Format is not null) a.AddRange(["-of", Format]);
        if (CreationOptions is not null)
        {
            foreach (var kv in CreationOptions) a.AddRange(["-co", $"{kv.Key}={kv.Value}"]);
        }
    }

    protected void AddExtra(List<string> a)
    {
        if (ExtraArgs is not null) a.AddRange(ExtraArgs);
    }

    internal static string ResamplingName(ResampleAlgorithm r) => r switch
    {
        ResampleAlgorithm.NearestNeighbour => "near",
        ResampleAlgorithm.Bilinear => "bilinear",
        ResampleAlgorithm.Cubic => "cubic",
        ResampleAlgorithm.CubicSpline => "cubicspline",
        ResampleAlgorithm.Lanczos => "lanczos",
        ResampleAlgorithm.Average => "average",
        ResampleAlgorithm.Mode => "mode",
        ResampleAlgorithm.Gauss => "gauss",
        ResampleAlgorithm.RootMeanSquare => "rms",
        _ => "near",
    };
}

/// <summary>Typed options for gdal_translate.</summary>
public sealed record TranslateOptions : UtilityOptionsBase
{
    /// <summary>Bands to copy (1-based), in order.</summary>
    public IReadOnlyList<int>? Bands { get; init; }
    /// <summary>Source window in pixels (-srcwin xoff yoff xsize ysize).</summary>
    public (int X, int Y, int Width, int Height)? SourceWindow { get; init; }
    /// <summary>Source window in georeferenced coordinates (-projwin ulx uly lrx lry).</summary>
    public Envelope? ProjectionWindow { get; init; }
    /// <summary>Output size in pixels (0 keeps aspect ratio).</summary>
    public (int Width, int Height)? OutputSize { get; init; }
    public DataType? OutputType { get; init; }
    /// <summary>Linear rescale: (srcMin, srcMax, dstMin, dstMax). Use (null) to auto-scale.</summary>
    public (double SrcMin, double SrcMax, double DstMin, double DstMax)? ScaleRange { get; init; }
    /// <summary>Auto-scale input range to output type range (-scale).</summary>
    public bool AutoScale { get; init; }
    public double? NoData { get; init; }
    public string? AssignSrs { get; init; }
    public Envelope? AssignBounds { get; init; }
    public ResampleAlgorithm? Resampling { get; init; }
    /// <summary>Expand a palette band into rgb/rgba ("rgb", "rgba", "gray").</summary>
    public string? Expand { get; init; }
    public bool Strict { get; init; }

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>();
        AddCommon(a);
        if (Bands is not null) foreach (var b in Bands) a.AddRange(["-b", b.ToString(CultureInfo.InvariantCulture)]);
        if (SourceWindow is { } w) a.AddRange(["-srcwin", w.X.ToString(CultureInfo.InvariantCulture), w.Y.ToString(CultureInfo.InvariantCulture), w.Width.ToString(CultureInfo.InvariantCulture), w.Height.ToString(CultureInfo.InvariantCulture)]);
        if (ProjectionWindow is { } p) a.AddRange(["-projwin", F(p.MinX), F(p.MaxY), F(p.MaxX), F(p.MinY)]);
        if (OutputSize is { } s) a.AddRange(["-outsize", s.Width.ToString(CultureInfo.InvariantCulture), s.Height.ToString(CultureInfo.InvariantCulture)]);
        if (OutputType is { } t) a.AddRange(["-ot", t.ToString()]);
        if (ScaleRange is { } sc) a.AddRange(["-scale", F(sc.SrcMin), F(sc.SrcMax), F(sc.DstMin), F(sc.DstMax)]);
        else if (AutoScale) a.Add("-scale");
        if (NoData is { } nd) a.AddRange(["-a_nodata", F(nd)]);
        if (AssignSrs is not null) a.AddRange(["-a_srs", AssignSrs]);
        if (AssignBounds is { } ab) a.AddRange(["-a_ullr", F(ab.MinX), F(ab.MaxY), F(ab.MaxX), F(ab.MinY)]);
        if (Resampling is { } r) a.AddRange(["-r", ResamplingName(r)]);
        if (Expand is not null) a.AddRange(["-expand", Expand]);
        if (Strict) a.Add("-strict");
        AddExtra(a);
        return a;
    }
}

/// <summary>Typed options for gdalwarp.</summary>
public sealed record WarpOptions : UtilityOptionsBase
{
    /// <summary>Target CRS (EPSG:xxxx, WKT, PROJ string, ...).</summary>
    public string? TargetSrs { get; init; }
    /// <summary>Override the source CRS.</summary>
    public string? SourceSrs { get; init; }
    public ResampleAlgorithm Resampling { get; init; } = ResampleAlgorithm.NearestNeighbour;
    /// <summary>Output bounds in target CRS (-te).</summary>
    public Envelope? OutputBounds { get; init; }
    /// <summary>Output resolution in target units (-tr).</summary>
    public (double X, double Y)? Resolution { get; init; }
    /// <summary>Output size in pixels (-ts).</summary>
    public (int Width, int Height)? OutputSize { get; init; }
    public double? SourceNoData { get; init; }
    public double? DestinationNoData { get; init; }
    /// <summary>Cutline vector file (clip by polygon).</summary>
    public string? CutlineFile { get; init; }
    /// <summary>Cutline as WKT geometry (in target CRS unless CutlineSrs is set).</summary>
    public string? CutlineWkt { get; init; }
    public string? CutlineSrs { get; init; }
    public bool CropToCutline { get; init; }
    /// <summary>Use thin plate spline (GCP georeferencing).</summary>
    public bool ThinPlateSpline { get; init; }
    /// <summary>Polynomial order for GCP warping (1-3).</summary>
    public int? PolynomialOrder { get; init; }
    /// <summary>Use RPC model (orthorectification). Combine with <see cref="RpcDem"/>.</summary>
    public bool Rpc { get; init; }
    /// <summary>DEM used for RPC orthorectification.</summary>
    public string? RpcDem { get; init; }
    public bool Multithread { get; init; } = true;
    public bool Overwrite { get; init; } = true;
    public DataType? OutputType { get; init; }
    public bool AddAlpha { get; init; }
    /// <summary>Warp memory in MB.</summary>
    public int? WarpMemoryMb { get; init; }

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>();
        AddCommon(a);
        if (SourceSrs is not null) a.AddRange(["-s_srs", SourceSrs]);
        if (TargetSrs is not null) a.AddRange(["-t_srs", TargetSrs]);
        a.AddRange(["-r", ResamplingName(Resampling)]);
        if (OutputBounds is { } b) a.AddRange(["-te", F(b.MinX), F(b.MinY), F(b.MaxX), F(b.MaxY)]);
        if (Resolution is { } r) a.AddRange(["-tr", F(r.X), F(r.Y)]);
        if (OutputSize is { } s) a.AddRange(["-ts", s.Width.ToString(CultureInfo.InvariantCulture), s.Height.ToString(CultureInfo.InvariantCulture)]);
        if (SourceNoData is { } sn) a.AddRange(["-srcnodata", F(sn)]);
        if (DestinationNoData is { } dn) a.AddRange(["-dstnodata", F(dn)]);
        if (CutlineFile is not null) a.AddRange(["-cutline", CutlineFile]);
        else if (CutlineWkt is not null) a.AddRange(["-cutline", CutlineWkt]);
        if (CutlineSrs is not null) a.AddRange(["-cutline_srs", CutlineSrs]);
        if (CropToCutline) a.Add("-crop_to_cutline");
        if (ThinPlateSpline) a.Add("-tps");
        if (PolynomialOrder is { } o) a.AddRange(["-order", o.ToString(CultureInfo.InvariantCulture)]);
        if (Rpc) a.Add("-rpc");
        if (RpcDem is not null) a.AddRange(["-to", $"RPC_DEM={RpcDem}"]);
        if (Multithread) a.AddRange(["-multi", "-wo", "NUM_THREADS=ALL_CPUS"]);
        if (Overwrite) a.Add("-overwrite");
        if (OutputType is { } t) a.AddRange(["-ot", t.ToString()]);
        if (AddAlpha) a.Add("-dstalpha");
        if (WarpMemoryMb is { } m) a.AddRange(["-wm", m.ToString(CultureInfo.InvariantCulture)]);
        AddExtra(a);
        return a;
    }
}

/// <summary>Typed options for ogr2ogr.</summary>
public sealed record VectorTranslateOptions : UtilityOptionsBase
{
    public string? TargetSrs { get; init; }
    public string? SourceSrs { get; init; }
    /// <summary>Attribute filter.</summary>
    public string? Where { get; init; }
    /// <summary>SQL statement whose result becomes the output layer.</summary>
    public string? Sql { get; init; }
    public string? Dialect { get; init; }
    /// <summary>Fields to keep.</summary>
    public IReadOnlyList<string>? Select { get; init; }
    public Envelope? SpatialFilter { get; init; }
    /// <summary>Clip to a rectangle (-clipsrc xmin ymin xmax ymax).</summary>
    public Envelope? ClipSource { get; init; }
    public string? NewLayerName { get; init; }
    /// <summary>Force output geometry type (POINT, MULTIPOLYGON, PROMOTE_TO_MULTI, ...).</summary>
    public string? GeometryType { get; init; }
    public bool Append { get; init; }
    public bool Overwrite { get; init; }
    public double? Simplify { get; init; }
    public double? Segmentize { get; init; }
    public bool MakeValid { get; init; }
    public bool SkipFailures { get; init; }
    public IDictionary<string, string>? LayerCreationOptions { get; init; }
    /// <summary>Source layers to copy (all by default).</summary>
    public IReadOnlyList<string>? Layers { get; init; }

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>();
        if (Format is not null) a.AddRange(["-f", Format]);
        if (CreationOptions is not null) foreach (var kv in CreationOptions) a.AddRange(["-dsco", $"{kv.Key}={kv.Value}"]);
        if (LayerCreationOptions is not null) foreach (var kv in LayerCreationOptions) a.AddRange(["-lco", $"{kv.Key}={kv.Value}"]);
        if (SourceSrs is not null) a.AddRange(["-s_srs", SourceSrs]);
        if (TargetSrs is not null) a.AddRange(["-t_srs", TargetSrs]);
        if (Where is not null) a.AddRange(["-where", Where]);
        if (Sql is not null) a.AddRange(["-sql", Sql]);
        if (Dialect is not null) a.AddRange(["-dialect", Dialect]);
        if (Select is not null) a.AddRange(["-select", string.Join(',', Select)]);
        if (SpatialFilter is { } sf) a.AddRange(["-spat", F(sf.MinX), F(sf.MinY), F(sf.MaxX), F(sf.MaxY)]);
        if (ClipSource is { } cs) a.AddRange(["-clipsrc", F(cs.MinX), F(cs.MinY), F(cs.MaxX), F(cs.MaxY)]);
        if (NewLayerName is not null) a.AddRange(["-nln", NewLayerName]);
        if (GeometryType is not null) a.AddRange(["-nlt", GeometryType]);
        if (Append) a.Add("-append");
        if (Overwrite) a.Add("-overwrite");
        if (Simplify is { } s) a.AddRange(["-simplify", F(s)]);
        if (Segmentize is { } sg) a.AddRange(["-segmentize", F(sg)]);
        if (MakeValid) a.Add("-makevalid");
        if (SkipFailures) a.Add("-skipfailures");
        AddExtra(a);
        if (Layers is not null) a.AddRange(Layers);
        return a;
    }
}

/// <summary>Typed options for gdalbuildvrt.</summary>
public sealed record BuildVrtOptions : UtilityOptionsBase
{
    /// <summary>Put each input in its own band (band stacking).</summary>
    public bool Separate { get; init; }
    /// <summary>"highest", "lowest", "average" or "user".</summary>
    public string? Resolution { get; init; }
    public (double X, double Y)? TargetResolution { get; init; }
    public double? SourceNoData { get; init; }
    public double? VrtNoData { get; init; }
    public ResampleAlgorithm? Resampling { get; init; }
    public bool AddAlpha { get; init; }
    public Envelope? Bounds { get; init; }

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>();
        if (Separate) a.Add("-separate");
        if (Resolution is not null) a.AddRange(["-resolution", Resolution]);
        if (TargetResolution is { } tr) a.AddRange(["-tr", F(tr.X), F(tr.Y)]);
        if (SourceNoData is { } sn) a.AddRange(["-srcnodata", F(sn)]);
        if (VrtNoData is { } vn) a.AddRange(["-vrtnodata", F(vn)]);
        if (Resampling is { } r) a.AddRange(["-r", ResamplingName(r)]);
        if (AddAlpha) a.Add("-addalpha");
        if (Bounds is { } b) a.AddRange(["-te", F(b.MinX), F(b.MinY), F(b.MaxX), F(b.MaxY)]);
        AddExtra(a);
        return a;
    }
}

/// <summary>Typed options for gdaldem.</summary>
public sealed record DemOptions : UtilityOptionsBase
{
    /// <summary>Vertical exaggeration.</summary>
    public double? ZFactor { get; init; }
    /// <summary>Ratio of vertical units to horizontal (111120 for degrees→meters).</summary>
    public double? Scale { get; init; }
    public double? Azimuth { get; init; }
    public double? Altitude { get; init; }
    /// <summary>Hillshade variant: null (standard), "combined", "multidirectional", "igor".</summary>
    public string? HillshadeVariant { get; init; }
    /// <summary>Compute values at image edges.</summary>
    public bool ComputeEdges { get; init; } = true;
    /// <summary>"Horn" or "ZevenbergenThorne".</summary>
    public string? Algorithm { get; init; }
    /// <summary>Slope expressed in percent instead of degrees.</summary>
    public bool SlopePercent { get; init; }
    /// <summary>Add alpha channel (color-relief).</summary>
    public bool Alpha { get; init; }
    /// <summary>Use exact color entries or nearest (color-relief): "exact" / "nearest".</summary>
    public string? ColorMatching { get; init; }

    public IReadOnlyList<string> ToArgs(DemProcessingMode mode)
    {
        var a = new List<string>();
        AddCommon(a);
        if (ZFactor is { } z) a.AddRange(["-z", F(z)]);
        if (Scale is { } s) a.AddRange(["-s", F(s)]);
        if (mode == DemProcessingMode.Hillshade)
        {
            if (Azimuth is { } az) a.AddRange(["-az", F(az)]);
            if (Altitude is { } alt) a.AddRange(["-alt", F(alt)]);
            if (HillshadeVariant is not null) a.Add("-" + HillshadeVariant);
        }
        if (ComputeEdges) a.Add("-compute_edges");
        if (Algorithm is not null) a.AddRange(["-alg", Algorithm]);
        if (mode == DemProcessingMode.Slope && SlopePercent) a.Add("-p");
        if (mode == DemProcessingMode.ColorRelief)
        {
            if (Alpha) a.Add("-alpha");
            if (ColorMatching == "exact") a.Add("-exact_color_entry");
            else if (ColorMatching == "nearest") a.Add("-nearest_color_entry");
        }
        AddExtra(a);
        return a;
    }
}

/// <summary>Typed options for gdal_rasterize.</summary>
public sealed record RasterizeOptions : UtilityOptionsBase
{
    /// <summary>Constant burn value(s) per band.</summary>
    public IReadOnlyList<double>? BurnValues { get; init; }
    /// <summary>Attribute field providing the burn value.</summary>
    public string? Attribute { get; init; }
    public IReadOnlyList<string>? Layers { get; init; }
    public string? Where { get; init; }
    public string? Sql { get; init; }
    public (double X, double Y)? Resolution { get; init; }
    public (int Width, int Height)? OutputSize { get; init; }
    public Envelope? Bounds { get; init; }
    public DataType? OutputType { get; init; }
    public double? NoData { get; init; }
    public double? InitValue { get; init; }
    /// <summary>Burn all pixels touched by lines/polygons.</summary>
    public bool AllTouched { get; init; }
    public string? TargetSrs { get; init; }

    public IReadOnlyList<string> ToArgs()
    {
        var a = new List<string>();
        AddCommon(a);
        if (BurnValues is not null) foreach (var v in BurnValues) a.AddRange(["-burn", F(v)]);
        if (Attribute is not null) a.AddRange(["-a", Attribute]);
        if (Layers is not null) foreach (var l in Layers) a.AddRange(["-l", l]);
        if (Where is not null) a.AddRange(["-where", Where]);
        if (Sql is not null) a.AddRange(["-sql", Sql]);
        if (Resolution is { } r) a.AddRange(["-tr", F(r.X), F(r.Y)]);
        if (OutputSize is { } s) a.AddRange(["-ts", s.Width.ToString(CultureInfo.InvariantCulture), s.Height.ToString(CultureInfo.InvariantCulture)]);
        if (Bounds is { } b) a.AddRange(["-te", F(b.MinX), F(b.MinY), F(b.MaxX), F(b.MaxY)]);
        if (OutputType is { } t) a.AddRange(["-ot", t.ToString()]);
        if (NoData is { } nd) a.AddRange(["-a_nodata", F(nd)]);
        if (InitValue is { } iv) a.AddRange(["-init", F(iv)]);
        if (AllTouched) a.Add("-at");
        if (TargetSrs is not null) a.AddRange(["-a_srs", TargetSrs]);
        AddExtra(a);
        return a;
    }
}
