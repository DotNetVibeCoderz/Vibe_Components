using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Survey;

/// <summary>Angles: decimal degrees ⇄ degrees-minutes-seconds, azimuth helpers.</summary>
public static class Angles
{
    public static double Dms(double degrees, double minutes = 0, double seconds = 0) =>
        Math.Sign(degrees == 0 ? (minutes == 0 ? seconds : minutes) : degrees) * (Math.Abs(degrees) + Math.Abs(minutes) / 60 + Math.Abs(seconds) / 3600);

    /// <summary>"123°45'06.7\"" or "123 45 06.7" → decimal degrees.</summary>
    public static double ParseDms(string s)
    {
        var p = s.Split(['°', '\'', '"', ' ', ':', 'd', 'm', 's'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        double v = Math.Abs(p[0]) + (p.Length > 1 ? p[1] / 60 : 0) + (p.Length > 2 ? p[2] / 3600 : 0);
        return s.TrimStart().StartsWith('-') ? -v : v;
    }

    public static string ToDms(double deg, int decimals = 1)
    {
        double a = Math.Abs(deg);
        int d = (int)a; double mf = (a - d) * 60; int m = (int)mf; double sec = Math.Round((mf - m) * 60, decimals);
        if (sec >= 60) { sec -= 60; m++; }
        if (m >= 60) { m -= 60; d++; }
        return string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(deg < 0 ? "-" : "")}{d}°{m:00}'{sec.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture).PadLeft(decimals + 3, '0')}\"");
    }

    /// <summary>Normalises to [0, 360).</summary>
    public static double Normalize(double deg) => ((deg % 360) + 360) % 360;

    /// <summary>Grid azimuth (from north, clockwise) and distance between two points.</summary>
    public static (double Azimuth, double Distance) Inverse(double e1, double n1, double e2, double n2) =>
        (Normalize(Math.Atan2(e2 - e1, n2 - n1) * 180 / Math.PI), Math.Sqrt((e2 - e1) * (e2 - e1) + (n2 - n1) * (n2 - n1)));
}

public enum TraverseAdjustment { Bowditch, Transit, None }

/// <summary>One traverse leg: the angle measured at the from-station (clockwise from the backsight) or a direct azimuth, and the horizontal distance.</summary>
public sealed record TraverseLeg(string To, double Distance, double? AngleRight = null, double? Azimuth = null);

/// <summary>Adjusted traverse: stations, legs, misclosures and precision.</summary>
public sealed class TraverseResult
{
    internal TraverseResult(DataFrame stations, DataFrame legs, double angularMisclosureSec, double linearMisclosure, double totalLength,
        double closureE, double closureN, CoordinateReferenceSystem? crs)
    {
        Stations = stations; Legs = legs; AngularMisclosureSeconds = angularMisclosureSec; LinearMisclosure = linearMisclosure;
        TotalLength = totalLength; MisclosureE = closureE; MisclosureN = closureN; Crs = crs;
    }

    /// <summary>Columns: station, e, n, e_unadjusted, n_unadjusted.</summary>
    public DataFrame Stations { get; }
    /// <summary>Columns: from, to, azimuth, distance, d_e, d_n, corr_e, corr_n.</summary>
    public DataFrame Legs { get; }
    public double AngularMisclosureSeconds { get; }
    public double LinearMisclosure { get; }
    public double MisclosureE { get; }
    public double MisclosureN { get; }
    public double TotalLength { get; }
    /// <summary>Relative precision 1 : N (N = total length / linear misclosure).</summary>
    public double PrecisionRatio => LinearMisclosure < 1e-12 ? double.PositiveInfinity : TotalLength / LinearMisclosure;
    public CoordinateReferenceSystem? Crs { get; }

    public GeoDataFrame StationPoints() =>
        GeoDataFrame.FromGeometries(Enumerable.Range(0, Stations.RowCount).Select(i => (NtsGeometry?)Geo.Point(Stations["e"].GetDouble(i), Stations["n"].GetDouble(i))), Crs, Stations["station"]);

    public GeoDataFrame LegLines()
    {
        // legs are sequential: leg i runs from station row i to row i + 1 (a loop that closes on its start may omit the repeated row).
        // Positions, not names, because a closed traverse lists its start station twice.
        int n = Stations.RowCount;
        var lines = Enumerable.Range(0, Legs.RowCount).Select(i =>
        {
            int a = i % n, b = (i + 1) % n;
            return (NtsGeometry?)Geo.LineString((Stations["e"].GetDouble(a), Stations["n"].GetDouble(a)), (Stations["e"].GetDouble(b), Stations["n"].GetDouble(b)));
        });
        return GeoDataFrame.FromGeometries(lines, Crs, Legs["from"], Legs["to"], Legs["distance"]);
    }

    public override string ToString() =>
        $"Traverse: {Legs.RowCount} legs, {TotalLength:F3} m, angular misclosure {AngularMisclosureSeconds:F1}\", linear misclosure {LinearMisclosure:F4} m (1:{PrecisionRatio:F0})";
}

/// <summary>
/// Plane traverse computation and adjustment (closed loop or connecting traverse): angle balancing, azimuth propagation,
/// latitudes/departures, Bowditch (compass) or Transit rule, precision ratio.
/// </summary>
public static class Traverse
{
    /// <summary>
    /// Computes a traverse that starts at <paramref name="startStation"/> (E, N) with a known azimuth to the backsight, and either
    /// closes on itself (loop) or on a known end point. If <paramref name="closingAzimuth"/> is given (azimuth from the last station to a
    /// foresight reference), the angular misclosure is distributed equally over the measured angles.
    /// </summary>
    public static TraverseResult Compute(string startStation, double startE, double startN, double backsightAzimuth, IReadOnlyList<TraverseLeg> legs,
        (double E, double N)? closeOn = null, double? closingAzimuth = null, double? closingAngleRight = null,
        TraverseAdjustment adjustment = TraverseAdjustment.Bowditch, CoordinateReferenceSystem? crs = null)
    {
        if (legs.Count == 0) throw new ArgumentException("A traverse needs at least one leg.");
        // 1. azimuths from angles (angle right = clockwise from backsight to foresight)
        var az = new double[legs.Count];
        double back = backsightAzimuth;
        int angleCount = 0;
        for (int i = 0; i < legs.Count; i++)
        {
            if (legs[i].Azimuth is { } a) az[i] = Angles.Normalize(a);
            else if (legs[i].AngleRight is { } ar) { az[i] = Angles.Normalize(back + ar); angleCount++; }
            else throw new ArgumentException($"Leg to '{legs[i].To}' needs an angle or an azimuth.");
            back = Angles.Normalize(az[i] + 180);
        }
        // 2. angular misclosure. Connecting traverse: the last direction must equal closingAzimuth. Loop: the closing angle measured at the
        //    start station must reproduce the first leg's azimuth (whose angle is fixed by the external orientation).
        double angMis = 0;
        bool loop = closeOn is null && legs[^1].To == startStation;
        double? known = closingAzimuth ?? (loop && closingAngleRight is not null ? az[0] : null);
        if (known is { } exp && angleCount > 0)
        {
            double computed = closingAngleRight is { } c ? Angles.Normalize(back + c) : az[^1];
            angMis = ((computed - exp + 540) % 360) - 180;
            bool skipFirst = loop && closingAzimuth is null && legs[0].AngleRight is not null;
            int nAngles = angleCount - (skipFirst ? 1 : 0) + (closingAngleRight is not null ? 1 : 0);
            double corr = -angMis / nAngles;
            back = backsightAzimuth;
            int k = 0;
            for (int i = 0; i < legs.Count; i++)
            {
                if (legs[i].AngleRight is { } ar)
                {
                    if (!(skipFirst && i == 0)) k++;
                    az[i] = Angles.Normalize(back + ar + corr * k);
                }
                else az[i] = Angles.Normalize(legs[i].Azimuth!.Value);
                back = Angles.Normalize(az[i] + 180);
            }
        }
        // 3. latitudes and departures
        var de = new double[legs.Count]; var dn = new double[legs.Count];
        double total = 0, sumE = 0, sumN = 0, sumAbsE = 0, sumAbsN = 0;
        for (int i = 0; i < legs.Count; i++)
        {
            double r = az[i] * Math.PI / 180;
            de[i] = legs[i].Distance * Math.Sin(r); dn[i] = legs[i].Distance * Math.Cos(r);
            total += legs[i].Distance; sumE += de[i]; sumN += dn[i]; sumAbsE += Math.Abs(de[i]); sumAbsN += Math.Abs(dn[i]);
        }
        var target = closeOn ?? (loop ? (startE, startN) : ((double, double)?)null);
        double misE = target is { } t ? startE + sumE - t.Item1 : 0, misN = target is { } t2 ? startN + sumN - t2.Item2 : 0;
        // 4. corrections
        var ce = new double[legs.Count]; var cn = new double[legs.Count];
        if (target is not null && adjustment != TraverseAdjustment.None)
            for (int i = 0; i < legs.Count; i++)
            {
                if (adjustment == TraverseAdjustment.Bowditch) { ce[i] = -misE * legs[i].Distance / total; cn[i] = -misN * legs[i].Distance / total; }
                else { ce[i] = sumAbsE == 0 ? 0 : -misE * Math.Abs(de[i]) / sumAbsE; cn[i] = sumAbsN == 0 ? 0 : -misN * Math.Abs(dn[i]) / sumAbsN; }
            }
        var names = new List<string> { startStation };
        var e = new List<double> { startE }; var n = new List<double> { startN };
        var eu = new List<double> { startE }; var nu = new List<double> { startN };
        string from = startStation;
        var froms = new string[legs.Count];
        for (int i = 0; i < legs.Count; i++)
        {
            froms[i] = from;
            eu.Add(eu[^1] + de[i]); nu.Add(nu[^1] + dn[i]);
            e.Add(e[^1] + de[i] + ce[i]); n.Add(n[^1] + dn[i] + cn[i]);
            names.Add(legs[i].To);
            from = legs[i].To;
        }
        if (loop) { names.RemoveAt(names.Count - 1); e.RemoveAt(e.Count - 1); n.RemoveAt(n.Count - 1); eu.RemoveAt(eu.Count - 1); nu.RemoveAt(nu.Count - 1); }
        var stations = new DataFrame(Column.Create("station", names.ToArray()), Column.Create("e", e.ToArray()), Column.Create("n", n.ToArray()),
            Column.Create("e_unadjusted", eu.ToArray()), Column.Create("n_unadjusted", nu.ToArray()));
        var legsDf = new DataFrame(Column.Create("from", froms), Column.Create("to", legs.Select(l => l.To).ToArray()),
            Column.Create("azimuth", az), Column.Create("distance", legs.Select(l => l.Distance).ToArray()),
            Column.Create("d_e", de), Column.Create("d_n", dn), Column.Create("corr_e", ce), Column.Create("corr_n", cn));
        return new TraverseResult(stations, legsDf, angMis * 3600, Math.Sqrt(misE * misE + misN * misN), total, misE, misN, crs);
    }
}

/// <summary>Coordinate geometry: intersections and resection.</summary>
public static class Cogo
{
    /// <summary>Intersection of two rays given by points and azimuths (degrees).</summary>
    public static (double E, double N)? BearingBearing(double e1, double n1, double az1, double e2, double n2, double az2)
    {
        double s1 = Math.Sin(az1 * Math.PI / 180), c1 = Math.Cos(az1 * Math.PI / 180), s2 = Math.Sin(az2 * Math.PI / 180), c2 = Math.Cos(az2 * Math.PI / 180);
        double den = s1 * c2 - c1 * s2;
        if (Math.Abs(den) < 1e-15) return null;
        double t = ((e2 - e1) * c2 - (n2 - n1) * s2) / den;
        return (e1 + t * s1, n1 + t * c1);
    }

    /// <summary>Intersections of two circles (distance–distance); zero, one or two solutions.</summary>
    public static IReadOnlyList<(double E, double N)> DistanceDistance(double e1, double n1, double r1, double e2, double n2, double r2)
    {
        double dx = e2 - e1, dy = n2 - n1, d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12 || d > r1 + r2 + 1e-9 || d < Math.Abs(r1 - r2) - 1e-9) return [];
        double a = (r1 * r1 - r2 * r2 + d * d) / (2 * d), h = Math.Sqrt(Math.Max(0, r1 * r1 - a * a));
        double xm = e1 + a * dx / d, ym = n1 + a * dy / d;
        if (h < 1e-12) return [(xm, ym)];
        return [(xm + h * dy / d, ym - h * dx / d), (xm - h * dy / d, ym + h * dx / d)];
    }

    /// <summary>
    /// Three-point resection (Tienstra): the station position from the horizontal directions (degrees, any zero) observed to three known
    /// points A, B, C. Directions are the readings to A, B and C.
    /// </summary>
    public static (double E, double N) Resection((double E, double N) a, (double E, double N) b, (double E, double N) c, double dirA, double dirB, double dirC)
    {
        double Ang(double from, double to) => Angles.Normalize(to - from) * Math.PI / 180;
        double alpha = Ang(dirB, dirC), beta = Ang(dirC, dirA), gamma = Ang(dirA, dirB); // angles at P subtended by BC, CA, AB
        double Interior((double E, double N) p, (double E, double N) q, (double E, double N) r)
        {
            double v1x = q.E - p.E, v1y = q.N - p.N, v2x = r.E - p.E, v2y = r.N - p.N;
            return Math.Acos(Math.Clamp((v1x * v2x + v1y * v2y) / Math.Sqrt((v1x * v1x + v1y * v1y) * (v2x * v2x + v2y * v2y)), -1, 1));
        }
        double A = Interior(a, b, c), B = Interior(b, c, a), C = Interior(c, a, b);
        double k1 = 1 / (1 / Math.Tan(A) - 1 / Math.Tan(alpha)), k2 = 1 / (1 / Math.Tan(B) - 1 / Math.Tan(beta)), k3 = 1 / (1 / Math.Tan(C) - 1 / Math.Tan(gamma));
        double k = k1 + k2 + k3;
        return ((k1 * a.E + k2 * b.E + k3 * c.E) / k, (k1 * a.N + k2 * b.N + k3 * c.N) / k);
    }
}
