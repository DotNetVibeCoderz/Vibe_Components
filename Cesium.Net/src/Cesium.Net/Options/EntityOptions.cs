namespace CesiumNet;

/// <summary>
/// Describes a Cesium entity: a position (static or time-sampled) plus any combination of graphics.
/// Unset (<c>null</c>) properties are not sent to JavaScript, keeping interop payloads small.
/// </summary>
public sealed class EntityOptions
{
    /// <summary>Unique id. Generated when omitted.</summary>
    public string? Id { get; set; }
    public string? Name { get; set; }
    /// <summary>HTML shown in the info box when the entity is selected.</summary>
    public string? Description { get; set; }
    public bool? Show { get; set; }
    /// <summary>Static position.</summary>
    public GeoPoint? Position { get; set; }
    /// <summary>Time-dynamic position (overrides <see cref="Position"/>).</summary>
    public SampledPositions? SampledPosition { get; set; }
    /// <summary>Fixed orientation (degrees).</summary>
    public HeadingPitchRoll? Orientation { get; set; }
    /// <summary>Orient models along the direction of travel (requires <see cref="SampledPosition"/>).</summary>
    public bool? OrientToVelocity { get; set; }
    /// <summary>Time interval during which the entity exists.</summary>
    public TimeInterval? Availability { get; set; }
    /// <summary>Id of a parent entity (shows/hides with it).</summary>
    public string? ParentId { get; set; }
    /// <summary>Arbitrary key/value properties, returned on picking.</summary>
    public Dictionary<string, string>? Properties { get; set; }

    public PointGraphics? Point { get; set; }
    public BillboardGraphics? Billboard { get; set; }
    public LabelGraphics? Label { get; set; }
    public PolylineGraphics? Polyline { get; set; }
    public PolygonGraphics? Polygon { get; set; }
    public EllipseGraphics? Ellipse { get; set; }
    public RectangleGraphics? Rectangle { get; set; }
    public BoxGraphics? Box { get; set; }
    public CylinderGraphics? Cylinder { get; set; }
    public EllipsoidGraphics? Ellipsoid { get; set; }
    public CorridorGraphics? Corridor { get; set; }
    public WallGraphics? Wall { get; set; }
    public ModelGraphics? Model { get; set; }
    public PathGraphics? Path { get; set; }
}

/// <summary>A closed time interval.</summary>
public sealed record TimeInterval(DateTimeOffset Start, DateTimeOffset Stop);

/// <summary>Time-tagged positions interpolated by CesiumJS (a <c>SampledPositionProperty</c>).</summary>
public sealed class SampledPositions
{
    public required IReadOnlyList<DateTimeOffset> Times { get; init; }
    public required IReadOnlyList<GeoPoint> Positions { get; init; }
    public InterpolationKind? Interpolation { get; init; }
    public int? InterpolationDegree { get; init; }
    /// <summary>Keep extrapolating beyond the last sample (useful for live feeds).</summary>
    public bool? Extrapolate { get; init; }

    /// <summary>Builds samples from (time, position) pairs.</summary>
    public static SampledPositions From(IEnumerable<(DateTimeOffset Time, GeoPoint Position)> samples, InterpolationKind interpolation = InterpolationKind.Lagrange, int degree = 3)
    {
        var times = new List<DateTimeOffset>();
        var positions = new List<GeoPoint>();
        foreach (var (t, p) in samples) { times.Add(t); positions.Add(p); }
        return new SampledPositions { Times = times, Positions = positions, Interpolation = interpolation, InterpolationDegree = degree };
    }
}

public sealed class PointGraphics
{
    public double? PixelSize { get; set; }
    public CesiumColor? Color { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? OutlineWidth { get; set; }
    public HeightReference? HeightReference { get; set; }
    public NearFarScalar? ScaleByDistance { get; set; }
    public DistanceDisplayCondition? DistanceDisplayCondition { get; set; }
    /// <summary>Render on top of terrain/3D tiles regardless of depth.</summary>
    public bool? AlwaysOnTop { get; set; }
}

public sealed class BillboardGraphics
{
    /// <summary>Image URL or data URL. When omitted, a pin is generated from <see cref="PinColor"/>.</summary>
    public string? Image { get; set; }
    /// <summary>Generates a map pin of this color (CesiumJS <c>PinBuilder</c>).</summary>
    public CesiumColor? PinColor { get; set; }
    /// <summary>Text (1-3 chars) drawn in the generated pin.</summary>
    public string? PinText { get; set; }
    /// <summary>Maki icon id drawn in the generated pin, e.g. <c>airport</c>, <c>hospital</c>.</summary>
    public string? PinIcon { get; set; }
    public double? PinSize { get; set; }
    public double? Scale { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public CesiumColor? Color { get; set; }
    /// <summary>Rotation in degrees.</summary>
    public double? Rotation { get; set; }
    public VerticalOrigin? VerticalOrigin { get; set; }
    public HorizontalOrigin? HorizontalOrigin { get; set; }
    public Cartesian2? PixelOffset { get; set; }
    public HeightReference? HeightReference { get; set; }
    public NearFarScalar? ScaleByDistance { get; set; }
    public DistanceDisplayCondition? DistanceDisplayCondition { get; set; }
    public bool? AlwaysOnTop { get; set; }
}

public sealed class LabelGraphics
{
    public required string Text { get; set; }
    /// <summary>CSS font, e.g. <c>600 14px "IBM Plex Sans", sans-serif</c>.</summary>
    public string? Font { get; set; }
    public CesiumColor? FillColor { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? OutlineWidth { get; set; }
    public LabelStyle? Style { get; set; }
    public bool? ShowBackground { get; set; }
    public CesiumColor? BackgroundColor { get; set; }
    public double? Scale { get; set; }
    public Cartesian2? PixelOffset { get; set; }
    public VerticalOrigin? VerticalOrigin { get; set; }
    public HorizontalOrigin? HorizontalOrigin { get; set; }
    public HeightReference? HeightReference { get; set; }
    public NearFarScalar? ScaleByDistance { get; set; }
    public DistanceDisplayCondition? DistanceDisplayCondition { get; set; }
    public bool? AlwaysOnTop { get; set; }
}

public sealed class PolylineGraphics
{
    public required IReadOnlyList<GeoPoint> Positions { get; set; }
    public double? Width { get; set; }
    public CesiumMaterial? Material { get; set; }
    /// <summary>Material used where the line is behind terrain.</summary>
    public CesiumMaterial? DepthFailMaterial { get; set; }
    public bool? ClampToGround { get; set; }
    /// <summary>Follow great-circle arcs (true, default) or straight rhumb/linear segments.</summary>
    public bool? Geodesic { get; set; }
    public DistanceDisplayCondition? DistanceDisplayCondition { get; set; }
}

public sealed class PolygonGraphics
{
    /// <summary>Outer ring.</summary>
    public required IReadOnlyList<GeoPoint> Positions { get; set; }
    /// <summary>Inner rings (holes).</summary>
    public IReadOnlyList<IReadOnlyList<GeoPoint>>? Holes { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? Height { get; set; }
    public double? ExtrudedHeight { get; set; }
    /// <summary>Use each position's own height instead of <see cref="Height"/>.</summary>
    public bool? PerPositionHeight { get; set; }
    public HeightReference? HeightReference { get; set; }
    public HeightReference? ExtrudedHeightReference { get; set; }
    public ShadowMode? Shadows { get; set; }
    /// <summary>When true and no height is set, drapes the polygon over terrain and 3D Tiles.</summary>
    public bool? ClassifyGround { get; set; }
}

public sealed class EllipseGraphics
{
    public required double SemiMajorAxis { get; set; }
    public required double SemiMinorAxis { get; set; }
    /// <summary>Rotation in degrees, clockwise from north.</summary>
    public double? Rotation { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? Height { get; set; }
    public double? ExtrudedHeight { get; set; }
    public HeightReference? HeightReference { get; set; }
}

public sealed class RectangleGraphics
{
    public required GeoRectangle Coordinates { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public double? Height { get; set; }
    public double? ExtrudedHeight { get; set; }
}

public sealed class BoxGraphics
{
    /// <summary>Length, width, height in meters.</summary>
    public required Cartesian3 Dimensions { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public HeightReference? HeightReference { get; set; }
    public ShadowMode? Shadows { get; set; }
}

public sealed class CylinderGraphics
{
    public required double Length { get; set; }
    public required double TopRadius { get; set; }
    public required double BottomRadius { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public int? Slices { get; set; }
    public HeightReference? HeightReference { get; set; }
    public ShadowMode? Shadows { get; set; }
}

/// <summary>
/// Ellipsoid, sphere, dome or sensor volume. Use the clock/cone angles (degrees) to carve
/// partial shells — e.g. a radar dome is a sphere with <c>MaximumCone = 90</c>.
/// </summary>
public sealed class EllipsoidGraphics
{
    /// <summary>Radii along x, y, z in meters.</summary>
    public required Cartesian3 Radii { get; set; }
    public Cartesian3? InnerRadii { get; set; }
    public double? MinimumClock { get; set; }
    public double? MaximumClock { get; set; }
    public double? MinimumCone { get; set; }
    public double? MaximumCone { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Fill { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
    public int? StackPartitions { get; set; }
    public int? SlicePartitions { get; set; }
    public HeightReference? HeightReference { get; set; }

    /// <summary>Creates a radar/coverage dome (upper hemisphere) of the given range.</summary>
    public static EllipsoidGraphics Dome(double range, CesiumMaterial? material = null, CesiumColor? outline = null) => new()
    {
        Radii = new Cartesian3(range, range, range),
        MaximumCone = 90,
        Material = material ?? CesiumColor.Cyan.WithAlpha(0.18f),
        Outline = outline is not null,
        OutlineColor = outline,
        StackPartitions = 24,
        SlicePartitions = 48,
    };

    /// <summary>Creates a sensor wedge: azimuth sector [minAz, maxAz] (degrees from east, counterclockwise) and elevation up to <paramref name="maxElevation"/>.</summary>
    public static EllipsoidGraphics Sector(double range, double minAzimuth, double maxAzimuth, double maxElevation = 60, CesiumMaterial? material = null) => new()
    {
        Radii = new Cartesian3(range, range, range),
        // A (tiny) inner radius makes CesiumJS close the volume with side walls; without it only
        // the outer spherical patch is drawn and the wedge looks like a flat sheet.
        InnerRadii = new Cartesian3(1, 1, 1),
        MinimumClock = minAzimuth,
        MaximumClock = maxAzimuth,
        MinimumCone = 90 - maxElevation,
        MaximumCone = 90,
        Material = material ?? CesiumColor.Orange.WithAlpha(0.25f),
        Outline = true,
        OutlineColor = CesiumColor.Orange.WithAlpha(0.6f),
    };
}

public sealed class CorridorGraphics
{
    public required IReadOnlyList<GeoPoint> Positions { get; set; }
    /// <summary>Width in meters.</summary>
    public required double Width { get; set; }
    public CesiumMaterial? Material { get; set; }
    public double? Height { get; set; }
    public double? ExtrudedHeight { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
}

public sealed class WallGraphics
{
    /// <summary>Wall top positions (height = top of wall unless <see cref="MaximumHeights"/> is set).</summary>
    public required IReadOnlyList<GeoPoint> Positions { get; set; }
    public IReadOnlyList<double>? MinimumHeights { get; set; }
    public IReadOnlyList<double>? MaximumHeights { get; set; }
    public CesiumMaterial? Material { get; set; }
    public bool? Outline { get; set; }
    public CesiumColor? OutlineColor { get; set; }
}

/// <summary>A glTF/GLB 3D model with PBR materials and animations.</summary>
public sealed class ModelGraphics
{
    /// <summary>URL of a .gltf or .glb file.</summary>
    public required string Uri { get; set; }
    public double? Scale { get; set; }
    /// <summary>Keeps the model at least this many pixels in size when zoomed out.</summary>
    public double? MinimumPixelSize { get; set; }
    public double? MaximumScale { get; set; }
    public bool? RunAnimations { get; set; }
    public CesiumColor? Color { get; set; }
    public CesiumColor? SilhouetteColor { get; set; }
    public double? SilhouetteSize { get; set; }
    public HeightReference? HeightReference { get; set; }
    public ShadowMode? Shadows { get; set; }
    public DistanceDisplayCondition? DistanceDisplayCondition { get; set; }
}

/// <summary>Trail drawn behind a moving (time-sampled) entity.</summary>
public sealed class PathGraphics
{
    /// <summary>Seconds of future path to show.</summary>
    public double? LeadTime { get; set; }
    /// <summary>Seconds of past path to show.</summary>
    public double? TrailTime { get; set; }
    public double? Width { get; set; }
    public double? Resolution { get; set; }
    public CesiumMaterial? Material { get; set; }
}

/// <summary>A lightweight position update for real-time tracking (see <see cref="EntitiesApi.UpdatePositionsAsync"/>).</summary>
/// <param name="Id">Entity id.</param>
/// <param name="Position">New position.</param>
/// <param name="Heading">Optional heading in degrees (rotates models/billboards).</param>
public readonly record struct EntityPositionUpdate(string Id, GeoPoint Position, double? Heading = null);
