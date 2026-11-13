using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Medical.Bpjs.Crypto;

namespace Medical.Bpjs.VClaim;

public enum BpjsEnvironment { Development, Production }

/// <summary>Decoded BPJS response: <c>metaData</c> plus the decrypted <c>response</c> JSON (null on business errors).</summary>
public sealed record BpjsResponse(string Code, string Message, JsonNode? Response)
{
    public bool IsSuccess => Code == "200";
}

public sealed class BpjsApiException(string code, string message) : Exception($"BPJS {code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>Shared transport for BPJS JKN web services: signing headers, envelope parsing, decryption and decompression.</summary>
public abstract class BpjsServiceClient(HttpClient http, BpjsCredentials credentials, string baseUrl, TimeProvider? clock = null)
{
    protected TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    public BpjsCredentials Credentials { get; } = credentials;
    public string BaseUrl { get; } = baseUrl.TrimEnd('/') + "/";

    /// <summary>Adds service-specific headers (e.g. PCare's X-authorization).</summary>
    protected virtual void Decorate(HttpRequestMessage request) { }

    /// <summary>Content type BPJS expects for request bodies (VClaim historically uses form encoding with a JSON body).</summary>
    protected virtual string BodyContentType => "application/x-www-form-urlencoded";

    public async Task<BpjsResponse> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken ct = default)
    {
        string ts = BpjsCrypto.Timestamp(Clock.GetUtcNow());
        using var req = new HttpRequestMessage(method, BaseUrl + path.TrimStart('/'));
        req.Headers.Add("X-cons-id", Credentials.ConsId);
        req.Headers.Add("X-timestamp", ts);
        req.Headers.Add("X-signature", BpjsCrypto.Signature(Credentials, ts));
        req.Headers.Add("user_key", Credentials.UserKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        Decorate(req);
        if (body is not null)
        {
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(BodyContentType);
        }
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode && text.Length == 0) throw new BpjsApiException(((int)res.StatusCode).ToString(), res.ReasonPhrase ?? "HTTP error");
        var root = JsonNode.Parse(text) ?? throw new InvalidDataException("Empty BPJS response.");
        var meta = root["metaData"] ?? root["metadata"];
        string code = meta?["code"]?.ToString() ?? ((int)res.StatusCode).ToString();
        string message = meta?["message"]?.ToString() ?? "";
        JsonNode? response = null;
        if (root["response"] is JsonValue v && v.TryGetValue<string>(out var encrypted) && encrypted.Length > 0)
            response = JsonNode.Parse(BpjsCrypto.DecryptResponse(Credentials, ts, encrypted));
        else if (root["response"] is { } plain && plain is not JsonValue)
            response = plain.DeepClone();
        return new BpjsResponse(code, message, response);
    }

    protected async Task<JsonNode> GetRequiredAsync(string path, CancellationToken ct)
    {
        var r = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
        return r.IsSuccess && r.Response is not null ? r.Response : throw new BpjsApiException(r.Code, r.Message);
    }
}

/// <summary>
/// VClaim (BPJS Kesehatan) for hospitals: participant eligibility, referrals, diagnosis reference and SEP (Surat Eligibilitas
/// Peserta). Paths follow the VClaim REST catalogue; payloads are returned as JSON nodes so new fields are never lost.
/// </summary>
public sealed class VClaimClient(HttpClient http, BpjsCredentials credentials, BpjsEnvironment environment = BpjsEnvironment.Development, TimeProvider? clock = null)
    : BpjsServiceClient(http, credentials, environment == BpjsEnvironment.Production
        ? "https://apijkn.bpjs-kesehatan.go.id/vclaim-rest/" : "https://apijkn-dev.bpjs-kesehatan.go.id/vclaim-rest-dev/", clock)
{
    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Participant by card number on the service date.</summary>
    public Task<JsonNode> PesertaByKartuAsync(string noKartu, DateOnly tglSep, CancellationToken ct = default) =>
        GetRequiredAsync($"Peserta/nokartu/{Uri.EscapeDataString(noKartu)}/tglSEP/{D(tglSep)}", ct);

    /// <summary>Participant by NIK on the service date.</summary>
    public Task<JsonNode> PesertaByNikAsync(string nik, DateOnly tglSep, CancellationToken ct = default) =>
        GetRequiredAsync($"Peserta/nik/{Uri.EscapeDataString(nik)}/tglSEP/{D(tglSep)}", ct);

    public Task<JsonNode> DiagnosaAsync(string keyword, CancellationToken ct = default) =>
        GetRequiredAsync($"referensi/diagnosa/{Uri.EscapeDataString(keyword)}", ct);

    /// <summary>Referral from a primary-care provider (FKTP) by referral number.</summary>
    public Task<JsonNode> RujukanAsync(string noRujukan, CancellationToken ct = default) =>
        GetRequiredAsync($"Rujukan/{Uri.EscapeDataString(noRujukan)}", ct);

    public Task<JsonNode> SepAsync(string noSep, CancellationToken ct = default) =>
        GetRequiredAsync($"SEP/{Uri.EscapeDataString(noSep)}", ct);

    /// <summary>Creates a SEP. <paramref name="tSep"/> is the <c>t_sep</c> object from the VClaim specification (version 2.0).</summary>
    public async Task<JsonNode> InsertSepAsync(JsonObject tSep, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "SEP/2.0/insert", new JsonObject { ["request"] = new JsonObject { ["t_sep"] = tSep } }, ct).ConfigureAwait(false);
        return r.IsSuccess && r.Response is not null ? r.Response : throw new BpjsApiException(r.Code, r.Message);
    }

    public async Task<BpjsResponse> DeleteSepAsync(string noSep, string user, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, "SEP/2.0/delete",
            new JsonObject { ["request"] = new JsonObject { ["t_sep"] = new JsonObject { ["noSep"] = noSep, ["user"] = user } } }, ct).ConfigureAwait(false);
}

/// <summary>
/// PCare (primary-care / FKTP) client: same signing and encryption as VClaim plus
/// <c>X-authorization: Basic base64(username:password:kdAplikasi)</c> (application code 095).
/// </summary>
public sealed class PCareClient(HttpClient http, BpjsCredentials credentials, string username, string password,
    BpjsEnvironment environment = BpjsEnvironment.Development, string applicationCode = "095", TimeProvider? clock = null)
    : BpjsServiceClient(http, credentials, environment == BpjsEnvironment.Production
        ? "https://apijkn.bpjs-kesehatan.go.id/pcare-rest/" : "https://apijkn-dev.bpjs-kesehatan.go.id/pcare-rest-dev/", clock)
{
    protected override string BodyContentType => "text/plain";

    protected override void Decorate(HttpRequestMessage request) =>
        request.Headers.Add("X-authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}:{applicationCode}")));

    public Task<JsonNode> PesertaAsync(string noKartu, CancellationToken ct = default) => GetRequiredAsync($"peserta/{Uri.EscapeDataString(noKartu)}", ct);

    public Task<JsonNode> DiagnosaAsync(string keyword, int start = 0, int limit = 10, CancellationToken ct = default) =>
        GetRequiredAsync($"diagnosa/{Uri.EscapeDataString(keyword)}/{start}/{limit}", ct);

    /// <summary>Registers a visit (<c>pendaftaran</c>); <paramref name="registration"/> follows the PCare specification.</summary>
    public async Task<JsonNode> PendaftaranAsync(JsonObject registration, CancellationToken ct = default)
    {
        var r = await SendAsync(HttpMethod.Post, "pendaftaran", registration, ct).ConfigureAwait(false);
        return r.IsSuccess && r.Response is not null ? r.Response : throw new BpjsApiException(r.Code, r.Message);
    }
}
