namespace Agritech.RemoteSensing;

public enum SpectralBand { Coastal, Blue, Green, Red, RedEdge1, RedEdge2, RedEdge3, Nir, NirNarrow, WaterVapour, Swir1, Swir2, Thermal }

public enum SatelliteSensor { Sentinel2, Landsat8, Landsat9, Modis, Drone }

/// <summary>Band metadata: native band id, central wavelength (nm) and spatial resolution (m).</summary>
public sealed record BandInfo(SpectralBand Band, string NativeId, double WavelengthNm, double ResolutionM);

/// <summary>Sensor band tables and reflectance scaling for common agricultural satellites.</summary>
public static class SensorCatalog
{
    public static IReadOnlyList<BandInfo> Bands(SatelliteSensor sensor) => sensor switch
    {
        SatelliteSensor.Sentinel2 =>
        [
            new(SpectralBand.Coastal, "B01", 443, 60), new(SpectralBand.Blue, "B02", 490, 10), new(SpectralBand.Green, "B03", 560, 10),
            new(SpectralBand.Red, "B04", 665, 10), new(SpectralBand.RedEdge1, "B05", 705, 20), new(SpectralBand.RedEdge2, "B06", 740, 20),
            new(SpectralBand.RedEdge3, "B07", 783, 20), new(SpectralBand.Nir, "B08", 842, 10), new(SpectralBand.NirNarrow, "B8A", 865, 20),
            new(SpectralBand.WaterVapour, "B09", 945, 60), new(SpectralBand.Swir1, "B11", 1610, 20), new(SpectralBand.Swir2, "B12", 2190, 20),
        ],
        SatelliteSensor.Landsat8 or SatelliteSensor.Landsat9 =>
        [
            new(SpectralBand.Coastal, "SR_B1", 443, 30), new(SpectralBand.Blue, "SR_B2", 482, 30), new(SpectralBand.Green, "SR_B3", 561, 30),
            new(SpectralBand.Red, "SR_B4", 655, 30), new(SpectralBand.Nir, "SR_B5", 865, 30), new(SpectralBand.Swir1, "SR_B6", 1609, 30),
            new(SpectralBand.Swir2, "SR_B7", 2201, 30), new(SpectralBand.Thermal, "ST_B10", 10895, 100),
        ],
        SatelliteSensor.Modis =>
        [
            new(SpectralBand.Red, "sur_refl_b01", 645, 250), new(SpectralBand.Nir, "sur_refl_b02", 858, 250), new(SpectralBand.Blue, "sur_refl_b03", 469, 500),
            new(SpectralBand.Green, "sur_refl_b04", 555, 500), new(SpectralBand.Swir1, "sur_refl_b06", 1640, 500), new(SpectralBand.Swir2, "sur_refl_b07", 2130, 500),
        ],
        _ => [new(SpectralBand.Blue, "B", 475, 0.05), new(SpectralBand.Green, "G", 560, 0.05), new(SpectralBand.Red, "R", 668, 0.05),
              new(SpectralBand.RedEdge1, "RE", 717, 0.05), new(SpectralBand.Nir, "NIR", 842, 0.05)],
    };

    /// <summary>Converts a raw digital number to surface reflectance (0–1).</summary>
    /// <param name="processingBaseline">Sentinel-2 processing baseline; ≥ 4.0 (2022-01-25+) applies the −1000 offset.</param>
    public static float ToReflectance(SatelliteSensor sensor, double dn, double processingBaseline = 5.0) => sensor switch
    {
        SatelliteSensor.Sentinel2 => (float)((dn + (processingBaseline >= 4.0 ? -1000 : 0)) / 10000.0),
        SatelliteSensor.Landsat8 or SatelliteSensor.Landsat9 => (float)(dn * 0.0000275 - 0.2),
        SatelliteSensor.Modis => (float)(dn * 0.0001),
        _ => (float)dn,
    };

    /// <summary>Revisit period in days (constellation).</summary>
    public static int RevisitDays(SatelliteSensor s) => s switch
    {
        SatelliteSensor.Sentinel2 => 5,
        SatelliteSensor.Landsat8 or SatelliteSensor.Landsat9 => 8,
        SatelliteSensor.Modis => 1,
        _ => 0,
    };

    /// <summary>Sentinel-2 L2A Scene Classification (SCL) values considered invalid (no data, saturated, shadow, cloud, cirrus, snow).</summary>
    public static bool IsSclInvalid(byte scl) => scl is 0 or 1 or 3 or 8 or 9 or 10 or 11;
}
