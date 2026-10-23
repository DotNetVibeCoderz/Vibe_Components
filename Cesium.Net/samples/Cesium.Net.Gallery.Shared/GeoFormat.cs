using System.Globalization;

namespace CesiumNet.Gallery;

/// <summary>Compact coordinate formatting for the gallery UI.</summary>
public static class GeoFormat
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>"6.18°S 106.83°E"</summary>
    public static string Short(GeoPoint p, int decimals = 2)
        => $"{Lat(p.Latitude, decimals)} {Lon(p.Longitude, decimals)}";

    public static string Lat(double v, int decimals = 4)
        => Math.Abs(v).ToString("F" + decimals, Inv) + "°" + (v >= 0 ? "N" : "S");

    public static string Lon(double v, int decimals = 4)
        => Math.Abs(v).ToString("F" + decimals, Inv) + "°" + (v >= 0 ? "E" : "W");

    /// <summary>"850 m" / "12.4 km" / "9,500 km"</summary>
    public static string Altitude(double meters) => Math.Abs(meters) switch
    {
        < 10_000 => meters.ToString("N0", Inv) + " m",
        < 1_000_000 => (meters / 1000).ToString("N1", Inv) + " km",
        _ => (meters / 1000).ToString("N0", Inv) + " km",
    };

    public static string Degrees(double v) => (Math.Round(v, 1) is var r && Math.Abs(r) >= 360 ? r % 360 : r).ToString("F1", Inv) + "°";

    public static string Describe(PickResult pick)
    {
        var where = pick.Position is { } p ? $"{Lat(p.Latitude)} {Lon(p.Longitude)} · {Altitude(p.Height)}" : "space";
        return pick.Kind switch
        {
            PickKind.Entity => $"Entity “{pick.EntityName ?? pick.EntityId}” at {where}",
            PickKind.TileFeature => $"3D Tiles feature at {where}" + (pick.Properties is { Count: > 0 } props
                ? " — " + string.Join(", ", props.Take(4).Select(kv => $"{kv.Key}={kv.Value}"))
                : ""),
            PickKind.Primitive => $"Primitive #{pick.Properties?.GetValueOrDefault("index")} ({pick.OwnerId}) at {where}",
            PickKind.Globe => $"Globe at {where}",
            _ => "Nothing picked",
        };
    }
}
