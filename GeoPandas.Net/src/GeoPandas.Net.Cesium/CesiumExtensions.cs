using CesiumNet;
using GeoPandas.Net.Data;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Plotting;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Cesium;

/// <summary>
/// Puts GeoPandas.Net data on a Cesium.Net globe (Blazor <c>&lt;CesiumViewer&gt;</c> or Avalonia <c>CesiumView</c>): GeoDataFrames as GeoJSON data
/// sources, point frames as GPU point collections, 3D Tiles from <see cref="Scene.Tiles3D"/>, and camera framing of a frame's extent.
/// The <c>To…Options</c> builders are pure, so they can be used (and tested) without a viewer.
/// </summary>
public static class CesiumExtensions
{
    private static GeoDataFrame ToWgs84(GeoDataFrame gdf) =>
        gdf.Crs is { } crs && !(crs.IsGeographic && crs.Epsg == 4326) ? gdf.ToCrs(CoordinateReferenceSystem.Wgs84) : gdf;

    /// <summary>GeoJSON data-source options (WGS 84) for a frame.</summary>
    public static DataSourceOptions ToDataSourceOptions(GeoDataFrame gdf, string? name = null, string? fill = null, string? stroke = null,
        double strokeWidth = 2, bool clampToGround = true, bool flyTo = false)
    {
        var o = new DataSourceOptions
        {
            Kind = DataSourceKind.GeoJson,
            Data = GeoJson.Write(ToWgs84(gdf)),
            Name = name,
            StrokeWidth = strokeWidth,
            ClampToGround = clampToGround,
            FlyTo = flyTo,
        };
        if (fill is not null) o.Fill = CesiumColor.FromHex(fill);
        if (stroke is not null) o.Stroke = CesiumColor.FromHex(stroke);
        return o;
    }

    /// <summary>GPU point-collection options from point geometries, with heights from <paramref name="heightColumn"/> (or Z) and colours from a column.</summary>
    public static PointCollectionOptions ToPointCollectionOptions(GeoDataFrame points, string? heightColumn = null, string? colorColumn = null,
        ColorMap? colorMap = null, double pixelSize = 4)
    {
        var p = ToWgs84(points);
        var positions = new List<GeoPoint>(p.RowCount);
        var colors = colorColumn is null ? null : new List<CesiumColor>(p.RowCount);
        double min = 0, max = 1;
        if (colorColumn is not null)
        {
            var vals = Enumerable.Range(0, points.RowCount).Where(i => !points[colorColumn].IsNull(i)).Select(i => points[colorColumn].GetDouble(i)).ToList();
            if (vals.Count > 0) { min = vals.Min(); max = vals.Max(); }
        }
        var cmap = colorMap ?? ColorMaps.Viridis;
        for (int i = 0; i < p.RowCount; i++)
        {
            if (p.Geometry[i] is not { } g || g.IsEmpty) continue;
            var c = g is Point pt ? pt.Coordinate : g.Centroid.Coordinate;
            double h = heightColumn is not null && !points[heightColumn].IsNull(i) ? points[heightColumn].GetDouble(i) : double.IsNaN(c.Z) ? 0 : c.Z;
            positions.Add(new GeoPoint(c.X, c.Y, h));
            if (colors is not null)
            {
                double v = points[colorColumn!].IsNull(i) ? min : points[colorColumn!].GetDouble(i);
                var rgba = cmap.Sample(max > min ? (v - min) / (max - min) : 0.5);
                colors.Add(CesiumColor.FromBytes(rgba.R, rgba.G, rgba.B, rgba.A));
            }
        }
        return new PointCollectionOptions { Positions = positions, Colors = colors, PixelSize = pixelSize };
    }

    /// <summary>The frame's extent as a WGS 84 rectangle (degrees).</summary>
    public static GeoRectangle ToRectangle(GeoDataFrame gdf, double marginFraction = 0.05)
    {
        var e = ToWgs84(gdf).TotalBounds;
        double mx = e.Width * marginFraction, my = e.Height * marginFraction;
        return new GeoRectangle(e.MinX - mx, e.MinY - my, e.MaxX + mx, e.MaxY + my);
    }

    /// <summary>Adds a frame as a GeoJSON data source; returns the data-source id.</summary>
    public static ValueTask<string> AddGeoDataFrameAsync(this CesiumController viewer, GeoDataFrame gdf, string? name = null, string? fill = null,
        string? stroke = null, bool clampToGround = true, bool flyTo = true, CancellationToken cancellationToken = default) =>
        viewer.DataSources.LoadAsync(ToDataSourceOptions(gdf, name, fill, stroke, clampToGround: clampToGround, flyTo: flyTo), cancellationToken);

    /// <summary>Adds points as a GPU point collection (100k+ points); returns the primitive id.</summary>
    public static ValueTask<string> AddPointsAsync(this CesiumController viewer, GeoDataFrame points, string? heightColumn = null, string? colorColumn = null,
        ColorMap? colorMap = null, double pixelSize = 4, CancellationToken cancellationToken = default) =>
        viewer.Primitives.AddPointsAsync(ToPointCollectionOptions(points, heightColumn, colorColumn, colorMap, pixelSize), cancellationToken);

    /// <summary>Adds a 3D Tiles tileset served at <paramref name="tilesetUrl"/> (for example one written by <see cref="Scene.Tiles3D"/>).</summary>
    public static ValueTask<string> AddTilesetAsync(this CesiumController viewer, string tilesetUrl, bool flyTo = true, TilesetStyle? style = null,
        CancellationToken cancellationToken = default)
    {
        var options = CesiumNet.TilesetOptions.FromUrl(tilesetUrl, flyTo);
        options.Style = style;
        return viewer.Tilesets.AddAsync(options, cancellationToken);
    }

    /// <summary>Flies the camera to a frame's extent.</summary>
    public static ValueTask FlyToAsync(this CesiumController viewer, GeoDataFrame gdf, double? duration = null) =>
        viewer.Camera.FlyToRectangleAsync(ToRectangle(gdf), duration);

    /// <summary>A 3D Tiles style colouring buildings by a numeric feature property (EXT_structural_metadata) in quantile-like steps.</summary>
    public static TilesetStyle ColorByProperty(string property, IReadOnlyList<(double Threshold, string CssColor)> steps, string otherwise = "color('#cccccc')")
    {
        var conds = steps.OrderByDescending(s => s.Threshold)
            .Select(s => new[] { $"${{{property}}} >= {s.Threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)}", $"color('{s.CssColor}')" })
            .Append(["true", otherwise]).ToList();
        return new TilesetStyle { ColorConditions = conds };
    }
}
