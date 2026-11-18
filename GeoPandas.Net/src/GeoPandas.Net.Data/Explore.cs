using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Plotting;

namespace GeoPandas.Net.Data;

/// <summary>Styling of an interactive map layer (colours are computed in .NET with the same classifiers as <c>Plot()</c>).</summary>
public sealed record ExploreStyle
{
    /// <summary>Attribute for a choropleth / categorical map (null = single colour).</summary>
    public string? Column { get; init; }
    public ColorMap? ColorMap { get; init; }
    public ClassificationScheme Scheme { get; init; } = ClassificationScheme.Quantiles;
    public int Classes { get; init; } = 5;
    /// <summary>Treat <see cref="Column"/> as categories (default for string columns).</summary>
    public bool? Categorical { get; init; }
    public string Fill { get; init; } = "#4e79a7";
    public string Stroke { get; init; } = "#1b2a33";
    public double StrokeWidth { get; init; } = 1;
    public double FillOpacity { get; init; } = 0.7;
    public double PointRadius { get; init; } = 5;
    /// <summary>Attributes in the hover tooltip (null = all, empty = none).</summary>
    public IReadOnlyList<string>? Tooltip { get; init; }
    public string? LegendTitle { get; init; }
}

/// <summary>
/// An interactive web map (GeoPandas' <c>explore()</c>): MapLibre GL JS with an OpenStreetMap or blank basemap, choropleth / categorical
/// styling, legends, hover tooltips and fit-to-data. Produces a standalone HTML page (data embedded as GeoJSON) for files, notebooks,
/// Blazor (<c>srcdoc</c>) and Avalonia web views.
/// </summary>
public sealed class ExploreMap
{
    private readonly List<(string Name, GeoDataFrame Frame, ExploreStyle Style)> _layers = [];

    /// <summary>"osm" (OpenStreetMap raster tiles), "none", or an XYZ tile URL template with {z}/{x}/{y}.</summary>
    public string Basemap { get; set; } = "osm";
    public string? Title { get; set; }
    public int Height { get; set; } = 600;
    /// <summary>MapLibre GL JS version loaded from unpkg (pinned).</summary>
    public string MapLibreVersion { get; set; } = "4.7.1";

    public IReadOnlyList<string> Layers => _layers.Select(l => l.Name).ToList();

    public ExploreMap Add(GeoDataFrame gdf, ExploreStyle? style = null, string? name = null)
    {
        _layers.Add((name ?? $"layer{_layers.Count + 1}", gdf, style ?? new ExploreStyle()));
        return this;
    }

    private static GeoDataFrame ToWgs84(GeoDataFrame g) =>
        g.Crs is { } crs && !(crs.IsGeographic && crs.Epsg == 4326) ? g.ToCrs(CoordinateReferenceSystem.Wgs84) : g;

    /// <summary>Per-feature colours and the legend entries of a layer.</summary>
    internal static (string[] Colors, List<(string Label, string Color)> Legend) Colorize(GeoDataFrame gdf, ExploreStyle s)
    {
        var colors = new string[gdf.RowCount];
        var legend = new List<(string, string)>();
        if (s.Column is null) { Array.Fill(colors, s.Fill); return (colors, legend); }
        var col = gdf[s.Column];
        bool categorical = s.Categorical ?? !(col.DataType == typeof(double) || col.DataType == typeof(float) || col.DataType == typeof(long) || col.DataType == typeof(int));
        static string Hex(Rgba c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        static string Num(double v) => Math.Abs(v) >= 1000 ? v.ToString("#,0", CultureInfo.InvariantCulture) : v.ToString("0.###", CultureInfo.InvariantCulture);
        if (categorical)
        {
            var cats = Enumerable.Range(0, gdf.RowCount).Select(i => Convert.ToString(col.GetValue(i), CultureInfo.InvariantCulture) ?? "").Distinct().Order(StringComparer.Ordinal).ToList();
            var palette = s.ColorMap is { } cm ? cm.Discrete(Math.Max(cats.Count, 1)) : ColorMaps.Categorical.ToArray();
            var map = cats.Select((c, i) => (c, Hex(palette[i % palette.Length]))).ToDictionary(t => t.c, t => t.Item2);
            for (int i = 0; i < gdf.RowCount; i++) colors[i] = map[Convert.ToString(col.GetValue(i), CultureInfo.InvariantCulture) ?? ""];
            legend.AddRange(cats.Select(c => (c, map[c])));
            return (colors, legend);
        }
        var values = Enumerable.Range(0, gdf.RowCount).Select(i => col.IsNull(i) ? double.NaN : col.GetDouble(i)).ToArray();
        var valid = values.Where(v => !double.IsNaN(v)).ToArray();
        var cmap = s.ColorMap ?? ColorMaps.Viridis;
        if (valid.Length == 0) { Array.Fill(colors, "#cccccc"); return (colors, legend); }
        if (s.Scheme == ClassificationScheme.Continuous)
        {
            double min = valid.Min(), max = valid.Max();
            for (int i = 0; i < values.Length; i++) colors[i] = double.IsNaN(values[i]) ? "#cccccc" : Hex(cmap.Sample(max > min ? (values[i] - min) / (max - min) : 0.5));
            for (int k = 0; k <= 4; k++) legend.Add((Num(min + (max - min) * k / 4), Hex(cmap.Sample(k / 4.0))));
            return (colors, legend);
        }
        var breaks = Classifier.Breaks(valid, s.Classes, s.Scheme);
        var swatches = cmap.Discrete(breaks.Length);
        for (int i = 0; i < values.Length; i++) colors[i] = double.IsNaN(values[i]) ? "#cccccc" : Hex(swatches[Classifier.ClassOf(values[i], breaks)]);
        double lower = valid.Min();
        for (int k = 0; k < breaks.Length; k++)
        {
            legend.Add(($"{Num(lower)} – {Num(breaks[k])}", Hex(swatches[k])));
            lower = breaks[k];
        }
        return (colors, legend);
    }

    /// <summary>The standalone HTML page.</summary>
    public string ToHtml()
    {
        var layersJson = new JsonArray();
        var legendHtml = new StringBuilder();
        double w = 180, s = 90, e = -180, n = -90;
        foreach (var (name, frame, style) in _layers)
        {
            var wgs = ToWgs84(frame);
            var (colors, legend) = Colorize(frame, style);
            var fc = JsonNode.Parse(GeoJson.Write(wgs))!.AsObject();
            var features = fc["features"]!.AsArray();
            for (int i = 0; i < features.Count; i++)
            {
                var props = features[i]!["properties"] as JsonObject ?? new JsonObject();
                features[i]!["properties"] = props;
                props["__fill"] = colors[i];
            }
            var env = wgs.TotalBounds;
            if (!env.IsNull) { w = Math.Min(w, env.MinX); s = Math.Min(s, env.MinY); e = Math.Max(e, env.MaxX); n = Math.Max(n, env.MaxY); }
            var tooltip = style.Tooltip ?? frame.Columns.Where(c => c.Name != frame.GeometryColumnName).Select(c => c.Name).ToList();
            layersJson.Add(new JsonObject
            {
                ["name"] = name, ["data"] = fc, ["stroke"] = style.Stroke, ["strokeWidth"] = style.StrokeWidth, ["opacity"] = style.FillOpacity,
                ["radius"] = style.PointRadius, ["tooltip"] = new JsonArray(tooltip.Select(t => (JsonNode)t).ToArray())
            });
            if (legend.Count > 0)
            {
                legendHtml.Append("<div class=\"lg\"><b>").Append(WebUtility.HtmlEncode(style.LegendTitle ?? style.Column ?? name)).Append("</b>");
                foreach (var (label, color) in legend)
                    legendHtml.Append("<div><span style=\"background:").Append(color).Append("\"></span>").Append(WebUtility.HtmlEncode(label)).Append("</div>");
                legendHtml.Append("</div>");
            }
        }
        if (w > e) { w = -180; s = -85; e = 180; n = 85; }
        string basemap = Basemap.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? "{\"version\":8,\"sources\":{},\"layers\":[{\"id\":\"bg\",\"type\":\"background\",\"paint\":{\"background-color\":\"#f4f6f2\"}}]}"
            : JsonSerializer.Serialize(new
            {
                version = 8,
                sources = new Dictionary<string, object>
                {
                    ["base"] = new
                    {
                        type = "raster", tileSize = 256,
                        tiles = new[] { Basemap.Equals("osm", StringComparison.OrdinalIgnoreCase) ? "https://tile.openstreetmap.org/{z}/{x}/{y}.png" : Basemap },
                        attribution = Basemap.Equals("osm", StringComparison.OrdinalIgnoreCase) ? "© OpenStreetMap contributors" : ""
                    }
                },
                layers = new object[] { new { id = "base", type = "raster", source = "base" } }
            });
        var ci = CultureInfo.InvariantCulture;
        string bounds = string.Create(ci, $"[[{w},{s}],[{e},{n}]]");
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(Title ?? "GeoPandas.Net explore")).Append("</title>");
        sb.Append(ci, $"<link rel=\"stylesheet\" href=\"https://unpkg.com/maplibre-gl@{MapLibreVersion}/dist/maplibre-gl.css\">");
        sb.Append(ci, $"<script src=\"https://unpkg.com/maplibre-gl@{MapLibreVersion}/dist/maplibre-gl.js\"></script>");
        sb.Append("<style>html,body{margin:0;height:100%;font:13px 'Segoe UI',system-ui,sans-serif}#map{position:absolute;inset:0}")
          .Append(".lgs{position:absolute;right:10px;bottom:28px;display:flex;flex-direction:column;gap:8px;z-index:2}")
          .Append(".lg{background:#fffffff0;padding:8px 10px;border-radius:8px;box-shadow:0 1px 4px #0003;max-height:40vh;overflow:auto}")
          .Append(".lg span{display:inline-block;width:12px;height:12px;margin-right:6px;border-radius:2px;vertical-align:-2px}")
          .Append(".ttl{position:absolute;left:10px;top:10px;z-index:2;background:#fffffff0;padding:6px 10px;border-radius:8px;font-weight:600}")
          .Append(".cr{position:absolute;left:120px;bottom:8px;z-index:2;font-size:11px;color:#555}</style></head><body><div id=\"map\"></div>");
        if (Title is not null) sb.Append("<div class=\"ttl\">").Append(WebUtility.HtmlEncode(Title)).Append("</div>");
        sb.Append("<div class=\"lgs\">").Append(legendHtml).Append("</div><div class=\"cr\">GeoPandas.Net · Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil</div>");
        sb.Append("<script>const layers=").Append(layersJson.ToJsonString()).Append(";const map=new maplibregl.Map({container:'map',style:")
          .Append(basemap).Append(",bounds:").Append(bounds).Append(",fitBoundsOptions:{padding:30}});")
          .Append("map.addControl(new maplibregl.NavigationControl());map.addControl(new maplibregl.ScaleControl());")
          .Append("map.on('load',()=>{layers.forEach((l,i)=>{const id='l'+i;map.addSource(id,{type:'geojson',data:l.data});")
          .Append("map.addLayer({id:id+'f',type:'fill',source:id,filter:['==',['geometry-type'],'Polygon'],paint:{'fill-color':['get','__fill'],'fill-opacity':l.opacity}});")
          .Append("map.addLayer({id:id+'l',type:'line',source:id,filter:['!=',['geometry-type'],'Point'],paint:{'line-color':['case',['==',['geometry-type'],'Polygon'],l.stroke,['get','__fill']],'line-width':l.strokeWidth}});")
          .Append("map.addLayer({id:id+'p',type:'circle',source:id,filter:['==',['geometry-type'],'Point'],paint:{'circle-color':['get','__fill'],'circle-radius':l.radius,'circle-stroke-color':'#fff','circle-stroke-width':1,'circle-opacity':Math.min(1,l.opacity+0.2)}});")
          .Append("if(l.tooltip.length){const pop=new maplibregl.Popup({closeButton:false,closeOnClick:false});[id+'f',id+'l',id+'p'].forEach(k=>{")
          .Append("map.on('mousemove',k,e=>{map.getCanvas().style.cursor='pointer';const p=e.features[0].properties;")
          .Append("pop.setLngLat(e.lngLat).setHTML('<table>'+l.tooltip.map(t=>'<tr><td><b>'+t+'</b></td><td>'+(p[t]??'')+'</td></tr>').join('')+'</table>').addTo(map);});")
          .Append("map.on('mouseleave',k,()=>{map.getCanvas().style.cursor='';pop.remove();});});}});});</script></body></html>");
        return sb.ToString();
    }

    public void Save(string path) => File.WriteAllText(path, ToHtml(), Encoding.UTF8);

    /// <summary>Saves to a temporary file and opens it in the default browser; returns the path.</summary>
    public string Open()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gpd-explore-{Guid.NewGuid():N}.html");
        Save(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return path;
    }
}

public static class ExploreExtensions
{
    /// <summary>An interactive web map of the frame (GeoPandas <c>gdf.explore()</c>).</summary>
    public static ExploreMap Explore(this GeoDataFrame gdf, string? column = null, ColorMap? cmap = null, ClassificationScheme scheme = ClassificationScheme.Quantiles,
        int k = 5, IReadOnlyList<string>? tooltip = null, string basemap = "osm", string? title = null) =>
        new ExploreMap { Basemap = basemap, Title = title }.Add(gdf, new ExploreStyle { Column = column, ColorMap = cmap, Scheme = scheme, Classes = k, Tooltip = tooltip });
}
