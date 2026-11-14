using System.Globalization;
using Electrical.Core.Network;

namespace Electrical.Gallery.Shared;

/// <summary>Key figure shown as a KPI / annunciator tile by both apps.</summary>
/// <param name="Tone">"ok", "warn", "alarm" or null (neutral).</param>
public sealed record Kpi(L Label, string Value, string? Unit = null, L? Note = null, string? Tone = null);

/// <summary>Test cases offered by the demos.</summary>
public static class GalleryCases
{
    public static readonly (string Key, L Label)[] All =
    [
        ("case9", new("WSCC 9-bus", "WSCC 9 bus")),
        ("case14", new("IEEE 14-bus", "IEEE 14 bus")),
        ("case33bw", new("Baran–Wu 33-bus feeder", "Penyulang Baran–Wu 33 bus")),
        ("synthetic", new("Synthetic 36-bus grid", "Jaringan sintetis 36 bus")),
    ];

    /// <summary>Loads a case (the "synthetic" grid uses 36 buses so the single-line diagram stays readable).</summary>
    public static PowerNetwork Load(string key) => key == "synthetic" ? TestCases.SyntheticGrid(36) : TestCases.ByName(key);

    /// <summary>Scales every load (P and Q) and non-slack generator dispatch by <paramref name="factor"/>.</summary>
    public static PowerNetwork Scaled(PowerNetwork net, double factor)
    {
        var c = net.Clone();
        foreach (var l in c.Loads) { l.P *= factor; l.Q *= factor; }
        foreach (var g in c.Generators)
            if (c.GetBus(g.Bus).Type != BusType.Slack) g.P *= factor;
        return c;
    }

    /// <summary>Gives unrated branches (MATPOWER case14 has none) a rating of 140 % of their base-case flow, rounded up to 10 MVA.</summary>
    public static bool AssignRatings(PowerNetwork net, Electrical.PowerFlow.PowerFlowResult baseCase)
    {
        bool any = false;
        foreach (var br in baseCase.Branches)
        {
            var b = net.Branches[br.Index];
            if (b.RateA > 0) continue;
            b.RateA = Math.Max(10, Math.Ceiling(1.4 * Math.Max(br.SFrom, br.STo) / 10) * 10);
            any = true;
        }
        return any;
    }
}

internal static class Fmt
{
    public static string F(double v, string f = "0.0") => v.ToString(f, CultureInfo.InvariantCulture);
}

/// <summary>Hex dump helpers for protocol inspectors.</summary>
public static class HexDump
{
    /// <summary>One labelled byte range of a frame (offsets inclusive start, exclusive end).</summary>
    public sealed record Segment(int Start, int End, string Key, L Label);

    /// <summary>Row of 16 bytes: offset, bytes, ASCII.</summary>
    public sealed record Row(int Offset, byte[] Bytes);

    public static IReadOnlyList<Row> Rows(byte[] data, int width = 16) =>
        Enumerable.Range(0, (data.Length + width - 1) / width).Select(r => new Row(r * width, data.Skip(r * width).Take(width).ToArray())).ToArray();

    public static string Ascii(byte b) => b is >= 0x20 and < 0x7F ? ((char)b).ToString() : "·";

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    public static string Spaced(ReadOnlySpan<byte> b) => string.Join(' ', b.ToArray().Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Segment key for byte <paramref name="i"/> (or null).</summary>
    public static string? KeyAt(IReadOnlyList<Segment> segs, int i) => segs.FirstOrDefault(s => i >= s.Start && i < s.End)?.Key;
}
