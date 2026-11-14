using Electrical.Circuit;
using Electrical.Core.Hosting;
using Electrical.Core.Network;
using Electrical.Harmonic;
using Electrical.Protection;
using Electrical.ShortCircuit;
using Electrical.Stability;
using Microsoft.Extensions.DependencyInjection;

namespace Electrical.Tests;

public class AnalysisHostingTests
{
    [Fact]
    public void AnalysisModules_RegisterServices()
    {
        var sp = new ServiceCollection()
            .AddElectrical(b => b.AddCircuit().AddShortCircuit().AddProtection().AddStability().AddHarmonic())
            .BuildServiceProvider();
        var reg = sp.GetRequiredService<ElectricalModuleRegistry>();
        foreach (var m in new[] { "Circuit", "ShortCircuit", "Protection", "Stability", "Harmonic", "PowerFlow" }) Assert.Contains(m, reg.Modules);
        Assert.NotNull(sp.GetRequiredService<ICircuitService>());
        Assert.NotNull(sp.GetRequiredService<IProtectionService>());
        Assert.NotNull(sp.GetRequiredService<IHarmonicService>());
        var sc = sp.GetRequiredService<IShortCircuitService>().ScanAllBuses(TestCases.Case9());
        Assert.Equal(9, sc.Count);
        Assert.True(sp.GetRequiredService<IStabilityService>().SmallSignal(TestCases.Case9()).IsStable(1e-5));
    }
}
