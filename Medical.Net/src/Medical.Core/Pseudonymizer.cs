using System.Security.Cryptography;
using System.Text;

namespace Medical.Core;

/// <summary>
/// Keyed, deterministic pseudonyms (HMAC-SHA256): the same identifier always maps to the same token under one secret,
/// so records can be linked after de-identification while the original value cannot be recovered without the key.
/// </summary>
public sealed class Pseudonymizer
{
    private readonly byte[] _key;

    public Pseudonymizer(string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Length < 16) throw new ArgumentException("Use a secret of at least 16 characters.", nameof(secret));
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    /// <summary>Base32 (Crockford, no padding) token of <paramref name="length"/> characters, optionally prefixed.</summary>
    public string Pseudonym(string value, string prefix = "", int length = 12)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value), mac);
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var sb = new StringBuilder(prefix, prefix.Length + length);
        int bits = 0, acc = 0;
        foreach (var b in mac)
        {
            acc = (acc << 8) | b; bits += 8;
            while (bits >= 5 && sb.Length < prefix.Length + length)
            {
                sb.Append(alphabet[(acc >> (bits - 5)) & 31]);
                bits -= 5;
            }
            if (sb.Length >= prefix.Length + length) break;
        }
        return sb.ToString();
    }

    /// <summary>Deterministic per-subject date shift in [-<paramref name="maxDays"/>, +<paramref name="maxDays"/>] (keeps intervals intact).</summary>
    public int DateShiftDays(string subjectId, int maxDays = 180)
    {
        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("shift:" + subjectId), mac);
        int v = BitConverter.ToInt32(mac);
        return (int)((uint)v % (2 * maxDays + 1)) - maxDays;
    }
}
