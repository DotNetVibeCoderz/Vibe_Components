using System.Buffers.Binary;
using System.Text;
using Medical.Core;

namespace Medical.Signals;

/// <summary>An ECG read from an SCP-ECG file, with the record header and decoding notes.</summary>
public sealed record ScpEcgRecord(Ecg12 Ecg, string PatientId, string? LastName, string? FirstName, Gender Sex, DateTime? Acquired,
    bool CrcValid, IReadOnlyList<string> Notes);

/// <summary>
/// SCP-ECG (EN 1064 / ISO 11073-91064) rhythm data: reads and writes Sections 0 (pointers), 1 (header tags),
/// 2 (Huffman tables: none or the default table), 3 (lead definition) and 6 (rhythm data, raw or first / second
/// differences). Files using bimodal compression or reference-beat subtraction (Sections 4/5) are rejected with a clear
/// error. Limb leads III, aVR, aVL, aVF are derived when a device stores only I, II and V1–V6.
/// </summary>
public static class ScpEcg
{
    private static readonly (EcgLead Lead, byte Id)[] LeadIds =
    [
        (EcgLead.I, 1), (EcgLead.II, 2), (EcgLead.V1, 3), (EcgLead.V2, 4), (EcgLead.V3, 5), (EcgLead.V4, 6), (EcgLead.V5, 7), (EcgLead.V6, 8),
        (EcgLead.III, 61), (EcgLead.aVR, 62), (EcgLead.aVL, 63), (EcgLead.aVF, 64),
    ];

    // Default Huffman table (EN 1064 Annex): ±0…8 direct, then escapes for 8- and 16-bit original values.
    private static readonly (string Code, int Value, int EscapeBits)[] DefaultTable =
    [
        ("0", 0, 0), ("100", 1, 0), ("101", -1, 0), ("1100", 2, 0), ("1101", -2, 0), ("11100", 3, 0), ("11101", -3, 0),
        ("111100", 4, 0), ("111101", -4, 0), ("1111100", 5, 0), ("1111101", -5, 0), ("11111100", 6, 0), ("11111101", -6, 0),
        ("111111100", 7, 0), ("111111101", -7, 0), ("1111111100", 8, 0), ("1111111101", -8, 0),
        ("1111111110", 0, 8), ("1111111111", 0, 16),
    ];

    /// <summary>
    /// Writes an SCP-ECG file. <paramref name="huffman"/> stores second differences with the default Huffman table
    /// (typically 2–4× smaller, depending on noise); otherwise raw 16-bit samples. <paramref name="amplitudeNv"/> is the amplitude value
    /// multiplier (nV per unit; 5000 = 5 µV resolution).
    /// </summary>
    public static byte[] Write(Ecg12 ecg, string patientId, string? lastName = null, string? firstName = null, Gender sex = Gender.Unknown,
        DateTime? acquired = null, bool huffman = true, int amplitudeNv = 5000)
    {
        var at = acquired ?? DateTime.Now;
        var sections = new SortedDictionary<int, byte[]>
        {
            [1] = Section(1, Section1(patientId, lastName, firstName, sex, at)),
            [3] = Section(3, Section3(ecg.Samples)),
            [6] = Section(6, Section6(ecg, huffman, amplitudeNv)),
        };
        if (huffman)
        {
            var s2 = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(s2, 19999);         // "use the default table"
            sections[2] = Section(2, s2);
        }

        // Section 0: pointers to sections 0 … 11 (absent ones have length 0 and index 0).
        const int pointerSize = 10, ids = 12;
        int sec0Len = 16 + ids * pointerSize;
        int offset = 6 + sec0Len;
        var index = new Dictionary<int, (int Length, int Index)> { [0] = (sec0Len, 7) };
        foreach (var (id, bytes) in sections) { index[id] = (bytes.Length, offset + 1); offset += bytes.Length; }
        var p0 = new byte[ids * pointerSize];
        for (int id = 0; id < ids; id++)
        {
            var (len, idx) = index.TryGetValue(id, out var v) ? v : (0, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(p0.AsSpan(id * pointerSize), (ushort)id);
            BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(id * pointerSize + 2), len);
            BinaryPrimitives.WriteInt32LittleEndian(p0.AsSpan(id * pointerSize + 6), idx);
        }
        var sec0 = Section(0, p0, "SCPECG"u8);

        var file = new byte[offset];
        sec0.CopyTo(file, 6);
        int pos = 6 + sec0.Length;
        foreach (var bytes in sections.Values) { bytes.CopyTo(file, pos); pos += bytes.Length; }
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file, Crc(file.AsSpan(2)));
        return file;
    }

    /// <summary>Reads an SCP-ECG file (see the class remarks for the supported encodings).</summary>
    public static ScpEcgRecord Read(ReadOnlySpan<byte> file)
    {
        if (file.Length < 6 + 16) throw new InvalidDataException("Too short for an SCP-ECG file.");
        var notes = new List<string>();
        bool crcOk = BinaryPrimitives.ReadUInt16LittleEndian(file) == Crc(file[2..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(file[2..]);
        if (size != file.Length) notes.Add($"Header size {size} differs from file length {file.Length}.");

        // Section 0 pointers
        var sec0 = file[6..];
        int sec0Len = BinaryPrimitives.ReadInt32LittleEndian(sec0[4..]);
        var ptr = new Dictionary<int, (int Length, int Index)>();
        for (int o = 16; o + 10 <= sec0Len; o += 10)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(sec0[o..]);
            int len = BinaryPrimitives.ReadInt32LittleEndian(sec0[(o + 2)..]);
            int idx = BinaryPrimitives.ReadInt32LittleEndian(sec0[(o + 6)..]);
            if (len > 0 && idx > 0 && idx - 1 + len <= file.Length) ptr[id] = (len, idx - 1);
        }
        ReadOnlySpan<byte> Body(ReadOnlySpan<byte> f, int id, out bool ok)
        {
            ok = false;
            if (!ptr.TryGetValue(id, out var p)) return [];
            var sec = f.Slice(p.Index, p.Length);
            ok = BinaryPrimitives.ReadUInt16LittleEndian(sec) == Crc(sec[2..]);
            return sec[16..];
        }

        var s1 = Body(file, 1, out bool c1);
        var s2 = Body(file, 2, out bool c2);
        var s3 = Body(file, 3, out bool c3);
        var s6 = Body(file, 6, out bool c6);
        if (s3.Length == 0 || s6.Length == 0) throw new InvalidDataException("Sections 3 (lead definition) and 6 (rhythm data) are required.");
        crcOk &= c3 && c6 && (s1.Length == 0 || c1) && (s2.Length == 0 || c2);
        if (ptr.ContainsKey(4) || ptr.ContainsKey(5)) notes.Add("Reference beats (sections 4/5) present; rhythm data must not use subtraction.");

        // Header tags
        string? last = null, first = null, pid = "";
        var sex = Gender.Unknown; DateTime? date = null; TimeSpan time = default;
        for (int o = 0; o + 3 <= s1.Length;)
        {
            byte tag = s1[o]; int len = BinaryPrimitives.ReadUInt16LittleEndian(s1[(o + 1)..]);
            if (tag == 255) break;
            var v = s1.Slice(o + 3, Math.Min(len, s1.Length - o - 3));
            string Str(ReadOnlySpan<byte> b) => Encoding.Latin1.GetString(b).TrimEnd('\0').Trim();
            switch (tag)
            {
                case 0: last = Str(v); break;
                case 1: first = Str(v); break;
                case 2: pid = Str(v); break;
                case 8 when v.Length >= 1: sex = v[0] switch { 1 => Gender.Male, 2 => Gender.Female, _ => Gender.Unknown }; break;
                case 25 when v.Length >= 4: date = new DateTime(BinaryPrimitives.ReadUInt16LittleEndian(v), Math.Max(1, (int)v[2]), Math.Max(1, (int)v[3])); break;
                case 26 when v.Length >= 3: time = new TimeSpan(v[0], v[1], v[2]); break;
            }
            o += 3 + len;
        }

        // Huffman tables
        bool huffman = false;
        if (s2.Length >= 2)
        {
            int tables = BinaryPrimitives.ReadUInt16LittleEndian(s2);
            if (tables == 19999) huffman = true;
            else if (tables != 0) throw new NotSupportedException("Custom Huffman tables are not supported (only the default table).");
        }

        // Lead definition
        int nLeads = s3[0]; byte flags = s3[1];
        if ((flags & 1) != 0) throw new NotSupportedException("Reference-beat subtraction is not supported.");
        var leads = new List<(int Start, int End, byte Id)>();
        for (int i = 0; i < nLeads; i++)
        {
            var e = s3.Slice(2 + i * 9, 9);
            leads.Add((BinaryPrimitives.ReadInt32LittleEndian(e) - 1, BinaryPrimitives.ReadInt32LittleEndian(e[4..]) - 1, e[8]));
        }

        // Rhythm data
        int avm = BinaryPrimitives.ReadUInt16LittleEndian(s6);
        int intervalUs = BinaryPrimitives.ReadUInt16LittleEndian(s6[2..]);
        int encoding = s6[4], bimodal = s6[5];
        if (bimodal != 0) throw new NotSupportedException("Bimodal compression is not supported.");
        if (encoding > 2) throw new NotSupportedException($"Difference encoding {encoding} is not supported.");
        int dataStart = 6 + 2 * nLeads;
        int n = leads.Max(l => l.End - l.Start + 1);
        var output = new float[12][];
        for (int i = 0, o = dataStart; i < nLeads; i++)
        {
            int byteLen = BinaryPrimitives.ReadUInt16LittleEndian(s6[(6 + 2 * i)..]);
            var raw = s6.Slice(o, byteLen);
            o += byteLen;
            int count = leads[i].End - leads[i].Start + 1;
            var values = huffman ? HuffmanDecode(raw, count) : RawDecode(raw, count);
            if (encoding == 1) for (int k = 1; k < values.Length; k++) values[k] += values[k - 1];
            else if (encoding == 2) for (int k = 2; k < values.Length; k++) values[k] += 2 * values[k - 1] - values[k - 2];
            var lead = LeadIds.FirstOrDefault(x => x.Id == leads[i].Id);
            if (lead.Id == 0) { notes.Add($"Lead id {leads[i].Id} ignored."); continue; }
            var x = new float[n];
            for (int k = 0; k < values.Length && leads[i].Start + k < n; k++) x[leads[i].Start + k] = (float)(values[k] * (double)avm / 1_000_000);
            output[(int)lead.Lead] = x;
        }
        if (output[0] is not null && output[1] is not null && output[2] is null && output[3] is null && output[4] is null && output[5] is null)
        {
            for (int l = 2; l <= 5; l++) output[l] = new float[n];
            Ecg12.DeriveLimbLeads(output[0], output[1], output[2], output[3], output[4], output[5]);
            notes.Add("III, aVR, aVL and aVF derived from I and II.");
        }
        for (int l = 0; l < 12; l++) output[l] ??= new float[n];
        if (!crcOk) notes.Add("CRC mismatch (file or section).");
        return new ScpEcgRecord(new Ecg12(output, 1_000_000.0 / intervalUs), pid, last, first, sex, date?.Add(time), crcOk, notes);
    }

    // ------------------------------------------------------------------ sections

    private static byte[] Section(int id, ReadOnlySpan<byte> body, ReadOnlySpan<byte> reserved = default)
    {
        int len = 16 + body.Length;
        len += len % 2;                                   // sections have an even length
        var s = new byte[len];
        BinaryPrimitives.WriteUInt16LittleEndian(s.AsSpan(2), (ushort)id);
        BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(4), len);
        s[8] = 20; s[9] = 20;                             // section / protocol version 2.0
        reserved.CopyTo(s.AsSpan(10, 6));
        body.CopyTo(s.AsSpan(16));
        BinaryPrimitives.WriteUInt16LittleEndian(s, Crc(s.AsSpan(2)));
        return s;
    }

    private static byte[] Section1(string patientId, string? last, string? first, Gender sex, DateTime at)
    {
        var ms = new MemoryStream();
        void Tag(byte tag, ReadOnlySpan<byte> value)
        {
            ms.WriteByte(tag);
            Span<byte> l = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(l, (ushort)value.Length);
            ms.Write(l);
            ms.Write(value);
        }
        byte[] Z(string s) => [.. Encoding.Latin1.GetBytes(s), 0];
        if (last is not null) Tag(0, Z(last));
        if (first is not null) Tag(1, Z(first));
        Tag(2, Z(patientId));
        Tag(8, [sex switch { Gender.Male => (byte)1, Gender.Female => (byte)2, _ => (byte)0 }]);
        // Tag 14: acquiring device
        var dev = new MemoryStream();
        dev.Write([0, 0, 0, 0, 1, 0]);                    // institution, department, device id
        dev.WriteByte(0);                                 // device type: cart
        dev.WriteByte(255);                               // manufacturer: other
        dev.Write("MEDNET"u8);                            // model description (6 bytes)
        dev.WriteByte(20);                                // SCP-ECG protocol revision 2.0
        dev.WriteByte(0xD0);                              // compatibility level: category II
        dev.WriteByte(0);                                 // language: 8-bit ASCII
        dev.WriteByte(0);                                 // capabilities
        dev.WriteByte(1);                                 // AC mains 50 Hz
        dev.Write(new byte[16]);                          // reserved
        var strings = new[] { "", "MEDNET-1", MedicalInfo.Version, $"Medical.Net {MedicalInfo.Version}", "Gravicode Studios" };
        dev.WriteByte(1);                                 // length of the first string (empty, null only)
        foreach (var s in strings) dev.Write(Z(s));
        Tag(14, dev.ToArray());
        Tag(25, [(byte)(at.Year & 0xFF), (byte)(at.Year >> 8), (byte)at.Month, (byte)at.Day]);
        Tag(26, [(byte)at.Hour, (byte)at.Minute, (byte)at.Second]);
        Tag(255, []);
        return ms.ToArray();
    }

    private static byte[] Section3(int samples)
    {
        var s = new byte[2 + 12 * 9];
        s[0] = 12;
        s[1] = (12 << 3) | 4;                             // all leads recorded simultaneously, no subtraction
        for (int i = 0; i < 12; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(2 + i * 9), 1);
            BinaryPrimitives.WriteInt32LittleEndian(s.AsSpan(2 + i * 9 + 4), samples);
            s[2 + i * 9 + 8] = LeadIds[i].Id;
        }
        return s;
    }

    private static byte[] Section6(Ecg12 ecg, bool huffman, int avm)
    {
        var header = new byte[6 + 2 * 12];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)avm);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), (ushort)Math.Round(1_000_000 / ecg.SampleRate));
        header[4] = (byte)(huffman ? 2 : 0);              // second differences with Huffman, raw otherwise
        header[5] = 0;
        var data = new List<byte[]>();
        for (int i = 0; i < 12; i++)
        {
            var x = ecg[LeadIds[i].Lead];
            var q = new int[x.Length];
            for (int k = 0; k < x.Length; k++) q[k] = (int)Math.Clamp(Math.Round(x[k] * 1_000_000.0 / avm, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            byte[] bytes;
            if (huffman)
            {
                var d = new int[q.Length];
                for (int k = 0; k < q.Length; k++) d[k] = k < 2 ? q[k] : q[k] - 2 * q[k - 1] + q[k - 2];
                bytes = HuffmanEncode(d);
            }
            else
            {
                bytes = new byte[q.Length * 2];
                for (int k = 0; k < q.Length; k++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(k * 2), (short)q[k]);
            }
            if (bytes.Length > ushort.MaxValue) throw new InvalidOperationException("Lead data exceeds 65535 bytes; shorten the recording.");
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6 + 2 * i), (ushort)bytes.Length);
            data.Add(bytes);
        }
        return [.. header, .. data.SelectMany(b => b)];
    }

    // ------------------------------------------------------------------ Huffman and CRC

    private static byte[] HuffmanEncode(int[] values)
    {
        var bits = new List<bool>(values.Length * 4);
        void Put(string code) { foreach (char c in code) bits.Add(c == '1'); }
        void PutBits(int v, int n) { for (int b = n - 1; b >= 0; b--) bits.Add(((v >> b) & 1) == 1); }
        foreach (int v in values)
        {
            if (v is >= -8 and <= 8) Put(DefaultTable.First(t => t.EscapeBits == 0 && t.Value == v).Code);
            else if (v is >= sbyte.MinValue and <= sbyte.MaxValue) { Put("1111111110"); PutBits(v & 0xFF, 8); }
            else { Put("1111111111"); PutBits(v & 0xFFFF, 16); }
        }
        var bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) if (bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
        return bytes;
    }

    private static int[] HuffmanDecode(ReadOnlySpan<byte> data, int count)
    {
        var result = new int[count];
        int bit = 0, total = data.Length * 8, k = 0;
        int Next(ReadOnlySpan<byte> d) { int b = (d[bit >> 3] >> (7 - (bit & 7))) & 1; bit++; return b; }
        while (k < count && bit < total)
        {
            // Codes are a run of ones: "1…1 0 s" (n = 1…8 ones, then the terminating 0 and a sign bit) codes ±n;
            // "0" codes 0; nine ones + 0 escapes an 8-bit original value, ten ones a 16-bit one.
            int ones = 0;
            while (ones < 10 && bit < total && Next(data) == 1) ones++;
            int v;
            if (ones == 0) v = 0;
            else if (ones <= 8)
            {
                if (bit >= total) break;
                v = Next(data) == 0 ? ones : -ones;
            }
            else if (ones <= 10)
            {
                int width = ones == 9 ? 8 : 16, raw = 0;
                if (bit + width > total) break;
                for (int b = 0; b < width; b++) raw = (raw << 1) | Next(data);
                v = width == 8 ? (sbyte)raw : (short)raw;
            }
            else break;
            result[k++] = v;
        }
        return result;
    }

    private static int[] RawDecode(ReadOnlySpan<byte> data, int count)
    {
        var r = new int[Math.Min(count, data.Length / 2)];
        for (int i = 0; i < r.Length; i++) r[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
        return r;
    }

    /// <summary>CRC-CCITT (polynomial 0x1021, initial value 0xFFFF), as specified for SCP-ECG.</summary>
    public static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }
}
