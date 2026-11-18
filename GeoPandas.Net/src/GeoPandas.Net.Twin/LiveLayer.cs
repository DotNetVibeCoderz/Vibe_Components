using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using GeoPandas.Net.Geometry.Index;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Twin;

/// <summary>One observation from a sensor or moving asset: position (layer CRS) and named values.</summary>
public sealed record SensorReading(string EntityId, DateTimeOffset Time, double X, double Y, IReadOnlyDictionary<string, double> Values, double Z = double.NaN);

public enum TwinEventKind { ZoneEnter, ZoneExit, ThresholdExceeded, ThresholdCleared, Stale }

/// <summary>An event raised by a live layer.</summary>
public sealed record TwinEvent(TwinEventKind Kind, string EntityId, DateTimeOffset Time, string? Zone = null, string? Rule = null, double? Value = null);

/// <summary>
/// A threshold rule on a variable, with hysteresis: it fires when the value crosses <see cref="Above"/> (or falls below <see cref="Below"/>)
/// and clears when it returns past the threshold by <see cref="Hysteresis"/>. Optionally limited to one zone.
/// </summary>
public sealed record ThresholdRule(string Name, string Variable, double? Above = null, double? Below = null, double Hysteresis = 0, string? Zone = null);

/// <summary>A zone (building footprint, district, geofence) of a live layer. Points on the boundary count as inside.</summary>
public sealed record TwinZone(string Id, NtsGeometry Geometry);

/// <summary>Latest state of one entity.</summary>
public sealed record EntityState(string EntityId, DateTimeOffset Time, double X, double Y, double Z, IReadOnlyDictionary<string, double> Values, IReadOnlySet<string> Zones);

/// <summary>
/// A live, thread-safe spatial layer for IoT streams: the latest position and values of every entity in a dynamic quadtree, a bounded
/// time window of history per entity, zones with enter/exit events, threshold rules, staleness checks, windowed zone statistics,
/// trajectories and GeoDataFrame snapshots.
/// </summary>
public sealed class LiveLayer
{
    private readonly object _gate = new();
    private readonly QuadTree<string> _index;
    private readonly Dictionary<string, EntityState> _state = [];
    private readonly Dictionary<string, Queue<SensorReading>> _history = [];
    private readonly List<(TwinZone Zone, IPreparedGeometry Prepared)> _zones = [];
    private readonly List<ThresholdRule> _rules = [];
    private readonly HashSet<(string Entity, string Rule)> _active = [];
    private readonly HashSet<string> _stale = [];
    private readonly Channel<TwinEvent> _events = Channel.CreateUnbounded<TwinEvent>(new UnboundedChannelOptions { SingleReader = false });

    public LiveLayer(string name, Envelope extent, CoordinateReferenceSystem? crs = null, TimeSpan? historyWindow = null, int maxHistoryPerEntity = 10_000)
    {
        Name = name; Crs = crs; Extent = extent;
        HistoryWindow = historyWindow ?? TimeSpan.FromHours(1);
        MaxHistoryPerEntity = maxHistoryPerEntity;
        _index = new QuadTree<string>(extent);
    }

    public string Name { get; }
    public CoordinateReferenceSystem? Crs { get; }
    public Envelope Extent { get; }
    public TimeSpan HistoryWindow { get; }
    public int MaxHistoryPerEntity { get; }
    public long ReadingCount { get; private set; }

    /// <summary>Raised synchronously for every event (also available as <see cref="Events"/>).</summary>
    public event Action<TwinEvent>? EventRaised;

    /// <summary>All events as an asynchronous stream.</summary>
    public ChannelReader<TwinEvent> Events => _events.Reader;

    public int EntityCount { get { lock (_gate) return _state.Count; } }

    public LiveLayer AddZone(string id, NtsGeometry geometry)
    {
        lock (_gate) _zones.Add((new TwinZone(id, geometry), PreparedGeometryFactory.Prepare(geometry)));
        return this;
    }

    /// <summary>Adds every row of a frame as a zone (id from <paramref name="idColumn"/>).</summary>
    public LiveLayer AddZones(GeoDataFrame zones, string idColumn)
    {
        for (int i = 0; i < zones.RowCount; i++)
            if (zones.Geometry[i] is { } g) AddZone(Convert.ToString(zones[idColumn].GetValue(i), CultureInfo.InvariantCulture)!, g);
        return this;
    }

    public LiveLayer AddRule(ThresholdRule rule)
    {
        lock (_gate) _rules.Add(rule);
        return this;
    }

    private void Raise(TwinEvent e)
    {
        _events.Writer.TryWrite(e);
        EventRaised?.Invoke(e);
    }

    /// <summary>Applies one reading: moves the entity, updates its zones (enter/exit), history and rules.</summary>
    public void Ingest(SensorReading r)
    {
        var raised = new List<TwinEvent>();
        lock (_gate)
        {
            ReadingCount++;
            var pt = Geo.Point(r.X, r.Y);
            var zones = _zones.Where(z => z.Prepared.Covers(pt)).Select(z => z.Zone.Id).ToHashSet();
            if (_state.TryGetValue(r.EntityId, out var old))
            {
                if (r.Time < old.Time) { AddHistory(r); return; }        // late data: history only
                _index.Remove(new Envelope(old.X, old.X, old.Y, old.Y), r.EntityId);
                foreach (var z in old.Zones.Except(zones)) raised.Add(new TwinEvent(TwinEventKind.ZoneExit, r.EntityId, r.Time, z));
                foreach (var z in zones.Except(old.Zones)) raised.Add(new TwinEvent(TwinEventKind.ZoneEnter, r.EntityId, r.Time, z));
            }
            else foreach (var z in zones) raised.Add(new TwinEvent(TwinEventKind.ZoneEnter, r.EntityId, r.Time, z));
            _index.Insert(new Envelope(r.X, r.X, r.Y, r.Y), r.EntityId);
            var merged = old is null ? new Dictionary<string, double>(r.Values) : new Dictionary<string, double>(old.Values);
            if (old is not null) foreach (var (k, v) in r.Values) merged[k] = v;
            _state[r.EntityId] = new EntityState(r.EntityId, r.Time, r.X, r.Y, r.Z, merged, zones);
            _stale.Remove(r.EntityId);
            AddHistory(r);
            foreach (var rule in _rules)
            {
                if (!r.Values.TryGetValue(rule.Variable, out var v)) continue;
                if (rule.Zone is not null && !zones.Contains(rule.Zone)) continue;
                var key = (r.EntityId, rule.Name);
                bool active = _active.Contains(key);
                bool fire = (rule.Above is { } a && v > a) || (rule.Below is { } b && v < b);
                bool clear = (rule.Above is not { } a2 || v <= a2 - rule.Hysteresis) && (rule.Below is not { } b2 || v >= b2 + rule.Hysteresis);
                if (!active && fire) { _active.Add(key); raised.Add(new TwinEvent(TwinEventKind.ThresholdExceeded, r.EntityId, r.Time, Rule: rule.Name, Value: v)); }
                else if (active && clear) { _active.Remove(key); raised.Add(new TwinEvent(TwinEventKind.ThresholdCleared, r.EntityId, r.Time, Rule: rule.Name, Value: v)); }
            }
        }
        foreach (var e in raised) Raise(e);
    }

    private void AddHistory(SensorReading r)
    {
        if (!_history.TryGetValue(r.EntityId, out var q)) _history[r.EntityId] = q = new Queue<SensorReading>();
        q.Enqueue(r);
        var cutoff = r.Time - HistoryWindow;
        while (q.Count > 0 && (q.Count > MaxHistoryPerEntity || q.Peek().Time < cutoff)) q.Dequeue();
    }

    /// <summary>Ingests a stream until it completes or is cancelled.</summary>
    public async Task IngestAsync(IAsyncEnumerable<SensorReading> readings, CancellationToken cancellationToken = default)
    {
        await foreach (var r in readings.WithCancellation(cancellationToken).ConfigureAwait(false)) Ingest(r);
    }

    public Task IngestAsync(ChannelReader<SensorReading> readings, CancellationToken cancellationToken = default) =>
        IngestAsync(readings.ReadAllAsync(cancellationToken), cancellationToken);

    /// <summary>Raises <see cref="TwinEventKind.Stale"/> once for every entity silent for longer than <paramref name="maxAge"/>.</summary>
    public IReadOnlyList<TwinEvent> CheckStale(DateTimeOffset now, TimeSpan maxAge)
    {
        var raised = new List<TwinEvent>();
        lock (_gate)
            foreach (var s in _state.Values)
                if (now - s.Time > maxAge && _stale.Add(s.EntityId)) raised.Add(new TwinEvent(TwinEventKind.Stale, s.EntityId, now));
        foreach (var e in raised) Raise(e);
        return raised;
    }

    public EntityState? Get(string entityId) { lock (_gate) return _state.GetValueOrDefault(entityId); }

    /// <summary>Entities within <paramref name="radius"/> of a point, nearest first.</summary>
    public IReadOnlyList<EntityState> Near(double x, double y, double radius)
    {
        lock (_gate)
            return _index.Query(new Envelope(x - radius, x + radius, y - radius, y + radius)).Select(id => _state[id])
                .Select(s => (s, d: Math.Sqrt((s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y)))).Where(t => t.d <= radius).OrderBy(t => t.d).Select(t => t.s).ToList();
    }

    public IReadOnlyList<EntityState> InZone(string zoneId) { lock (_gate) return _state.Values.Where(s => s.Zones.Contains(zoneId)).ToList(); }

    /// <summary>The entity's track within the history window (time-ordered).</summary>
    public LineString? Trajectory(string entityId)
    {
        lock (_gate)
        {
            if (!_history.TryGetValue(entityId, out var q) || q.Count < 2) return null;
            return Geo.LineString(q.OrderBy(r => r.Time).Select(r => new Coordinate(r.X, r.Y)));
        }
    }

    /// <summary>Latest state of all entities: id, time, every value, zones (";"-joined) and point geometry.</summary>
    public GeoDataFrame Snapshot()
    {
        List<EntityState> states;
        lock (_gate) states = [.. _state.Values.OrderBy(s => s.EntityId, StringComparer.Ordinal)];
        var variables = states.SelectMany(s => s.Values.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
        var cols = new List<Column>
        {
            Column.Create("entity", states.Select(s => s.EntityId).ToArray()),
            Column.Create("time", states.Select(s => s.Time.UtcDateTime).ToArray()),
            Column.Create("zones", states.Select(s => string.Join(";", s.Zones.Order(StringComparer.Ordinal))).ToArray()),
        };
        foreach (var v in variables)
            cols.Add(Column<double>.FromNullable(v, states.Select(s => s.Values.TryGetValue(v, out var x) ? x : (double?)null).ToArray()));
        return GeoDataFrame.FromGeometries(states.Select(s => (NtsGeometry?)Geo.Point(s.X, s.Y)), Crs, cols.ToArray());
    }

    /// <summary>
    /// Per-zone statistics of a variable over readings in the last <paramref name="window"/> (up to <paramref name="now"/>):
    /// count, mean, min, max and the number of distinct entities.
    /// </summary>
    public DataFrame ZoneStatistics(string variable, TimeSpan window, DateTimeOffset now)
    {
        List<(TwinZone Zone, IPreparedGeometry Prepared)> zones;
        List<SensorReading> readings;
        lock (_gate)
        {
            zones = [.. _zones];
            readings = _history.Values.SelectMany(q => q).Where(r => r.Time > now - window && r.Time <= now && r.Values.ContainsKey(variable)).ToList();
        }
        var rows = zones.Select(z =>
        {
            var inside = readings.Where(r => z.Prepared.Covers(Geo.Point(r.X, r.Y))).ToList();
            var vals = inside.Select(r => r.Values[variable]).ToList();
            return (z.Zone.Id, Count: (long)vals.Count, Mean: vals.Count > 0 ? vals.Average() : double.NaN, Min: vals.Count > 0 ? vals.Min() : double.NaN,
                Max: vals.Count > 0 ? vals.Max() : double.NaN, Entities: (long)inside.Select(r => r.EntityId).Distinct().Count());
        }).ToList();
        return new DataFrame(Column.Create("zone", rows.Select(r => r.Id).ToArray()), Column.Create("count", rows.Select(r => r.Count).ToArray()),
            Column.Create("mean", rows.Select(r => r.Mean).ToArray()), Column.Create("min", rows.Select(r => r.Min).ToArray()),
            Column.Create("max", rows.Select(r => r.Max).ToArray()), Column.Create("entities", rows.Select(r => r.Entities).ToArray()));
    }
}

/// <summary>Parses JSON sensor messages (MQTT, WebSocket, HTTP) into readings.</summary>
public static class TwinJson
{
    /// <summary>
    /// Parses <c>{"id":…, "time":…, "lon"|"x":…, "lat"|"y":…, "z"?:…, "values":{…}}</c> or a flat message whose other numeric fields are values.
    /// Geographic positions are transformed to <paramref name="layerCrs"/> when it is projected.
    /// </summary>
    public static SensorReading Parse(string json, CoordinateReferenceSystem? layerCrs = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string id = (root.TryGetProperty("id", out var idp) ? idp : root.GetProperty("entity")).ToString();
        var time = root.TryGetProperty("time", out var tp)
            ? tp.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeMilliseconds(tp.GetInt64()) : DateTimeOffset.Parse(tp.GetString()!, CultureInfo.InvariantCulture)
            : DateTimeOffset.UtcNow;
        bool geographic = root.TryGetProperty("lon", out _);
        double x = geographic ? root.GetProperty("lon").GetDouble() : root.GetProperty("x").GetDouble();
        double y = geographic ? root.GetProperty("lat").GetDouble() : root.GetProperty("y").GetDouble();
        double z = root.TryGetProperty("z", out var zp) ? zp.GetDouble() : root.TryGetProperty("alt", out var ap) ? ap.GetDouble() : double.NaN;
        var values = new Dictionary<string, double>();
        if (root.TryGetProperty("values", out var vp) && vp.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in vp.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) values[p.Name] = p.Value.GetDouble();
        }
        else
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Name is not ("time" or "lon" or "lat" or "x" or "y" or "z" or "alt" or "id"))
                    values[p.Name] = p.Value.GetDouble();
        if (geographic && layerCrs is { IsGeographic: false })
            (x, y) = CoordinateTransformer.Create(CoordinateReferenceSystem.Wgs84, layerCrs).Transform(x, y);
        return new SensorReading(id, time, x, y, values, z);
    }
}

/// <summary>
/// A digital twin: named static layers (buildings, networks, parcels) and live layers whose zones come from static features, with
/// per-asset roll-ups for building, city and infrastructure twins.
/// </summary>
public sealed class DigitalTwin
{
    private readonly ConcurrentDictionary<string, GeoDataFrame> _static = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LiveLayer> _live = new(StringComparer.OrdinalIgnoreCase);

    public DigitalTwin(string name, CoordinateReferenceSystem? crs = null) { Name = name; Crs = crs; }

    public string Name { get; }
    public CoordinateReferenceSystem? Crs { get; }
    public IReadOnlyCollection<string> StaticLayers => _static.Keys.ToList();
    public IReadOnlyCollection<string> LiveLayers => _live.Keys.ToList();

    public DigitalTwin AddStatic(string name, GeoDataFrame layer) { _static[name] = layer; return this; }
    public GeoDataFrame Static(string name) => _static[name];
    public LiveLayer Live(string name) => _live[name];

    /// <summary>Creates a live layer covering the static layers, with zones from a static layer's features.</summary>
    public LiveLayer AddLive(string name, string? zonesFrom = null, string? zoneIdColumn = null, TimeSpan? historyWindow = null)
    {
        var extent = new Envelope();
        foreach (var l in _static.Values) extent.ExpandToInclude(l.TotalBounds);
        if (extent.IsNull) extent = new Envelope(-180, 180, -90, 90);
        extent.ExpandBy(Math.Max(extent.Width, extent.Height) * 0.1 + 1);
        var layer = new LiveLayer(name, extent, Crs, historyWindow);
        if (zonesFrom is not null) layer.AddZones(_static[zonesFrom], zoneIdColumn ?? throw new ArgumentNullException(nameof(zoneIdColumn)));
        _live[name] = layer;
        return layer;
    }

    /// <summary>Joins a live layer's windowed zone statistics back onto the static features (e.g. mean temperature per building).</summary>
    public GeoDataFrame RollUp(string liveLayer, string staticLayer, string idColumn, string variable, TimeSpan window, DateTimeOffset now)
    {
        var stats = _live[liveLayer].ZoneStatistics(variable, window, now);
        var lookup = Enumerable.Range(0, stats.RowCount).ToDictionary(i => (string)stats["zone"].GetValue(i)!, i => i);
        var features = _static[staticLayer];
        double[] Col(string c) => Enumerable.Range(0, features.RowCount).Select(i =>
            lookup.TryGetValue(Convert.ToString(features[idColumn].GetValue(i), CultureInfo.InvariantCulture)!, out var j) ? stats[c].GetDouble(j) : double.NaN).ToArray();
        return features.WithColumn(Column.Create($"{variable}_mean", Col("mean"))).WithColumn(Column.Create($"{variable}_max", Col("max")))
            .WithColumn(Column.Create($"{variable}_count", Col("count")));
    }
}
