using GDAL.Net.Rendering;
using GDAL.Net.Srs;
using GDAL.Net.Vector;

namespace GDAL.Net.Gallery;

public static partial class Catalog
{
    private static VectorStyle CityStyle(VectorData data)
    {
        var max = data.Features.Max(f => Convert.ToDouble(f["population"] ?? 0));
        return new VectorStyle
        {
            Stroke = new Rgba(255, 255, 255),
            StrokeWidth = 1.2,
            LabelField = "name",
            FillSelector = f => ColorRamp.Inferno.Evaluate(0.25 + 0.65 * Math.Sqrt(Convert.ToDouble(f["population"] ?? 0) / max)) with { A = 230 },
            RadiusSelector = f => 3 + 13 * Math.Sqrt(Convert.ToDouble(f["population"] ?? 0) / max),
        };
    }

    private static IEnumerable<GallerySample> VectorSamples()
    {
        yield return new GallerySample(
            "cities",
            SampleCategory.Vector,
            new("Indonesian cities", "Kota-kota Indonesia"),
            new("Read a GeoPackage point layer, reproject it to Web Mercator and draw proportional symbols. Filter features with an OGR SQL WHERE clause.",
                "Baca layer titik GeoPackage, reproyeksikan ke Web Mercator, lalu gambar simbol proporsional. Saring fitur dengan klausa WHERE OGR SQL."),
            """
            using var ds = Gdal.OpenVector("indonesia_cities.gpkg");
            Layer cities = ds.GetLayer("cities");
            cities.SetAttributeFilter("island = 'Java' AND population > 1000000");

            // Snapshot for display, reprojected to Web Mercator
            VectorData data = VectorData.Load(cities, SpatialReference.WebMercator);
            foreach (var f in data.Features)
                Console.WriteLine($"{f["name"]}: {f["population"]:N0}");
            """,
            (ctx, args) =>
            {
                using var ds = ctx.OpenCities();
                var layer = ds.GetLayer("cities");
                var island = args.Get("island");
                var min = args.Number("min", 0) * 1000;
                var filter = $"population >= {min:0}" + (island == "All" ? "" : $" AND island = '{island}'");
                layer.SetAttributeFilter(filter);
                using var merc = SpatialReference.WebMercator;
                var data = VectorData.Load(layer, merc);
                layer.SetAttributeFilter(null);
                var all = VectorData.Load(layer, merc);
                var result = new SampleResult { Crs = "EPSG:3857", Table = TableData.From(data), Extent = all.Extent.Inflate(0.06) };
                result.Vectors.Add(new StyledVector(all, VectorStyle.Points(new Rgba(201, 211, 207, 200), 3), "All cities"));
                if (data.Features.Count > 0) result.Vectors.Add(new StyledVector(data, CityStyle(all), "Selected"));
                result.Metrics.Add(new(new("Selected", "Terpilih"), $"{data.Features.Count} / {all.Features.Count}"));
                result.Metrics.Add(new(new("Population", "Populasi"), F(data.Features.Sum(f => Convert.ToDouble(f["population"] ?? 0)), "#,0")));
                result.Output = $"WHERE {filter}";
                result.OutputLanguage = "sql";
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("island", new("Island", "Pulau"), "All", "All", "Java", "Sumatra", "Kalimantan", "Sulawesi", "Bali", "Nusa Tenggara", "Maluku", "Papua"),
                SampleParameter.Number("min", new("Min. population (thousands)", "Populasi min. (ribu)"), 0, 0, 3000, 50),
            ],
            Equivalent = "ogrinfo -where",
        };

        yield return new GallerySample(
            "sql",
            SampleCategory.Vector,
            new("SQL engine", "Mesin SQL"),
            new("Query vector data with OGR SQL or the SQLite/SpatiaLite dialect: aggregation, ordering, spatial functions and joins — on any format.",
                "Kueri data vektor dengan OGR SQL atau dialek SQLite/SpatiaLite: agregasi, pengurutan, fungsi spasial, dan join — di format apa pun."),
            """
            using var ds = Gdal.OpenVector("indonesia_cities.gpkg");
            using var result = ds.ExecuteSql(
                "SELECT island, COUNT(*) AS cities, SUM(population) AS people " +
                "FROM cities GROUP BY island ORDER BY people DESC",
                dialect: "SQLITE");
            foreach (var row in result!.ReadRecords())
                Console.WriteLine($"{row.Attributes["island"]}: {row.Attributes["people"]:N0}");
            """,
            (ctx, args) =>
            {
                using var ds = ctx.OpenCities();
                var dialect = args.Get("dialect");
                using var sql = ds.ExecuteSql(args.Get("sql"), dialect: dialect == "OGR SQL" ? null : dialect);
                var result = new SampleResult { Output = args.Get("sql"), OutputLanguage = "sql", Crs = "EPSG:3857" };
                if (sql is null)
                {
                    result.Note = new("The statement returned no result set.", "Pernyataan tidak mengembalikan hasil.");
                    return result;
                }
                using var merc = SpatialReference.WebMercator;
                var data = VectorData.Load(sql, merc);
                result.Table = TableData.From(data);
                if (data.Features.Any(f => f.Shapes.Count > 0))
                {
                    result.Vectors.Add(new StyledVector(data, VectorStyle.Points(new Rgba(200, 85, 61), 6) with { LabelField = data.Fields.FirstOrDefault(f => f.Name == "name").Name }, "Result"));
                }
                result.Metrics.Add(new(new("Rows", "Baris"), data.Features.Count.ToString()));
                result.Metrics.Add(new(new("Columns", "Kolom"), data.Fields.Count.ToString()));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("dialect", new("Dialect", "Dialek"), "SQLITE", "SQLITE", "OGR SQL"),
                SampleParameter.TextBox("sql", new("SQL", "SQL"), "SELECT island, COUNT(*) AS cities, SUM(population) AS people\nFROM cities\nGROUP BY island\nORDER BY people DESC", multiline: true) with { Hint = new("Try: SELECT name, population, geom FROM cities WHERE ST_Y(geom) < -7", "Coba: SELECT name, population, geom FROM cities WHERE ST_Y(geom) < -7"),
                },
            ],
            Equivalent = "ogrinfo -sql -dialect SQLITE",
        };

        yield return new GallerySample(
            "geoprocessing",
            SampleCategory.Vector,
            new("Buffer, hull & dissolve", "Buffer, hull & dissolve"),
            new("GEOS geoprocessing on real distances: buffer Java's big cities in a metric CRS, dissolve overlapping buffers, and wrap everything in a convex hull.",
                "Geoprocessing GEOS dengan jarak nyata: buffer kota besar di Jawa dalam CRS metrik, gabungkan (dissolve) buffer yang tumpang tindih, lalu bungkus dengan convex hull."),
            """
            using var utm = SpatialReference.FromEpsg(32749);
            var buffers = new List<Geometry>();
            foreach (var city in layer)
            {
                using (city)
                {
                    var g = city.Geometry!.Clone();
                    g.TransformTo(utm);
                    buffers.Add(g.Buffer(25_000));          // 25 km
                }
            }
            using var dissolved = Geometry.UnionAll(buffers);
            using var hull = dissolved.ConvexHull();
            Console.WriteLine($"{dissolved.Area / 1e6:N0} km²");
            """,
            (ctx, args) =>
            {
                using var ds = ctx.OpenCities();
                var layer = ds.GetLayer(0);
                layer.SetAttributeFilter("island = 'Java'");
                using var merc = SpatialReference.WebMercator;
                using var utm = SpatialReference.FromEpsg(32749);
                using var toMerc = CoordinateTransformation.Create(utm, merc);
                var km = args.Number("distance", 25);
                var buffers = new List<Geometry>();
                foreach (var f in layer)
                {
                    using (f)
                    {
                        var g = f.Geometry!.Clone();
                        g.TransformTo(utm);
                        buffers.Add(g.Buffer(km * 1000, 24));
                        g.Dispose();
                    }
                }
                var op = args.Get("operation");
                var output = new List<Geometry>();
                using var dissolved = Geometry.UnionAll(buffers);
                switch (op)
                {
                    case "Buffers":
                        output.AddRange(buffers.Select(b => b.Clone()));
                        break;
                    case "Convex hull":
                        output.Add(dissolved.ConvexHull());
                        break;
                    case "Concave hull":
                        output.Add(dissolved.ConcaveHull(0.3));
                        break;
                    default:
                        output.Add(dissolved.Clone());
                        break;
                }
                var area = output.Sum(g => g.Area) / 1e6;
                foreach (var g in output) g.Transform(toMerc);
                var cities = VectorData.Load(layer, merc);
                layer.SetAttributeFilter(null);
                var result = new SampleResult { Crs = "EPSG:3857" };
                result.Vectors.Add(new StyledVector(VectorData.FromGeometries(op, output), OutlineStyle, op));
                result.Vectors.Add(new StyledVector(cities, VectorStyle.Points(new Rgba(200, 85, 61), 5) with { LabelField = "name" }, "Cities"));
                result.Metrics.Add(new(new("Geometries", "Geometri"), output.Count.ToString()));
                result.Metrics.Add(new(new("Area", "Luas"), F(area, "#,0"), "km²"));
                result.Output = output.Count == 1 ? output[0].ToWkt()[..Math.Min(4000, output[0].ToWkt().Length)] + "…" : null;
                result.OutputLanguage = "wkt";
                foreach (var b in buffers) b.Dispose();
                foreach (var g in output) g.Dispose();
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Number("distance", new("Buffer distance (km)", "Jarak buffer (km)"), 25, 5, 150, 5),
                SampleParameter.Choice("operation", new("Show", "Tampilkan"), "Dissolve", "Buffers", "Dissolve", "Convex hull", "Concave hull"),
            ],
            Equivalent = "OGR_G_Buffer / UnaryUnion / ConvexHull",
        };

        yield return new GallerySample(
            "spatial-join",
            SampleCategory.Vector,
            new("Spatial join & overlay", "Spatial join & overlay"),
            new("Scatter points of interest over the basin and join each one to the land cover polygon it falls in — then intersect land cover with a study area using layer overlay.",
                "Sebar titik minat di cekungan dan gabungkan masing-masing ke poligon tutupan lahan tempatnya berada — lalu potong tutupan lahan dengan area studi memakai overlay layer."),
            """
            // Point-in-polygon join with SpatiaLite SQL
            using var joined = ds.ExecuteSql(
                "SELECT p.id, l.name AS land_cover FROM poi p, land_cover l " +
                "WHERE ST_Intersects(p.geom, l.geom)", dialect: "SQLITE");

            // Layer overlay (OGR_L_Intersection)
            landCover.Overlay(LayerOverlay.Intersection, studyArea, resultLayer);
            """,
            (ctx, args) =>
            {
                using var lc = ctx.OpenLandCover();
                var land = lc.GetLayer(0);
                using var utm = SpatialReference.FromEpsg(32748);
                using var mem = Gdal.GetDriver("GPKG").CreateVector(ctx.Scratch("join.gpkg"));
                var copied = mem.CopyLayer(land, "land_cover");
                var poi = mem.CreateLayer("poi", utm, GeometryType.Point);
                poi.CreateField("id", FieldType.Integer);
                var rnd = new Random((int)args.Number("seed", 7));
                var count = (int)args.Number("count", 150);
                using (var tx = mem.BeginTransaction())
                {
                    for (var i = 0; i < count; i++)
                    {
                        using var p = Geometry.Point(772_000 + rnd.NextDouble() * 32_000, 9_230_000 + rnd.NextDouble() * 24_000);
                        poi.AddFeature(p, new Dictionary<string, object?> { ["id"] = i + 1 });
                    }
                    tx.Commit();
                }

                var (cx, cy) = SampleContext.BandungUtm;
                var study = mem.CreateLayer("study_area", utm, GeometryType.Polygon);
                using (var circle = Geometry.Point(cx, cy).Buffer(args.Number("radius", 6) * 1000, 32)) study.AddFeature(circle);
                var clipped = mem.CreateLayer("clipped", utm, GeometryType.Polygon);
                copied.Overlay(LayerOverlay.Intersection, study, clipped);

                using var joined = mem.ExecuteSql(
                    "SELECT l.name AS land_cover, COUNT(*) AS points FROM poi p JOIN land_cover l ON ST_Intersects(p.geom, l.geom) GROUP BY l.name ORDER BY points DESC",
                    dialect: "SQLITE")!;
                var rows = joined.ReadRecords(includeGeometry: false);

                var result = new SampleResult { Crs = "EPSG:32748" };
                result.Vectors.Add(new StyledVector(VectorData.Load(copied), LandCoverStyle with { FillSelector = f => LandCoverStyle.FillFor(f) with { A = 70 } }, "Land cover"));
                result.Vectors.Add(new StyledVector(VectorData.Load(clipped), LandCoverStyle with { Stroke = new Rgba(18, 38, 58), StrokeWidth = 1 }, "Intersection"));
                result.Vectors.Add(new StyledVector(VectorData.Load(study), VectorStyle.Lines(new Rgba(18, 38, 58), 2), "Study area"));
                result.Vectors.Add(new StyledVector(VectorData.Load(poi), VectorStyle.Points(new Rgba(255, 255, 255), 3) with { Stroke = new Rgba(18, 38, 58) }, "POI"));
                result.Table = Table(["Land cover", "Points"], rows.Select(r => new[] { TableData.Format(r.Attributes["land_cover"]), TableData.Format(r.Attributes["points"]) }));
                result.Metrics.Add(new(new("Points joined", "Titik tergabung"), rows.Sum(r => Convert.ToInt64(r.Attributes["points"])).ToString()));
                result.Metrics.Add(new(new("Clipped parcels", "Persil terpotong"), clipped.FeatureCount.ToString()));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Number("count", new("Points", "Jumlah titik"), 150, 10, 1000, 10),
                SampleParameter.Number("radius", new("Study radius (km)", "Radius studi (km)"), 6, 1, 15, 0.5),
                SampleParameter.Number("seed", new("Random seed", "Seed acak"), 7, 1, 999, 1),
            ],
            Equivalent = "ogr2ogr -sql ST_Intersects / OGR_L_Intersection",
        };

        yield return new GallerySample(
            "convert-vector",
            SampleCategory.Vector,
            new("Convert vector formats", "Konversi format vektor"),
            new("ogr2ogr in-process: export to GeoJSON, GeoPackage, FlatGeobuf, Shapefile, KML, GML or CSV, reprojecting and filtering on the way.",
                "ogr2ogr di dalam proses: ekspor ke GeoJSON, GeoPackage, FlatGeobuf, Shapefile, KML, GML, atau CSV sambil reproyeksi dan penyaringan."),
            """
            using var src = Gdal.OpenVector("indonesia_cities.gpkg");
            using var dst = GdalUtils.VectorTranslate(src, "cities.fgb", new VectorTranslateOptions
            {
                Format = "FlatGeobuf",
                TargetSrs = "EPSG:3857",
                Where = "population > 1000000",
            });
            """,
            (ctx, args) =>
            {
                using var src = ctx.OpenCities();
                var format = args.Get("format");
                var ext = format switch
                {
                    "GeoJSON" => ".geojson",
                    "GPKG" => ".gpkg",
                    "FlatGeobuf" => ".fgb",
                    "ESRI Shapefile" => ".shp",
                    "KML" => ".kml",
                    "GML" => ".gml",
                    _ => ".csv",
                };
                var path = ctx.Scratch("cities" + ext);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using (GdalUtils.VectorTranslate(src, path, new VectorTranslateOptions
                {
                    Format = format,
                    TargetSrs = args.Get("srs"),
                    Where = string.IsNullOrWhiteSpace(args.Get("where")) ? null : args.Get("where"),
                    LayerCreationOptions = format == "CSV" ? new Dictionary<string, string> { ["GEOMETRY"] = "AS_WKT" } : null,
                })) { }
                var elapsed = sw.Elapsed;
                using var reopened = Gdal.OpenVector(path);
                var layer = reopened.GetLayer(0);
                var files = reopened.FileList;
                var textual = ext is ".geojson" or ".kml" or ".gml" or ".csv";
                var result = new SampleResult
                {
                    Output = textual ? Truncate(File.ReadAllText(path), 6000) : GdalUtils.VectorInfo(reopened, "-so", "-al"),
                    OutputLanguage = ext switch { ".geojson" => "json", ".kml" or ".gml" => "xml", _ => "text" },
                };
                result.Metrics.Add(new(new("Features", "Fitur"), layer.FeatureCount.ToString()));
                result.Metrics.Add(new(new("Files", "File"), string.Join(", ", files.Select(Path.GetExtension))));
                result.Metrics.Add(new(new("Size", "Ukuran"), FileSize(files.Sum(f => new FileInfo(f).Length))));
                result.Metrics.Add(new(new("Time", "Waktu"), F(elapsed.TotalMilliseconds, "0"), "ms"));
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("format", new("Format", "Format"), "GeoJSON", "GeoJSON", "GPKG", "FlatGeobuf", "ESRI Shapefile", "KML", "GML", "CSV"),
                SampleParameter.Choice("srs", new("Target CRS", "CRS tujuan"), "EPSG:4326", "EPSG:4326", "EPSG:3857", "EPSG:4755", "EPSG:23830"),
                SampleParameter.TextBox("where", new("Filter (WHERE)", "Filter (WHERE)"), "population > 1000000"),
            ],
            Equivalent = "ogr2ogr",
        };

        yield return new GallerySample(
            "rasterize",
            SampleCategory.Vector,
            new("Rasterize & grid", "Rasterisasi & grid"),
            new("Burn land cover polygons into a raster by attribute, or interpolate scattered elevation samples into a surface with gdal_grid (inverse distance, nearest, moving average).",
                "Bakar poligon tutupan lahan menjadi raster berdasarkan atribut, atau interpolasikan sampel elevasi tersebar menjadi permukaan dengan gdal_grid (inverse distance, nearest, moving average)."),
            """
            using var classes = GdalUtils.Rasterize(landCover, "classes.tif", new RasterizeOptions
            {
                Attribute = "class", Resolution = (60, 60), OutputType = DataType.Byte, AllTouched = true,
            });

            using var surface = GdalUtils.Grid(samples, "surface.tif",
                ["-a", "invdist:power=2:smoothing=20", "-outsize", "600", "450"]);
            """,
            (ctx, args) =>
            {
                var mode = args.Get("mode");
                var result = new SampleResult { Crs = "EPSG:32748" };
                if (mode == "Rasterize land cover")
                {
                    using var lc = ctx.OpenLandCover();
                    var res = args.Number("resolution", 120);
                    var raster = GdalUtils.Rasterize(lc, "", new RasterizeOptions { Format = "MEM", Attribute = "class", Resolution = (res, res), OutputType = DataType.Byte, NoData = 0, AllTouched = true });
                    using var ct = new ColorTable();
                    ct[0] = new ColorEntry(0, 0, 0, 0);
                    foreach (var c in SampleDataGenerator.LandCoverClasses)
                    {
                        var rgba = Rgba.FromHex(c.Color);
                        ct[c.Code] = ColorEntry.FromRgb(rgba.R, rgba.G, rgba.B);
                    }
                    raster.GetBand(1).ColorTable = ct;
                    result.Rasters.Add(new RasterLayer(raster, new RenderOptions { Mode = RenderMode.Palette }, "Classes"));
                    result.Metrics.Add(new(new("Raster size", "Ukuran raster"), $"{raster.RasterWidth} × {raster.RasterHeight}", "px"));
                }
                else
                {
                    using var pts = Gdal.CreateMemoryVector();
                    using var utm = SpatialReference.FromEpsg(32748);
                    var layer = pts.CreateLayer("samples", utm, GeometryType.PointZ);
                    var rnd = new Random(3);
                    var n = (int)args.Number("samples", 400);
                    for (var i = 0; i < n; i++)
                    {
                        var x = 770_000 + rnd.NextDouble() * 36_000;
                        var y = 9_229_000 + rnd.NextDouble() * 27_000;
                        using var p = Geometry.Point(x, y, SampleDataGenerator.Elevation(x, y));
                        layer.AddFeature(p);
                    }
                    var alg = mode switch
                    {
                        "Grid: nearest neighbour" => "nearest",
                        "Grid: moving average" => "average:radius1=3000:radius2=3000",
                        _ => "invdist:power=2:smoothing=20",
                    };
                    var surface = GdalUtils.Grid(pts, "", ["-of", "MEM", "-a", alg, "-ot", "Float32", "-txe", "770000", "806000", "-tye", "9229000", "9256000", "-outsize", "360", "270"]);
                    result.Rasters.Add(new RasterLayer(surface, new RenderOptions { Ramp = ColorRamp.Terrain, Hillshade = true }, "Interpolated surface"));
                    result.Vectors.Add(new StyledVector(VectorData.Load(layer), VectorStyle.Points(new Rgba(18, 38, 58), 2.5) with { Stroke = new Rgba(255, 255, 255) }, "Samples"));
                    result.Metrics.Add(new(new("Samples", "Sampel"), n.ToString()));
                    result.Metrics.Add(new(new("Algorithm", "Algoritma"), alg.Split(':')[0]));
                }
                return result;
            })
        {
            Parameters =
            [
                SampleParameter.Choice("mode", new("Operation", "Operasi"), "Rasterize land cover", "Rasterize land cover", "Grid: inverse distance", "Grid: nearest neighbour", "Grid: moving average"),
                SampleParameter.Number("resolution", new("Cell size (m)", "Ukuran sel (m)"), 120, 60, 600, 30),
                SampleParameter.Number("samples", new("Grid samples", "Sampel grid"), 400, 50, 3000, 50),
            ],
            Equivalent = "gdal_rasterize / gdal_grid",
        };
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n…";
}
