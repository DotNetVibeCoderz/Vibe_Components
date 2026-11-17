using GeoPandas.Net.Data;

namespace GeoPandas.Net.Arrow;

/// <summary>Registers GeoParquet (.parquet, .geoparquet) and Arrow IPC (.arrow, .feather, .ipc) with <see cref="GeoIO"/>.</summary>
public static class ArrowDrivers
{
    private static int s_registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 1) return;
        GeoIO.Register(new GeoDriver("GeoParquet", [".parquet", ".geoparquet"], (p, _) => GeoParquet.Read(p), (g, p, _) => GeoParquet.Write(g, p)));
        GeoIO.Register(new GeoDriver("Arrow IPC", [".arrow", ".feather", ".ipc"], (p, _) => ArrowInterop.ReadGeoArrowFile(p), (g, p, _) => g.WriteArrowFile(p)));
        GeoIO.RegisterScanner([".parquet", ".geoparquet"], (p, _) => new GeoParquetScanSource(p));
    }
}
