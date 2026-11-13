using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Medical.Bpjs.InaCbg;

/// <summary>
/// E-Klaim web-service payload encryption (as in the E-Klaim manual's <c>inacbg_encrypt</c>/<c>inacbg_decrypt</c>):
/// AES-256-CBC with the hex encryption key from E-Klaim setup, a random IV, and a 10-byte HMAC-SHA256 signature;
/// wire format <c>base64(signature ‖ iv ‖ ciphertext)</c>. Responses are wrapped in
/// <c>----BEGIN ENCRYPTED DATA----</c> / <c>----END ENCRYPTED DATA----</c>.
/// </summary>
public static class EKlaimCrypto
{
    public const string Begin = "----BEGIN ENCRYPTED DATA----";
    public const string End = "----END ENCRYPTED DATA----";

    private static byte[] Key(string hexKey)
    {
        var key = Convert.FromHexString(hexKey.Trim());
        return key.Length == 32 ? key : throw new ArgumentException("The E-Klaim key must be 64 hex characters (256 bit).", nameof(hexKey));
    }

    public static string Encrypt(string plain, string hexKey)
    {
        var key = Key(hexKey);
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), iv, PaddingMode.PKCS7);
        var sig = HMACSHA256.HashData(key, cipher).AsSpan(0, 10);
        return Convert.ToBase64String([.. sig, .. iv, .. cipher], Base64FormattingOptions.InsertLineBreaks);
    }

    /// <summary>Decrypts a payload (with or without the BEGIN/END envelope); throws when the signature does not match.</summary>
    public static string Decrypt(string payload, string hexKey)
    {
        var key = Key(hexKey);
        string body = payload;
        int b = body.IndexOf(Begin, StringComparison.Ordinal), e = body.IndexOf(End, StringComparison.Ordinal);
        if (b >= 0 && e > b) body = body[(b + Begin.Length)..e];
        var raw = Convert.FromBase64String(new string(body.Where(c => !char.IsWhiteSpace(c)).ToArray()));
        if (raw.Length < 27) throw new InvalidDataException("E-Klaim payload too short.");
        var sig = raw.AsSpan(0, 10);
        var iv = raw.AsSpan(10, 16);
        var cipher = raw.AsSpan(26);
        if (!CryptographicOperations.FixedTimeEquals(sig, HMACSHA256.HashData(key, cipher).AsSpan(0, 10)))
            throw new CryptographicException("E-Klaim signature does not match (wrong key or tampered payload).");
        using var aes = Aes.Create();
        aes.Key = key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(cipher, iv, PaddingMode.PKCS7));
    }
}

public sealed class EKlaimException(string code, string message) : Exception($"E-Klaim {code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// Client for the E-Klaim INA-CBG web service (<c>http://&lt;server&gt;/E-Klaim/ws.php</c>). Each call posts
/// <c>{"metadata":{"method":…},"data":{…}}</c> encrypted with <see cref="EKlaimCrypto"/>. The flow is
/// <see cref="NewClaimAsync"/> → <see cref="SetClaimDataAsync"/> → <see cref="GrouperAsync"/> → <see cref="ClaimFinalAsync"/> → <see cref="SendClaimAsync"/>.
/// </summary>
public sealed class EKlaimClient(HttpClient http, string wsUrl, string hexKey, bool encrypted = true)
{
    public async Task<JsonNode> CallAsync(string method, JsonObject data, JsonObject? extraMetadata = null, CancellationToken ct = default)
    {
        var metadata = new JsonObject { ["method"] = method };
        foreach (var kv in extraMetadata ?? []) metadata[kv.Key] = kv.Value?.DeepClone();
        string json = new JsonObject { ["metadata"] = metadata, ["data"] = data }.ToJsonString();
        string url = encrypted ? wsUrl : wsUrl + (wsUrl.Contains('?') ? "&" : "?") + "mode=debug";
        using var res = await http.PostAsync(url, new StringContent(encrypted ? EKlaimCrypto.Encrypt(json, hexKey) : json, Encoding.UTF8, "application/x-www-form-urlencoded"), ct).ConfigureAwait(false);
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var root = JsonNode.Parse(encrypted && text.Contains(EKlaimCrypto.Begin, StringComparison.Ordinal) ? EKlaimCrypto.Decrypt(text, hexKey) : text)
                   ?? throw new InvalidDataException("Empty E-Klaim response.");
        string code = root["metadata"]?["code"]?.ToString() ?? "?";
        if (code != "200") throw new EKlaimException(code, root["metadata"]?["message"]?.ToString() ?? "");
        return root["response"] ?? root;
    }

    public Task<JsonNode> NewClaimAsync(InaCbgClaim c, CancellationToken ct = default) => CallAsync("new_claim", c.NewClaimData(), ct: ct);

    public Task<JsonNode> SetClaimDataAsync(InaCbgClaim c, CancellationToken ct = default) =>
        CallAsync("set_claim_data", c.ClaimData(), new JsonObject { ["nomor_sep"] = c.NomorSep }, ct);

    /// <summary>Runs the INA-CBG grouper (stage 1, or stage 2 with special CMG options).</summary>
    public Task<JsonNode> GrouperAsync(string nomorSep, int stage = 1, string? specialCmg = null, CancellationToken ct = default)
    {
        var data = new JsonObject { ["nomor_sep"] = nomorSep };
        if (specialCmg is not null) data["special_cmg"] = specialCmg;
        return CallAsync("grouper", data, new JsonObject { ["stage"] = stage.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
    }

    public Task<JsonNode> ClaimFinalAsync(string nomorSep, string coderNik, CancellationToken ct = default) =>
        CallAsync("claim_final", new JsonObject { ["nomor_sep"] = nomorSep, ["coder_nik"] = coderNik }, ct: ct);

    public Task<JsonNode> SendClaimAsync(string nomorSep, CancellationToken ct = default) =>
        CallAsync("send_claim_individual", new JsonObject { ["nomor_sep"] = nomorSep }, ct: ct);

    public Task<JsonNode> GetClaimDataAsync(string nomorSep, CancellationToken ct = default) =>
        CallAsync("get_claim_data", new JsonObject { ["nomor_sep"] = nomorSep }, ct: ct);
}
