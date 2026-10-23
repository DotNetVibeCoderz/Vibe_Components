using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CesiumNet;

/// <summary>
/// A geodetic position on the WGS84 ellipsoid, expressed in <b>degrees</b> and <b>meters</b>.
/// This is the primary position type used throughout Cesium.Net.
/// Serialized compactly to JavaScript as <c>[lon, lat, height]</c>.
/// </summary>
/// <param name="Longitude">Longitude in degrees, -180..180.</param>
/// <param name="Latitude">Latitude in degrees, -90..90.</param>
/// <param name="Height">Height above the ellipsoid in meters.</param>
[JsonConverter(typeof(GeoPointJsonConverter))]
public readonly record struct GeoPoint(double Longitude, double Latitude, double Height = 0)
{
    /// <summary>Creates a point from radians.</summary>
    public static GeoPoint FromRadians(double longitude, double latitude, double height = 0)
        => new(GeoMath.ToDegrees(longitude), GeoMath.ToDegrees(latitude), height);

    /// <summary>Converts to a <see cref="Cartographic"/> (radians).</summary>
    public Cartographic ToCartographic() => Cartographic.FromDegrees(Longitude, Latitude, Height);

    /// <summary>Converts to an Earth-Centered, Earth-Fixed Cartesian position (meters) on WGS84.</summary>
    public Cartesian3 ToCartesian() => CoordinateConverter.GeodeticToEcef(this);

    /// <summary>Returns a copy with a different height.</summary>
    public GeoPoint WithHeight(double height) => this with { Height = height };

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"({Longitude:F6}°, {Latitude:F6}°, {Height:F1} m)");
}

/// <summary>Serializes <see cref="GeoPoint"/> as a compact <c>[lon, lat, height]</c> JSON array.</summary>
public sealed class GeoPointJsonConverter : JsonConverter<GeoPoint>
{
    public override GeoPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read(); var lon = reader.GetDouble();
            reader.Read(); var lat = reader.GetDouble();
            reader.Read();
            double h = 0;
            if (reader.TokenType != JsonTokenType.EndArray)
            {
                h = reader.TokenType == JsonTokenType.Null ? 0 : reader.GetDouble();
                reader.Read();
            }
            return new GeoPoint(lon, lat, h);
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            double lon = 0, lat = 0, h = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "lon" or "longitude": lon = reader.GetDouble(); break;
                    case "lat" or "latitude": lat = reader.GetDouble(); break;
                    case "h" or "height": h = reader.GetDouble(); break;
                    default: reader.Skip(); break;
                }
            }
            return new GeoPoint(lon, lat, h);
        }

        throw new JsonException("Expected [lon, lat, height] array for GeoPoint.");
    }

    public override void Write(Utf8JsonWriter writer, GeoPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.Longitude);
        writer.WriteNumberValue(value.Latitude);
        writer.WriteNumberValue(value.Height);
        writer.WriteEndArray();
    }
}
