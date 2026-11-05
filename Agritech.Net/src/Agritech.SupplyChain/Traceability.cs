using System.Security.Cryptography;
using System.Text;
using Agritech.Core.Geo;
using Agritech.Core.Hosting;

namespace Agritech.SupplyChain;

public enum TraceEventType { Harvested, Received, Processed, Packed, Stored, Shipped, Delivered, Sold, Recalled }

/// <summary>Tamper-evident supply-chain event: each event hash chains the previous one (SHA-256).</summary>
public sealed record TraceEvent(string LotId, TraceEventType Type, DateTimeOffset Time, string Actor, string Location, string? Notes, string PreviousHash, string Hash);

/// <summary>A traceable lot / batch of product.</summary>
public sealed class Lot(string id, string product, double quantityKg, string originFarm, DateOnly harvestDate)
{
    public string Id { get; } = id;
    public string Product { get; } = product;
    public double QuantityKg { get; set; } = quantityKg;
    public string OriginFarm { get; } = originFarm;
    public DateOnly HarvestDate { get; } = harvestDate;
    public GeoCoordinate? OriginLocation { get; init; }
    public string? Grade { get; set; }
    public List<string> ParentLots { get; } = [];
    public List<TraceEvent> Events { get; } = [];
    public bool Recalled => Events.Any(e => e.Type == TraceEventType.Recalled);
}

/// <summary>
/// Farm-to-fork traceability ledger with lot genealogy (split/merge), hash-chained events and recall tracing
/// (one-step-back / one-step-forward, as required by food-safety regulations).
/// </summary>
public sealed class TraceabilityLedger
{
    private readonly Dictionary<string, Lot> _lots = new(StringComparer.Ordinal);
    private int _seq;

    public IReadOnlyCollection<Lot> Lots => _lots.Values;
    public Lot this[string id] => _lots[id];

    public Lot Harvest(string product, double kg, string farm, DateOnly date, string actor, GeoCoordinate? location = null)
    {
        var lot = new Lot(NewId(product), product, kg, farm, date) { OriginLocation = location };
        _lots[lot.Id] = lot;
        Record(lot.Id, TraceEventType.Harvested, actor, location?.ToString() ?? farm, $"{kg} kg harvested");
        return lot;
    }

    public TraceEvent Record(string lotId, TraceEventType type, string actor, string location, string? notes = null, DateTimeOffset? time = null)
    {
        var lot = _lots[lotId];
        string prev = lot.Events.Count == 0 ? new string('0', 64) : lot.Events[^1].Hash;
        var t = time ?? DateTimeOffset.UtcNow;
        string hash = ComputeHash(lotId, type, t, actor, location, notes, prev);
        var ev = new TraceEvent(lotId, type, t, actor, location, notes, prev, hash);
        lot.Events.Add(ev);
        return ev;
    }

    /// <summary>Splits a lot into child lots (e.g. grading/packing).</summary>
    public IReadOnlyList<Lot> Split(string lotId, string actor, params (double Kg, string Grade)[] parts)
    {
        var parent = _lots[lotId];
        if (parts.Sum(p => p.Kg) > parent.QuantityKg + 1e-9) throw new InvalidOperationException("Split exceeds lot quantity.");
        var children = parts.Select(p =>
        {
            var c = new Lot(NewId(parent.Product), parent.Product, p.Kg, parent.OriginFarm, parent.HarvestDate) { OriginLocation = parent.OriginLocation, Grade = p.Grade };
            c.ParentLots.Add(parent.Id);
            _lots[c.Id] = c;
            Record(c.Id, TraceEventType.Packed, actor, "packhouse", $"Split from {parent.Id}, grade {p.Grade}");
            return c;
        }).ToList();
        parent.QuantityKg -= parts.Sum(p => p.Kg);
        Record(lotId, TraceEventType.Processed, actor, "packhouse", $"Split into {string.Join(", ", children.Select(c => c.Id))}");
        return children;
    }

    /// <summary>Merges lots (e.g. aggregation at a collector).</summary>
    public Lot Merge(IReadOnlyList<string> lotIds, string actor, string location)
    {
        var src = lotIds.Select(i => _lots[i]).ToList();
        var m = new Lot(NewId(src[0].Product), src[0].Product, src.Sum(s => s.QuantityKg), string.Join("+", src.Select(s => s.OriginFarm).Distinct()), src.Min(s => s.HarvestDate));
        m.ParentLots.AddRange(lotIds);
        _lots[m.Id] = m;
        foreach (var s in src) { Record(s.Id, TraceEventType.Processed, actor, location, $"Merged into {m.Id}"); s.QuantityKg = 0; }
        Record(m.Id, TraceEventType.Received, actor, location, $"Merged from {string.Join(", ", lotIds)}");
        return m;
    }

    /// <summary>All ancestor lots (trace back to farms).</summary>
    public IReadOnlyList<Lot> TraceBack(string lotId)
    {
        var res = new List<Lot>(); var stack = new Stack<string>([lotId]); var seen = new HashSet<string>();
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id)) continue;
            var l = _lots[id];
            if (id != lotId) res.Add(l);
            foreach (var p in l.ParentLots) stack.Push(p);
        }
        return res;
    }

    /// <summary>All descendant lots (trace forward for recalls).</summary>
    public IReadOnlyList<Lot> TraceForward(string lotId)
    {
        var res = new List<Lot>(); var q = new Queue<string>([lotId]); var seen = new HashSet<string> { lotId };
        while (q.Count > 0)
        {
            var id = q.Dequeue();
            foreach (var c in _lots.Values.Where(l => l.ParentLots.Contains(id)))
                if (seen.Add(c.Id)) { res.Add(c); q.Enqueue(c.Id); }
        }
        return res;
    }

    /// <summary>Recalls a lot and every descendant; returns affected lots.</summary>
    public IReadOnlyList<Lot> Recall(string lotId, string reason, string actor)
    {
        var affected = new List<Lot> { _lots[lotId] };
        affected.AddRange(TraceForward(lotId));
        foreach (var l in affected) Record(l.Id, TraceEventType.Recalled, actor, "-", reason);
        return affected;
    }

    /// <summary>Verifies the hash chain of every lot; returns ids of tampered lots.</summary>
    public IReadOnlyList<string> Verify()
    {
        var bad = new List<string>();
        foreach (var l in _lots.Values)
        {
            string prev = new('0', 64);
            foreach (var e in l.Events)
            {
                if (e.PreviousHash != prev || e.Hash != ComputeHash(e.LotId, e.Type, e.Time, e.Actor, e.Location, e.Notes, e.PreviousHash)) { bad.Add(l.Id); break; }
                prev = e.Hash;
            }
        }
        return bad;
    }

    private static string ComputeHash(string lot, TraceEventType type, DateTimeOffset t, string actor, string loc, string? notes, string prev) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{lot}|{type}|{t.ToUnixTimeMilliseconds()}|{actor}|{loc}|{notes}|{prev}")));

    private string NewId(string product) => $"LOT-{product.ToUpperInvariant()[..Math.Min(3, product.Length)]}-{DateTime.UtcNow:yyMMdd}-{Interlocked.Increment(ref _seq):0000}";
}

/// <summary>GS1 identifier helpers (GTIN / SSCC check digits).</summary>
public static class Gs1
{
    /// <summary>Computes the GS1 mod-10 check digit for the given digits (without check digit).</summary>
    public static int CheckDigit(string digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            if (d is < 0 or > 9) throw new FormatException("Digits only.");
            sum += d * (i % 2 == 0 ? 3 : 1);
        }
        return (10 - sum % 10) % 10;
    }

    public static bool IsValid(string code) => code.Length > 1 && CheckDigit(code[..^1]) == code[^1] - '0';

    /// <summary>Builds an 18-digit SSCC for a pallet/shipping unit.</summary>
    public static string Sscc(char extension, string companyPrefix, long serial)
    {
        string body = $"{extension}{companyPrefix}{serial.ToString().PadLeft(16 - companyPrefix.Length, '0')}";
        if (body.Length != 17) throw new ArgumentException("Company prefix + serial must produce 17 digits.");
        return body + CheckDigit(body);
    }
}
