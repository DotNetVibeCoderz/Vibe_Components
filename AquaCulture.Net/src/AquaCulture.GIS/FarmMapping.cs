using AquaCulture.Core;
using AquaCulture.Core.Farming;
using AquaCulture.Core.Geo;
using AquaCulture.Core.Hosting;

namespace AquaCulture.GIS;

/// <summary>A georeferenced raster grid (row 0 = north).</summary>
public sealed class GeoRaster
{
    public GeoRaster(GeoBounds bounds, int width, int height, float[]? data = null)
    {
        Bounds = bounds; Width = width; Height = height;
        Data = data ?? new float[width * height];
        if (Data.Length != width * height) throw new ArgumentException("Data size mismatch.");
    }

    public GeoBounds Bounds { get; }
    public int Width { get; }
    public int Height { get; }
    public float[] Data { get; }
    public float this[int col, int row] { get => Data[row * Width + col]; set => Data[row * Width + col] = value; }

    public GeoCoordinate CellCenter(int col, int row) => new(
        Bounds.MaxLat - (row + 0.5) * (Bounds.MaxLat - Bounds.MinLat) / Height,
        Bounds.MinLon + (col + 0.5) * (Bounds.MaxLon - Bounds.MinLon) / Width);

    public (int Col, int Row) CellOf(GeoCoordinate c) => (
        Math.Clamp((int)((c.Longitude - Bounds.MinLon) / (Bounds.MaxLon - Bounds.MinLon) * Width), 0, Width - 1),
        Math.Clamp((int)((Bounds.MaxLat - c.Latitude) / (Bounds.MaxLat - Bounds.MinLat) * Height), 0, Height - 1));

    public double CellSizeM => GeoMath.Haversine(new(Bounds.MinLat, Bounds.MinLon), new(Bounds.MinLat, Bounds.MinLon + (Bounds.MaxLon - Bounds.MinLon) / Width));
}

/// <summary>Farm, pond and sensor mapping.</summary>
public static class FarmMapping
{
    /// <summary>
    /// Lays out a rectangular block of ponds (rows × cols) south-west → north-east from an origin, separated by dikes.
    /// Returns ponds with outlines and centre coordinates.
    /// </summary>
    public static IReadOnlyList<Pond> PondBlock(GeoCoordinate origin, int rows, int cols, double pondWidthM, double pondLengthM, double dikeM = 4,
        double depthM = 1.2, CultureSystem system = CultureSystem.EarthenPond, string prefix = "P")
    {
        var list = new List<Pond>();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var center = GeoMath.FromLocal(origin, new LocalPoint(c * (pondWidthM + dikeM) + pondWidthM / 2, r * (pondLengthM + dikeM) + pondLengthM / 2));
                var outline = GeoPolygon.Rectangle(center, pondWidthM, pondLengthM);
                list.Add(new Pond
                {
                    Id = $"{prefix}-{r * cols + c + 1:00}", Name = $"{prefix}{r + 1}{(char)('A' + c)}", System = system,
                    AreaM2 = pondWidthM * pondLengthM, DepthM = depthM, Outline = outline, Location = center,
                });
            }
        return list;
    }

    /// <summary>Map features for a farm: pond polygons with their key attributes.</summary>
    public static IReadOnlyList<MapFeature> Features(Farm farm, IReadOnlyDictionary<string, double>? valueByPond = null, string valueName = "value") =>
        farm.Ponds.Where(p => p.Outline is not null).Select(p => new MapFeature(p.Id, "pond", p.Outline, Properties: new Dictionary<string, object?>
        {
            ["name"] = p.Name, ["system"] = p.System.ToString(), ["area_m2"] = Math.Round(p.AreaM2), ["depth_m"] = p.DepthM,
            ["species"] = farm.ActiveCycle(p.Id)?.Species.Id,
            [valueName] = valueByPond is not null && valueByPond.TryGetValue(p.Id, out var v) ? v : null,
        })).ToList();

    /// <summary>Inverse-distance-weighted interpolation of point values onto a raster (e.g. DO across a reservoir).</summary>
    public static GeoRaster Idw(IReadOnlyList<(GeoCoordinate Point, double Value)> samples, GeoBounds bounds, int width, int height, double power = 2, GeoPolygon? mask = null)
    {
        var r = new GeoRaster(bounds, width, height);
        var o = bounds.Center;
        var pts = samples.Select(s => (P: GeoMath.ToLocal(o, s.Point), s.Value)).ToArray();
        for (int row = 0; row < height; row++)
            for (int col = 0; col < width; col++)
            {
                var c = r.CellCenter(col, row);
                if (mask is not null && !mask.Contains(c)) { r[col, row] = float.NaN; continue; }
                var q = GeoMath.ToLocal(o, c);
                double num = 0, den = 0; bool exact = false;
                foreach (var (p, v) in pts)
                {
                    double d2 = (p.East - q.East) * (p.East - q.East) + (p.North - q.North) * (p.North - q.North);
                    if (d2 < 1e-6) { r[col, row] = (float)v; exact = true; break; }
                    double w = 1 / Math.Pow(d2, power / 2);
                    num += w * v; den += w;
                }
                if (!exact) r[col, row] = (float)(num / den);
            }
        return r;
    }
}

public enum WaterSourceKind { River, Sea, Groundwater, IrrigationCanal, Reservoir, Rainwater }

/// <summary>A candidate water source.</summary>
public sealed record WaterSource(string Id, WaterSourceKind Kind, GeoCoordinate Location, double SalinityPpt, double QualityScore = 0.8, double SeasonalReliability = 0.8);

/// <summary>Evaluation of a water source for a site.</summary>
public sealed record WaterSourceEvaluation(WaterSource Source, double DistanceM, double HeadM, double PumpingKwhPerDay, double Suitability, Text2 Note);

/// <summary>Water source analysis: distance, pumping head and energy, salinity match, quality and reliability.</summary>
public static class WaterSourceAnalysis
{
    /// <summary>Pumping energy (kWh/day) to lift Q (m³/day) over total head H (m) with efficiency η.</summary>
    public static double PumpingEnergyKwh(double flowM3PerDay, double headM, double efficiency = 0.6) => 1000 * 9.81 * flowM3PerDay * headM / efficiency / 3.6e6;

    public static IReadOnlyList<WaterSourceEvaluation> Evaluate(GeoCoordinate site, double siteElevationM, IEnumerable<WaterSource> sources, double demandM3PerDay,
        Core.Species.SpeciesProfile species)
    {
        var list = new List<WaterSourceEvaluation>();
        foreach (var s in sources)
        {
            double dist = GeoMath.Haversine(site, s.Location);
            double head = Math.Max(0, siteElevationM - s.Location.Elevation) + dist * 0.002 + (s.Kind == WaterSourceKind.Groundwater ? 25 : 0);
            double kwh = PumpingEnergyKwh(demandM3PerDay, head);
            double salFit = species.Salinity.Suitability(s.SalinityPpt);
            double distScore = Math.Exp(-dist / 1500);
            double suit = Math.Round(0.35 * salFit + 0.2 * distScore + 0.2 * s.QualityScore + 0.15 * s.SeasonalReliability + 0.1 * Math.Exp(-kwh / 200), 3);
            var note = salFit < 0.3
                ? new Text2($"Salinity {s.SalinityPpt} ppt does not suit {species.Name.En}", $"Salinitas {s.SalinityPpt} ppt tidak cocok untuk {species.Name.Id}")
                : dist > 3000 ? new Text2("Far: high conveyance cost", "Jauh: biaya penyaluran tinggi") : new Text2("Suitable", "Layak");
            list.Add(new WaterSourceEvaluation(s, Math.Round(dist), Math.Round(head, 1), Math.Round(kwh, 1), suit, note));
        }
        return list.OrderByDescending(e => e.Suitability).ToList();
    }
}

/// <summary>D8 drainage result.</summary>
public sealed record DrainageResult(byte[] FlowDirection, int[] Accumulation, float[] SlopePct, int Width, int Height, IReadOnlyList<int> Outlets, IReadOnlyList<int> Sinks)
{
    /// <summary>Cells whose upstream area exceeds a threshold (drainage lines).</summary>
    public IEnumerable<int> Channels(int minCells) => Enumerable.Range(0, Accumulation.Length).Where(i => Accumulation[i] >= minCells);
}

/// <summary>Drainage mapping on a DEM: D8 flow directions, flow accumulation, slope, sinks, outlets and flow paths.</summary>
public static class DrainageAnalysis
{
    private static readonly int[] Dc = [1, 1, 0, -1, -1, -1, 0, 1];
    private static readonly int[] Dr = [0, 1, 1, 1, 0, -1, -1, -1];

    /// <summary>Runs D8 on an elevation raster (m). Direction codes 0–7 (E, SE, S, SW, W, NW, N, NE), 255 = sink/outlet.</summary>
    public static DrainageResult D8(GeoRaster dem)
    {
        int w = dem.Width, h = dem.Height, n = w * h;
        double cell = dem.CellSizeM;
        var dir = new byte[n]; var slope = new float[n];
        var outlets = new List<int>(); var sinks = new List<int>();
        for (int r = 0; r < h; r++)
            for (int c = 0; c < w; c++)
            {
                int k = r * w + c; float z = dem.Data[k];
                double best = 0; int bd = 255;
                for (int d = 0; d < 8; d++)
                {
                    int cc = c + Dc[d], rr = r + Dr[d];
                    if (cc < 0 || rr < 0 || cc >= w || rr >= h) continue;
                    double dist = cell * (d % 2 == 0 ? 1 : Math.Sqrt(2));
                    double s = (z - dem.Data[rr * w + cc]) / dist;
                    if (s > best) { best = s; bd = d; }
                }
                dir[k] = (byte)bd; slope[k] = (float)(best * 100);
                if (bd == 255) (c == 0 || r == 0 || c == w - 1 || r == h - 1 ? outlets : sinks).Add(k);
            }
        // accumulation by processing cells from high to low
        var acc = Enumerable.Repeat(1, n).ToArray();
        foreach (int k in Enumerable.Range(0, n).OrderByDescending(i => dem.Data[i]))
        {
            if (dir[k] == 255) continue;
            int c = k % w + Dc[dir[k]], r = k / w + Dr[dir[k]];
            acc[r * w + c] += acc[k];
        }
        return new DrainageResult(dir, acc, slope, w, h, outlets, sinks);
    }

    /// <summary>Follows flow directions from a cell to its outlet or sink.</summary>
    public static IReadOnlyList<int> FlowPath(DrainageResult d, int start, int maxSteps = 100_000)
    {
        var path = new List<int> { start };
        int k = start;
        for (int s = 0; s < maxSteps && d.FlowDirection[k] != 255; s++)
        {
            int c = k % d.Width + Dc[d.FlowDirection[k]], r = k / d.Width + Dr[d.FlowDirection[k]];
            k = r * d.Width + c; path.Add(k);
        }
        return path;
    }

    /// <summary>Synthetic coastal-plain DEM for demos: gentle seaward slope, a meandering creek and noise.</summary>
    public static GeoRaster SyntheticDem(GeoBounds bounds, int width, int height, ulong seed = 3)
    {
        var rng = new Core.Data.DeterministicRandom(seed);
        var dem = new GeoRaster(bounds, width, height);
        for (int r = 0; r < height; r++)
            for (int c = 0; c < width; c++)
            {
                double x = (double)c / width, y = (double)r / height;
                double creek = Math.Abs(x - (0.5 + 0.15 * Math.Sin(y * 7)));
                dem[c, r] = (float)(4.0 * (1 - y) + 1.2 * Math.Min(1, creek * 6) + 0.05 * rng.NextGaussian());
            }
        return dem;
    }
}

public static class GisBuilderExtensions
{
    public static AquaBuilder AddGis(this AquaBuilder b) => b.TryAddModule("GIS", _ => { });
}
