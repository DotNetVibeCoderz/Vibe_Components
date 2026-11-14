namespace Electrical.Pmu.C37118;

/// <summary>
/// CRC-CCITT used by the C37.118.2 CHK field: polynomial x¹⁶ + x¹² + x⁵ + 1 (0x1021), initial value 0xFFFF, no reflection,
/// no final XOR ("CRC-16/CCITT-FALSE"). Check value: ASCII "123456789" → 0x29B1.
/// </summary>
public static class CrcCcitt
{
    private static readonly ushort[] Table = BuildTable();

    private static ushort[] BuildTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort c = (ushort)(i << 8);
            for (int b = 0; b < 8; b++) c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ 0x1021 : c << 1);
            t[i] = c;
        }
        return t;
    }

    /// <summary>Computes the CRC of <paramref name="data"/>.</summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data) crc = (ushort)((crc << 8) ^ Table[((crc >> 8) ^ b) & 0xFF]);
        return crc;
    }
}
