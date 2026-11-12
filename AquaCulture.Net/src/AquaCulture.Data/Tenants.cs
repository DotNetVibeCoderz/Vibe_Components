using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AquaCulture.Data;

/// <summary>A tenant (a farming company or cooperative) of a multi-tenant deployment.</summary>
public sealed record Tenant(string Id, string Name, DateTime CreatedUtc, string Plan = "standard")
{
    /// <summary>SHA-256 of the tenant's API key (hex); the key itself is never stored.</summary>
    public string ApiKeyHash { get; init; } = "";
    public bool Disabled { get; init; }
}

/// <summary>
/// Database-per-tenant isolation: a small JSON catalog of tenants and one <see cref="AquaStore"/> per tenant — SQLite files under a root
/// folder by default, or any EF Core provider through <paramref name="optionsFactory"/> (for example one PostgreSQL database per tenant).
/// Tenant ids are restricted to <c>[a-z0-9-]</c> so they can never escape the root folder; API keys are stored as SHA-256 hashes and
/// compared in constant time.
/// </summary>
public sealed partial class AquaTenants(string rootDirectory, Func<string, DbContextOptions<AquaDbContext>>? optionsFactory = null) : IDisposable
{
    private readonly ConcurrentDictionary<string, AquaStore> _stores = new();
    private readonly object _gate = new();

    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);
    private string CatalogPath => Path.Combine(RootDirectory, "tenants.json");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    public IReadOnlyList<Tenant> List()
    {
        lock (_gate) return Load();
    }

    public Tenant? Find(string id) => List().FirstOrDefault(t => t.Id == id);

    /// <summary>Creates a tenant and its database; returns the tenant and its API key (shown once).</summary>
    public (Tenant Tenant, string ApiKey) Create(string id, string name, string plan = "standard")
    {
        if (!IsValidId(id)) throw new ArgumentException("Tenant ids are 2–40 characters of a–z, 0–9 and '-', starting with a letter or digit.", nameof(id));
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tenant = new Tenant(id, name, DateTime.UtcNow, plan) { ApiKeyHash = Hash(key) };
        lock (_gate)
        {
            var all = Load();
            if (all.Any(t => t.Id == id)) throw new InvalidOperationException($"Tenant '{id}' already exists.");
            all.Add(tenant);
            Save(all);
        }
        _ = Store(id);                                                  // creates the schema
        return (tenant, key);
    }

    /// <summary>Checks an API key for a tenant (constant-time comparison of hashes). Disabled tenants never authenticate.</summary>
    public bool Authenticate(string id, string apiKey)
    {
        var t = Find(id);
        if (t is null || t.Disabled || string.IsNullOrEmpty(apiKey)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(t.ApiKeyHash), Convert.FromHexString(Hash(apiKey)));
    }

    /// <summary>Issues a new API key, invalidating the old one.</summary>
    public string RotateKey(string id)
    {
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Update(id, t => t with { ApiKeyHash = Hash(key) });
        return key;
    }

    public void SetDisabled(string id, bool disabled) => Update(id, t => t with { Disabled = disabled });

    /// <summary>The store of a tenant (cached). Throws for unknown or disabled tenants.</summary>
    public AquaStore Store(string id)
    {
        var t = Find(id) ?? throw new KeyNotFoundException($"Unknown tenant '{id}'.");
        if (t.Disabled) throw new InvalidOperationException($"Tenant '{id}' is disabled.");
        return _stores.GetOrAdd(id, i => optionsFactory is not null
            ? new AquaStore(optionsFactory(i))
            : AquaStore.OpenSqlite(Path.Combine(RootDirectory, "tenants", i + ".db")));
    }

    /// <summary>Path of a tenant's SQLite database (default provider).</summary>
    public string DatabasePath(string id) => IsValidId(id) ? Path.Combine(RootDirectory, "tenants", id + ".db") : throw new ArgumentException("Invalid tenant id.");

    private void Update(string id, Func<Tenant, Tenant> change)
    {
        lock (_gate)
        {
            var all = Load();
            int i = all.FindIndex(t => t.Id == id);
            if (i < 0) throw new KeyNotFoundException($"Unknown tenant '{id}'.");
            all[i] = change(all[i]);
            Save(all);
        }
    }

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private List<Tenant> Load()
    {
        if (!File.Exists(CatalogPath)) return [];
        var arr = JsonNode.Parse(File.ReadAllText(CatalogPath)) as JsonArray ?? [];
        return arr.OfType<JsonObject>().Select(o => new Tenant(o["id"]!.GetValue<string>(), o["name"]?.GetValue<string>() ?? "",
            DateTime.Parse(o["created"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), o["plan"]?.GetValue<string>() ?? "standard")
        {
            ApiKeyHash = o["keyHash"]?.GetValue<string>() ?? "", Disabled = o["disabled"]?.GetValue<bool>() ?? false,
        }).ToList();
    }

    private void Save(List<Tenant> all)
    {
        Directory.CreateDirectory(Path.Combine(RootDirectory, "tenants"));
        var arr = new JsonArray(all.Select(t => (JsonNode)new JsonObject
        {
            ["id"] = t.Id, ["name"] = t.Name, ["created"] = t.CreatedUtc.ToString("O", CultureInfo.InvariantCulture), ["plan"] = t.Plan,
            ["keyHash"] = t.ApiKeyHash, ["disabled"] = t.Disabled,
        }).ToArray());
        string tmp = CatalogPath + ".tmp";
        File.WriteAllText(tmp, arr.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, CatalogPath, true);
    }

    public void Dispose()
    {
        foreach (var s in _stores.Values) s.Dispose();
        _stores.Clear();
        SqliteConnection.ClearAllPools();
    }
}
