using System.Text.RegularExpressions;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Interop;
using Electrical.PowerFlow;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Electrical.Tests;

public class IeeeCdfAndFormatsTests(ITestOutputHelper output)
{
    private static readonly PowerFlowResult Ref = PowerFlowSolver.Solve(TestCases.Case14());

    [Fact]
    public void Cdf14_ReproducesMatpowerCase14()
    {
        // MATPOWER case14 was derived from the UW archive ieee14cdf.txt; the solved point is identical (losses 13.393 MW).
        var r = IeeeCdf.Read(InteropSamples.Ieee14Cdf);
        var net = r.Network;
        Assert.Equal(NetworkFormat.IeeeCdf, r.Format);
        Assert.Equal("IEEE 14 Bus Test Case", net.Name);
        Assert.Equal(100, net.BaseMva);
        Assert.Equal((14, 11, 5, 20), (net.Buses.Count, net.Loads.Count, net.Generators.Count, net.Branches.Count));
        Assert.Equal("Bus 7     ZV", net.GetBus(7).Name);
        Assert.Equal(19, net.GetBus(9).Bs, 9);              // B = 0.19 pu → 19 Mvar
        Assert.Equal(14, net.GetBus(7).BaseKv);
        Assert.Equal(5, net.Branches.Count(b => b.Kind == BranchKind.Transformer));
        Assert.Equal(1.09, net.Generators.Single(g => g.Bus == 8).Vset);
        Assert.Equal((50.0, -40.0), (net.Generators.Single(g => g.Bus == 2).Qmax, net.Generators.Single(g => g.Bus == 2).Qmin));

        var pf = PowerFlowSolver.Solve(net);
        output.WriteLine(pf.ToString());
        Assert.True(pf.Converged);
        Assert.Equal(13.393, pf.LossesMw, 3);
        Assert.True(Math.Abs(pf.LossesMw - Ref.LossesMw) < 1e-6);
        foreach (var b in Ref.Buses) Assert.Equal(b.Vm, pf.Bus(b.BusId).Vm, 9);
    }

    [Fact]
    public void Cdf_BlankSeparatedVariantParsesTheSame()
    {
        // Collapse the fixed columns: the reader falls back to blank-separated fields.
        var squeezed = string.Join('\n', InteropSamples.Ieee14Cdf.Split('\n').Select(l => Regex.Replace(l.TrimEnd('\r'), " {2,}", " ")));
        var a = IeeeCdf.Read(InteropSamples.Ieee14Cdf).Network;
        var b = IeeeCdf.Read(squeezed).Network;
        Assert.Equal(a.Buses.Select(x => (x.Id, x.Vm, x.Bs, x.Type)), b.Buses.Select(x => (x.Id, x.Vm, x.Bs, x.Type)));
        Assert.Equal(a.Branches.Select(x => (x.From, x.To, x.R, x.X, x.B, x.Tap)), b.Branches.Select(x => (x.From, x.To, x.R, x.X, x.B, x.Tap)));
        Assert.Equal(PowerFlowSolver.Solve(a).LossesMw, PowerFlowSolver.Solve(b).LossesMw, 9);
        Assert.Throws<FormatException>(() => IeeeCdf.Parse("no cdf here"));
    }

    [Fact]
    public void Detect_RecognisesEachFormat()
    {
        Assert.Equal(NetworkFormat.Matpower, NetworkFormats.Detect(Matpower.Write(TestCases.Case14())));
        Assert.Equal(NetworkFormat.PsseRaw, NetworkFormats.Detect(InteropSamples.Ieee14RawV33));
        Assert.Equal(NetworkFormat.PsseRaw, NetworkFormats.Detect(InteropSamples.Ieee14RawV35));
        Assert.Equal(NetworkFormat.PsseRaw, NetworkFormats.Detect(PsseRaw.Write(TestCases.Case9(), 35)));
        Assert.Equal(NetworkFormat.IeeeCdf, NetworkFormats.Detect(InteropSamples.Ieee14Cdf));
        Assert.Equal(NetworkFormat.CloudJson, NetworkFormats.Detect(Electrical.Cloud.NetworkJson.Serialize(TestCases.Case9(), indented: true)));
        Assert.Equal(NetworkFormat.Unknown, NetworkFormats.Detect("lorem ipsum\n1 2 3"));
        Assert.Throws<FormatException>(() => NetworkFormats.Parse("lorem ipsum"));
    }

    [Fact]
    public void Load_AutoDetectsFilesAndGivesSamePowerFlow()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eln-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new (string Name, string Text, NetworkFormat Format)[]
            {
                ("case14.m", Matpower.Write(TestCases.Case14()), NetworkFormat.Matpower),
                ("ieee14_v33.raw", InteropSamples.Ieee14RawV33, NetworkFormat.PsseRaw),
                ("ieee14_v35.raw", InteropSamples.Ieee14RawV35, NetworkFormat.PsseRaw),
                ("ieee14cdf.txt", InteropSamples.Ieee14Cdf, NetworkFormat.IeeeCdf),
                ("case14.json", Electrical.Cloud.NetworkJson.Serialize(TestCases.Case14()), NetworkFormat.CloudJson),
                ("noext", PsseRaw.Write(TestCases.Case14(), 33), NetworkFormat.PsseRaw),
            };
            foreach (var f in files)
            {
                var path = Path.Combine(dir, f.Name);
                File.WriteAllText(path, f.Text);
                var r = NetworkFormats.Load(path);
                var pf = PowerFlowSolver.Solve(r.Network);
                output.WriteLine($"{f.Name}: {r.Format}, {r.Network}, losses {pf.LossesMw:0.######} MW, {r.Warnings.Count} warnings");
                Assert.Equal(f.Format, r.Format);
                Assert.True(Math.Abs(pf.LossesMw - Ref.LossesMw) < 1e-6, f.Name);
            }
            Assert.Equal(NetworkFormat.PsseRaw, PsseRaw.Load(Path.Combine(dir, "ieee14_v33.raw")).Format);
            Assert.Equal(NetworkFormat.IeeeCdf, IeeeCdf.Read(Path.Combine(dir, "ieee14cdf.txt")).Format);
            Assert.Throws<FileNotFoundException>(() => NetworkFormats.Load(Path.Combine(dir, "missing.raw")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AddInterop_RegistersImporter()
    {
        using var sp = new ServiceCollection().AddElectrical(b => b.AddInterop().AddInterop()).BuildServiceProvider();
        var imp = sp.GetRequiredService<INetworkImporter>();
        Assert.Contains("Interop", sp.GetRequiredService<ElectricalModuleRegistry>().Modules);
        Assert.Equal(NetworkFormat.IeeeCdf, imp.Detect(InteropSamples.Ieee14Cdf));
        var net = imp.Parse(imp.WriteRaw(TestCases.Case14(), 35)).Network;
        Assert.Equal(13.393, PowerFlowSolver.Solve(net).LossesMw, 3);
    }
}
