using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GeoPandas.Net.Cloud;

/// <summary>AWS credentials (an access key pair, plus a session token for temporary credentials).</summary>
public sealed record AwsCredentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null)
{
    /// <summary>From AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY / AWS_SESSION_TOKEN.</summary>
    public static AwsCredentials? FromEnvironment() =>
        Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID") is { Length: > 0 } id && Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY") is { Length: > 0 } secret
            ? new AwsCredentials(id, secret, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN") is { Length: > 0 } t ? t : null)
            : null;

    public override string ToString() => $"AwsCredentials({AccessKeyId}, ***)";
}

/// <summary>
/// AWS Signature Version 4 request signing (header form): canonical request, string to sign, HMAC-SHA256 key derivation.
/// Signs host, x-amz-date, content-type (when present) and x-amz-security-token (temporary credentials).
/// </summary>
public static class AwsSigV4
{
    /// <summary>Signs a request in place; <paramref name="body"/> is the exact payload that will be sent.</summary>
    public static void Sign(HttpRequestMessage request, ReadOnlySpan<byte> body, AwsCredentials credentials, string region, string service, DateTimeOffset now)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("Request has no URI.");
        string amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string date = amzDate[..8];
        request.Headers.Remove("X-Amz-Date");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", amzDate);
        if (credentials.SessionToken is { } token)
        {
            request.Headers.Remove("X-Amz-Security-Token");
            request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", token);
        }

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}",
            ["x-amz-date"] = amzDate,
        };
        if (request.Content?.Headers.ContentType is { } ct) headers["content-type"] = ct.ToString();
        if (credentials.SessionToken is { } tok) headers["x-amz-security-token"] = tok;
        string signedHeaders = string.Join(';', headers.Keys);
        string canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value.Trim()}\n"));

        string canonicalRequest = string.Join('\n',
            request.Method.Method,
            CanonicalPath(uri),
            CanonicalQuery(uri),
            canonicalHeaders,
            signedHeaders,
            Hex(SHA256.HashData(body)));
        string scope = $"{date}/{region}/{service}/aws4_request";
        string stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)))}";

        byte[] key = Hmac(Encoding.UTF8.GetBytes("AWS4" + credentials.SecretAccessKey), date);
        key = Hmac(key, region);
        key = Hmac(key, service);
        key = Hmac(key, "aws4_request");
        string signature = Hex(Hmac(key, stringToSign));
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={credentials.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
    }

    internal static string CanonicalPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (path.Length == 0) return "/";
        return string.Join('/', path.Split('/').Select(Encode));
    }

    internal static string CanonicalQuery(Uri uri)
    {
        var q = uri.Query.TrimStart('?');
        if (q.Length == 0) return "";
        var pairs = q.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p =>
        {
            int eq = p.IndexOf('=');
            string k = eq < 0 ? p : p[..eq], v = eq < 0 ? "" : p[(eq + 1)..];
            return (K: Encode(Uri.UnescapeDataString(k.Replace('+', ' '))), V: Encode(Uri.UnescapeDataString(v.Replace('+', ' '))));
        }).OrderBy(p => p.K, StringComparer.Ordinal).ThenBy(p => p.V, StringComparer.Ordinal);
        return string.Join('&', pairs.Select(p => $"{p.K}={p.V}"));
    }

    /// <summary>RFC 3986 encoding: everything except A–Z a–z 0–9 - _ . ~ is percent-encoded (upper-case hex).</summary>
    internal static string Encode(string s)
    {
        var sb = new StringBuilder();
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            char c = (char)b;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '~') sb.Append(c);
            else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
