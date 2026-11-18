using System.Globalization;

namespace GeoPandas.Net.Survey;

/// <summary>GPS broadcast ephemeris (LNAV) as defined in IS-GPS-200 (angles in radians, times in GPS seconds of week).</summary>
public sealed record GpsEphemeris(
    int Prn, DateTime Toc, double Af0, double Af1, double Af2,
    double Iode, double Crs, double DeltaN, double M0, double Cuc, double E, double Cus, double SqrtA,
    double Toe, double Cic, double Omega0, double Cis, double I0, double Crc, double Omega, double OmegaDot,
    double IDot, double Week, double Health, double Tgd, double Iodc);

/// <summary>RINEX 2.x / 3.x GPS navigation message.</summary>
public sealed class RinexNavigation
{
    public string Version { get; init; } = "";
    public IReadOnlyList<GpsEphemeris> Ephemerides { get; init; } = [];
    /// <summary>Klobuchar α0..α3 and β0..β3 (null when the header has none).</summary>
    public double[]? IonoAlpha { get; init; }
    public double[]? IonoBeta { get; init; }

    /// <summary>The healthy ephemeris of <paramref name="prn"/> with the closest Toe (within 4 h).</summary>
    public GpsEphemeris? Select(int prn, double gpsWeekSeconds, int week)
    {
        GpsEphemeris? best = null; double bestDt = double.MaxValue;
        foreach (var e in Ephemerides)
        {
            if (e.Prn != prn || e.Health != 0) continue;
            double dt = Math.Abs((week - e.Week) * 604800 + gpsWeekSeconds - e.Toe);
            if (dt < bestDt && dt <= 4 * 3600) { best = e; bestDt = dt; }
        }
        return best;
    }

    public static RinexNavigation Read(string path)
    {
        var lines = File.ReadAllLines(path);
        int i = 0;
        string version = "";
        double[]? alpha = null, beta = null;
        for (; i < lines.Length; i++)
        {
            var l = lines[i];
            string label = l.Length > 60 ? l[60..].Trim() : "";
            if (label.StartsWith("RINEX VERSION")) version = l[..9].Trim();
            else if (label == "ION ALPHA") alpha = Nums(l[2..60], 4);
            else if (label == "ION BETA") beta = Nums(l[2..60], 4);
            else if (label == "IONOSPHERIC CORR")
            {
                if (l.StartsWith("GPSA")) alpha = Nums(l[5..60], 4);
                else if (l.StartsWith("GPSB")) beta = Nums(l[5..60], 4);
            }
            else if (label == "END OF HEADER") { i++; break; }
        }
        bool v3 = version.StartsWith('3') || version.StartsWith('4');
        var list = new List<GpsEphemeris>();
        while (i < lines.Length)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l)) { i++; continue; }
            if (v3 && l[0] != 'G')
            {
                // skip other constellations: GLONASS/SBAS records have 3 orbit lines, others 7
                int skip = l[0] is 'R' or 'S' ? 4 : 8;
                i += skip;
                continue;
            }
            if (i + 7 >= lines.Length) break;
            int prn; DateTime toc; double[] clk;
            if (v3)
            {
                prn = int.Parse(l.Substring(1, 2), CultureInfo.InvariantCulture);
                toc = new DateTime(Int(l, 4, 4), Int(l, 9, 2), Int(l, 12, 2), Int(l, 15, 2), Int(l, 18, 2), Int(l, 21, 2), DateTimeKind.Utc);
                clk = Fixed(l, 23, 3);
            }
            else
            {
                prn = Int(l, 0, 2);
                int yy = Int(l, 3, 2);
                double sec = Dbl(l.Substring(17, 5));
                toc = new DateTime(yy < 80 ? 2000 + yy : 1900 + yy, Int(l, 6, 2), Int(l, 9, 2), Int(l, 12, 2), Int(l, 15, 2), 0, DateTimeKind.Utc).AddSeconds(sec);
                clk = Fixed(l, 22, 3);
            }
            int start = v3 ? 4 : 3;
            var o = new double[28];
            for (int k = 0; k < 7; k++)
            {
                var vals = Fixed(lines[i + 1 + k], start, 4);
                for (int j = 0; j < 4; j++) o[k * 4 + j] = vals[j];
            }
            list.Add(new GpsEphemeris(prn, toc, clk[0], clk[1], clk[2],
                o[0], o[1], o[2], o[3], o[4], o[5], o[6], o[7], o[8], o[9], o[10], o[11], o[12], o[13], o[14], o[15],
                o[16], o[18], o[21], o[22], o[23]));
            i += 8;
        }
        return new RinexNavigation { Version = version, Ephemerides = list, IonoAlpha = alpha, IonoBeta = beta };
    }

    private static int Int(string s, int start, int len) => int.Parse(s.Substring(start, Math.Min(len, s.Length - start)).Trim(), CultureInfo.InvariantCulture);
    internal static double Dbl(string s) { s = s.Trim().Replace('D', 'E').Replace('d', 'e'); return s.Length == 0 ? 0 : double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

    private static double[] Fixed(string line, int start, int count)
    {
        var r = new double[count];
        for (int k = 0; k < count; k++)
        {
            int s = start + 19 * k;
            r[k] = s >= line.Length ? 0 : Dbl(line.Substring(s, Math.Min(19, line.Length - s)));
        }
        return r;
    }

    private static double[] Nums(string s, int n)
    {
        var r = new double[n];
        for (int k = 0; k < n; k++)
        {
            int st = k * 12;
            r[k] = st >= s.Length ? 0 : Dbl(s.Substring(st, Math.Min(12, s.Length - st)));
        }
        return r;
    }
}

/// <summary>One observation epoch: GPS time and, per satellite ("G05"), the observation values by RINEX code ("C1C", "C1", "L1"…).</summary>
public sealed record RinexEpoch(DateTime Time, int Flag, IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> Satellites);

/// <summary>RINEX 2.x / 3.x observation file (all constellations parsed; GPS used for positioning).</summary>
public sealed class RinexObservation
{
    public string Version { get; init; } = "";
    public string MarkerName { get; init; } = "";
    /// <summary>Header APPROX POSITION XYZ (ECEF metres), when present.</summary>
    public (double X, double Y, double Z)? ApproxPosition { get; init; }
    public double AntennaHeight { get; init; }
    public IReadOnlyDictionary<char, string[]> ObservationTypes { get; init; } = new Dictionary<char, string[]>();
    public IReadOnlyList<RinexEpoch> Epochs { get; init; } = [];

    public static RinexObservation Read(string path)
    {
        var lines = File.ReadAllLines(path);
        int i = 0;
        string version = "", marker = "";
        (double, double, double)? approx = null;
        double antH = 0;
        var types = new Dictionary<char, string[]>();
        var v2Types = new List<string>();
        char lastSys = ' ';
        int pendingTypes = 0;
        for (; i < lines.Length; i++)
        {
            var l = lines[i].PadRight(80);
            string label = l[60..].Trim();
            switch (label)
            {
                case "RINEX VERSION / TYPE": version = l[..9].Trim(); break;
                case "MARKER NAME": marker = l[..60].Trim(); break;
                case "APPROX POSITION XYZ":
                    var p = l[..60].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(RinexNavigation.Dbl).ToArray();
                    if (p.Length >= 3 && (p[0] != 0 || p[1] != 0)) approx = (p[0], p[1], p[2]);
                    break;
                case "ANTENNA: DELTA H/E/N": antH = RinexNavigation.Dbl(l[..14]); break;
                case "# / TYPES OF OBSERV":
                    if (l[..6].Trim().Length > 0) pendingTypes = int.Parse(l[..6].Trim(), CultureInfo.InvariantCulture);
                    for (int k = 0; k < 9 && v2Types.Count < pendingTypes; k++) v2Types.Add(l.Substring(10 + 6 * k, 2).Trim());
                    break;
                case "SYS / # / OBS TYPES":
                    if (l[0] != ' ') { lastSys = l[0]; types[lastSys] = []; }
                    var codes = l.Substring(7, 53).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    types[lastSys] = [.. types[lastSys], .. codes];
                    break;
            }
            if (label == "END OF HEADER") { i++; break; }
        }
        bool v3 = version.StartsWith('3') || version.StartsWith('4');
        if (!v3) foreach (var s in "GRESJC") types[s] = v2Types.ToArray();

        var epochs = new List<RinexEpoch>();
        while (i < lines.Length)
        {
            var l = lines[i];
            if (string.IsNullOrWhiteSpace(l)) { i++; continue; }
            if (v3)
            {
                if (l[0] != '>') { i++; continue; }
                if (Int2(l, 31, 1) > 1) { i += 1 + int.Parse(l.Substring(32, 3).Trim(), CultureInfo.InvariantCulture); continue; }
                var t = new DateTime(int.Parse(l.Substring(2, 4), CultureInfo.InvariantCulture), Int2(l, 7), Int2(l, 10), Int2(l, 13), Int2(l, 16), 0, DateTimeKind.Utc)
                    .AddSeconds(RinexNavigation.Dbl(l.Substring(18, 11)));
                int flag = Int2(l, 31, 1), n = int.Parse(l.Substring(32, 3).Trim(), CultureInfo.InvariantCulture);
                i++;
                if (flag > 1) { i += n; continue; }
                var sats = new Dictionary<string, IReadOnlyDictionary<string, double>>();
                for (int k = 0; k < n && i < lines.Length; k++, i++)
                {
                    var sl = lines[i];
                    string sat = sl[..3].Replace(' ', '0');
                    if (!types.TryGetValue(sat[0], out var tlist)) continue;
                    var obs = new Dictionary<string, double>();
                    for (int j = 0; j < tlist.Length; j++)
                    {
                        int st = 3 + 16 * j;
                        if (st >= sl.Length) break;
                        var f = sl.Substring(st, Math.Min(14, sl.Length - st));
                        if (!string.IsNullOrWhiteSpace(f)) obs[tlist[j]] = RinexNavigation.Dbl(f);
                    }
                    sats[sat] = obs;
                }
                epochs.Add(new RinexEpoch(t, flag, sats));
            }
            else
            {
                int evFlag = Int2(l, 28, 1);
                if (evFlag > 1)
                {
                    // event record: the date may be blank; skip the special records that follow
                    int count = l.Length >= 32 ? int.Parse(l.Substring(29, 3).Trim(), CultureInfo.InvariantCulture) : 0;
                    i += 1 + count;
                    continue;
                }
                int yy = Int2(l, 1);
                var t = new DateTime(yy < 80 ? 2000 + yy : 1900 + yy, Int2(l, 4), Int2(l, 7), Int2(l, 10), Int2(l, 13), 0, DateTimeKind.Utc)
                    .AddSeconds(RinexNavigation.Dbl(l.Substring(15, 11)));
                int flag = Int2(l, 28, 1), n = int.Parse(l.Substring(29, 3).Trim(), CultureInfo.InvariantCulture);
                var satIds = new List<string>();
                int lineSat = 0;
                while (satIds.Count < n)
                {
                    var sl = lines[i + lineSat];
                    for (int k = 0; k < 12 && satIds.Count < n; k++)
                    {
                        int st = 32 + 3 * k;
                        if (st + 3 > sl.Length) break;
                        var id = sl.Substring(st, 3);
                        satIds.Add((id[0] == ' ' ? 'G' : id[0]) + id[1..].Replace(' ', '0'));
                    }
                    lineSat++;
                }
                i += lineSat;
                if (flag > 1) { i += n; continue; }
                int perSat = (v2Types.Count + 4) / 5;
                var sats = new Dictionary<string, IReadOnlyDictionary<string, double>>();
                foreach (var sat in satIds)
                {
                    var obs = new Dictionary<string, double>();
                    for (int j = 0; j < v2Types.Count; j++)
                    {
                        var sl = i + j / 5 < lines.Length ? lines[i + j / 5] : "";
                        int st = 16 * (j % 5);
                        if (st >= sl.Length) continue;
                        var f = sl.Substring(st, Math.Min(14, sl.Length - st));
                        if (!string.IsNullOrWhiteSpace(f)) obs[v2Types[j]] = RinexNavigation.Dbl(f);
                    }
                    sats[sat] = obs;
                    i += perSat;
                }
                epochs.Add(new RinexEpoch(t, flag, sats));
            }
        }
        return new RinexObservation { Version = version, MarkerName = marker, ApproxPosition = approx, AntennaHeight = antH, ObservationTypes = types, Epochs = epochs };
    }

    private static int Int2(string s, int start, int len = 2)
    {
        if (start >= s.Length) return 0;
        var f = s.Substring(start, Math.Min(len, s.Length - start)).Trim();
        return f.Length == 0 ? 0 : int.Parse(f, CultureInfo.InvariantCulture);
    }
}
