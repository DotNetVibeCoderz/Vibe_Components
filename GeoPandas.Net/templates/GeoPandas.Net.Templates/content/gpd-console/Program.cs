using GeoPandas.Net;
using GeoPandas.Net.Data;
using GeoPandas.Net.Datasets;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Plotting;

Console.WriteLine(Gpd.About);

// 1. Load data (replace with Gpd.ReadFile("your.gpkg") / .shp / .geojson / .csv / .dxf)
var districts = SampleData.Districts();
var pois = SampleData.PointsOfInterest();
Console.WriteLine(districts.Head());

// 2. Spatial join: which district contains each point?
var joined = pois.SJoin(districts, SpatialPredicate.Within);
var perDistrict = joined.GroupBy("name_right").Agg(("name_left", AggFunc.Count, "facilities"));
Console.WriteLine(perDistrict.SortBy("facilities", ascending: false).Head(5));

// 3. Map it
var map = ((GeoDataFrame)districts.Merge(perDistrict, "name", "name_right", JoinHow.Left))
    .Plot(new LayerStyle { Column = "facilities", ColorMap = ColorMaps.YlGnBu, LegendTitle = "Facilities" }, "Facilities per district");
map.ShowFrame = true;
map.SaveSvg("facilities.svg");

// 4. Export
joined.ToFile("joined.gpkg");
districts.ToCrs("EPSG:4326").ToFile("districts.geojson");
Console.WriteLine("Wrote facilities.svg, joined.gpkg and districts.geojson");
