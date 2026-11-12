using System.Globalization;
using System.Text;
using AquaCulture.Core;
using AquaCulture.Core.Data;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Hosting;
using AquaCulture.Core.Species;
using AquaCulture.DigitalTwin;
using AquaCulture.IoT;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ParquetSharp;
using ParquetSharp.IO;

namespace AquaCulture.Data;

/// <summary>
/// Persistence for farms, culture cycles (with all their logs), sensor readings and twin decisions.
/// SQLite by default (<see cref="OpenSqlite"/>); pass any <see cref="DbContextOptions{AquaDbContext}"/> for other providers.
/// </summary>
public sealed class AquaStore : IDisposable, IAsyncDisposable
{
    private readonly DbContextOptions<AquaDbContext> _options;

    public AquaStore(DbContextOptions<AquaDbContext> options)
    {
        _options = options;
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    /// <summary>Opens (or creates) a SQLite database file.</summary>
    public static AquaStore OpenSqlite(string path) => new(new DbContextOptionsBuilder<AquaDbContext>().UseSqlite($"Data Source={path}").Options);

    public AquaDbContext NewContext() => new(_options);

    // ───────── farms and cycles ─────────

    /// <summary>Saves a farm, its ponds and its cycles with every log (replacing the stored copy).</summary>
    public async Task SaveFarmAsync(Farm farm, CancellationToken ct = default)
    {
        await using var db = NewContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.CycleEvents.Where(x => x.FarmId == farm.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Cycles.Where(x => x.FarmId == farm.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Ponds.Where(x => x.FarmId == farm.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.Farms.Where(x => x.Id == farm.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        db.Farms.Add(new FarmRow { Id = farm.Id, Name = farm.Name, Type = farm.Type.ToString(), Latitude = farm.Location.Latitude, Longitude = farm.Location.Longitude, ElevationM = farm.ElevationM, Owner = farm.Owner });
        foreach (var p in farm.Ponds)
            db.Ponds.Add(new PondRow
            {
                FarmId = farm.Id, Id = p.Id, Name = p.Name, System = p.System.ToString(), AreaM2 = p.AreaM2, DepthM = p.DepthM, AerationKw = p.AerationKw,
                WaterExchangePctPerDay = p.WaterExchangePctPerDay, OutlineGeoJson = p.Outline is null ? null : RingToJson(p.Outline),
                Latitude = p.Location?.Latitude, Longitude = p.Location?.Longitude,
            });
        foreach (var c in farm.Cycles)
        {
            db.Cycles.Add(new CycleRow
            {
                FarmId = farm.Id, Id = c.Id, PondId = c.PondId, SpeciesId = c.Species.Id, StockingDate = c.StockingDate, StockedCount = c.StockedCount,
                StockingWeightG = c.StockingWeightG, SeedPricePerUnit = c.SeedPricePerUnit, ClosedDate = c.ClosedDate,
            });
            CycleEventRow E(CycleEventKind k, DateOnly d) => new() { FarmId = farm.Id, CycleId = c.Id, Kind = k, Date = d };
            foreach (var s in c.Samplings) db.CycleEvents.Add(E(CycleEventKind.Sampling, s.Date) is var r ? Fill(r, s.AverageWeightG, s.SampleSize, 0, "", s.AverageLengthCm) : null!);
            foreach (var f in c.Feedings) db.CycleEvents.Add(Fill(E(CycleEventKind.Feed, f.Date), f.Kg, 0, f.PricePerKg, f.FeedId));
            foreach (var m in c.Mortalities) db.CycleEvents.Add(Fill(E(CycleEventKind.Mortality, m.Date), m.Count, m.Count, 0, m.Cause));
            foreach (var h in c.Harvests) db.CycleEvents.Add(Fill(E(CycleEventKind.Harvest, h.Date), h.Kg, h.Count, h.PricePerKg, "", null, h.Partial));
            foreach (var x in c.Expenses) db.CycleEvents.Add(Fill(E(CycleEventKind.Expense, x.Date), x.Amount, 0, 0, x.Category, null, false, x.Note));
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private static CycleEventRow Fill(CycleEventRow r, double value, long count, double price, string text, double? extra = null, bool flag = false, string note = "")
    {
        r.Value = value; r.Count = count; r.Price = price; r.Text = text; r.Extra = extra; r.Flag = flag; r.Note = note;
        return r;
    }

    /// <summary>Loads a farm with its ponds and cycles; null when it does not exist. Unknown species ids throw.</summary>
    public async Task<Farm?> LoadFarmAsync(string farmId, CancellationToken ct = default)
    {
        await using var db = NewContext();
        var f = await db.Farms.AsNoTracking().FirstOrDefaultAsync(x => x.Id == farmId, ct).ConfigureAwait(false);
        if (f is null) return null;
        var farm = new Farm { Id = f.Id, Name = f.Name, Type = Enum.Parse<FarmType>(f.Type), Location = new GeoCoordinate(f.Latitude, f.Longitude), ElevationM = f.ElevationM, Owner = f.Owner };
        foreach (var p in await db.Ponds.AsNoTracking().Where(x => x.FarmId == farmId).OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            farm.Ponds.Add(new Pond
            {
                Id = p.Id, Name = p.Name, System = Enum.Parse<CultureSystem>(p.System), AreaM2 = p.AreaM2, DepthM = p.DepthM, AerationKw = p.AerationKw,
                WaterExchangePctPerDay = p.WaterExchangePctPerDay, Outline = p.OutlineGeoJson is null ? null : JsonToRing(p.OutlineGeoJson),
                Location = p.Latitude is { } la && p.Longitude is { } lo ? new GeoCoordinate(la, lo) : null,
            });
        var events = (await db.CycleEvents.AsNoTracking().Where(x => x.FarmId == farmId).OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false)).ToLookup(e => e.CycleId);
        foreach (var c in await db.Cycles.AsNoTracking().Where(x => x.FarmId == farmId).OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
        {
            var cycle = new CultureCycle
            {
                Id = c.Id, PondId = c.PondId, Species = SpeciesCatalog.Get(c.SpeciesId), StockingDate = c.StockingDate, StockedCount = c.StockedCount,
                StockingWeightG = c.StockingWeightG, SeedPricePerUnit = c.SeedPricePerUnit, ClosedDate = c.ClosedDate,
            };
            foreach (var e in events[c.Id])
                switch (e.Kind)
                {
                    case CycleEventKind.Sampling: cycle.Samplings.Add(new SamplingRecord(e.Date, e.Value, (int)e.Count, e.Extra)); break;
                    case CycleEventKind.Feed: cycle.Feedings.Add(new FeedRecord(e.Date, e.Value, e.Text, e.Price)); break;
                    case CycleEventKind.Mortality: cycle.Mortalities.Add(new MortalityRecord(e.Date, e.Count, e.Text)); break;
                    case CycleEventKind.Harvest: cycle.Harvests.Add(new HarvestRecord(e.Date, e.Value, e.Count, e.Price, e.Flag)); break;
                    case CycleEventKind.Expense: cycle.Expenses.Add(new ExpenseRecord(e.Date, e.Text, e.Value, e.Note)); break;
                }
            farm.Cycles.Add(cycle);
        }
        return farm;
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> ListFarmsAsync(CancellationToken ct = default)
    {
        await using var db = NewContext();
        return (await db.Farms.AsNoTracking().OrderBy(x => x.Id).Select(x => new { x.Id, x.Name }).ToListAsync(ct).ConfigureAwait(false)).Select(x => (x.Id, x.Name)).ToList();
    }

    private static string RingToJson(GeoPolygon p) =>
        "[" + string.Join(",", p.Ring.Append(p.Ring[0]).Select(c => string.Create(CultureInfo.InvariantCulture, $"[{c.Longitude:0.#######},{c.Latitude:0.#######}]"))) + "]";

    private static GeoPolygon JsonToRing(string json)
    {
        using var d = System.Text.Json.JsonDocument.Parse(json);
        return new GeoPolygon(d.RootElement.EnumerateArray().Select(a => new GeoCoordinate(a[1].GetDouble(), a[0].GetDouble())).ToList());
    }

    // ───────── readings ─────────

    /// <summary>Appends readings in one transaction (thousands per call are fine on SQLite).</summary>
    public async Task<int> AppendReadingsAsync(IEnumerable<SensorReading> readings, CancellationToken ct = default)
    {
        await using var db = NewContext();
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        int n = 0;
        foreach (var r in readings)
        {
            db.Readings.Add(new ReadingRow { DeviceId = r.DeviceId, PondId = r.PondId, Kind = (int)r.Kind, Value = r.Value, TimestampUtc = r.Timestamp.ToUniversalTime(), Quality = r.Quality });
            n++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return n;
    }

    /// <summary>Readings of a pond (optionally of one kind) between two instants, oldest first.</summary>
    public async Task<IReadOnlyList<SensorReading>> QueryReadingsAsync(string pondId, SensorKind? kind = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        await using var db = NewContext();
        var q = db.Readings.AsNoTracking().Where(x => x.PondId == pondId);
        if (kind is { } k) q = q.Where(x => x.Kind == (int)k);
        if (fromUtc is { } f) q = q.Where(x => x.TimestampUtc >= f);
        if (toUtc is { } t) q = q.Where(x => x.TimestampUtc <= t);
        var rows = await q.OrderBy(x => x.TimestampUtc).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToReading).ToList();
    }

    /// <summary>A pond parameter as a <see cref="TimeSeries"/> (for forecasting and charts).</summary>
    public async Task<TimeSeries> SeriesAsync(string pondId, SensorKind kind, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = await QueryReadingsAsync(pondId, kind, fromUtc, toUtc, ct).ConfigureAwait(false);
        return new TimeSeries(rows.Select(r => new TimePoint(r.Timestamp, r.Value)), kind.ToString(), SensorReading.DefaultUnit(kind));
    }

    /// <summary>Deletes readings older than a cut-off (retention); returns the number removed.</summary>
    public async Task<int> PurgeReadingsAsync(DateTime olderThanUtc, CancellationToken ct = default)
    {
        await using var db = NewContext();
        return await db.Readings.Where(x => x.TimestampUtc < olderThanUtc).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static SensorReading ToReading(ReadingRow x) =>
        new(x.DeviceId, x.PondId, (SensorKind)x.Kind, x.Value, DateTime.SpecifyKind(x.TimestampUtc, DateTimeKind.Utc), SensorReading.DefaultUnit((SensorKind)x.Kind), x.Quality);

    // ───────── decisions ─────────

    public async Task AppendDecisionsAsync(IEnumerable<TwinDecision> decisions, CancellationToken ct = default)
    {
        await using var db = NewContext();
        foreach (var d in decisions)
            db.Decisions.Add(new DecisionRow
            {
                PondId = d.PondId, Kind = d.Kind.ToString(), Severity = d.Severity.ToString(), TitleEn = d.Title.En, TitleId = d.Title.Id,
                RationaleEn = d.Rationale.En, RationaleId = d.Rationale.Id, CommandJson = d.Command?.ToJson(), TimestampUtc = d.Timestamp.ToUniversalTime(),
            });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DecisionRow>> QueryDecisionsAsync(string? pondId = null, DateTime? fromUtc = null, CancellationToken ct = default)
    {
        await using var db = NewContext();
        var q = db.Decisions.AsNoTracking().AsQueryable();
        if (pondId is not null) q = q.Where(x => x.PondId == pondId);
        if (fromUtc is { } f) q = q.Where(x => x.TimestampUtc >= f);
        return await q.OrderBy(x => x.TimestampUtc).ToListAsync(ct).ConfigureAwait(false);
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Wraps a transport so that every reading is also written to an <see cref="AquaStore"/> (batched).</summary>
public sealed class RecordingTransport : ISensorTransport
{
    private readonly ISensorTransport _inner;
    private readonly AquaStore _store;
    private readonly List<SensorReading> _buffer = [];
    private readonly object _gate = new();

    public RecordingTransport(ISensorTransport inner, AquaStore store, int batchSize = 200)
    {
        _inner = inner; _store = store; BatchSize = batchSize;
        _inner.ReadingReceived += OnReading;
    }

    public int BatchSize { get; }
    public event Action<SensorReading>? ReadingReceived;
    public bool IsConnected => _inner.IsConnected;
    public int Recorded { get; private set; }

    private void OnReading(SensorReading r)
    {
        List<SensorReading>? flush = null;
        lock (_gate)
        {
            _buffer.Add(r);
            if (_buffer.Count >= BatchSize) { flush = [.. _buffer]; _buffer.Clear(); }
        }
        if (flush is not null) { _store.AppendReadingsAsync(flush).GetAwaiter().GetResult(); Recorded += flush.Count; }
        ReadingReceived?.Invoke(r);
    }

    /// <summary>Writes buffered readings now.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        List<SensorReading> flush;
        lock (_gate) { flush = [.. _buffer]; _buffer.Clear(); }
        if (flush.Count > 0) { await _store.AppendReadingsAsync(flush, ct).ConfigureAwait(false); Recorded += flush.Count; }
    }

    public Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public Task SendCommandAsync(ActuatorCommand command, CancellationToken ct = default) => _inner.SendCommandAsync(command, ct);

    public async ValueTask DisposeAsync()
    {
        await FlushAsync().ConfigureAwait(false);
        _inner.ReadingReceived -= OnReading;
        await _inner.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Time-series export and import: CSV (RFC 4180, ISO-8601 UTC) and Apache Parquet (ParquetSharp, Snappy) for pandas, DuckDB, Spark and Power BI.
/// Parquet columns: <c>timestamp_utc</c> (timestamp, UTC), <c>pond_id</c>, <c>device_id</c>, <c>kind</c>, <c>value</c> (double), <c>unit</c>.
/// </summary>
public static class TimeSeriesExport
{
    private static Column[] Schema() =>
    [
        new Column<DateTime>("timestamp_utc", LogicalType.Timestamp(isAdjustedToUtc: true, TimeUnit.Micros)),
        new Column<string>("pond_id"), new Column<string>("device_id"), new Column<string>("kind"),
        new Column<double>("value"), new Column<string>("unit"),
    ];

    /// <summary>Writes readings as one Parquet row group per 100 000 rows. The stream is left open.</summary>
    public static Task WriteParquetAsync(IEnumerable<SensorReading> readings, Stream output, CancellationToken ct = default)
    {
        using var managed = new ManagedOutputStream(output, leaveOpen: true);
        using var props = new WriterPropertiesBuilder().Compression(Compression.Snappy).CreatedBy("AquaCulture.Net").Build();
        using var file = new ParquetFileWriter(managed, Schema(), props);
        foreach (var chunk in readings.Chunk(100_000))
        {
            ct.ThrowIfCancellationRequested();
            using var rg = file.AppendRowGroup();
            using (var w = rg.NextColumn().LogicalWriter<DateTime>()) w.WriteBatch(chunk.Select(r => DateTime.SpecifyKind(r.Timestamp.ToUniversalTime(), DateTimeKind.Utc)).ToArray());
            using (var w = rg.NextColumn().LogicalWriter<string>()) w.WriteBatch(chunk.Select(r => r.PondId).ToArray());
            using (var w = rg.NextColumn().LogicalWriter<string>()) w.WriteBatch(chunk.Select(r => r.DeviceId).ToArray());
            using (var w = rg.NextColumn().LogicalWriter<string>()) w.WriteBatch(chunk.Select(r => r.Kind.ToString()).ToArray());
            using (var w = rg.NextColumn().LogicalWriter<double>()) w.WriteBatch(chunk.Select(r => r.Value).ToArray());
            using (var w = rg.NextColumn().LogicalWriter<string>()) w.WriteBatch(chunk.Select(r => r.Unit).ToArray());
            rg.Close();
        }
        file.Close();
        return Task.CompletedTask;
    }

    /// <summary>Reads a Parquet file written by <see cref="WriteParquetAsync"/> (or any file with the same column names).</summary>
    public static Task<IReadOnlyList<SensorReading>> ReadParquetAsync(Stream input, CancellationToken ct = default)
    {
        using var managed = new ManagedRandomAccessFile(input, leaveOpen: true);
        using var file = new ParquetFileReader(managed);
        var schema = file.FileMetaData.Schema;
        int Col(string name) { int i = schema.ColumnIndex(name); return i >= 0 ? i : throw new FormatException($"Parquet column '{name}' not found."); }
        int cTs = Col("timestamp_utc"), cPond = Col("pond_id"), cDev = Col("device_id"), cKind = Col("kind"), cVal = Col("value"), cUnit = Col("unit");
        var list = new List<SensorReading>();
        for (int g = 0; g < file.FileMetaData.NumRowGroups; g++)
        {
            ct.ThrowIfCancellationRequested();
            using var rg = file.RowGroup(g);
            int n = checked((int)rg.MetaData.NumRows);
            T[] Read<T>(int c) { using var r = rg.Column(c).LogicalReader<T>(); return r.ReadAll(n); }
            var ts = Read<DateTime>(cTs); var pond = Read<string>(cPond); var dev = Read<string>(cDev); var kind = Read<string>(cKind);
            var val = Read<double>(cVal); var unit = Read<string>(cUnit);
            for (int i = 0; i < n; i++)
                list.Add(new SensorReading(dev[i], pond[i], Enum.Parse<SensorKind>(kind[i]), val[i], DateTime.SpecifyKind(ts[i], DateTimeKind.Utc), unit[i]));
        }
        file.Close();
        return Task.FromResult<IReadOnlyList<SensorReading>>(list);
    }

    public static string WriteCsv(IEnumerable<SensorReading> readings)
    {
        var sb = new StringBuilder("timestamp_utc,pond_id,device_id,kind,value,unit\r\n");
        static string Q(string s) => s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        foreach (var r in readings)
            sb.Append(r.Timestamp.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',').Append(Q(r.PondId)).Append(',')
              .Append(Q(r.DeviceId)).Append(',').Append(r.Kind).Append(',').Append(r.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(Q(r.Unit)).Append("\r\n");
        return sb.ToString();
    }

    public static IReadOnlyList<SensorReading> ReadCsv(string csv)
    {
        var list = new List<SensorReading>();
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var t = line.TrimEnd('\r');
            if (t.Length == 0) continue;
            var f = SplitCsv(t);
            var kind = Enum.Parse<SensorKind>(f[3]);
            list.Add(new SensorReading(f[2], f[1], kind, double.Parse(f[4], CultureInfo.InvariantCulture),
                DateTime.Parse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), f.Count > 5 ? f[5] : SensorReading.DefaultUnit(kind)));
        }
        return list;
    }

    private static List<string> SplitCsv(string line)
    {
        var o = new List<string>(); var cur = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else if (c == '"') q = false; else cur.Append(c); }
            else if (c == '"') q = true;
            else if (c == ',') { o.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        o.Add(cur.ToString());
        return o;
    }
}

public static class DataBuilderExtensions
{
    /// <summary>Registers an <see cref="AquaStore"/> on a SQLite file.</summary>
    public static AquaBuilder AddSqliteStore(this AquaBuilder b, string path) => b.TryAddModule("Data", s => s.TryAddSingleton(_ => AquaStore.OpenSqlite(path)));
}
