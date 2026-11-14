using Electrical.Core;

namespace Electrical.Cli;

public static partial class Commands
{
    public static Task<int> Help()
    {
        Console.WriteLine($"""
            {ElectricalInfo.Name} CLI {ElectricalInfo.Version} — {ElectricalInfo.Tagline}
            {ElectricalInfo.CreditEn} · {ElectricalInfo.CreditId}

            Usage: eln <command> [options]
            Network input (most commands): --case case9|case14|case33bw|synthetic<N>  or  --file grid.m|.raw|.cdf|.json|.xml

              info                                    Framework, modules and native kernel status
              cases                                   Built-in test networks
              pf [--method nr|fd|gs|dc|bfs] [--scale 1.2] [--qlim] [--json] [--out sld.svg]   Power flow
              n1 [--top 15]                           N-1 branch contingency ranking
              plf [--samples 500] [--std 0.1] [--seed 2026]                                   Probabilistic (Monte-Carlo) load flow
              bench [--sizes 100,1000,3000]           Power-flow scaling benchmark on synthetic grids
              sld --out sld.svg [--literal --dark]    Single-line diagram with power-flow overlay
              geojson --out grid.geojson [--svg map.svg] [--lat -6.2 --lon 106.8]            GIS export
              convert --out grid.m|.json|.xml|.raw [--version 33|35]   MATPOWER / JSON / CIM / PSS/E RAW conversion
              rtu [--port 1502] [--seconds 0]         Modbus TCP RTU simulator serving power-flow measurements
              serve [--urls http://localhost:5300]    REST API (Electrical.Cloud)
            {ExtraHelp()}
            """);
        return Ok();
    }

    static string ExtraHelp()
    {
        string s = "";
        MoreHelp(ref s);
        return s;
    }

    static partial void MoreHelp(ref string text);
}
