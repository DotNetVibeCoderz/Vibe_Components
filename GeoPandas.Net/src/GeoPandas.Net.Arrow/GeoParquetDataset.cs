using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetTopologySuite.Geometries;

namespace GeoPandas.Net.Arrow;

/// <summary>One file of a partitioned dataset, as listed in the manifest.</summary>
public sealed record DatasetPartition(string Key, string Path, long Rows, Envelope Bounds);

/// <summary>
/// Partitioned GeoParquet datasets: one GeoParquet file per partition (Hive-style <c>partition=&lt;key&gt;/part-0.parquet</c>) plus a
/// <c>_dataset.json</c> manifest with each partition's row count and bounds. Reads with a bounding box open only the partitions whose
/// bounds intersect it, then prune row groups inside each file with the GeoParquet bbox covering column. Partitions are read in parallel.
/// </summary>
public static class GeoParquetDataset
{
    public const string ManifestName = "_dataset.json";

    /// <summary>Writes a partitioned frame to a directory (existing partition files of the same keys are overwritten).</summary>
    public static async Task WriteAsync(PartitionedGeoDataFrame data, string directory, GeoParquetWriteOptions? options = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var entries = new JsonArray();
        var crs = data.Partitions.FirstOrDefault()?.Frame.Crs;
        foreach (var p in data.Partitions)
        {
            var rel = $"partition={Sanitize(p.Key)}/part-0.parquet";
            var full = System.IO.Path.Combine(directory, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            await GeoParquet.WriteAsync(p.Frame, full, options, ct).ConfigureAwait(false);
            entries.Add(new JsonObject
            {
                ["key"] = p.Key,
                ["path"] = rel,
                ["rows"] = p.RowCount,
                ["bbox"] = p.Bounds.IsNull ? null : new JsonArray(p.Bounds.MinX, p.Bounds.MinY, p.Bounds.MaxX, p.Bounds.MaxY),
            });
        }
        var manifest = new JsonObject
        {
            ["format"] = "geopandas-net-dataset",
            ["version"] = 1,
            ["crs"] = crs?.ToString(),
            ["partitions"] = entries,
        };
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, ManifestName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8, ct).ConfigureAwait(false);
    }

    public static void Write(PartitionedGeoDataFrame data, string directory, GeoParquetWriteOptions? options = null) =>
        WriteAsync(data, directory, options).GetAwaiter().GetResult();

    /// <summary>Partitions a frame and writes it (see <see cref="PartitionedGeoDataFrame.Create"/>).</summary>
    public static void Write(GeoDataFrame gdf, string directory, PartitionScheme scheme, int size = 4, string? column = null, GeoParquetWriteOptions? options = null) =>
        Write(PartitionedGeoDataFrame.Create(gdf, scheme, size, column), directory, options);

    /// <summary>The manifest entries of a dataset.</summary>
    public static IReadOnlyList<DatasetPartition> Partitions(string directory)
    {
        var path = System.IO.Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) throw new FileNotFoundException("Not a partitioned GeoParquet dataset (no manifest).", path);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        return root["partitions"]!.AsArray().Select(n =>
        {
            var b = n!["bbox"] is JsonArray a ? new Envelope(a[0]!.GetValue<double>(), a[2]!.GetValue<double>(), a[1]!.GetValue<double>(), a[3]!.GetValue<double>()) : new Envelope();
            var rel = n["path"]!.GetValue<string>();
            if (System.IO.Path.IsPathRooted(rel) || rel.Split('/', '\\').Contains("..")) throw new InvalidDataException($"Partition path escapes the dataset: {rel}");
            return new DatasetPartition(n["key"]!.GetValue<string>(), rel, n["rows"]!.GetValue<long>(), b);
        }).ToList();
    }

    /// <summary>Reads the partitions intersecting <paramref name="bbox"/> (all when null), keeping one partition per key.</summary>
    public static async Task<PartitionedGeoDataFrame> ReadPartitionedAsync(string directory, Envelope? bbox = null, IReadOnlyList<string>? columns = null, CancellationToken ct = default)
    {
        var selected = Partitions(directory).Where(p => bbox is null || (!p.Bounds.IsNull && p.Bounds.Intersects(bbox))).ToList();
        var frames = await Task.WhenAll(selected.Select(p => GeoParquet.ReadAsync(System.IO.Path.Combine(directory, p.Path), bbox, columns, ct))).ConfigureAwait(false);
        int offset = 0;
        var parts = new List<GeoPartition>();
        for (int k = 0; k < selected.Count; k++)
        {
            var f = frames[k];
            if (bbox is not null)
            {
                // row-group pruning is coarse: keep only rows whose geometry intersects the box
                var poly = Geometry.Geo.Box(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY);
                f = f.Take(Enumerable.Range(0, f.RowCount).Where(i => f.Geometry[i] is { } g && g.Intersects(poly)).ToArray());
            }
            parts.Add(new GeoPartition(selected[k].Key, f, Enumerable.Range(offset, f.RowCount).ToArray(), BoundsOf(f)));
            offset += f.RowCount;
        }
        return new PartitionedGeoDataFrame(parts);
    }

    public static PartitionedGeoDataFrame ReadPartitioned(string directory, Envelope? bbox = null, IReadOnlyList<string>? columns = null) =>
        ReadPartitionedAsync(directory, bbox, columns).GetAwaiter().GetResult();

    /// <summary>Reads a dataset (or the part of it intersecting <paramref name="bbox"/>) as one frame.</summary>
    public static GeoDataFrame Read(string directory, Envelope? bbox = null, IReadOnlyList<string>? columns = null)
    {
        var p = ReadPartitioned(directory, bbox, columns);
        if (p.PartitionCount == 0) throw new InvalidOperationException("No partition intersects the bounding box.");
        return GeoDataFrame.Concat(p.Partitions.Select(x => x.Frame).ToArray());
    }

    private static Envelope BoundsOf(GeoDataFrame f)
    {
        var e = new Envelope();
        for (int i = 0; i < f.RowCount; i++) if (f.Geometry[i] is { IsEmpty: false } g) e.ExpandToInclude(g.EnvelopeInternal);
        return e;
    }

    private static string Sanitize(string key)
    {
        var sb = new StringBuilder();
        foreach (var ch in key) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '%' + ((int)ch).ToString("X2"));
        var s = sb.ToString();
        return s is "" or "." or ".." ? "_" + s : s;
    }
}
