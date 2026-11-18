using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net;

/// <summary>H3 hexagonal indexing for GeoDataFrames: cell columns, hexagon frames and hexagonal binning.</summary>
public static class H3Frame
{

    /// <summary>Adds an H3 cell column (hex strings) for point geometries in WGS 84 (polygons use their centroid).</summary>
    public static GeoDataFrame IndexPoints(GeoDataFrame gdf, int res, string column = "h3")
    {
        var src = gdf.Crs is { } crs && !crs.IsGeographic ? gdf.ToCrs(4326) : gdf;
        var cells = new string?[gdf.RowCount];
        for (int i = 0; i < cells.Length; i++)
        {
            var g = src.Geometry[i];
            if (g is null || g.IsEmpty) continue;
            var c = g is Point p ? p.Coordinate : g.Centroid.Coordinate;
            cells[i] = H3.ToString(H3.LatLngToCell(c.Y, c.X, res));
        }
        return gdf.WithColumn(new Column<string?>(column, cells));
    }

    /// <summary>Cells as a GeoDataFrame of hexagons (WGS 84) with h3, resolution and area (km²).</summary>
    public static GeoDataFrame CellsToFrame(IEnumerable<ulong> cells)
    {
        var list = cells.ToList();
        return GeoDataFrame.FromGeometries(list.Select(c => (NetTopologySuite.Geometries.Geometry?)H3.CellToPolygon(c)), CoordinateReferenceSystem.Wgs84,
            new Column<string?>("h3", list.Select(c => (string?)H3.ToString(c)).ToArray()),
            Column.Create("resolution", list.Select(c => (long)H3.GetResolution(c)).ToArray()),
            Column.Create("area_km2", list.Select(H3.CellAreaKm2).ToArray()));
    }

    /// <summary>Hexagonal binning: counts (and optional sum/mean of a column) of points per H3 cell, as hexagon polygons.</summary>
    public static GeoDataFrame Bin(GeoDataFrame points, int res, string? valueColumn = null)
    {
        var indexed = IndexPoints(points, res, "__h3");
        var groups = Enumerable.Range(0, indexed.RowCount).Where(i => indexed["__h3"].GetValue(i) is not null)
            .GroupBy(i => (string)indexed["__h3"].GetValue(i)!).ToList();
        var cells = groups.Select(g => H3.Parse(g.Key)).ToList();
        var frame = CellsToFrame(cells).WithColumn(Column.Create("count", groups.Select(g => (long)g.Count()).ToArray()));
        if (valueColumn is not null)
        {
            var col = points[valueColumn];
            frame = frame.WithColumn(Column.Create($"{valueColumn}_sum", groups.Select(g => g.Sum(i => col.GetDouble(i))).ToArray()))
                         .WithColumn(Column.Create($"{valueColumn}_mean", groups.Select(g => g.Average(i => col.GetDouble(i))).ToArray()));
        }
        return frame;
    }
}
