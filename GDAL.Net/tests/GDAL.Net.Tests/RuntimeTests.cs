namespace GDAL.Net.Tests;

public class RuntimeTests
{
    [Fact]
    public void LoadsNativeGdal()
    {
        Assert.StartsWith("3.", Gdal.Version);
        Assert.True(Gdal.VersionNumber >= 3_000_000);
        Assert.NotNull(Gdal.NativeLibraryPath);
        Assert.True(Gdal.Drivers.Count > 50);
    }

    [Fact]
    public void FindsSupportData()
    {
        Assert.NotNull(Gdal.DataPath);
        Assert.NotNull(Gdal.ProjDataPath);
        Assert.True(Gdal.ProjVersion.Major >= 9);
    }

    [Theory]
    [InlineData("GTiff")]
    [InlineData("COG")]
    [InlineData("GPKG")]
    [InlineData("GeoJSON")]
    [InlineData("ESRI Shapefile")]
    [InlineData("FlatGeobuf")]
    [InlineData("MEM")]
    [InlineData("VRT")]
    [InlineData("netCDF")]
    [InlineData("KML")]
    [InlineData("PNG")]
    public void HasCoreDrivers(string driver) => Assert.NotNull(Gdal.TryGetDriver(driver));
}
