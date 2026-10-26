namespace GDAL.Net;

/// <summary>Pixel data types (GDALDataType).</summary>
public enum DataType
{
    Unknown = 0,
    Byte = 1,
    UInt16 = 2,
    Int16 = 3,
    UInt32 = 4,
    Int32 = 5,
    Float32 = 6,
    Float64 = 7,
    CInt16 = 8,
    CInt32 = 9,
    CFloat32 = 10,
    CFloat64 = 11,
    UInt64 = 12,
    Int64 = 13,
    Int8 = 14,
    Float16 = 15,
    CFloat16 = 16,
}

/// <summary>Flags for <see cref="Gdal.Open(string, OpenFlags, IEnumerable{string}?, IDictionary{string, string}?)"/> (GDAL_OF_*).</summary>
[Flags]
public enum OpenFlags : uint
{
    ReadOnly = 0x00,
    Update = 0x01,
    Raster = 0x02,
    Vector = 0x04,
    Gnm = 0x08,
    MultidimensionalRaster = 0x10,
    Shared = 0x20,
    VerboseError = 0x40,
    /// <summary>Raster or vector.</summary>
    All = Raster | Vector,
}

/// <summary>Resampling algorithms used by RasterIO, overviews and warping.</summary>
public enum ResampleAlgorithm
{
    NearestNeighbour = 0,
    Bilinear = 1,
    Cubic = 2,
    CubicSpline = 3,
    Lanczos = 4,
    Average = 5,
    Mode = 6,
    Gauss = 7,
    RootMeanSquare = 14,
}

/// <summary>Band color interpretation (GDALColorInterp).</summary>
public enum ColorInterpretation
{
    Undefined = 0,
    Gray = 1,
    Palette = 2,
    Red = 3,
    Green = 4,
    Blue = 5,
    Alpha = 6,
    Hue = 7,
    Saturation = 8,
    Lightness = 9,
    Cyan = 10,
    Magenta = 11,
    Yellow = 12,
    Black = 13,
    YCbCrY = 14,
    YCbCrCb = 15,
    YCbCrCr = 16,
    PanChromatic = 17,
    Coastal = 18,
    RedEdge = 19,
    NearInfrared = 20,
    ShortWaveInfrared = 21,
    MidWaveInfrared = 22,
    LongWaveInfrared = 23,
    ThermalInfrared = 24,
}

/// <summary>Palette interpretation of a color table.</summary>
public enum PaletteInterpretation
{
    Gray = 0,
    Rgb = 1,
    Cmyk = 2,
    Hls = 3,
}

/// <summary>Mask flags (GMF_*).</summary>
[Flags]
public enum MaskFlags
{
    None = 0,
    AllValid = 0x01,
    PerDataset = 0x02,
    Alpha = 0x04,
    NoData = 0x08,
}

/// <summary>OGR geometry types (OGRwkbGeometryType), 2D, ISO Z/M/ZM and legacy 2.5D variants.</summary>
public enum GeometryType : uint
{
    Unknown = 0,
    Point = 1,
    LineString = 2,
    Polygon = 3,
    MultiPoint = 4,
    MultiLineString = 5,
    MultiPolygon = 6,
    GeometryCollection = 7,
    CircularString = 8,
    CompoundCurve = 9,
    CurvePolygon = 10,
    MultiCurve = 11,
    MultiSurface = 12,
    Curve = 13,
    Surface = 14,
    PolyhedralSurface = 15,
    Tin = 16,
    Triangle = 17,
    None = 100,
    LinearRing = 101,

    PointZ = 1001,
    LineStringZ = 1002,
    PolygonZ = 1003,
    MultiPointZ = 1004,
    MultiLineStringZ = 1005,
    MultiPolygonZ = 1006,
    GeometryCollectionZ = 1007,

    PointM = 2001,
    LineStringM = 2002,
    PolygonM = 2003,

    PointZM = 3001,
    LineStringZM = 3002,
    PolygonZM = 3003,

    Point25D = 0x80000001,
    LineString25D = 0x80000002,
    Polygon25D = 0x80000003,
    MultiPoint25D = 0x80000004,
    MultiLineString25D = 0x80000005,
    MultiPolygon25D = 0x80000006,
    GeometryCollection25D = 0x80000007,
}

/// <summary>Attribute field types (OGRFieldType).</summary>
public enum FieldType
{
    Integer = 0,
    IntegerList = 1,
    Real = 2,
    RealList = 3,
    String = 4,
    StringList = 5,
    WideString = 6,
    WideStringList = 7,
    Binary = 8,
    Date = 9,
    Time = 10,
    DateTime = 11,
    Integer64 = 12,
    Integer64List = 13,
}

/// <summary>Attribute field sub types (OGRFieldSubType).</summary>
public enum FieldSubType
{
    None = 0,
    Boolean = 1,
    Int16 = 2,
    Float32 = 3,
    Json = 4,
    Uuid = 5,
}

/// <summary>OGR error codes (OGRErr).</summary>
public enum OgrError
{
    None = 0,
    NotEnoughData = 1,
    NotEnoughMemory = 2,
    UnsupportedGeometryType = 3,
    UnsupportedOperation = 4,
    CorruptData = 5,
    Failure = 6,
    UnsupportedSrs = 7,
    InvalidHandle = 8,
    NonExistingFeature = 9,
}

/// <summary>Raster attribute table column type.</summary>
public enum RatFieldType
{
    Integer = 0,
    Real = 1,
    String = 2,
    Boolean = 3,
    DateTime = 4,
    WkbGeometry = 5,
}

/// <summary>Raster attribute table column usage.</summary>
public enum RatFieldUsage
{
    Generic = 0,
    PixelCount = 1,
    Name = 2,
    Min = 3,
    Max = 4,
    MinMax = 5,
    Red = 6,
    Green = 7,
    Blue = 8,
    Alpha = 9,
    RedMin = 10,
    GreenMin = 11,
    BlueMin = 12,
    AlphaMin = 13,
    RedMax = 14,
    GreenMax = 15,
    BlueMax = 16,
    AlphaMax = 17,
}

/// <summary>Axis order used when interpreting coordinates of a spatial reference.</summary>
public enum AxisMappingStrategy
{
    /// <summary>Longitude/Easting first, Latitude/Northing second (GIS friendly, default in GDAL.Net).</summary>
    TraditionalGisOrder = 0,
    /// <summary>Axis order as defined by the authority (e.g. lat/lon for EPSG:4326).</summary>
    AuthorityCompliant = 1,
    Custom = 2,
}

/// <summary>Kind of CRS reported by the PROJ database.</summary>
public enum CrsType
{
    Geographic2D = 0,
    Geographic3D = 1,
    Geocentric = 2,
    Projected = 3,
    Vertical = 4,
    Compound = 5,
    Other = 6,
}

/// <summary>DEM analysis modes of <c>GdalUtils.DemProcessing</c>.</summary>
public enum DemProcessingMode
{
    Hillshade,
    Slope,
    Aspect,
    ColorRelief,
    TerrainRuggednessIndex,
    TopographicPositionIndex,
    Roughness,
}

/// <summary>Viewshed cell evaluation mode.</summary>
public enum ViewshedMode
{
    Diagonal = 1,
    Edge = 2,
    Max = 3,
    Min = 4,
}

/// <summary>Spatial overlay operations between two layers.</summary>
public enum LayerOverlay
{
    Intersection,
    Union,
    SymDifference,
    Identity,
    Update,
    Clip,
    Erase,
}
