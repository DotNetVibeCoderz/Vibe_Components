using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GeoPandas.Net.Versioning;

namespace GeoPandas.Net.Data.Versioning;

/// <summary>One committed version of a dataset.</summary>
public sealed record DatasetCommit(
    string Id, string? Parent, string Message, string? Author, DateTimeOffset Timestamp,
    int Rows, int Columns, string ContentHash, string Snapshot, string? Summary)
{
    public string ShortId => Id[..Math.Min(10, Id.Length)];
    public override string ToString() => $"{ShortId} {Timestamp:yyyy-MM-dd HH:mm} {Message} ({Rows} rows{(Summary is null ? "" : "; " + Summary)})";
}

/// <summary>
/// Git-like version store for immutable GeoDataFrames. Each commit is a content-addressed FlatGeobuf snapshot
/// (identical content is stored once), with a linear history, tags and diffs between any two versions.
/// The repository is a plain directory: <c>repo.json</c> plus <c>snapshots/&lt;sha256&gt;.fgb</c>.
/// </summary>
/// <example>
/// <code>
/// var repo = DatasetRepository.Open("data/parcels.repo", key: "parcel_id");
/// repo.Commit(parcels, "Initial import from BPN");
/// repo.Commit(edited, "Merge parcels 12 and 13");
/// var diff = repo.Diff("HEAD~1", "HEAD");          // +0 added, -2 removed, ~1 modified
/// var old = repo.Checkout("v1.0");
/// </code>
/// </example>
public sealed class DatasetRepository
{
    private const string Format = "geopandas.net-repo/1";
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly string _dir;
    private Manifest _m;

    private DatasetRepository(string dir, Manifest m) { _dir = dir; _m = m; }

    /// <summary>Opens a repository directory, creating it when missing. <paramref name="key"/> sets the row identity column for diffs.</summary>
    public static DatasetRepository Open(string directory, string? key = null)
    {
        Directory.CreateDirectory(Path.Combine(directory, "snapshots"));
        var file = Path.Combine(directory, "repo.json");
        Manifest m;
        if (File.Exists(file))
        {
            m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), s_json) ?? throw new InvalidDataException("repo.json is empty.");
            if (m.Format != Format) throw new InvalidDataException($"Unsupported repository format '{m.Format}'.");
            if (key is not null && m.Key is null) m = m with { Key = key };
        }
        else m = new Manifest { Format = Format, Key = key };
        var repo = new DatasetRepository(directory, m);
        repo.Save();
        return repo;
    }

    public string RootDirectory => _dir;
    /// <summary>Row identity column used for diffs (null = compare rows as a multiset).</summary>
    public string? Key => _m.Key;
    public DatasetCommit? Head => _m.Head is null ? null : Get(_m.Head);
    public IReadOnlyDictionary<string, string> Tags => _m.Tags;

    /// <summary>Commits, newest first.</summary>
    public IReadOnlyList<DatasetCommit> Log()
    {
        var res = new List<DatasetCommit>();
        for (var c = Head; c is not null; c = c.Parent is null ? null : Get(c.Parent)) res.Add(c);
        return res;
    }

    /// <summary>
    /// Stores a new version. Returns the existing head (no new commit) when the content is unchanged.
    /// </summary>
    public DatasetCommit Commit(GeoDataFrame frame, string message, string? author = null)
    {
        var hash = FrameDiff.ContentHash(frame);
        var head = Head;
        if (head is not null && head.ContentHash == hash) return head;

        string? summary = null;
        if (head is not null)
        {
            FrameDiff diff;
            try { diff = FrameDiff.Compare(Checkout(head.Id), frame, Key is not null && frame.HasColumn(Key) ? Key : null); }
            catch (ArgumentException) { diff = FrameDiff.Compare(Checkout(head.Id), frame); }
            if (diff.IsEmpty) return head; // same rows in a different order (e.g. re-read from a Hilbert-sorted file)
            summary = diff.Summary;
        }
        var snapshot = $"snapshots/{hash}.fgb";
        var full = Path.Combine(_dir, snapshot);
        if (!File.Exists(full))
        {
            var tmp = full + ".tmp";
            FlatGeobuf.Write(frame, tmp, new FlatGeobufWriteOptions { SpatialIndex = false, LayerName = "snapshot", Description = message });
            File.Move(tmp, full, overwrite: true);
        }
        var ts = DateTimeOffset.UtcNow;
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{head?.Id}\n{hash}\n{message}\n{author}\n{ts:O}")));
        var commit = new DatasetCommit(id, head?.Id, message, author, ts, frame.RowCount, frame.ColumnCount, hash, snapshot, summary);
        _m = _m with { Head = id, Commits = [.. _m.Commits, commit] };
        Save();
        return commit;
    }

    /// <summary>Names a version (e.g. a release).</summary>
    public void Tag(string name, string reference = "HEAD")
    {
        var c = Resolve(reference);
        _m = _m with { Tags = new Dictionary<string, string>(_m.Tags) { [name] = c.Id } };
        Save();
    }

    /// <summary>Resolves "HEAD", "HEAD~n", a tag or a (prefix of a) commit id.</summary>
    public DatasetCommit Resolve(string reference)
    {
        if (reference.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            int back = 0;
            if (reference.Length > 4)
            {
                if (reference[4] != '~' || !int.TryParse(reference.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out back))
                    throw new ArgumentException($"Bad reference '{reference}'. Use HEAD or HEAD~n.");
            }
            var log = Log();
            if (back >= log.Count) throw new ArgumentException($"'{reference}' goes beyond the first commit ({log.Count} commits).");
            return log[back];
        }
        if (_m.Tags.TryGetValue(reference, out var tagged)) return Get(tagged);
        var matches = _m.Commits.Where(c => c.Id.StartsWith(reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new KeyNotFoundException($"No commit or tag '{reference}'."),
            _ => throw new ArgumentException($"Commit prefix '{reference}' is ambiguous.")
        };
    }

    /// <summary>Reads a version back (memory-mapped FlatGeobuf).</summary>
    public GeoDataFrame Checkout(string reference = "HEAD") => FlatGeobuf.Read(Path.Combine(_dir, Resolve(reference).Snapshot));

    /// <summary>Differences between two versions (by <see cref="Key"/> when set).</summary>
    public FrameDiff Diff(string from, string to = "HEAD", string? key = null)
    {
        var a = Checkout(from); var b = Checkout(to);
        var k = key ?? Key;
        return FrameDiff.Compare(a, b, k is not null && a.HasColumn(k) && b.HasColumn(k) ? k : null);
    }

    private DatasetCommit Get(string id) => _m.Commits.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException($"Commit {id} is missing from repo.json.");

    private void Save()
    {
        var file = Path.Combine(_dir, "repo.json");
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_m, s_json));
        File.Move(tmp, file, overwrite: true);
    }

    private sealed record Manifest
    {
        public string Format { get; init; } = "";
        public string? Key { get; init; }
        public string? Head { get; init; }
        public DatasetCommit[] Commits { get; init; } = [];
        public Dictionary<string, string> Tags { get; init; } = new();
    }
}
