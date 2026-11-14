using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Electrical.Gallery.Web;

/// <summary>
/// Small in-memory store for binary demo output (off-screen renders, GLB downloads) served at <c>/blob/{key}/{name}</c>, so large byte
/// arrays never travel through the Blazor circuit. Keys are content hashes; the oldest entries are evicted beyond <see cref="Capacity"/>.
/// </summary>
public static class BlobStore
{
    private const int Capacity = 64;
    private static readonly ConcurrentDictionary<string, (byte[] Data, string MediaType, DateTime Added)> Items = new();

    /// <summary>Stores <paramref name="data"/> and returns its relative URL.</summary>
    public static string Put(byte[] data, string mediaType, string fileName)
    {
        string key = Convert.ToHexString(SHA256.HashData(data))[..20].ToLowerInvariant();
        Items[key] = (data, mediaType, DateTime.UtcNow);
        if (Items.Count > Capacity)
            foreach (var old in Items.OrderBy(kv => kv.Value.Added).Take(Items.Count - Capacity)) Items.TryRemove(old.Key, out _);
        return $"blob/{key}/{Uri.EscapeDataString(fileName)}";
    }

    public static void Map(WebApplication app) =>
        app.MapGet("/blob/{key}/{name}", (string key, string name) =>
            Items.TryGetValue(key, out var b) ? Results.File(b.Data, b.MediaType, name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? null : name) : Results.NotFound());
}
