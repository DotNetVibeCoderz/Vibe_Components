using System.Text.Json;
using System.Text.Json.Nodes;
using Electrical.Core.Network;
using Electrical.PowerFlow;

namespace Electrical.Cloud;

/// <summary>AOT-safe JSON contracts (no reflection) for networks and study results.</summary>
public static class NetworkJson
{
    public static JsonObject ToJson(PowerNetwork net) => new()
    {
        ["name"] = net.Name,
        ["baseMva"] = net.BaseMva,
        ["frequencyHz"] = net.FrequencyHz,
        ["buses"] = new JsonArray(net.Buses.Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.Id, ["name"] = b.Name, ["type"] = b.Type.ToString(), ["baseKv"] = b.BaseKv, ["vm"] = b.Vm, ["vaDeg"] = b.VaDeg,
            ["vmin"] = b.Vmin, ["vmax"] = b.Vmax, ["gs"] = b.Gs, ["bs"] = b.Bs,
            ["lat"] = b.Location?.Latitude, ["lon"] = b.Location?.Longitude,
        }).ToArray()),
        ["loads"] = new JsonArray(net.Loads.Select(l => (JsonNode)new JsonObject
        {
            ["bus"] = l.Bus, ["name"] = l.Name, ["p"] = l.P, ["q"] = l.Q, ["inService"] = l.InService, ["category"] = l.Category,
        }).ToArray()),
        ["generators"] = new JsonArray(net.Generators.Select(g => (JsonNode)new JsonObject
        {
            ["bus"] = g.Bus, ["name"] = g.Name, ["kind"] = g.Kind.ToString(), ["p"] = g.P, ["q"] = g.Q, ["vset"] = g.Vset,
            ["pmin"] = g.Pmin, ["pmax"] = g.Pmax, ["qmin"] = g.Qmin, ["qmax"] = g.Qmax, ["inService"] = g.InService,
            ["c2"] = g.Cost.C2, ["c1"] = g.Cost.C1, ["c0"] = g.Cost.C0,
        }).ToArray()),
        ["branches"] = new JsonArray(net.Branches.Select(r => (JsonNode)new JsonObject
        {
            ["from"] = r.From, ["to"] = r.To, ["name"] = r.Name, ["kind"] = r.Kind.ToString(), ["r"] = r.R, ["x"] = r.X, ["b"] = r.B,
            ["rateA"] = r.RateA, ["tap"] = r.Tap, ["shiftDeg"] = r.ShiftDeg, ["inService"] = r.InService, ["lengthKm"] = r.LengthKm,
        }).ToArray()),
    };

    public static PowerNetwork FromJson(JsonNode node)
    {
        var o = node.AsObject();
        var net = new PowerNetwork
        {
            Name = (string?)o["name"] ?? "network",
            BaseMva = (double?)o["baseMva"] ?? 100,
            FrequencyHz = (double?)o["frequencyHz"] ?? 50,
        };
        foreach (var b in o["buses"]?.AsArray() ?? [])
        {
            var bus = new Bus
            {
                Id = (int)b!["id"]!, Name = (string?)b["name"], Type = Enum.Parse<BusType>((string?)b["type"] ?? "PQ", true),
                BaseKv = (double?)b["baseKv"] ?? 1, Vm = (double?)b["vm"] ?? 1, VaDeg = (double?)b["vaDeg"] ?? 0,
                Vmin = (double?)b["vmin"] ?? 0.9, Vmax = (double?)b["vmax"] ?? 1.1, Gs = (double?)b["gs"] ?? 0, Bs = (double?)b["bs"] ?? 0,
            };
            if (b["lat"] is not null && b["lon"] is not null) bus.Location = new GeoPoint((double)b["lat"]!, (double)b["lon"]!);
            net.Buses.Add(bus);
        }
        foreach (var l in o["loads"]?.AsArray() ?? [])
            net.Loads.Add(new Load
            {
                Bus = (int)l!["bus"]!, Name = (string?)l["name"], P = (double?)l["p"] ?? 0, Q = (double?)l["q"] ?? 0,
                InService = (bool?)l["inService"] ?? true, Category = (string?)l["category"],
            });
        foreach (var g in o["generators"]?.AsArray() ?? [])
            net.Generators.Add(new Generator
            {
                Bus = (int)g!["bus"]!, Name = (string?)g["name"], Kind = Enum.Parse<GeneratorKind>((string?)g["kind"] ?? "Thermal", true),
                P = (double?)g["p"] ?? 0, Q = (double?)g["q"] ?? 0, Vset = (double?)g["vset"] ?? 1, Pmin = (double?)g["pmin"] ?? 0,
                Pmax = (double?)g["pmax"] ?? 9999, Qmin = (double?)g["qmin"] ?? -9999, Qmax = (double?)g["qmax"] ?? 9999,
                InService = (bool?)g["inService"] ?? true,
                Cost = new PolynomialCost((double?)g["c2"] ?? 0, (double?)g["c1"] ?? 0, (double?)g["c0"] ?? 0),
            });
        foreach (var r in o["branches"]?.AsArray() ?? [])
            net.Branches.Add(new Branch
            {
                From = (int)r!["from"]!, To = (int)r["to"]!, Name = (string?)r["name"],
                Kind = Enum.Parse<BranchKind>((string?)r["kind"] ?? "Line", true), R = (double?)r["r"] ?? 0, X = (double?)r["x"] ?? 0,
                B = (double?)r["b"] ?? 0, RateA = (double?)r["rateA"] ?? 0, Tap = (double?)r["tap"] ?? 0, ShiftDeg = (double?)r["shiftDeg"] ?? 0,
                InService = (bool?)r["inService"] ?? true, LengthKm = (double?)r["lengthKm"] ?? 0,
            });
        return net;
    }

    public static PowerNetwork Parse(string json) => FromJson(JsonNode.Parse(json) ?? throw new JsonException("Empty JSON."));

    public static string Serialize(PowerNetwork net, bool indented = false) =>
        ToJson(net).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public static JsonObject ToJson(PowerFlowResult r) => new()
    {
        ["network"] = r.Network.Name,
        ["method"] = r.Method.ToString(),
        ["converged"] = r.Converged,
        ["iterations"] = r.Iterations,
        ["mismatch"] = r.MaxMismatch,
        ["elapsedMs"] = r.Elapsed.TotalMilliseconds,
        ["totalGenerationMw"] = r.TotalGenerationMw,
        ["totalLoadMw"] = r.TotalLoadMw,
        ["lossesMw"] = r.LossesMw,
        ["lossesMvar"] = r.LossesMvar,
        ["buses"] = new JsonArray(r.Buses.Select(b => (JsonNode)new JsonObject
        {
            ["id"] = b.BusId, ["vm"] = b.Vm, ["vaDeg"] = b.VaDeg, ["pGen"] = b.PGen, ["qGen"] = b.QGen, ["pLoad"] = b.PLoad, ["qLoad"] = b.QLoad,
        }).ToArray()),
        ["branches"] = new JsonArray(r.Branches.Select(b => (JsonNode)new JsonObject
        {
            ["from"] = b.From, ["to"] = b.To, ["pFrom"] = b.PFrom, ["qFrom"] = b.QFrom, ["pTo"] = b.PTo, ["qTo"] = b.QTo,
            ["lossMw"] = b.PLoss, ["loadingPercent"] = b.LoadingPercent,
        }).ToArray()),
        ["generators"] = new JsonArray(r.Generators.Select(g => (JsonNode)new JsonObject
        {
            ["bus"] = g.Bus, ["name"] = g.Name, ["p"] = g.P, ["q"] = g.Q, ["atQLimit"] = g.AtQLimit,
        }).ToArray()),
    };

    public static JsonArray ToJson(IEnumerable<ContingencyResult> results) => new(results.Select(c => (JsonNode)new JsonObject
    {
        ["outage"] = c.Outage, ["converged"] = c.Converged, ["islanding"] = c.Islanding, ["maxLoadingPercent"] = c.MaxLoadingPercent,
        ["mostLoadedBranch"] = c.MostLoadedBranch, ["minVoltage"] = c.MinVoltage, ["minVoltageBus"] = c.MinVoltageBus,
        ["overloads"] = c.Overloads, ["voltageViolations"] = c.VoltageViolations,
        ["severity"] = double.IsInfinity(c.Severity) ? null : c.Severity,
    }).ToArray());
}
