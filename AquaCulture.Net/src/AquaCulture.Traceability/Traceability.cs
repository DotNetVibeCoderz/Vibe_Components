using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Hosting;

namespace AquaCulture.Traceability;

/// <summary>GS1 identifiers (GTIN, GLN, SSCC) with check digits and their GS1 Digital Link URIs.</summary>
public static class Gs1
{
    /// <summary>GS1 mod-10 check digit for the digits given (without the check digit).</summary>
    public static int CheckDigit(string digits)
    {
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit)) throw new ArgumentException("Digits only.", nameof(digits));
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }
        return (10 - sum % 10) % 10;
    }

    public static bool IsValid(string code) => code.Length >= 2 && code.All(char.IsAsciiDigit) && CheckDigit(code[..^1]) == code[^1] - '0';

    /// <summary>Completes a 13-digit GTIN body (or a 12-digit GLN body, 17-digit SSCC body) with its check digit.</summary>
    public static string WithCheckDigit(string body) => body + CheckDigit(body).ToString(CultureInfo.InvariantCulture);

    /// <summary>GTIN-14 (left-padded with zeros) for Digital Link URIs.</summary>
    public static string Gtin14(string gtin)
    {
        if (!IsValid(gtin) || gtin.Length > 14) throw new ArgumentException($"Invalid GTIN {gtin}.", nameof(gtin));
        return gtin.PadLeft(14, '0');
    }

    public const string Resolver = "https://id.gs1.org";

    /// <summary>A product class and lot (LGTIN): https://id.gs1.org/01/{gtin14}/10/{lot}.</summary>
    public static string Lot(string gtin, string lot) => $"{Resolver}/01/{Gtin14(gtin)}/10/{Uri.EscapeDataString(lot)}";

    /// <summary>A location: https://id.gs1.org/414/{gln}[/254/{extension}].</summary>
    public static string Location(string gln, string? extension = null)
    {
        if (!IsValid(gln) || gln.Length != 13) throw new ArgumentException($"Invalid GLN {gln}.", nameof(gln));
        return extension is { Length: > 0 } ? $"{Resolver}/414/{gln}/254/{Uri.EscapeDataString(extension)}" : $"{Resolver}/414/{gln}";
    }

    /// <summary>A logistic unit (pallet, tote): https://id.gs1.org/00/{sscc}.</summary>
    public static string Sscc(string sscc)
    {
        if (!IsValid(sscc) || sscc.Length != 18) throw new ArgumentException($"Invalid SSCC {sscc}.", nameof(sscc));
        return $"{Resolver}/00/{sscc}";
    }

    /// <summary>Builds an SSCC from an extension digit, a GS1 company prefix and a serial reference.</summary>
    public static string MakeSscc(int extension, string companyPrefix, long serial)
    {
        string body = extension.ToString(CultureInfo.InvariantCulture) + companyPrefix + serial.ToString(CultureInfo.InvariantCulture).PadLeft(16 - companyPrefix.Length, '0');
        return WithCheckDigit(body);
    }
}

public enum EpcisEventType { ObjectEvent, AggregationEvent, TransformationEvent }
public enum EpcisAction { ADD, OBSERVE, DELETE }

/// <summary>A quantity of a class-level identifier (a lot), as EPCIS quantityList entries carry it.</summary>
public sealed record QuantityElement(string EpcClass, double Quantity, string Uom = "KGM");

/// <summary>
/// An EPCIS 2.0 event. Only the fields aquaculture traceability needs are modelled: what (EPCs, quantities, parent, inputs/outputs),
/// when, where (read point, business location), why (business step, disposition) and lot master data (ILMD).
/// </summary>
public sealed record EpcisEvent
{
    public required EpcisEventType Type { get; init; }
    public required DateTimeOffset EventTime { get; init; }
    public EpcisAction? Action { get; init; }
    public string? BizStep { get; init; }
    public string? Disposition { get; init; }
    public string? ReadPoint { get; init; }
    public string? BizLocation { get; init; }
    public string? ParentId { get; init; }
    public IReadOnlyList<string> EpcList { get; init; } = [];
    public IReadOnlyList<QuantityElement> Quantities { get; init; } = [];
    public IReadOnlyList<QuantityElement> Inputs { get; init; } = [];
    public IReadOnlyList<QuantityElement> Outputs { get; init; } = [];
    /// <summary>Instance/lot master data (species, production method, pond, harvest date…), as "cbvmda:" or custom keys.</summary>
    public IReadOnlyDictionary<string, string> Ilmd { get; init; } = new Dictionary<string, string>();
    public string? EventId { get; init; }
}

/// <summary>EPCIS 2.0 JSON-LD documents (GS1 EPCIS / CBV 2.0 context) — write and read.</summary>
public static class EpcisJson
{
    public const string Context = "https://ref.gs1.org/standards/epcis/2.0.0/epcis-context.jsonld";
    private const string Cbv = "https://ref.gs1.org/cbv/";

    public static string BizStep(string name) => $"{Cbv}BizStep-{name}";
    public static string Disposition(string name) => $"{Cbv}Disp-{name}";

    public static string ToDocument(IEnumerable<EpcisEvent> events, DateTimeOffset? created = null)
    {
        var list = new JsonArray();
        foreach (var e in events) list.Add((JsonNode)ToJson(e));
        var doc = new JsonObject
        {
            ["@context"] = new JsonArray(Context, new JsonObject { ["aqua"] = "https://aquaculture-net.dev/epcis/" }),
            ["type"] = "EPCISDocument",
            ["schemaVersion"] = "2.0",
            ["creationDate"] = (created ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            ["epcisBody"] = new JsonObject { ["eventList"] = list },
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonArray Q(IEnumerable<QuantityElement> q) =>
        new(q.Select(x => (JsonNode)new JsonObject { ["epcClass"] = x.EpcClass, ["quantity"] = x.Quantity, ["uom"] = x.Uom }).ToArray());

    private static JsonObject ToJson(EpcisEvent e)
    {
        var o = new JsonObject
        {
            ["type"] = e.Type.ToString(),
            ["eventTime"] = e.EventTime.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture),
            ["eventTimeZoneOffset"] = e.EventTime.ToString("zzz", CultureInfo.InvariantCulture),
        };
        if (e.EventId is not null) o["eventID"] = e.EventId;
        if (e.ParentId is not null) o["parentID"] = e.ParentId;
        if (e.EpcList.Count > 0) o[e.Type == EpcisEventType.AggregationEvent ? "childEPCs" : "epcList"] = new JsonArray(e.EpcList.Select(x => (JsonNode)x!).ToArray());
        if (e.Quantities.Count > 0) o[e.Type == EpcisEventType.AggregationEvent ? "childQuantityList" : "quantityList"] = Q(e.Quantities);
        if (e.Inputs.Count > 0) o["inputQuantityList"] = Q(e.Inputs);
        if (e.Outputs.Count > 0) o["outputQuantityList"] = Q(e.Outputs);
        if (e.Action is { } a) o["action"] = a.ToString();
        if (e.BizStep is not null) o["bizStep"] = e.BizStep;
        if (e.Disposition is not null) o["disposition"] = e.Disposition;
        if (e.ReadPoint is not null) o["readPoint"] = new JsonObject { ["id"] = e.ReadPoint };
        if (e.BizLocation is not null) o["bizLocation"] = new JsonObject { ["id"] = e.BizLocation };
        if (e.Ilmd.Count > 0)
        {
            var ilmd = new JsonObject();
            foreach (var (k, v) in e.Ilmd) ilmd[k] = v;
            o["ilmd"] = ilmd;
        }
        return o;
    }

    public static IReadOnlyList<EpcisEvent> ParseDocument(string json)
    {
        var root = JsonNode.Parse(json) ?? throw new FormatException("Empty document.");
        if (root["type"]?.GetValue<string>() != "EPCISDocument") throw new FormatException("Not an EPCISDocument.");
        var list = root["epcisBody"]?["eventList"] as JsonArray ?? throw new FormatException("No epcisBody.eventList.");
        return list.OfType<JsonObject>().Select(Parse).ToList();
    }

    private static IReadOnlyList<QuantityElement> PQ(JsonNode? n) => n is JsonArray a
        ? a.OfType<JsonObject>().Select(x => new QuantityElement(x["epcClass"]!.GetValue<string>(), x["quantity"]?.GetValue<double>() ?? 0, x["uom"]?.GetValue<string>() ?? "")).ToList()
        : [];

    private static EpcisEvent Parse(JsonObject o)
    {
        var type = Enum.Parse<EpcisEventType>(o["type"]!.GetValue<string>());
        var epcs = (o["epcList"] ?? o["childEPCs"]) is JsonArray a ? a.Select(x => x!.GetValue<string>()).ToList() : [];
        var ilmd = o["ilmd"] is JsonObject im ? im.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "") : new Dictionary<string, string>();
        return new EpcisEvent
        {
            Type = type,
            EventTime = DateTimeOffset.Parse(o["eventTime"]!.GetValue<string>(), CultureInfo.InvariantCulture),
            EventId = o["eventID"]?.GetValue<string>(),
            Action = o["action"]?.GetValue<string>() is { } act ? Enum.Parse<EpcisAction>(act) : null,
            BizStep = o["bizStep"]?.GetValue<string>(), Disposition = o["disposition"]?.GetValue<string>(),
            ReadPoint = o["readPoint"]?["id"]?.GetValue<string>(), BizLocation = o["bizLocation"]?["id"]?.GetValue<string>(),
            ParentId = o["parentID"]?.GetValue<string>(), EpcList = epcs,
            Quantities = PQ(o["quantityList"] ?? o["childQuantityList"]), Inputs = PQ(o["inputQuantityList"]), Outputs = PQ(o["outputQuantityList"]),
            Ilmd = ilmd,
        };
    }
}

/// <summary>Product codes and locations of a farm for traceability.</summary>
public sealed record TraceabilitySetup(string FarmGln, string HatcheryGln, string SeedGtin, string HarvestGtin, string CompanyPrefix)
{
    /// <summary>Feed id → (GTIN, lot) of the feed product; feeds without a mapping use "feed id" as a lot of <see cref="DefaultFeedGtin"/>.</summary>
    public IReadOnlyDictionary<string, (string Gtin, string Lot)> FeedLots { get; init; } = new Dictionary<string, (string, string)>();
    public string? DefaultFeedGtin { get; init; }
}

/// <summary>Turns farm records into EPCIS events.</summary>
public static class FarmEpcis
{
    /// <summary>Harvest lot number of a cycle: farm, pond and harvest date.</summary>
    public static string HarvestLot(string farmId, CultureCycle c, DateOnly date) => $"{farmId}-{c.PondId}-{date:yyyyMMdd}";

    /// <summary>
    /// Events of one culture cycle: hatchery commissioning of the seed lot, shipping and receiving at the pond, and for each harvest a
    /// transformation (seed + feed lots in, harvest lot out, with ILMD) — the GDST farm-harvest key data elements in EPCIS form.
    /// </summary>
    public static IReadOnlyList<EpcisEvent> CycleEvents(Farm farm, CultureCycle c, TraceabilitySetup s, string seedLot)
    {
        var seed = Gs1.Lot(s.SeedGtin, seedLot);
        var hatchery = Gs1.Location(s.HatcheryGln);
        var pond = Gs1.Location(s.FarmGln, c.PondId);
        var t0 = new DateTimeOffset(c.StockingDate.ToDateTime(new TimeOnly(6, 0)), TimeSpan.FromHours(7));
        var list = new List<EpcisEvent>
        {
            new() { Type = EpcisEventType.ObjectEvent, EventTime = t0.AddDays(-1), Action = EpcisAction.ADD, BizStep = EpcisJson.BizStep("commissioning"),
                Disposition = EpcisJson.Disposition("active"), ReadPoint = hatchery, BizLocation = hatchery, Quantities = [new(seed, c.StockedCount, "H87")],
                Ilmd = new Dictionary<string, string> { ["cbvmda:speciesForFisheryStatisticsPurposesName"] = c.Species.ScientificName, ["aqua:stage"] = "PL" } },
            new() { Type = EpcisEventType.ObjectEvent, EventTime = t0.AddHours(-6), Action = EpcisAction.OBSERVE, BizStep = EpcisJson.BizStep("shipping"),
                Disposition = EpcisJson.Disposition("in_transit"), ReadPoint = hatchery, Quantities = [new(seed, c.StockedCount, "H87")] },
            new() { Type = EpcisEventType.ObjectEvent, EventTime = t0, Action = EpcisAction.OBSERVE, BizStep = EpcisJson.BizStep("receiving"),
                Disposition = EpcisJson.Disposition("in_progress"), ReadPoint = pond, BizLocation = pond, Quantities = [new(seed, c.StockedCount, "H87")] },
        };
        string FeedClass(string feedId) => s.FeedLots.TryGetValue(feedId, out var f) ? Gs1.Lot(f.Gtin, f.Lot)
            : s.DefaultFeedGtin is { } g ? Gs1.Lot(g, feedId.Length == 0 ? "unknown" : feedId) : $"urn:aqua:feed:{feedId}";
        var harvests = c.Harvests.OrderBy(h => h.Date).ToList();
        var previous = c.StockingDate.AddDays(-1);
        foreach (var h in harvests)
        {
            var feeds = c.Feedings.Where(f => f.Date > previous && f.Date <= h.Date).GroupBy(f => f.FeedId)
                .Select(g => new QuantityElement(FeedClass(g.Key), Math.Round(g.Sum(f => f.Kg), 2))).ToList();
            var lot = Gs1.Lot(s.HarvestGtin, HarvestLot(farm.Id, c, h.Date));
            list.Add(new EpcisEvent
            {
                Type = EpcisEventType.TransformationEvent, EventTime = new DateTimeOffset(h.Date.ToDateTime(new TimeOnly(5, 0)), TimeSpan.FromHours(7)),
                BizStep = EpcisJson.BizStep("commissioning"), Disposition = EpcisJson.Disposition("active"), ReadPoint = pond, BizLocation = pond,
                Inputs = [new(seed, c.StockedCount, "H87"), .. feeds], Outputs = [new(lot, h.Kg)],
                Ilmd = new Dictionary<string, string>
                {
                    ["cbvmda:speciesForFisheryStatisticsPurposesName"] = c.Species.ScientificName,
                    ["cbvmda:productionMethodForFishAndSeafoodCode"] = "AQUACULTURE",
                    ["cbvmda:harvestStartDate"] = h.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["aqua:pond"] = c.PondId, ["aqua:cycle"] = c.Id, ["aqua:partialHarvest"] = h.Partial ? "true" : "false",
                    ["aqua:countPerKg"] = h.Kg > 0 ? (h.Count / h.Kg).ToString("0", CultureInfo.InvariantCulture) : "",
                },
            });
            previous = h.Date;
        }
        return list;
    }

    /// <summary>Packs harvest lots into a pallet (SSCC) and ships it to a customer location.</summary>
    public static IReadOnlyList<EpcisEvent> PackAndShip(string sscc, IReadOnlyList<QuantityElement> contents, string fromLocation, string toLocation, DateTimeOffset time) =>
    [
        new() { Type = EpcisEventType.AggregationEvent, EventTime = time, Action = EpcisAction.ADD, BizStep = EpcisJson.BizStep("packing"),
            ParentId = Gs1.Sscc(sscc), Quantities = contents, ReadPoint = fromLocation, BizLocation = fromLocation },
        new() { Type = EpcisEventType.ObjectEvent, EventTime = time.AddHours(1), Action = EpcisAction.OBSERVE, BizStep = EpcisJson.BizStep("shipping"),
            Disposition = EpcisJson.Disposition("in_transit"), EpcList = [Gs1.Sscc(sscc)], ReadPoint = fromLocation },
        new() { Type = EpcisEventType.ObjectEvent, EventTime = time.AddHours(8), Action = EpcisAction.OBSERVE, BizStep = EpcisJson.BizStep("receiving"),
            Disposition = EpcisJson.Disposition("in_progress"), EpcList = [Gs1.Sscc(sscc)], ReadPoint = toLocation, BizLocation = toLocation },
    ];
}

/// <summary>
/// The traceability graph of a set of EPCIS events: transformations link input lots to output lots, aggregations link lots to pallets.
/// <see cref="TraceBack"/> walks upstream (where did this lot come from?), <see cref="TraceForward"/> downstream (what does a recall reach?).
/// </summary>
public sealed class TraceGraph
{
    private readonly Dictionary<string, HashSet<string>> _down = [], _up = [];
    private readonly List<EpcisEvent> _events;

    public TraceGraph(IEnumerable<EpcisEvent> events)
    {
        _events = events.OrderBy(e => e.EventTime).ToList();
        foreach (var e in _events)
        {
            if (e.Type == EpcisEventType.TransformationEvent)
                foreach (var i in e.Inputs) foreach (var o in e.Outputs) Link(i.EpcClass, o.EpcClass);
            if (e.Type == EpcisEventType.AggregationEvent && e.ParentId is { } parent && e.Action == EpcisAction.ADD)
            {
                foreach (var q in e.Quantities) Link(q.EpcClass, parent);
                foreach (var c in e.EpcList) Link(c, parent);
            }
        }
    }

    private void Link(string from, string to)
    {
        (_down.TryGetValue(from, out var d) ? d : _down[from] = []).Add(to);
        (_up.TryGetValue(to, out var u) ? u : _up[to] = []).Add(from);
    }

    public IReadOnlyCollection<string> TraceBack(string id) => Walk(id, _up);
    public IReadOnlyCollection<string> TraceForward(string id) => Walk(id, _down);

    private static HashSet<string> Walk(string start, Dictionary<string, HashSet<string>> edges)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([start]);
        while (stack.Count > 0)
            foreach (var n in edges.TryGetValue(stack.Pop(), out var next) ? next : [])
                if (seen.Add(n)) stack.Push(n);
        return seen;
    }

    /// <summary>Events that mention an identifier (as EPC, quantity, input, output or parent), oldest first: the custody history.</summary>
    public IReadOnlyList<EpcisEvent> History(string id) => _events.Where(e =>
        e.EpcList.Contains(id) || e.ParentId == id || e.Quantities.Any(q => q.EpcClass == id) || e.Inputs.Any(q => q.EpcClass == id) || e.Outputs.Any(q => q.EpcClass == id)).ToList();

    /// <summary>Lot master data recorded where a lot was created (the transformation or commissioning event).</summary>
    public IReadOnlyDictionary<string, string> MasterData(string lot) =>
        _events.LastOrDefault(e => e.Outputs.Any(o => o.EpcClass == lot) || e.BizStep?.EndsWith("commissioning", StringComparison.Ordinal) == true && e.Quantities.Any(q => q.EpcClass == lot))?.Ilmd
        ?? new Dictionary<string, string>();
}

public static class TraceabilityBuilderExtensions
{
    public static AquaBuilder AddTraceability(this AquaBuilder b) => b.TryAddModule("Traceability", _ => { });
}
