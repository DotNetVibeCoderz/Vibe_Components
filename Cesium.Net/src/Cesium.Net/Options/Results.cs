namespace CesiumNet;

/// <summary>Current camera state.</summary>
public sealed class CameraState
{
    public GeoPoint Position { get; init; }
    public double Heading { get; init; }
    public double Pitch { get; init; }
    public double Roll { get; init; }
    /// <summary>Visible area (null when the horizon/space is in view).</summary>
    public GeoRectangle? ViewRectangle { get; init; }

    public override string ToString() => $"{Position} hdg {Heading:F1}° pitch {Pitch:F1}°";
}

/// <summary>Result of a pick (click/move) on the scene.</summary>
public sealed class PickResult
{
    public PickKind Kind { get; init; }
    /// <summary>Screen position in CSS pixels.</summary>
    public Cartesian2 Screen { get; init; }
    /// <summary>World position under the cursor (globe, terrain or 3D tiles), if any.</summary>
    public GeoPoint? Position { get; init; }
    /// <summary>Picked entity id (for <see cref="PickKind.Entity"/>).</summary>
    public string? EntityId { get; init; }
    public string? EntityName { get; init; }
    /// <summary>Properties of the picked entity or 3D Tiles feature.</summary>
    public Dictionary<string, string>? Properties { get; init; }
    /// <summary>Id of the tileset, data source or point collection that owns the picked object.</summary>
    public string? OwnerId { get; init; }
}

/// <summary>Result of an interactive drawing.</summary>
public sealed class DrawResult
{
    public DrawMode Mode { get; init; }
    /// <summary>Id of the created entity (null if <see cref="DrawOptions.KeepEntity"/> was false).</summary>
    public string? EntityId { get; init; }
    public IReadOnlyList<GeoPoint> Positions { get; init; } = [];
    /// <summary>Circle radius in meters (circle mode).</summary>
    public double? Radius { get; init; }

    /// <summary>Ellipsoidal path length in meters.</summary>
    public double Length => Geodesy.PathLength(Positions);
    /// <summary>Ellipsoidal area in m² (polygon / rectangle / circle).</summary>
    public double Area => Mode switch
    {
        DrawMode.Circle when Radius is { } r => System.Math.PI * r * r,
        DrawMode.Rectangle when Positions.Count == 2 => Geodesy.Area(RectangleCorners(Positions[0], Positions[1])),
        DrawMode.Polygon or DrawMode.Rectangle or DrawMode.MeasureArea => Geodesy.Area(Positions),
        _ => 0,
    };

    private static GeoPoint[] RectangleCorners(GeoPoint a, GeoPoint b)
        => [a, new GeoPoint(b.Longitude, a.Latitude), b, new GeoPoint(a.Longitude, b.Latitude)];
}

/// <summary>Raised when an entity's geometry was edited interactively.</summary>
public sealed class EntityEditedResult
{
    public required string EntityId { get; init; }
    public IReadOnlyList<GeoPoint> Positions { get; init; } = [];
}

/// <summary>Clock state snapshot.</summary>
public sealed class ClockState
{
    public DateTimeOffset CurrentTime { get; init; }
    public double Multiplier { get; init; }
    public bool ShouldAnimate { get; init; }
}

/// <summary>An error raised by the JavaScript side.</summary>
public sealed class CesiumError
{
    public string Message { get; init; } = "";
    public string? Source { get; init; }
    public string? Stack { get; init; }
    public override string ToString() => Source is null ? Message : $"{Source}: {Message}";
}

/// <summary>Elevation profile sample.</summary>
/// <param name="Distance">Distance from start in meters.</param>
/// <param name="Position">Sampled terrain position (height = terrain height).</param>
public readonly record struct ProfileSample(double Distance, GeoPoint Position);

/// <summary>Exception thrown when a JavaScript call fails.</summary>
public sealed class CesiumInteropException(string method, string message) : Exception($"Cesium call '{method}' failed: {message}")
{
    public string Method { get; } = method;
    public string JsMessage { get; } = message;
}
