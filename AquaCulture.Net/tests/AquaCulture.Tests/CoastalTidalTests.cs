using AquaCulture.Hydrodynamic;

namespace AquaCulture.Tests;

public class CoastalTidalTests
{
    [Fact]
    public void Bathymetry_grid_flips_north_up_rasters_and_marks_land_dry()
    {
        // 3 × 2 raster stored north-up: top row (north) deep, bottom row (south) land
        float[] depth = [5, 6, 7, -1, float.NaN, 0.1f];
        var g = CoastalTidal.FromBathymetry(depth, 3, 2, 10);
        Assert.Equal(0, g.Wet[g.Index(0, 0)]);                       // south row → j = 0, dry
        Assert.Equal(1, g.Wet[g.Index(2, 1)]);
        Assert.Equal(7f, g.Depth[g.Index(2, 1)]);
        Assert.Equal(3, g.WetCount);
    }

    [Fact]
    public void Tidal_current_follows_depth_and_the_tide()
    {
        var g = CoastalTidal.FromBathymetry(CoastalTidal.SyntheticShelf(60, 40), 60, 40, 50);
        CoastalTidal.TidalCurrent(g, 0.4, 12.42 / 4);                // flood peak, along +x
        int shallow = g.Index(5, 6), deep = g.Index(5, 38);
        Assert.True(g.U[deep] > g.U[shallow] && g.U[shallow] > 0);
        Assert.Equal(Math.Pow(g.Depth[deep] / g.Depth[shallow], 2.0 / 3), g.U[deep] / g.U[shallow], 3);
        Assert.Equal(0, g.U[g.Index(5, 1)]);                          // land
        CoastalTidal.TidalCurrent(g, 0.4, 12.42 * 3 / 4);            // ebb
        Assert.True(g.U[deep] < 0);
        CoastalTidal.TidalCurrent(g, 0.4, 2, shoreAngleDeg: 90);
        Assert.Equal(0, g.U[deep], 6);
        Assert.True(g.V[deep] > 0);
    }

    [Fact]
    public void An_effluent_plume_sloshes_with_the_tide_and_reaches_receptors_downstream()
    {
        var g = CoastalTidal.FromBathymetry(CoastalTidal.SyntheticShelf(80, 40), 80, 40, 50);
        var outlet = new ScalarSource(40, 6, 0.002, 1);                // pond outlet near the shore
        var r = CoastalTidal.Disperse(g, [outlet], hours: 12.42, tidalAmplitudeMs: 0.3, diffusivity: 2, threshold: 0.5,
            receptors: [new Receptor("intake-east", 52, 6), new Receptor("seagrass-offshore", 40, 35)]);
        var xs = r.Track.Select(t => t.CentroidX).ToList();
        double flood = xs.Take(xs.Count / 2).Max(), ebb = xs.Skip(xs.Count / 2).Min();
        Assert.True(flood > 40 * 50 + 100, $"plume moves east on the flood ({flood:0} m)");
        Assert.True(ebb < flood - 200, "and back on the ebb");
        Assert.True(r.Track[^1].Mass > r.Track[0].Mass, "a continuous source accumulates");
        Assert.True(r.ReceptorMax["intake-east"] > r.ReceptorMax["seagrass-offshore"], "the along-shore intake is exposed more than the offshore bed");
        Assert.True(r.FootprintM2 > 0);
        Assert.All(Enumerable.Range(0, g.Count).Where(k => g.Wet[k] == 0), k => Assert.Equal(0f, r.MaxField[k]));
    }
}
