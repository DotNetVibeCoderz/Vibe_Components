using System.Text;

namespace Medical.Bpjs.Crypto;

/// <summary>
/// Port of lz-string 1.5 (pieroxy, MIT) for the URI-safe variant used by BPJS VClaim and PCare responses
/// (<c>compressToEncodedURIComponent</c> / <c>decompressFromEncodedURIComponent</c>). Operates on UTF-16 code units
/// exactly like the JavaScript original, so output is byte-for-byte compatible.
/// </summary>
public static class LzString
{
    private const string UriAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-$";
    private static readonly Dictionary<char, int> UriReverse = UriAlphabet.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);

    public static string CompressToEncodedURIComponent(string input) =>
        input is null ? "" : Compress(input, 6, i => UriAlphabet[i]);

    public static string? DecompressFromEncodedURIComponent(string input)
    {
        if (input is null) return "";
        if (input.Length == 0) return null;
        input = input.Replace(' ', '+');
        return Decompress(input.Length, 32, i => UriReverse[input[i]]);
    }

    private static string Compress(string uncompressed, int bitsPerChar, Func<int, char> getCharFromInt)
    {
        var dictionary = new Dictionary<string, int>();
        var dictionaryToCreate = new HashSet<string>();
        string w = "";
        int enlargeIn = 2, dictSize = 3, numBits = 2, dataVal = 0, dataPosition = 0;
        var data = new StringBuilder();

        void WriteBit(int bit)
        {
            dataVal = (dataVal << 1) | bit;
            if (dataPosition == bitsPerChar - 1) { dataPosition = 0; data.Append(getCharFromInt(dataVal)); dataVal = 0; }
            else dataPosition++;
        }
        void WriteBits(int value, int count)
        {
            for (int i = 0; i < count; i++) { WriteBit(value & 1); value >>= 1; }
        }
        void EmitW()
        {
            if (dictionaryToCreate.Contains(w))
            {
                int ch = w[0];
                if (ch < 256) { WriteBits(0, numBits); WriteBits(ch, 8); }
                else { WriteBits(1, numBits); WriteBits(ch, 16); }
                if (--enlargeIn == 0) { enlargeIn = 1 << numBits; numBits++; }
                dictionaryToCreate.Remove(w);
            }
            else WriteBits(dictionary[w], numBits);
            if (--enlargeIn == 0) { enlargeIn = 1 << numBits; numBits++; }
        }

        foreach (char c in uncompressed)
        {
            string cs = c.ToString();
            if (!dictionary.ContainsKey(cs)) { dictionary[cs] = dictSize++; dictionaryToCreate.Add(cs); }
            string wc = w + cs;
            if (dictionary.ContainsKey(wc)) w = wc;
            else
            {
                EmitW();
                dictionary[wc] = dictSize++;
                w = cs;
            }
        }
        if (w.Length > 0) EmitW();
        WriteBits(2, numBits); // end of stream
        while (true)
        {
            dataVal <<= 1;
            if (dataPosition == bitsPerChar - 1) { data.Append(getCharFromInt(dataVal)); break; }
            dataPosition++;
        }
        return data.ToString();
    }

    private static string? Decompress(int length, int resetValue, Func<int, int> getNextValue)
    {
        var dictionary = new List<string> { "0", "1", "2" };
        int enlargeIn = 4, dictSize = 4, numBits = 3;
        var result = new StringBuilder();
        int val = getNextValue(0), position = resetValue, index = 1;

        int ReadBits(int count)
        {
            int bits = 0, power = 1, maxpower = 1 << count;
            while (power != maxpower)
            {
                int resb = val & position;
                position >>= 1;
                if (position == 0)
                {
                    position = resetValue;
                    val = index < length ? getNextValue(index++) : 0;
                }
                bits |= (resb > 0 ? 1 : 0) * power;
                power <<= 1;
            }
            return bits;
        }

        string c;
        switch (ReadBits(2))
        {
            case 0: c = ((char)ReadBits(8)).ToString(); break;
            case 1: c = ((char)ReadBits(16)).ToString(); break;
            case 2: return "";
            default: return null;
        }
        dictionary.Add(c);
        string w = c;
        result.Append(c);
        while (true)
        {
            if (index > length) return "";
            int cc = ReadBits(numBits);
            switch (cc)
            {
                case 0:
                    dictionary.Add(((char)ReadBits(8)).ToString());
                    cc = dictSize++ ;
                    enlargeIn--;
                    break;
                case 1:
                    dictionary.Add(((char)ReadBits(16)).ToString());
                    cc = dictSize++;
                    enlargeIn--;
                    break;
                case 2:
                    return result.ToString();
            }
            if (enlargeIn == 0) { enlargeIn = 1 << numBits; numBits++; }
            string entry;
            if (cc < dictionary.Count) entry = dictionary[cc];
            else if (cc == dictSize) entry = w + w[0];
            else return null;
            result.Append(entry);
            dictionary.Add(w + entry[0]);
            dictSize++;
            enlargeIn--;
            w = entry;
            if (enlargeIn == 0) { enlargeIn = 1 << numBits; numBits++; }
        }
    }
}
