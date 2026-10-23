namespace CesiumNet;

/// <summary>Describes an imagery (raster map) layer.</summary>
public sealed class ImageryLayerOptions
{
    public ImageryProviderKind Provider { get; set; } = ImageryProviderKind.OpenStreetMap;
    /// <summary>Service URL or URL template (meaning depends on <see cref="Provider"/>).</summary>
    public string? Url { get; set; }
    /// <summary>WMS layers (comma separated) or WMTS layer name.</summary>
    public string? Layers { get; set; }
    /// <summary>WMTS style.</summary>
    public string? Style { get; set; }
    /// <summary>WMTS tile matrix set id.</summary>
    public string? TileMatrixSetId { get; set; }
    /// <summary>Image MIME type, e.g. <c>image/png</c>.</summary>
    public string? Format { get; set; }
    /// <summary>Extra WMS/WMTS query parameters.</summary>
    public Dictionary<string, string>? Parameters { get; set; }
    /// <summary>URL template subdomains, e.g. <c>["a","b","c"]</c>.</summary>
    public IReadOnlyList<string>? Subdomains { get; set; }
    /// <summary>API key (Bing).</summary>
    public string? Key { get; set; }
    /// <summary>Bing map style: Aerial, AerialWithLabels, Road, CanvasDark…</summary>
    public string? MapStyle { get; set; }
    /// <summary>Cesium ion asset id.</summary>
    public long? AssetId { get; set; }
    public int? MinimumLevel { get; set; }
    public int? MaximumLevel { get; set; }
    /// <summary>Restricts the layer to a rectangle (also required for <see cref="ImageryProviderKind.SingleTile"/>).</summary>
    public GeoRectangle? Rectangle { get; set; }
    /// <summary>Attribution text.</summary>
    public string? Credit { get; set; }
    public CesiumColor? Color { get; set; }

    // Layer appearance (also updatable later via ImageryApi.UpdateAsync)
    public double? Alpha { get; set; }
    public double? Brightness { get; set; }
    public double? Contrast { get; set; }
    public double? Hue { get; set; }
    public double? Saturation { get; set; }
    public double? Gamma { get; set; }
    public bool? Show { get; set; }
    public SplitDirection? SplitDirection { get; set; }

    public static ImageryLayerOptions OpenStreetMap() => new() { Provider = ImageryProviderKind.OpenStreetMap };
    public static ImageryLayerOptions Satellite() => new() { Provider = ImageryProviderKind.ArcGisWorldImagery };
    public static ImageryLayerOptions Streets() => new() { Provider = ImageryProviderKind.ArcGisWorldStreetMap };
    public static ImageryLayerOptions Topographic() => new() { Provider = ImageryProviderKind.ArcGisWorldTopo };

    /// <summary>Esri dark gray canvas basemap (no key required) — ideal for operational dashboards.</summary>
    public static ImageryLayerOptions Dark() => new()
    {
        Provider = ImageryProviderKind.UrlTemplate,
        Url = "https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Dark_Gray_Base/MapServer/tile/{z}/{y}/{x}",
        MaximumLevel = 16,
        Credit = "Esri, HERE, Garmin, © OpenStreetMap contributors",
    };

    /// <summary>Esri light gray canvas basemap (no key required).</summary>
    public static ImageryLayerOptions Light() => new()
    {
        Provider = ImageryProviderKind.UrlTemplate,
        Url = "https://services.arcgisonline.com/ArcGIS/rest/services/Canvas/World_Light_Gray_Base/MapServer/tile/{z}/{y}/{x}",
        MaximumLevel = 16,
        Credit = "Esri, HERE, Garmin, © OpenStreetMap contributors",
    };

    /// <summary>XYZ template layer.</summary>
    public static ImageryLayerOptions FromTemplate(string urlTemplate, string? credit = null, int? maximumLevel = null) => new()
    {
        Provider = ImageryProviderKind.UrlTemplate, Url = urlTemplate, Credit = credit, MaximumLevel = maximumLevel,
    };

    /// <summary>OGC WMS layer.</summary>
    public static ImageryLayerOptions Wms(string url, string layers, string format = "image/png", bool transparent = true) => new()
    {
        Provider = ImageryProviderKind.Wms, Url = url, Layers = layers, Format = format,
        Parameters = new() { ["transparent"] = transparent ? "true" : "false", ["format"] = format },
    };

    /// <summary>OGC WMTS layer.</summary>
    public static ImageryLayerOptions Wmts(string url, string layer, string tileMatrixSetId, string style = "default", string format = "image/png", int? maximumLevel = null) => new()
    {
        Provider = ImageryProviderKind.Wmts, Url = url, Layers = layer, TileMatrixSetId = tileMatrixSetId, Style = style, Format = format, MaximumLevel = maximumLevel,
    };
}

/// <summary>Appearance properties of an existing imagery layer.</summary>
public sealed class ImageryLayerUpdate
{
    public double? Alpha { get; set; }
    public double? Brightness { get; set; }
    public double? Contrast { get; set; }
    public double? Hue { get; set; }
    public double? Saturation { get; set; }
    public double? Gamma { get; set; }
    public bool? Show { get; set; }
    public SplitDirection? SplitDirection { get; set; }
}

/// <summary>Describes the terrain (elevation) provider.</summary>
public sealed class TerrainOptions
{
    public TerrainProviderKind Provider { get; set; } = TerrainProviderKind.Ellipsoid;
    public string? Url { get; set; }
    public long? AssetId { get; set; }
    /// <summary>Request normals for terrain lighting.</summary>
    public bool? RequestVertexNormals { get; set; }
    /// <summary>Request the water mask for ocean rendering.</summary>
    public bool? RequestWaterMask { get; set; }

    public static TerrainOptions Ellipsoid() => new() { Provider = TerrainProviderKind.Ellipsoid };
    public static TerrainOptions WorldTerrain(bool lighting = true, bool water = true) => new() { Provider = TerrainProviderKind.CesiumWorldTerrain, RequestVertexNormals = lighting, RequestWaterMask = water };
    public static TerrainOptions ArcGis() => new() { Provider = TerrainProviderKind.ArcGisElevation };
}

/// <summary>Describes a 3D Tiles tileset (buildings, photogrammetry, point clouds, BIM…).</summary>
public sealed class TilesetOptions
{
    public TilesetKind Kind { get; set; } = TilesetKind.Url;
    public string? Url { get; set; }
    public long? AssetId { get; set; }
    /// <summary>Google Maps API key (only for <see cref="TilesetKind.GooglePhotorealistic"/> without ion).</summary>
    public string? Key { get; set; }
    /// <summary>Screen-space error threshold; lower = more detail, higher = faster (default 16).</summary>
    public double? MaximumScreenSpaceError { get; set; }
    public bool? Show { get; set; }
    /// <summary>Fly the camera to the tileset once loaded.</summary>
    public bool? FlyTo { get; set; }
    /// <summary>Vertical offset in meters (e.g. to sit photogrammetry on terrain).</summary>
    public double? HeightOffset { get; set; }
    /// <summary>Declarative 3D Tiles style (color/show/pointSize expressions).</summary>
    public TilesetStyle? Style { get; set; }
    /// <summary>Point cloud attenuation and eye-dome lighting.</summary>
    public PointCloudShading? PointCloudShading { get; set; }
    /// <summary>GLSL custom shader applied to every tile.</summary>
    public CustomShaderOptions? CustomShader { get; set; }
    public ShadowMode? Shadows { get; set; }
    /// <summary>Hide parts of the tileset inside (or outside, when <see cref="ClipInverse"/>) these polygons.</summary>
    public IReadOnlyList<IReadOnlyList<GeoPoint>>? ClippingPolygons { get; set; }
    public bool? ClipInverse { get; set; }
    /// <summary>Skip levels of detail for faster loading of massive datasets.</summary>
    public bool? SkipLevelOfDetail { get; set; }
    /// <summary>Lower detail while the camera is moving.</summary>
    public bool? DynamicScreenSpaceError { get; set; }
    /// <summary>Show tile bounding volumes (debug).</summary>
    public bool? DebugShowBoundingVolume { get; set; }

    public static TilesetOptions FromUrl(string url, bool flyTo = false) => new() { Kind = TilesetKind.Url, Url = url, FlyTo = flyTo };
    public static TilesetOptions FromIon(long assetId, bool flyTo = false) => new() { Kind = TilesetKind.Ion, AssetId = assetId, FlyTo = flyTo };
    public static TilesetOptions OsmBuildings() => new() { Kind = TilesetKind.OsmBuildings };
    public static TilesetOptions GooglePhotorealistic(string? key = null) => new() { Kind = TilesetKind.GooglePhotorealistic, Key = key };
}

/// <summary>
/// A declarative 3D Tiles style. Expressions use the 3D Tiles styling language, e.g.
/// <c>Color = "color('white')"</c> or conditions such as <c>["${height} > 100", "color('red')"]</c>.
/// </summary>
public sealed class TilesetStyle
{
    /// <summary>Simple color expression, e.g. <c>color('#1F4FE0', 0.8)</c>. Ignored when <see cref="ColorConditions"/> is set.</summary>
    public string? Color { get; set; }
    /// <summary>Ordered (condition, color) pairs. The first matching condition wins.</summary>
    public IReadOnlyList<string[]>? ColorConditions { get; set; }
    /// <summary>Show expression, e.g. <c>${height} > 20</c>.</summary>
    public string? Show { get; set; }
    /// <summary>Point size expression for point clouds.</summary>
    public string? PointSize { get; set; }
    /// <summary>Named variables/defines usable in expressions.</summary>
    public Dictionary<string, string>? Defines { get; set; }
}

public sealed class PointCloudShading
{
    public bool? Attenuation { get; set; }
    public double? MaximumAttenuation { get; set; }
    public bool? EyeDomeLighting { get; set; }
    public double? EyeDomeLightingStrength { get; set; }
    public double? EyeDomeLightingRadius { get; set; }
}

/// <summary>
/// A CesiumJS <c>CustomShader</c>. Write GLSL for
/// <c>void fragmentMain(FragmentInput fsInput, inout czm_modelMaterial material)</c>.
/// </summary>
public sealed class CustomShaderOptions
{
    public string? FragmentShaderText { get; set; }
    public string? VertexShaderText { get; set; }
    /// <summary>"PBR" or "UNLIT".</summary>
    public string? LightingModel { get; set; }
    /// <summary>Float uniforms; animate with <see cref="TilesetsApi.SetShaderUniformAsync"/>.</summary>
    public Dictionary<string, double>? Uniforms { get; set; }
    /// <summary>Automatically sets a <c>u_time</c> uniform (seconds) each frame.</summary>
    public bool? AnimateTime { get; set; }
}

/// <summary>Describes a vector data source (GeoJSON, TopoJSON, KML/KMZ, CZML, GPX).</summary>
public sealed class DataSourceOptions
{
    public DataSourceKind Kind { get; set; } = DataSourceKind.GeoJson;
    /// <summary>Remote URL to load.</summary>
    public string? Url { get; set; }
    /// <summary>Inline document text (GeoJSON / TopoJSON / CZML JSON or KML XML).</summary>
    public string? Data { get; set; }
    public string? Name { get; set; }
    public bool? FlyTo { get; set; }
    public bool? ClampToGround { get; set; }
    // GeoJSON styling
    public CesiumColor? Stroke { get; set; }
    public double? StrokeWidth { get; set; }
    public CesiumColor? Fill { get; set; }
    public CesiumColor? MarkerColor { get; set; }
    public double? MarkerSize { get; set; }
    /// <summary>Extrude polygons by this numeric feature property (meters).</summary>
    public string? ExtrudeByProperty { get; set; }
    /// <summary>Multiplier applied to <see cref="ExtrudeByProperty"/>.</summary>
    public double? ExtrudeScale { get; set; }
    /// <summary>Randomize polygon fill colors per feature (choropleth-like look).</summary>
    public bool? RandomFill { get; set; }

    public static DataSourceOptions GeoJsonUrl(string url, bool flyTo = true) => new() { Kind = DataSourceKind.GeoJson, Url = url, FlyTo = flyTo };
    public static DataSourceOptions GeoJsonData(string json, bool flyTo = true) => new() { Kind = DataSourceKind.GeoJson, Data = json, FlyTo = flyTo };
    public static DataSourceOptions Kml(string url, bool flyTo = true) => new() { Kind = DataSourceKind.Kml, Url = url, FlyTo = flyTo, ClampToGround = true };
    public static DataSourceOptions Czml(string url, bool flyTo = true) => new() { Kind = DataSourceKind.Czml, Url = url, FlyTo = flyTo };
    public static DataSourceOptions Gpx(string url, bool flyTo = true) => new() { Kind = DataSourceKind.Gpx, Url = url, FlyTo = flyTo, ClampToGround = true };
    public static DataSourceOptions TopoJson(string url, bool flyTo = true) => new() { Kind = DataSourceKind.TopoJson, Url = url, FlyTo = flyTo };
}

/// <summary>A large batch of points rendered with a single <c>PointPrimitiveCollection</c> (GPU instanced).</summary>
public sealed class PointCollectionOptions
{
    public required IReadOnlyList<GeoPoint> Positions { get; set; }
    /// <summary>Per-point colors (same length as positions), or null to use <see cref="Color"/>.</summary>
    public IReadOnlyList<CesiumColor>? Colors { get; set; }
    public CesiumColor? Color { get; set; }
    public double? PixelSize { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? OutlineWidth { get; set; }
    public NearFarScalar? ScaleByDistance { get; set; }
}
