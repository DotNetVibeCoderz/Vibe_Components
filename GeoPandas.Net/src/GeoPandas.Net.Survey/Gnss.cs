using GeoPandas.Net.Geometry;
using GeoPandas.Net.Geometry.Crs;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace GeoPandas.Net.Survey;

/// <summary>Options for <see cref="Gnss.SinglePointPositioning"/>.</summary>
public sealed record SppOptions
{
    /// <summary>Elevation mask in degrees.</summary>
    public double ElevationMask { get; init; } = 10;
    public bool Ionosphere { get; init; } = true;
    public bool Troposphere { get; init; } = true;
    /// <summary>Pseudorange codes to try, in order (RINEX 3 then RINEX 2 names).</summary>
    public string[] Codes { get; init; } = ["C1C", "C1", "P1", "C1W"];
    public int MinSatellites { get; init; } = 4;
    /// <summary>Fault detection and exclusion: when the residual RMS exceeds this (m) and there is redundancy, drop the worst satellite. 0 disables.</summary>
    public double FdeRmsThreshold { get; init; } = 1.5;
}

/// <summary>
/// GNSS processing: GPS broadcast orbits and clocks (IS-GPS-200), Klobuchar ionosphere, Saastamoinen troposphere, Earth rotation
/// (Sagnac) correction and iterative weighted least-squares single-point positioning (SPP) from RINEX observation + navigation files.
/// </summary>
public static class Gnss
{
    public const double C = 299_792_458.0;
    private const double Gm = 3.986005e14, OmegaE = 7.2921151467e-5, F = -4.442807633e-10;
    private static readonly DateTime GpsEpoch = new(1980, 1, 6, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>GPS week and seconds of week of a GPS-time instant.</summary>
    public static (int Week, double Seconds) GpsTime(DateTime t)
    {
        double s = (t - GpsEpoch).TotalSeconds;
        int w = (int)Math.Floor(s / 604800);
        return (w, s - w * 604800.0);
    }

    /// <summary>Satellite ECEF position (m) and clock bias (s, including the relativistic term, excluding TGD) at GPS time <paramref name="t"/> (seconds of week).</summary>
    public static (double X, double Y, double Z, double ClockBias) SatellitePosition(GpsEphemeris eph, double t)
    {
        double a = eph.SqrtA * eph.SqrtA;
        double tk = t - eph.Toe;
        if (tk > 302400) tk -= 604800; else if (tk < -302400) tk += 604800;
        double n = Math.Sqrt(Gm / (a * a * a)) + eph.DeltaN;
        double m = eph.M0 + n * tk;
        double e = m;
        for (int i = 0; i < 30; i++)
        {
            double next = m + eph.E * Math.Sin(e);
            if (Math.Abs(next - e) < 1e-14) { e = next; break; }
            e = next;
        }
        double v = Math.Atan2(Math.Sqrt(1 - eph.E * eph.E) * Math.Sin(e), Math.Cos(e) - eph.E);
        double phi = v + eph.Omega;
        double s2 = Math.Sin(2 * phi), c2 = Math.Cos(2 * phi);
        double u = phi + eph.Cus * s2 + eph.Cuc * c2;
        double r = a * (1 - eph.E * Math.Cos(e)) + eph.Crs * s2 + eph.Crc * c2;
        double inc = eph.I0 + eph.Cis * s2 + eph.Cic * c2 + eph.IDot * tk;
        double xp = r * Math.Cos(u), yp = r * Math.Sin(u);
        double om = eph.Omega0 + (eph.OmegaDot - OmegaE) * tk - OmegaE * eph.Toe;
        double x = xp * Math.Cos(om) - yp * Math.Cos(inc) * Math.Sin(om);
        double y = xp * Math.Sin(om) + yp * Math.Cos(inc) * Math.Cos(om);
        double z = yp * Math.Sin(inc);
        var (_, toc) = GpsTime(eph.Toc);
        double dt = t - toc;
        if (dt > 302400) dt -= 604800; else if (dt < -302400) dt += 604800;
        double clk = eph.Af0 + eph.Af1 * dt + eph.Af2 * dt * dt + F * eph.E * eph.SqrtA * Math.Sin(e);
        return (x, y, z, clk);
    }

    /// <summary>Klobuchar (GPS broadcast) L1 ionospheric delay in metres.</summary>
    public static double Klobuchar(double[] alpha, double[] beta, double latDeg, double lonDeg, double azDeg, double elDeg, double gpsSeconds)
    {
        double el = elDeg / 180, az = azDeg * Math.PI / 180, phiU = latDeg / 180, lamU = lonDeg / 180;
        double psi = 0.0137 / (el + 0.11) - 0.022;
        double phiI = Math.Clamp(phiU + psi * Math.Cos(az), -0.416, 0.416);
        double lamI = lamU + psi * Math.Sin(az) / Math.Cos(phiI * Math.PI);
        double phiM = phiI + 0.064 * Math.Cos((lamI - 1.617) * Math.PI);
        double t = ((43200 * lamI + gpsSeconds) % 86400 + 86400) % 86400;
        double amp = alpha[0] + phiM * (alpha[1] + phiM * (alpha[2] + phiM * alpha[3]));
        double per = beta[0] + phiM * (beta[1] + phiM * (beta[2] + phiM * beta[3]));
        amp = Math.Max(amp, 0); per = Math.Max(per, 72000);
        double x = 2 * Math.PI * (t - 50400) / per;
        double f = 1 + 16 * Math.Pow(0.53 - el, 3);
        double delay = Math.Abs(x) < 1.57 ? f * (5e-9 + amp * (1 - x * x / 2 + x * x * x * x / 24)) : f * 5e-9;
        return delay * C;
    }

    /// <summary>Saastamoinen tropospheric delay (standard atmosphere) in metres.</summary>
    public static double Saastamoinen(double latDeg, double heightM, double elDeg)
    {
        if (elDeg <= 0) return 0;
        double h = Math.Clamp(heightM, -100, 9000);
        double p = 1013.25 * Math.Pow(1 - 2.2557e-5 * h, 5.2568), temp = 15 - 6.5e-3 * h + 273.16, hum = 0.7;
        double e = 6.108 * hum * Math.Exp((17.15 * temp - 4684) / (temp - 38.45));
        double z = Math.PI / 2 - elDeg * Math.PI / 180;
        double zhd = 0.0022768 * p / (1 - 0.00266 * Math.Cos(2 * latDeg * Math.PI / 180) - 0.00028 * h / 1000);
        double zwd = 0.002277 * (1255 / temp + 0.05) * e;
        return (zhd + zwd) / Math.Cos(z);
    }

    /// <summary>ECEF → geodetic (WGS 84) degrees / metres.</summary>
    public static (double Lon, double Lat, double H) EcefToGeodetic(double x, double y, double z)
    {
        CoordinateTransformer.GeocentricToGeodetic(Ellipsoid.Wgs84, x, y, z, out var lon, out var lat, out var h);
        return (lon * 180 / Math.PI, lat * 180 / Math.PI, h);
    }

    public static (double X, double Y, double Z) GeodeticToEcef(double lonDeg, double latDeg, double h)
    {
        CoordinateTransformer.GeodeticToGeocentric(Ellipsoid.Wgs84, lonDeg * Math.PI / 180, latDeg * Math.PI / 180, h, out var x, out var y, out var z);
        return (x, y, z);
    }

    /// <summary>Azimuth and elevation (degrees) of a satellite seen from a receiver (ECEF).</summary>
    public static (double Az, double El) AzEl((double X, double Y, double Z) rx, (double X, double Y, double Z) sat)
    {
        var (lon, lat, _) = EcefToGeodetic(rx.X, rx.Y, rx.Z);
        double la = lat * Math.PI / 180, lo = lon * Math.PI / 180;
        double dx = sat.X - rx.X, dy = sat.Y - rx.Y, dz = sat.Z - rx.Z;
        double e = -Math.Sin(lo) * dx + Math.Cos(lo) * dy;
        double n = -Math.Sin(la) * Math.Cos(lo) * dx - Math.Sin(la) * Math.Sin(lo) * dy + Math.Cos(la) * dz;
        double u = Math.Cos(la) * Math.Cos(lo) * dx + Math.Cos(la) * Math.Sin(lo) * dy + Math.Sin(la) * dz;
        return (Angles.Normalize(Math.Atan2(e, n) * 180 / Math.PI), Math.Atan2(u, Math.Sqrt(e * e + n * n)) * 180 / Math.PI);
    }

    /// <summary>
    /// Single-point positioning of every epoch: GPS C/A pseudoranges, broadcast orbits/clocks, TGD, Earth rotation, Klobuchar and Saastamoinen,
    /// elevation-weighted least squares. Returns a frame with time, lon, lat, h, x, y, z, clock_m, satellites, pdop, hdop, vdop, rms.
    /// </summary>
    public static GeoDataFrame SinglePointPositioning(RinexObservation obs, RinexNavigation nav, SppOptions? options = null)
    {
        options ??= new SppOptions();
        var time = new List<DateTime>(); var lon = new List<double>(); var lat = new List<double>(); var hgt = new List<double>();
        var xs = new List<double>(); var ys = new List<double>(); var zs = new List<double>(); var clk = new List<double>();
        var nsat = new List<long>(); var pdop = new List<double>(); var hdop = new List<double>(); var vdop = new List<double>(); var rms = new List<double>();
        var x0 = obs.ApproxPosition ?? (0, 0, 0);
        foreach (var ep in obs.Epochs)
        {
            var sol = SolveEpoch(ep, nav, options, x0);
            // RAIM-style FDE: exclude the satellite whose removal best restores consistency
            var current = ep;
            while (sol is { } bad && options.FdeRmsThreshold > 0 && bad.Rms > options.FdeRmsThreshold && bad.N > options.MinSatellites + 1)
            {
                (RinexEpoch Ep, (double X, double Y, double Z, double Clock, int N, double Pdop, double Hdop, double Vdop, double Rms) Sol)? best = null;
                foreach (var sat in current.Satellites.Keys.Where(k => k[0] == 'G'))
                {
                    var sub = new RinexEpoch(current.Time, current.Flag, current.Satellites.Where(kv => kv.Key != sat).ToDictionary(kv => kv.Key, kv => kv.Value));
                    if (SolveEpoch(sub, nav, options, x0) is { } r && (best is null || r.Rms < best.Value.Sol.Rms)) best = (sub, r);
                }
                if (best is null || best.Value.Sol.Rms > bad.Rms * 0.5) break;
                current = best.Value.Ep;
                sol = best.Value.Sol;
            }
            if (sol is null) continue;
            var s = sol.Value;
            x0 = (s.X, s.Y, s.Z);
            var (lo, la, h) = EcefToGeodetic(s.X, s.Y, s.Z);
            time.Add(ep.Time); lon.Add(lo); lat.Add(la); hgt.Add(h); xs.Add(s.X); ys.Add(s.Y); zs.Add(s.Z); clk.Add(s.Clock);
            nsat.Add(s.N); pdop.Add(s.Pdop); hdop.Add(s.Hdop); vdop.Add(s.Vdop); rms.Add(s.Rms);
        }
        var geoms = lon.Select((l, i) => (NtsGeometry?)Geo.Point(l, lat[i], hgt[i])).ToArray();
        return GeoDataFrame.FromGeometries(geoms, CoordinateReferenceSystem.Wgs84,
            Column.Create("time", time.ToArray()), Column.Create("lon", lon.ToArray()), Column.Create("lat", lat.ToArray()), Column.Create("h", hgt.ToArray()),
            Column.Create("x", xs.ToArray()), Column.Create("y", ys.ToArray()), Column.Create("z", zs.ToArray()), Column.Create("clock_m", clk.ToArray()),
            Column.Create("satellites", nsat.ToArray()), Column.Create("pdop", pdop.ToArray()), Column.Create("hdop", hdop.ToArray()),
            Column.Create("vdop", vdop.ToArray()), Column.Create("rms", rms.ToArray()));
    }

    /// <summary>Solves one epoch; null when fewer than the minimum satellites are usable.</summary>
    public static (double X, double Y, double Z, double Clock, int N, double Pdop, double Hdop, double Vdop, double Rms)? SolveEpoch(
        RinexEpoch ep, RinexNavigation nav, SppOptions options, (double X, double Y, double Z) start)
    {
        var (week, tow) = GpsTime(ep.Time);
        var meas = new List<(int Prn, double Pr, GpsEphemeris Eph)>();
        foreach (var (sat, values) in ep.Satellites)
        {
            if (sat[0] != 'G') continue;
            int prn = int.Parse(sat.AsSpan(1));
            double pr = double.NaN;
            foreach (var c in options.Codes) if (values.TryGetValue(c, out var v) && v > 1e6) { pr = v; break; }
            if (double.IsNaN(pr)) continue;
            if (nav.Select(prn, tow, week) is { } eph) meas.Add((prn, pr, eph));
        }
        if (meas.Count < options.MinSatellites) return null;
        double x = start.X, y = start.Y, z = start.Z, cdt = 0;
        int used = 0; double pd = double.NaN, hd = double.NaN, vd = double.NaN, rmsOut = double.NaN;
        for (int iter = 0; iter < 10; iter++)
        {
            var rows = new List<(double Ax, double Ay, double Az, double L, double W)>();
            bool havePos = x * x + y * y + z * z > 1e12;
            var (rlon, rlat, rh) = havePos ? EcefToGeodetic(x, y, z) : (0, 0, 0);
            foreach (var (prn, pr, eph) in meas)
            {
                // transmission time: receiver time − pseudorange/c − satellite clock
                double ttx = tow - pr / C;
                var (_, _, _, dts0) = SatellitePosition(eph, ttx);
                ttx -= dts0;
                var (sx, sy, sz, dts) = SatellitePosition(eph, ttx);
                // Earth rotation during the signal travel time
                double tau = Math.Sqrt((sx - x) * (sx - x) + (sy - y) * (sy - y) + (sz - z) * (sz - z)) / C;
                double th = OmegaE * tau;
                double rx = Math.Cos(th) * sx + Math.Sin(th) * sy, ry = -Math.Sin(th) * sx + Math.Cos(th) * sy;
                double rho = Math.Sqrt((rx - x) * (rx - x) + (ry - y) * (ry - y) + (sz - z) * (sz - z));
                double w = 1, iono = 0, tropo = 0;
                if (havePos && iter > 0)
                {
                    var (az, el) = AzEl((x, y, z), (rx, ry, sz));
                    if (el < options.ElevationMask) continue;
                    double se = Math.Sin(el * Math.PI / 180);
                    w = se * se;
                    if (options.Ionosphere && nav.IonoAlpha is { } a && nav.IonoBeta is { } b) iono = Klobuchar(a, b, rlat, rlon, az, el, tow);
                    if (options.Troposphere) tropo = Saastamoinen(rlat, rh, el);
                }
                double predicted = rho + cdt - C * (dts - eph.Tgd) + iono + tropo;
                rows.Add(((x - rx) / rho, (y - ry) / rho, (z - sz) / rho, pr - predicted, w));
            }
            if (rows.Count < options.MinSatellites) return null;
            // weighted normal equations (4 unknowns)
            var n = new double[4, 4]; var u = new double[4];
            foreach (var (ax, ay, az, l, w) in rows)
            {
                double[] g = [ax, ay, az, 1];
                for (int i = 0; i < 4; i++) { u[i] += g[i] * w * l; for (int j = 0; j < 4; j++) n[i, j] += g[i] * w * g[j]; }
            }
            var inv = Invert4(n);
            if (inv is null) return null;
            var d = new double[4];
            for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) d[i] += inv[i, j] * u[j];
            x += d[0]; y += d[1]; z += d[2]; cdt += d[3];
            used = rows.Count;
            // unweighted DOPs in the local frame and residual rms
            var q = new double[4, 4];
            foreach (var r in rows) { double[] g = [r.Ax, r.Ay, r.Az, 1]; for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) q[i, j] += g[i] * g[j]; }
            var qi = Invert4(q);
            if (qi is not null)
            {
                pd = Math.Sqrt(qi[0, 0] + qi[1, 1] + qi[2, 2]);
                var (lo2, la2, _) = EcefToGeodetic(x, y, z);
                double sl = Math.Sin(la2 * Math.PI / 180), cl = Math.Cos(la2 * Math.PI / 180), so = Math.Sin(lo2 * Math.PI / 180), co = Math.Cos(lo2 * Math.PI / 180);
                double[,] rot = { { -so, co, 0 }, { -sl * co, -sl * so, cl }, { cl * co, cl * so, sl } };
                var qe = new double[3, 3];
                for (int a = 0; a < 3; a++) for (int b = 0; b < 3; b++) { double s = 0; for (int k = 0; k < 3; k++) for (int l2 = 0; l2 < 3; l2++) s += rot[a, k] * qi[k, l2] * rot[b, l2]; qe[a, b] = s; }
                hd = Math.Sqrt(qe[0, 0] + qe[1, 1]); vd = Math.Sqrt(qe[2, 2]);
            }
            double ss = 0;
            foreach (var r in rows) { double res = r.L - (r.Ax * d[0] + r.Ay * d[1] + r.Az * d[2] + d[3]); ss += res * res; }
            rmsOut = Math.Sqrt(ss / Math.Max(1, rows.Count - 4));
            if (Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]) < 1e-4 && iter > 0) break;
        }
        return (x, y, z, cdt, used, pd, hd, vd, rmsOut);
    }

    private static double[,]? Invert4(double[,] m)
    {
        int n = 4;
        var a = new double[n, 2 * n];
        for (int i = 0; i < n; i++) { for (int j = 0; j < n; j++) a[i, j] = m[i, j]; a[i, n + i] = 1; }
        for (int c = 0; c < n; c++)
        {
            int piv = c;
            for (int r = c + 1; r < n; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[piv, c])) piv = r;
            if (Math.Abs(a[piv, c]) < 1e-15) return null;
            if (piv != c) for (int k = 0; k < 2 * n; k++) (a[c, k], a[piv, k]) = (a[piv, k], a[c, k]);
            double d = a[c, c];
            for (int k = 0; k < 2 * n; k++) a[c, k] /= d;
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                double f = a[r, c];
                for (int k = 0; k < 2 * n; k++) a[r, k] -= f * a[c, k];
            }
        }
        var inv = new double[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) inv[i, j] = a[i, n + j];
        return inv;
    }
}
