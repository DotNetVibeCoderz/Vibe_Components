using System.Text.Json.Nodes;
using Electrical.ShortCircuit;

namespace Electrical.Cloud;

public static partial class StudyCatalog
{
    static partial void RegisterExtra(StudyRunner runner)
    {
        // parameters: { "bus": 4, "type": "SingleLineToGround" }  — without "bus" all buses are scanned
        runner.Register("shortcircuit", (net, p) =>
        {
            if (p["bus"] is JsonNode bus)
            {
                var type = Enum.TryParse<FaultType>((string?)p["type"] ?? "ThreePhase", true, out var t) ? t : FaultType.ThreePhase;
                var r = ShortCircuitAnalysis.Calculate(net, (int)bus, type);
                return new JsonObject
                {
                    ["bus"] = r.BusId, ["type"] = r.Type.ToString(), ["baseKv"] = r.BaseKv, ["ikKa"] = r.IkKa, ["ipKa"] = r.IpKa,
                    ["ibKa"] = r.IbKa, ["ithKa"] = r.IthKa, ["earthCurrentKa"] = r.EarthCurrentKa, ["kappa"] = r.Kappa, ["skMva"] = r.SkMva,
                    ["voltages"] = new JsonArray(r.Voltages.Select(v => (JsonNode)new JsonObject
                    {
                        ["bus"] = v.BusId, ["va"] = v.Va, ["vb"] = v.Vb, ["vc"] = v.Vc,
                    }).ToArray()),
                };
            }
            return new JsonArray(ShortCircuitAnalysis.ScanAllBuses(net).Select(s => (JsonNode)new JsonObject
            {
                ["bus"] = s.BusId, ["baseKv"] = s.BaseKv, ["ik3Ka"] = s.Ik3Ka, ["ik2Ka"] = s.Ik2Ka, ["ik1Ka"] = s.Ik1Ka,
                ["ik2eKa"] = s.Ik2EKa, ["ipKa"] = s.IpKa, ["sk3Mva"] = s.Sk3Mva, ["rx"] = s.RxRatio,
            }).ToArray());
        });
    }
}
