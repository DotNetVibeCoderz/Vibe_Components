using GDAL.Net.Srs;

namespace GDAL.Net.Tests;

public class SrsTests
{
    [Fact]
    public void EpsgAndFormats()
    {
        using var srs = SpatialReference.FromEpsg(32748);
        Assert.True(srs.IsProjected);
        Assert.Equal(32748, srs.Epsg);
        Assert.Equal(-48, srs.UtmZone);
        Assert.Contains("UTM zone 48S", srs.Name);
        Assert.StartsWith("PROJCRS", srs.ToWkt(WktFormat.Wkt2_2019));
        Assert.StartsWith("PROJCS", srs.ToWkt(WktFormat.Wkt1));
        Assert.Contains("\"type\": \"ProjectedCRS\"", srs.ToProjJson());
        Assert.Contains("+proj=utm", srs.ToProj4());
        Assert.Equal("metre", srs.LinearUnits.Name);
        Assert.NotNull(srs.AreaOfUse);
    }

    [Theory]
    [InlineData("EPSG:4326")]
    [InlineData("WGS84")]
    [InlineData("urn:ogc:def:crs:EPSG::3857")]
    [InlineData("+proj=longlat +datum=WGS84 +no_defs")]
    public void ParsesUserInput(string input)
    {
        using var srs = SpatialReference.FromUserInput(input);
        Assert.True(srs.Validate());
    }

    [Fact]
    public void RoundTripsAllFormats()
    {
        using var srs = SpatialReference.FromEpsg(3857);
        using var fromWkt = SpatialReference.FromWkt(srs.ToWkt(WktFormat.Wkt2_2019));
        using var fromJson = SpatialReference.FromProjJson(srs.ToProjJson());
        using var fromEsri = SpatialReference.FromEsri(srs.ToEsriWkt());
        Assert.True(srs.IsSame(fromWkt));
        Assert.True(srs.IsSame(fromJson));
        Assert.True(fromEsri.IsProjected);
    }

    [Fact]
    public void TransformsCoordinates()
    {
        using var ct = CoordinateTransformation.Create(4326, 32748);
        var (x, y) = ct.Transform(107.6191, -6.9175);
        Assert.InRange(x, 788_000, 791_000);
        Assert.InRange(y, 9_234_000, 9_236_000);

        double[] xs = [106.8456, 107.6191, 112.7521];
        double[] ys = [-6.2088, -6.9175, -7.2575];
        Assert.Equal(3, ct.TransformInPlace(xs, ys));
        Assert.InRange(xs[1], 788_000, 791_000);

        using var inverse = CoordinateTransformation.Create(32748, 4326);
        var back = inverse.Transform(x, y);
        Assert.Equal(107.6191, back.X, 6);

        var bounds = ct.TransformBounds(new Envelope(107, -7.5, 108, -6.5));
        Assert.True(bounds.Width > 100_000);
    }

    [Fact]
    public void DatumTransformationWithAreaOfInterest()
    {
        // WGS 84 → DGN95 (Indonesian datum, EPSG:4755)
        using var ct = CoordinateTransformation.Create(SpatialReference.Wgs84, SpatialReference.FromEpsg(4755),
            new TransformationOptions { AreaOfInterest = new Envelope(95, -11, 141, 6) });
        var (lon, lat) = ct.Transform(106.8456, -6.2088);
        Assert.Equal(106.8456, lon, 3);
        Assert.Equal(-6.2088, lat, 3);
    }

    [Fact]
    public void CrsDatabaseSearch()
    {
        var all = CrsDatabase.GetAll();
        Assert.True(all.Count > 5000);
        var results = CrsDatabase.Search("UTM 48S").ToList();
        Assert.Contains(results, c => c.Code == "32748");
        var here = CrsDatabase.At(107.6, -6.9).ToList();
        Assert.Contains(here, c => c.Code == "32748");
    }
}
