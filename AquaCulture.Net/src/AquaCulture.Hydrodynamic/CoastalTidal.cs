using AquaCulture.Core.Hosting;

namespace AquaCulture.Hydrodynamic;

/// <summary>A receptor (intake, seagrass bed, neighbouring farm) where concentrations are reported.</summary>
public sealed record Receptor(string Name, int I, int J);

/// <summary>Result of a tidal dispersion run.</summary>
public sealed record TidalPlumeResult(float[] FinalField, float[] MaxField, IReadOnlyList<(double Hour, double CentroidX, double CentroidY, double Mass)> Track,
    IReadOnlyDictionary<string, double> ReceptorMax, double FootprintM2, double Threshold);

/// <summary>
/// Coastal transport over real bathymetry: a <see cref="Grid2D"/> built from a depth raster (for example a GeoTIFF read with
/// <c>GdalRaster.Read</c>), a depth-dependent tidal current, and dispersion of farm effluent or cage waste over tidal cycles with the
/// platform's advection–diffusion kernel. The current is a kinematic approximation (Manning scaling of a uniform along-shore tide,
/// u ∝ h^⅔ at a common surface slope) — not a shallow-water solution; use it for screening footprints and receptor exposure.
/// </summary>
public static class CoastalTidal
{
    /// <summary>
    /// Builds a grid from depths (m, positive down; ≤ <paramref name="minDepthM"/>, NaN or negative = land). Rasters stored north-up
    /// (row 0 = north, as GDAL and <c>GeoRaster</c> do) are flipped so that j grows northward.
    /// </summary>
    public static Grid2D FromBathymetry(ReadOnlySpan<float> depthM, int nx, int ny, double cellSizeM, double minDepthM = 0.3, bool northUp = true)
    {
        if (depthM.Length != nx * ny) throw new ArgumentException("Depth array size mismatch.");
        var g = new Grid2D(nx, ny, cellSizeM);
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int src = (northUp ? ny - 1 - j : j) * nx + i, k = g.Index(i, j);
                float d = depthM[src];
                bool wet = !float.IsNaN(d) && d > minDepthM;
                g.Wet[k] = (byte)(wet ? 1 : 0);
                g.Depth[k] = wet ? d : 0;
            }
        return g;
    }

    /// <summary>
    /// Sets a tidal current u(t) = U₀ sin(2πt/T) along <paramref name="shoreAngleDeg"/> (0 = +x/east, 90 = +y/north), scaled per cell by
    /// (h / h_ref)^⅔ where h_ref is the mean wet depth, and zero on land.
    /// </summary>
    public static void TidalCurrent(Grid2D g, double amplitudeMs, double timeHours, double shoreAngleDeg = 0, double periodHours = 12.42)
    {
        double meanDepth = 0; int n = 0;
        for (int k = 0; k < g.Count; k++) if (g.Wet[k] != 0) { meanDepth += g.Depth[k]; n++; }
        meanDepth = n == 0 ? 1 : meanDepth / n;
        double s = amplitudeMs * Math.Sin(2 * Math.PI * timeHours / periodHours);
        double ux = Math.Cos(shoreAngleDeg * Math.PI / 180), uy = Math.Sin(shoreAngleDeg * Math.PI / 180);
        for (int k = 0; k < g.Count; k++)
        {
            if (g.Wet[k] == 0) { g.U[k] = 0; g.V[k] = 0; continue; }
            double f = Math.Pow(Math.Max(0.05, g.Depth[k] / meanDepth), 2.0 / 3);
            g.U[k] = (float)(s * f * ux); g.V[k] = (float)(s * f * uy);
        }
    }

    /// <summary>
    /// Releases scalar sources (mg/L per second at their cells; e.g. TSS or TAN from a pond outlet or a cage block) and follows the plume
    /// for <paramref name="hours"/>, updating the tidal current every <paramref name="stepMinutes"/>. Reports the plume centroid and mass
    /// over time, the maximum at each receptor, and the area where the maximum ever exceeded <paramref name="threshold"/>.
    /// </summary>
    public static TidalPlumeResult Disperse(Grid2D g, IEnumerable<ScalarSource> sources, double hours, double tidalAmplitudeMs = 0.4, double shoreAngleDeg = 0,
        double diffusivity = 1.0, double decayPerSecond = 0, double threshold = 1, IEnumerable<Receptor>? receptors = null, double stepMinutes = 10, double periodHours = 12.42)
    {
        var field = new float[g.Count];
        var max = new float[g.Count];
        var track = new List<(double, double, double, double)>();
        var rec = (receptors ?? []).ToList();
        var recMax = rec.ToDictionary(r => r.Name, _ => 0.0);
        var srcList = sources.ToList();
        int chunks = (int)Math.Ceiling(hours * 60 / stepMinutes);
        for (int c = 0; c < chunks; c++)
        {
            double tMid = (c + 0.5) * stepMinutes / 60;
            TidalCurrent(g, tidalAmplitudeMs, tMid, shoreAngleDeg, periodHours);
            var model = new ScalarTransport(g) { Diffusivity = diffusivity, Decay = decayPerSecond };
            model.Sources.AddRange(srcList);
            field = model.Run(field, stepMinutes * 60, stepMinutes * 60).Field;
            double mass = 0, cx = 0, cy = 0;
            for (int j = 0; j < g.Ny; j++)
                for (int i = 0; i < g.Nx; i++)
                {
                    int k = g.Index(i, j);
                    if (g.Wet[k] == 0) continue;
                    double m = Math.Max(0, field[k]) * g.Depth[k];
                    mass += m; cx += m * i; cy += m * j;
                    if (field[k] > max[k]) max[k] = field[k];
                }
            track.Add(((c + 1) * stepMinutes / 60, mass > 0 ? cx / mass * g.Dx : double.NaN, mass > 0 ? cy / mass * g.Dx : double.NaN, mass * g.Dx * g.Dx / 1000));
            foreach (var r in rec) recMax[r.Name] = Math.Max(recMax[r.Name], field[g.Index(r.I, r.J)]);
        }
        double footprint = 0;
        for (int k = 0; k < g.Count; k++) if (g.Wet[k] != 0 && max[k] >= threshold) footprint += g.Dx * g.Dx;
        return new TidalPlumeResult(field, max, track, recMax, footprint, threshold);
    }

    /// <summary>A synthetic shelf for tests and demos: land along the south edge, depth increasing offshore with a channel.</summary>
    public static float[] SyntheticShelf(int nx, int ny, double maxDepthM = 12, int landRows = 4)
    {
        var d = new float[nx * ny];
        for (int row = 0; row < ny; row++)
            for (int i = 0; i < nx; i++)
            {
                int fromSouth = ny - 1 - row;                      // north-up storage
                double depth = fromSouth < landRows ? -1 : maxDepthM * (fromSouth - landRows + 1) / (ny - landRows)
                    + 3 * Math.Exp(-Math.Pow((i - nx * 0.6) / (nx * 0.08), 2));     // a channel
                d[row * nx + i] = (float)depth;
            }
        return d;
    }
}
