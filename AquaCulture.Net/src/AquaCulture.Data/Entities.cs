using Microsoft.EntityFrameworkCore;

namespace AquaCulture.Data;

// Persistence models: kept separate from the domain types so the domain stays free of EF Core.

public sealed class FarmRow
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double ElevationM { get; set; }
    public string Owner { get; set; } = "";
}

public sealed class PondRow
{
    public string FarmId { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string System { get; set; } = "";
    public double AreaM2 { get; set; }
    public double DepthM { get; set; }
    public double AerationKw { get; set; }
    public double WaterExchangePctPerDay { get; set; }
    /// <summary>Outline as a GeoJSON polygon geometry (WGS-84), or null.</summary>
    public string? OutlineGeoJson { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
}

public sealed class CycleRow
{
    public string FarmId { get; set; } = "";
    public string Id { get; set; } = "";
    public string PondId { get; set; } = "";
    public string SpeciesId { get; set; } = "";
    public DateOnly StockingDate { get; set; }
    public long StockedCount { get; set; }
    public double StockingWeightG { get; set; }
    public double SeedPricePerUnit { get; set; }
    public DateOnly? ClosedDate { get; set; }
}

/// <summary>One logged event of a culture cycle (sampling, feed, mortality, harvest or expense).</summary>
public sealed class CycleEventRow
{
    public long Id { get; set; }
    public string FarmId { get; set; } = "";
    public string CycleId { get; set; } = "";
    public CycleEventKind Kind { get; set; }
    public DateOnly Date { get; set; }
    /// <summary>ABW g (sampling), kg (feed, harvest), count (mortality) or amount (expense).</summary>
    public double Value { get; set; }
    /// <summary>Sample size (sampling) or harvested count (harvest).</summary>
    public long Count { get; set; }
    /// <summary>Feed or sale price per kg.</summary>
    public double Price { get; set; }
    /// <summary>Feed id, mortality cause or expense category.</summary>
    public string Text { get; set; } = "";
    public string Note { get; set; } = "";
    public bool Flag { get; set; }
    public double? Extra { get; set; }
}

public enum CycleEventKind { Sampling, Feed, Mortality, Harvest, Expense }

public sealed class ReadingRow
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = "";
    public string PondId { get; set; } = "";
    public int Kind { get; set; }
    public double Value { get; set; }
    public DateTime TimestampUtc { get; set; }
    public byte Quality { get; set; } = 100;
}

public sealed class DecisionRow
{
    public long Id { get; set; }
    public string PondId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Severity { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string TitleId { get; set; } = "";
    public string RationaleEn { get; set; } = "";
    public string RationaleId { get; set; } = "";
    public string? CommandJson { get; set; }
    public DateTime TimestampUtc { get; set; }
}

/// <summary>Sync state of an edge gateway: the highest sequence number stored (watermark) and its pushed configuration.</summary>
public sealed class GatewaySyncRow
{
    public string GatewayId { get; set; } = "";
    public long AckedSeq { get; set; }
    public long Received { get; set; }
    public long Duplicates { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public string? ConfigJson { get; set; }
    public int ConfigVersion { get; set; }
}

/// <summary>EF Core context for AquaCulture.Net (SQLite by default; any EF Core provider works).</summary>
public sealed class AquaDbContext(DbContextOptions<AquaDbContext> options) : DbContext(options)
{
    public DbSet<FarmRow> Farms => Set<FarmRow>();
    public DbSet<PondRow> Ponds => Set<PondRow>();
    public DbSet<CycleRow> Cycles => Set<CycleRow>();
    public DbSet<CycleEventRow> CycleEvents => Set<CycleEventRow>();
    public DbSet<ReadingRow> Readings => Set<ReadingRow>();
    public DbSet<DecisionRow> Decisions => Set<DecisionRow>();
    public DbSet<GatewaySyncRow> Gateways => Set<GatewaySyncRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FarmRow>(e => { e.ToTable("farms"); e.HasKey(x => x.Id); });
        b.Entity<PondRow>(e => { e.ToTable("ponds"); e.HasKey(x => new { x.FarmId, x.Id }); });
        b.Entity<CycleRow>(e => { e.ToTable("cycles"); e.HasKey(x => new { x.FarmId, x.Id }); e.HasIndex(x => new { x.FarmId, x.PondId }); });
        b.Entity<CycleEventRow>(e => { e.ToTable("cycle_events"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.FarmId, x.CycleId, x.Kind, x.Date }); });
        b.Entity<ReadingRow>(e => { e.ToTable("readings"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.PondId, x.Kind, x.TimestampUtc }); });
        b.Entity<DecisionRow>(e => { e.ToTable("decisions"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.PondId, x.TimestampUtc }); });
        b.Entity<GatewaySyncRow>(e => { e.ToTable("sync_gateways"); e.HasKey(x => x.GatewayId); });
    }
}
