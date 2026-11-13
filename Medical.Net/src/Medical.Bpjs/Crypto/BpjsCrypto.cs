using System.Security.Cryptography;
using System.Text;

namespace Medical.Bpjs.Crypto;

/// <summary>Credentials issued by BPJS Kesehatan for the web-service APIs (VClaim, PCare, Antrean…).</summary>
public sealed record BpjsCredentials(string ConsId, string SecretKey, string UserKey)
{
    /// <summary>Reads <c>BPJS_CONS_ID</c>, <c>BPJS_SECRET_KEY</c> and <c>BPJS_USER_KEY</c>.</summary>
    public static BpjsCredentials FromEnvironment() => new(
        Environment.GetEnvironmentVariable("BPJS_CONS_ID") ?? "",
        Environment.GetEnvironmentVariable("BPJS_SECRET_KEY") ?? "",
        Environment.GetEnvironmentVariable("BPJS_USER_KEY") ?? "");

    public bool IsConfigured => ConsId.Length > 0 && SecretKey.Length > 0 && UserKey.Length > 0;
}

/// <summary>
/// BPJS web-service security: request signature <c>X-signature = Base64(HMAC-SHA256(secretKey, consId + "&amp;" + timestamp))</c>
/// and response decryption <c>AES-256-CBC(key = SHA256(consId + secretKey + timestamp), iv = key[0..16])</c> followed by
/// LZ-String <c>decompressFromEncodedURIComponent</c>.
/// </summary>
public static class BpjsCrypto
{
    /// <summary>Seconds since the Unix epoch (UTC), as used in <c>X-timestamp</c>.</summary>
    public static string Timestamp(DateTimeOffset now) => now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static string Signature(BpjsCredentials c, string timestamp) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(c.SecretKey), Encoding.UTF8.GetBytes($"{c.ConsId}&{timestamp}")));

    /// <summary>Decrypts and decompresses a <c>response</c> payload to JSON.</summary>
    public static string DecryptResponse(BpjsCredentials c, string timestamp, string payload)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(c.ConsId + c.SecretKey + timestamp));
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] plain = aes.DecryptCbc(Convert.FromBase64String(payload), key.AsSpan(0, 16), PaddingMode.PKCS7);
        return LzString.DecompressFromEncodedURIComponent(Encoding.UTF8.GetString(plain))
               ?? throw new InvalidDataException("BPJS response could not be decompressed.");
    }

    /// <summary>The inverse of <see cref="DecryptResponse"/> — used to build test doubles of BPJS services.</summary>
    public static string EncryptResponse(BpjsCredentials c, string timestamp, string json)
    {
        byte[] key = SHA256.HashData(Encoding.UTF8.GetBytes(c.ConsId + c.SecretKey + timestamp));
        using var aes = Aes.Create();
        aes.Key = key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(LzString.CompressToEncodedURIComponent(json)), key.AsSpan(0, 16), PaddingMode.PKCS7));
    }
}
