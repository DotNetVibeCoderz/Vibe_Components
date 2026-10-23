namespace CesiumNet;

/// <summary>How the globe is projected.</summary>
public enum SceneMode { Scene3D, Scene2D, ColumbusView }

/// <summary>How an entity's height is interpreted relative to terrain.</summary>
public enum HeightReference { None, ClampToGround, RelativeToGround }

/// <summary>Vertical anchor of a billboard or label.</summary>
public enum VerticalOrigin { Center, Bottom, Baseline, Top }

/// <summary>Horizontal anchor of a billboard or label.</summary>
public enum HorizontalOrigin { Center, Left, Right }

/// <summary>Style of a label's text.</summary>
public enum LabelStyle { Fill, Outline, FillAndOutline }

/// <summary>Shadow casting/receiving mode for 3D geometry.</summary>
public enum ShadowMode { Disabled, Enabled, CastOnly, ReceiveOnly }

/// <summary>Interpolation used for time-sampled positions.</summary>
public enum InterpolationKind { Linear, Lagrange, Hermite }

/// <summary>Clock behavior at the end of its range.</summary>
public enum ClockRange { Unbounded, Clamped, LoopStop }

/// <summary>Built-in material kinds.</summary>
public enum MaterialKind { Color, PolylineGlow, PolylineDash, PolylineArrow, PolylineOutline, Stripe, Grid, Checkerboard, Image }

/// <summary>Built-in imagery provider kinds.</summary>
public enum ImageryProviderKind
{
    /// <summary>OpenStreetMap standard tiles (or any OSM-compatible tile server via <c>Url</c>).</summary>
    OpenStreetMap,
    /// <summary>Esri World Imagery (satellite) — no key required.</summary>
    ArcGisWorldImagery,
    /// <summary>Esri World Street Map.</summary>
    ArcGisWorldStreetMap,
    /// <summary>Esri World Topographic Map.</summary>
    ArcGisWorldTopo,
    /// <summary>Any ArcGIS MapServer endpoint (<c>Url</c>).</summary>
    ArcGisMapServer,
    /// <summary>Bing Maps (requires <c>Key</c>).</summary>
    Bing,
    /// <summary>OGC Web Map Service (<c>Url</c>, <c>Layers</c>).</summary>
    Wms,
    /// <summary>OGC Web Map Tile Service (<c>Url</c>, <c>Layers</c>, <c>TileMatrixSetId</c>).</summary>
    Wmts,
    /// <summary>Tile Map Service (gdal2tiles / MapTiler output).</summary>
    Tms,
    /// <summary>XYZ / slippy-map URL template, e.g. <c>https://{s}.tile.example.com/{z}/{x}/{y}.png</c>.</summary>
    UrlTemplate,
    /// <summary>Cesium ion imagery asset (<c>AssetId</c>).</summary>
    Ion,
    /// <summary>A single georeferenced image (<c>Url</c>, <c>Rectangle</c>).</summary>
    SingleTile,
    /// <summary>Debug grid showing tile coordinates.</summary>
    TileCoordinates,
    /// <summary>Solid color grid (debug / styling).</summary>
    Grid,
}

/// <summary>Built-in terrain provider kinds.</summary>
public enum TerrainProviderKind
{
    /// <summary>Smooth WGS84 ellipsoid, no elevation (no network access).</summary>
    Ellipsoid,
    /// <summary>Cesium World Terrain (Cesium ion, requires an access token).</summary>
    CesiumWorldTerrain,
    /// <summary>Quantized-mesh terrain server at <c>Url</c>.</summary>
    CesiumTerrainServer,
    /// <summary>Cesium ion terrain asset <c>AssetId</c>.</summary>
    Ion,
    /// <summary>Esri World Elevation 3D (ArcGIS tiled elevation) or a custom ImageServer <c>Url</c>.</summary>
    ArcGisElevation,
}

/// <summary>Built-in 3D Tiles sources.</summary>
public enum TilesetKind
{
    /// <summary>A <c>tileset.json</c> at <c>Url</c>.</summary>
    Url,
    /// <summary>Cesium ion asset <c>AssetId</c>.</summary>
    Ion,
    /// <summary>Cesium OSM Buildings (ion, ~350M buildings worldwide).</summary>
    OsmBuildings,
    /// <summary>Google Photorealistic 3D Tiles (ion or Google Maps key).</summary>
    GooglePhotorealistic,
}

/// <summary>Kinds of vector data sources.</summary>
public enum DataSourceKind { GeoJson, TopoJson, Kml, Czml, Gpx }

/// <summary>Interactive drawing modes.</summary>
public enum DrawMode { Point, Polyline, Polygon, Rectangle, Circle, MeasureDistance, MeasureArea }

/// <summary>Camera translation directions.</summary>
public enum CameraDirection { Forward, Backward, Left, Right, Up, Down }

/// <summary>Imagery split side for swipe comparisons.</summary>
public enum SplitDirection { None, Left, Right }

/// <summary>Built-in screen-space weather effects.</summary>
public enum WeatherKind { None, Rain, Snow, Fog }

/// <summary>What was hit by a pick.</summary>
public enum PickKind { None, Globe, Entity, TileFeature, Primitive }
