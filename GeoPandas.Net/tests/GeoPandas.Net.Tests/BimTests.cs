using System.Text.Json.Nodes;
using GeoPandas.Net.BIM;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;

namespace GeoPandas.Net.Tests;

/// <summary>IFC reading against IfcOpenShell 0.9 (OpenCascade) reference geometry; IFC4 writing round trips.</summary>
public sealed class BimTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "data", name);
    private static readonly Lazy<JsonNode> Ref = new(() => JsonNode.Parse(File.ReadAllText(Data("bim_reference.json")))!);

    [Fact]
    public void Step_ParsesStringsListsAndTypedValues()
    {
        var f = StepFile.Parse("""
            ISO-10303-21;
            HEADER;
            FILE_DESCRIPTION(('ViewDefinition [CoordinationView]'),'2;1');
            FILE_SCHEMA(('IFC4'));
            ENDSEC;
            DATA;
            #1= IFCPROPERTYSINGLEVALUE('Caf\X2\00E9\X0\ ''Ber\X2\00FC\X0\hmt''',$,IFCLABEL('a\\b'),$);
            #2=IFCCARTESIANPOINT((1.5E3,-2.,0.));
            #3=IFCWALL('2O2Fr$t4X7Zf8NOew3FLOH',#9,'W',$,$,#4,#5,$,.STANDARD.);
            /* a comment */
            #10=(IFCA(1)IFCB('x'));
            ENDSEC;
            END-ISO-10303-21;
            """);
        Assert.Equal(["IFC4"], f.Schemas);
        Assert.Equal("Café 'Berühmt'", f.Entities[1][0]);
        Assert.Equal(new StepTyped("IFCLABEL", "a\\b"), f.Entities[1][2]);
        Assert.Equal(new List<object?> { 1500.0, -2.0, 0.0 }, (List<object?>)f.Entities[2][0]!);
        Assert.Equal(new StepRef(4), f.Entities[3][5]);
        Assert.Equal(new StepEnum("STANDARD"), f.Entities[3][8]);
        Assert.Equal("COMPLEX", f.Entities[10].Type);
        Assert.Equal("'Caf\\X2\\00E9\\X0\\ ''q'''", StepFile.Quote("Café 'q'"));
    }

    [Theory]
    [InlineData("bim_house_ifc4")]
    [InlineData("bim_site_ifc2x3")]
    public void Elements_MatchIfcOpenShellGeometry(string key)
    {
        var model = IfcModel.Read(Data(key + ".ifc"));
        var reference = Ref.Value[key]!;
        Assert.Equal(reference["count"]!.GetValue<int>(), model.Elements.Count);
        foreach (var e in reference["elements"]!.AsArray())
        {
            var el = model.Elements.Single(x => x.GlobalId == e!["global_id"]!.GetValue<string>());
            Assert.Equal(e!["ifc_class"]!.GetValue<string>().ToUpperInvariant(), el.IfcType);
            Assert.Equal(e["storey"]?.GetValue<string>(), el.Storey);
            var mn = e["min"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            var mx = e["max"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            var env = el.Footprint!.EnvelopeInternal;
            Assert.Equal(mn[0], env.MinX, 9); Assert.Equal(mn[1], env.MinY, 9); Assert.Equal(mx[0], env.MaxX, 9); Assert.Equal(mx[1], env.MaxY, 9);
            Assert.Equal(mn[2], el.ZMin, 9); Assert.Equal(mx[2], el.ZMax, 9);
            Assert.Equal(e["footprint_area"]!.GetValue<double>(), el.Footprint.Area, 9);
            Assert.Equal(e["psets"]!.AsObject().Sum(p => p.Value!.AsObject().Count), el.Properties.Count);
        }
    }

    [Fact]
    public void House_MapCoordinatesUnitsPropertiesAndLod1()
    {
        var model = IfcModel.Read(Data("bim_house_ifc4.ifc"));
        Assert.Equal(0.001, model.LengthUnit);
        Assert.Equal(32748, model.Georeference!.Crs!.Epsg);
        Assert.Equal(["Ground floor", "First floor"], model.Storeys.Select(s => s.Name));
        Assert.Equal(3.0, model.Storeys[1].Elevation, 9);
        var frame = model.ElementsFrame();
        Assert.Equal(32748, frame.Crs!.Epsg);
        foreach (var e in Ref.Value["bim_house_ifc4"]!["elements"]!.AsArray())
        {
            int row = Enumerable.Range(0, frame.RowCount).Single(i => (string)frame["global_id"].GetValue(i)! == e!["global_id"]!.GetValue<string>());
            var env = frame.Geometry[row]!.EnvelopeInternal;
            var mmin = e!["map_min"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            var mmax = e["map_max"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            Assert.Equal(mmin[0], env.MinX, 6); Assert.Equal(mmin[1], env.MinY, 6); Assert.Equal(mmax[0], env.MaxX, 6); Assert.Equal(mmax[1], env.MaxY, 6);
            Assert.Equal(mmin[2], frame["z_min"].GetDouble(row), 6); Assert.Equal(mmax[2], frame["z_max"].GetDouble(row), 6);
        }
        int wall = Enumerable.Range(0, frame.RowCount).First(i => (string?)frame["name"].GetValue(i) == "W00");
        Assert.True((bool)frame["Pset_WallCommon.IsExternal"].GetValue(wall)!);
        Assert.Equal("REI60", frame["Pset_WallCommon.FireRating"].GetValue(wall));
        Assert.Equal(10, frame["Qto_WallBaseQuantities.Length"].GetDouble(wall), 9);   // mm → m
        Assert.Equal(6.0, frame["volume"].GetDouble(wall), 9);                       // 10 × 0.2 × 3
        Assert.Equal(2.0, model.Elements.Single(x => x.Name == "Roof light").Volume, 9);   // pyramid 2 × 2 × 1.5 / 3
        Assert.Equal(0.27, model.Elements.Single(x => x.Name == "C0").Volume, 9);          // mapped column type

        var lod1 = model.BuildingFootprint();
        Assert.Equal("House A", lod1["name"].GetValue(0));
        Assert.Equal(2L, lod1["storeys"].GetValue(0));
        Assert.Equal(80, lod1.Geometry[0]!.Area, 6);              // 10 × 8 slab outline
        Assert.Equal(9.8, lod1["ground_z"].GetDouble(0), 9);       // slab underside −0.2 + orthogonal height 10
        Assert.Equal(7.7, lod1["height"].GetDouble(0), 9);         // up to the roof light at 7.5
    }

    [Fact]
    public void Ifc2x3_SiteLatLonGeoreferencesToUtm()
    {
        var model = IfcModel.Read(Data("bim_site_ifc2x3.ifc"));
        Assert.Equal("IfcSite RefLatitude/RefLongitude", model.Georeference!.Source);
        Assert.Equal(32748, model.Georeference.Crs!.Epsg);
        var env = model.ElementsFrame().Geometry[0]!.EnvelopeInternal;
        // pyproj: (107°36'36" E, 6°54'30" S) → UTM 48S (788430.6057, 9235593.9523); the wall's corner is at local (4.788, 2.0)
        Assert.Equal(788430.6056626376 + 4.787867965644036, env.MinX, 3);
        Assert.Equal(9235593.952316277 + 2.0, env.MinY, 3);
        Assert.Equal(768, model.ElementsFrame()["z_min"].GetDouble(0), 9);
    }

    [Fact]
    public void Writer_FootprintsRoundTripThroughIfc4()
    {
        var crs = CoordinateReferenceSystem.FromEpsg(32748);
        var a = Geo.FromWkt("POLYGON ((700000 9300000, 700030 9300000, 700030 9300020, 700000 9300020, 700000 9300000), (700010 9300005, 700020 9300005, 700020 9300015, 700010 9300015, 700010 9300005))");
        var b = Geo.Box(700050, 9300010, 700062, 9300040);
        var gdf = GeoDataFrame.FromGeometries([a, b], crs, Column.Create("name", new[] { "Courtyard block", "Tower" }),
            Column.Create("height", new[] { 9.0, 45.5 }), Column.Create("floors", new long[] { 3, 13 }));
        var model = IfcModel.Parse(IfcWriter.BuildingsToIfc(gdf));
        Assert.Equal("IFC4", model.Schema);
        var back = model.ElementsFrame();
        Assert.Equal(32748, back.Crs!.Epsg);
        for (int i = 0; i < 2; i++)
        {
            Assert.True(back.Geometry[i]!.EqualsTopologically(gdf.Geometry[i]!));
            Assert.Equal(gdf["height"].GetDouble(i), back["height"].GetDouble(i), 9);
            Assert.Equal(gdf["floors"].GetDouble(i), back["GPD_Attributes.floors"].GetDouble(i));
        }
        Assert.Equal(500 * 9.0, back["volume"].GetDouble(0), 6);
        var g = Guid.NewGuid();
        Assert.Equal(22, IfcWriter.CompressGuid(g).Length);
        Assert.Equal(g, IfcWriter.ExpandGuid(IfcWriter.CompressGuid(g)));
        // reference value from ifcopenshell.guid.compress
        Assert.Equal("0V4b4ycB110xP9GkvUaLRB", IfcWriter.CompressGuid(Guid.Parse("1f12513c-98b0-4103-b649-42ee5e9156cb")));
    }
}
