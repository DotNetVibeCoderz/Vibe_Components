using Electrical.AI;
using Electrical.DigitalTwin;

namespace Electrical.Cli;

public static partial class Extra
{
    static partial void DispatchIntelligence(Args cli, ref Task<int>? result)
    {
        result ??= cli.Command switch
        {
            "ask" => Intelligence.AskAsync(cli),
            "twin" => Intelligence.Twin(cli),
            _ => null,
        };
        DispatchDistribution(cli, ref result);
    }

    static partial void DispatchDistribution(Args cli, ref Task<int>? result);
}

public static partial class Commands
{
    static partial void MoreHelpIntelligence(ref string text)
    {
        text += """

              ask "Run a power flow on case14 at 120% load" | "Berapa arus hubung singkat di bus 4?"   AI grid agent
                                                      (LLM via ELECTRICAL_LLM_PROVIDER/_ENDPOINT/_API_KEY/_MODEL, offline otherwise)
              twin [--steps 24] [--lang en|id]        Digital twin: SCADA → state estimation → explained alarms → asset health
            """;
        MoreHelpDistribution(ref text);
    }

    static partial void MoreHelpDistribution(ref string text);
}

internal static class Intelligence
{
    public static async Task<int> AskAsync(Args cli)
    {
        var question = string.Join(' ', cli.Positional);
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Usage: eln ask \"<question>\"");
        var agent = new GridAgent(new GridAgentContext { CaseName = cli.Get("case", "case14") });
        var a = await agent.AskAsync(question);
        Console.WriteLine(a.Text);
        Console.WriteLine();
        Console.WriteLine($"[model: {a.Model} · tools: {(a.ToolsUsed.Count == 0 ? "-" : string.Join(", ", a.ToolsUsed))} · language: {a.Language}]");
        return 0;
    }

    public static Task<int> Twin(Args cli)
    {
        var net = cli.Network();
        string lang = cli.Get("lang", "en");
        var sim = new TwinSimulator(net, new TwinSimulatorOptions { Steps = cli.Int("steps", 24) });
        var r = sim.Run();
        Console.WriteLine($"Digital twin of {net.Name}: {r.States.Count} cycles, state-estimation voltage error mean {r.MeanVoltageErrorPu:0.00000} pu, max {r.MaxVoltageErrorPu:0.00000} pu");
        Console.WriteLine(sim.Twin.Summary(lang));
        Console.WriteLine(lang == "id" ? "Alarm yang dijelaskan:" : "Explained alarms:");
        foreach (var a in r.RaisedAlarms.Take(cli.Int("rows", 8)))
            Console.WriteLine($"  [{a.Severity}] {(lang == "id" ? a.MessageId : a.Message)}");
        Console.WriteLine(lang == "id" ? "Kesehatan aset (terburuk):" : "Asset health (worst):");
        foreach (var h in r.AssetHealth.OrderBy(h => h.HealthIndex).Take(5)) Console.WriteLine($"  {h}");
        return Task.FromResult(0);
    }
}
