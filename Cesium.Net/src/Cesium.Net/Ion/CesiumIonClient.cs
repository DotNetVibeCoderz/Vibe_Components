using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CesiumNet.Ion;

/// <summary>A Cesium ion asset.</summary>
public sealed class IonAsset
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    /// <summary>3DTILES, GLTF, IMAGERY, TERRAIN, KML, CZML, GEOJSON.</summary>
    public string Type { get; init; } = "";
    public long Bytes { get; init; }
    public DateTimeOffset DateAdded { get; init; }
    /// <summary>COMPLETE, IN_PROGRESS, AWAITING_FILES, NOT_STARTED, DATA_ERROR, ERROR.</summary>
    public string Status { get; init; } = "";
    public int PercentComplete { get; init; }
}

internal sealed class IonAssetList
{
    public List<IonAsset> Items { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IonAssetList))]
[JsonSerializable(typeof(IonAsset))]
internal sealed partial class IonJsonContext : JsonSerializerContext;

/// <summary>
/// Minimal client for the Cesium ion REST API (https://api.cesium.com) — list and inspect hosted
/// assets (terrain, imagery, 3D Tiles, photogrammetry, BIM conversions) to feed into the viewer.
/// </summary>
public sealed class CesiumIonClient(HttpClient httpClient, string accessToken)
{
    private const string BaseUrl = "https://api.cesium.com/v1/";

    /// <summary>Lists assets visible to the token (requires the <c>assets:list</c> scope).</summary>
    public async Task<IReadOnlyList<IonAsset>> ListAssetsAsync(CancellationToken cancellationToken = default)
    {
        using var req = Request(HttpMethod.Get, "assets");
        using var res = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        var list = await res.Content.ReadFromJsonAsync(IonJsonContext.Default.IonAssetList, cancellationToken).ConfigureAwait(false);
        return list?.Items ?? [];
    }

    /// <summary>Gets one asset (requires the <c>assets:read</c> scope).</summary>
    public async Task<IonAsset?> GetAssetAsync(long id, CancellationToken cancellationToken = default)
    {
        using var req = Request(HttpMethod.Get, $"assets/{id}");
        using var res = await httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync(IonJsonContext.Default.IonAsset, cancellationToken).ConfigureAwait(false);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return req;
    }

    /// <summary>Well-known public ion asset ids.</summary>
    public static class WellKnownAssets
    {
        public const long CesiumWorldTerrain = 1;
        public const long BingAerial = 2;
        public const long BingAerialWithLabels = 3;
        public const long BingRoad = 4;
        public const long CesiumOsmBuildings = 96188;
        public const long GooglePhotorealistic3DTiles = 2275207;
        public const long Sentinel2 = 3954;
        public const long EarthAtNight = 3812;
    }
}
