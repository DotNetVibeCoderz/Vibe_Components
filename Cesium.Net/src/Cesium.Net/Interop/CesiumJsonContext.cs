using System.Text.Json.Serialization;

namespace CesiumNet.Interop;

/// <summary>
/// Source-generated JSON metadata for all interop payloads (reflection-free, trimming/AOT safe).
/// Enums are written as PascalCase strings; the JavaScript bridge compares them case-insensitively.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(CesiumViewerOptions))]
[JsonSerializable(typeof(EntityOptions))]
[JsonSerializable(typeof(IReadOnlyList<EntityOptions>))]
[JsonSerializable(typeof(IReadOnlyList<EntityPositionUpdate>))]
[JsonSerializable(typeof(ImageryLayerOptions))]
[JsonSerializable(typeof(ImageryLayerUpdate))]
[JsonSerializable(typeof(TerrainOptions))]
[JsonSerializable(typeof(TilesetOptions))]
[JsonSerializable(typeof(TilesetStyle))]
[JsonSerializable(typeof(DataSourceOptions))]
[JsonSerializable(typeof(PointCollectionOptions))]
[JsonSerializable(typeof(CameraView))]
[JsonSerializable(typeof(CameraControllerOptions))]
[JsonSerializable(typeof(HeadingPitchRange))]
[JsonSerializable(typeof(HeadingPitchRange?))]
[JsonSerializable(typeof(GeoPoint?))]
[JsonSerializable(typeof(Cartesian2?))]
[JsonSerializable(typeof(IReadOnlyList<DateTimeOffset>))]
[JsonSerializable(typeof(CesiumEventOptions))]
[JsonSerializable(typeof(ClockOptions))]
[JsonSerializable(typeof(SceneSettings))]
[JsonSerializable(typeof(WeatherOptions))]
[JsonSerializable(typeof(DrawOptions))]
[JsonSerializable(typeof(FlightModeOptions))]
[JsonSerializable(typeof(GeoRectangle))]
[JsonSerializable(typeof(GeoPoint))]
[JsonSerializable(typeof(GeoPoint[]))]
[JsonSerializable(typeof(IReadOnlyList<GeoPoint>))]
[JsonSerializable(typeof(IReadOnlyList<IReadOnlyList<GeoPoint>>))]
[JsonSerializable(typeof(CameraState))]
[JsonSerializable(typeof(PickResult))]
[JsonSerializable(typeof(DrawResult))]
[JsonSerializable(typeof(EntityEditedResult))]
[JsonSerializable(typeof(ClockState))]
[JsonSerializable(typeof(CesiumError))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
internal sealed partial class CesiumJsonContext : JsonSerializerContext;
