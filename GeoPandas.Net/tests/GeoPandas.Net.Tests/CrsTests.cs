namespace GeoPandas.Net.Tests;

public class CrsTests
{
    [Fact]
    public void WebMercator_KnownValues()
    {
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.WebMercator);
        var (x, y) = t.Transform(180, 0);
        Assert.Equal(20037508.342789244, x, 3);
        Assert.Equal(0, y, 6);
        var (_, y2) = t.Transform(0, 85.0511287798066);
        Assert.Equal(20037508.342789244, y2, 0);
    }

    [Fact]
    public void Utm_CentralMeridianAtEquator_IsFalseEasting()
    {
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(32631));
        var (x, y) = t.Transform(3, 0);
        Assert.Equal(500000, x, 6);
        Assert.Equal(0, y, 6);
    }

    [Fact]
    public void Utm_EiffelTower_MatchesPublishedCoordinates()
    {
        // Eiffel Tower: reference from an independent 6th-order Krüger series (Karney 2011)
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(32631));
        var (x, y) = t.Transform(2.294481, 48.85837);
        Assert.Equal(448250.577, x, 0.01);
        Assert.Equal(5411951.588, y, 0.01);
    }

    [Theory]
    [InlineData(32748, 106.8456, -6.2088)]   // Jakarta, UTM 48S
    [InlineData(23834, 106.8456, -6.2088)]   // DGN95 TM-3 48.2
    [InlineData(3857, 139.6917, 35.6895)]
    [InlineData(3395, 139.6917, 35.6895)]
    [InlineData(2154, 2.3522, 48.8566)]      // Lambert-93
    [InlineData(5070, -98.5, 39.8)]          // CONUS Albers
    [InlineData(27700, -0.12458, 51.50069)]  // British National Grid (datum shift)
    [InlineData(26918, -74.0445, 40.6892)]   // NAD83 UTM 18N
    public void RoundTrip_IsAccurate(int epsg, double lon, double lat)
    {
        var fwd = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(epsg));
        var inv = CoordinateTransformer.Create(CoordinateReferenceSystem.FromEpsg(epsg), CoordinateReferenceSystem.Wgs84);
        var (x, y) = fwd.Transform(lon, lat);
        var (lon2, lat2) = inv.Transform(x, y);
        Assert.Equal(lon, lon2, 7);
        Assert.Equal(lat, lat2, 7);
    }

    [Fact]
    public void BritishNationalGrid_BigBen_WithinMetres()
    {
        // Big Ben ≈ TQ 30268 79640 → E 530268, N 179640
        var t = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, CoordinateReferenceSystem.FromEpsg(27700));
        var (x, y) = t.Transform(-0.12458, 51.50069);
        Assert.InRange(x, 530268 - 25, 530268 + 25);
        Assert.InRange(y, 179640 - 25, 179640 + 25);
    }

    [Theory]
    [InlineData("EPSG:4326", 4326)]
    [InlineData("epsg:32748", 32748)]
    [InlineData("urn:ogc:def:crs:EPSG::3857", 3857)]
    [InlineData("http://www.opengis.net/def/crs/EPSG/0/2154", 2154)]
    [InlineData("+proj=utm +zone=48 +south +datum=WGS84 +units=m +no_defs", 32748)]
    [InlineData("+proj=longlat +datum=WGS84 +no_defs", 4326)]
    [InlineData("+proj=merc +a=6378137 +b=6378137 +lat_ts=0 +lon_0=0 +x_0=0 +y_0=0 +k=1 +units=m +nadgrids=@null +wktext +no_defs", 3857)]
    public void Parse_Definitions(string def, int expected) => Assert.Equal(expected, CoordinateReferenceSystem.Parse(def).Epsg);

    [Fact]
    public void Parse_EsriPrj_Utm()
    {
        const string prj = "PROJCS[\"WGS_1984_UTM_Zone_48S\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Transverse_Mercator\"],PARAMETER[\"False_Easting\",500000.0],PARAMETER[\"False_Northing\",10000000.0],PARAMETER[\"Central_Meridian\",105.0],PARAMETER[\"Scale_Factor\",0.9996],PARAMETER[\"Latitude_Of_Origin\",0.0],UNIT[\"Meter\",1.0]]";
        Assert.Equal(32748, CoordinateReferenceSystem.Parse(prj).Epsg);
    }

    [Fact]
    public void Parse_EsriPrj_WebMercator()
    {
        const string prj = "PROJCS[\"WGS_1984_Web_Mercator_Auxiliary_Sphere\",GEOGCS[\"GCS_WGS_1984\",DATUM[\"D_WGS_1984\",SPHEROID[\"WGS_1984\",6378137.0,298.257223563]],PRIMEM[\"Greenwich\",0.0],UNIT[\"Degree\",0.0174532925199433]],PROJECTION[\"Mercator_Auxiliary_Sphere\"],PARAMETER[\"False_Easting\",0.0],PARAMETER[\"False_Northing\",0.0],PARAMETER[\"Central_Meridian\",0.0],PARAMETER[\"Standard_Parallel_1\",0.0],PARAMETER[\"Auxiliary_Sphere_Type\",0.0],UNIT[\"Meter\",1.0]]";
        Assert.Equal(3857, CoordinateReferenceSystem.Parse(prj).Epsg);
    }

    [Fact]
    public void Parse_Wkt2_LambertWithoutAuthority()
    {
        const string wkt2 = """
            PROJCRS["Custom LCC",
              BASEGEOGCRS["RGF93",DATUM["Reseau Geodesique Francais 1993",ELLIPSOID["GRS 1980",6378137,298.257222101]]],
              CONVERSION["Lambert-93",METHOD["Lambert Conic Conformal (2SP)"],
                PARAMETER["Latitude of false origin",46.5],PARAMETER["Longitude of false origin",3],
                PARAMETER["Latitude of 1st standard parallel",49],PARAMETER["Latitude of 2nd standard parallel",44],
                PARAMETER["Easting at false origin",700000],PARAMETER["Northing at false origin",6600000]],
              CS[Cartesian,2],AXIS["easting",east],AXIS["northing",north],LENGTHUNIT["metre",1]]
            """;
        var custom = CoordinateReferenceSystem.Parse(wkt2);
        var reference = CoordinateReferenceSystem.FromEpsg(2154);
        var a = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, custom).Transform(2.3522, 48.8566);
        var b = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, reference).Transform(2.3522, 48.8566);
        Assert.Equal(b.X, a.X, 3);
        Assert.Equal(b.Y, a.Y, 3);
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(32748)]
    [InlineData(2154)]
    [InlineData(27700)]
    [InlineData(5070)]
    public void Wkt_And_Proj4_RoundTrip(int epsg)
    {
        var crs = CoordinateReferenceSystem.FromEpsg(epsg);
        var fromWkt = CoordinateReferenceSystem.Parse(crs.ToWkt());
        Assert.Equal(crs, fromWkt);
        var fromProj = CoordinateReferenceSystem.Parse(crs.ToProj4());
        var p1 = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, crs).Transform(1.5, 47.0);
        var p2 = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, fromProj).Transform(1.5, 47.0);
        Assert.Equal(p1.X, p2.X, 3);
        Assert.Equal(p1.Y, p2.Y, 3);
    }

    [Fact]
    public void EstimateUtm_PicksCorrectZone()
    {
        Assert.Equal(32748, CoordinateReferenceSystem.EstimateUtm(106.8, -6.2).Epsg);
        Assert.Equal(32631, CoordinateReferenceSystem.EstimateUtm(2.3, 48.8).Epsg);
    }

    [Fact]
    public void IndonesiaTm3_ZoneNamesAndMeridians()
    {
        var z = CoordinateReferenceSystem.FromEpsg(23834);
        Assert.Contains("48.2", z.Name);
        Assert.Equal(106.5, z.Projection!.Parameters.Lon0);
    }
}
