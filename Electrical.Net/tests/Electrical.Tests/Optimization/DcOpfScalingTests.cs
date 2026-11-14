using Electrical.Core.Network;
using Electrical.Optimization;

namespace Electrical.Tests;

public class DcOpfScalingTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public void SyntheticGrid_WithoutCongestion_EqualsEconomicDispatch(int buses)
    {
        // The synthetic grids load their lines to < 40 % at the economic dispatch, so DC-OPF must reproduce it.
        // Starting the interior point at x = 0 used to stall (300 buses: 100 iterations, 205 062 $/h instead of 186 364 $/h);
        // the Pmax-proportional start with central-path slacks/duals converges in < 15 iterations.
        var net = TestCases.SyntheticGrid(buses);
        var opf = DcOpf.Solve(net);
        var ed = EconomicDispatch.Solve(net);
        Assert.True(opf.Converged);
        Assert.InRange(opf.Iterations, 1, 15);
        Assert.Equal(ed.TotalCost, opf.TotalCost, 3);
    }
}
