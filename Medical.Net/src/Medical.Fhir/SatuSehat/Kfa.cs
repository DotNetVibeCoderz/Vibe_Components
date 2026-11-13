using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Medical.Fhir.SatuSehat;

/// <summary>A product from KFA (Kamus Farmasi dan Alat Kesehatan, Kemenkes RI).</summary>
public sealed record KfaProduct(string Code, string Name, string? Generic = null, string? DosageForm = null, bool Active = true);

/// <summary>
/// Generic-name → KFA mapping maintained by the facility (or filled from <see cref="KfaClient"/>). Medical.Net ships no
/// KFA codes: load your formulary with <see cref="LoadCsv"/> (columns: generic,kfa_code,name).
/// </summary>
public sealed class KfaCatalog
{
    private readonly ConcurrentDictionary<string, KfaProduct> _byGeneric = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byGeneric.Count;

    public void Add(string generic, KfaProduct product) => _byGeneric[generic.Trim()] = product;

    public KfaProduct? Find(string generic) => _byGeneric.GetValueOrDefault(generic.Trim());

    public static KfaCatalog LoadCsv(string path)
    {
        var c = new KfaCatalog();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = SplitCsv(line);
            if (f.Length >= 3 && f[1].Length > 0) c.Add(f[0], new KfaProduct(f[1], f[2], f[0]));
        }
        return c;
    }

    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { result.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return [.. result];
    }
}

/// <summary>
/// Client for the SATUSEHAT KFA API (<c>{host}/kfa-v2/…</c>), authenticated with the same OAuth2 token as the FHIR API.
/// Response parsing is shape-tolerant: any object with a <c>kfa_code</c> property is read as a product.
/// </summary>
public sealed class KfaClient(HttpClient http, SatuSehatClient satuSehat)
{
    public async Task<IReadOnlyList<KfaProduct>> SearchAsync(string keyword, int size = 10, string productType = "farmasi", CancellationToken ct = default)
    {
        string url = $"{satuSehat.Options.Host}/kfa-v2/products/all?page=1&size={size}&product_type={Uri.EscapeDataString(productType)}&keyword={Uri.EscapeDataString(keyword)}";
        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<KfaProduct?> GetByCodeAsync(string kfaCode, CancellationToken ct = default) =>
        (await GetAsync($"{satuSehat.Options.Host}/kfa-v2/products?identifier=kfa&code={Uri.EscapeDataString(kfaCode)}", ct).ConfigureAwait(false)).FirstOrDefault();

    /// <summary>Fills <paramref name="catalog"/> for generics it does not know yet (first active match per keyword). Review the result before production use.</summary>
    public async Task<int> ResolveIntoAsync(KfaCatalog catalog, IEnumerable<string> generics, CancellationToken ct = default)
    {
        int added = 0;
        foreach (var g in generics.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (catalog.Find(g) is not null) continue;
            var hit = (await SearchAsync(g, 5, ct: ct).ConfigureAwait(false)).FirstOrDefault(p => p.Active);
            if (hit is null) continue;
            catalog.Add(g, hit with { Generic = g });
            added++;
        }
        return added;
    }

    private async Task<IReadOnlyList<KfaProduct>> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await satuSehat.GetAccessTokenAsync(ct).ConfigureAwait(false));
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        var list = new List<KfaProduct>();
        Collect(doc.RootElement, list);
        return list;
    }

    private static void Collect(JsonElement e, List<KfaProduct> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("kfa_code", out var code) && code.ValueKind == JsonValueKind.String)
                {
                    string name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! :
                                  e.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString()! : code.GetString()!;
                    string? form = e.TryGetProperty("dosage_form", out var df) && df.ValueKind == JsonValueKind.Object && df.TryGetProperty("name", out var dfn) ? dfn.GetString() : null;
                    bool active = !e.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;
                    into.Add(new KfaProduct(code.GetString()!, name, null, form, active));
                    return;
                }
                foreach (var p in e.EnumerateObject()) Collect(p.Value, into);
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, into);
                break;
        }
    }
}
