using GDAL.Net.Native;

namespace GDAL.Net.Srs;

/// <summary>An entry of the PROJ CRS database.</summary>
public sealed record CrsInfo(
    string Authority,
    string Code,
    string Name,
    CrsType Type,
    bool Deprecated,
    Envelope? AreaOfUseBounds,
    string? AreaName,
    string? ProjectionMethod)
{
    /// <summary>"EPSG:4326" style identifier.</summary>
    public string Id => $"{Authority}:{Code}";
}

/// <summary>Searches the PROJ database (EPSG, ESRI, IAU, OGC, ... authorities).</summary>
public static unsafe class CrsDatabase
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, IReadOnlyList<CrsInfo>> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lists all CRSs of an authority (cached after the first call).</summary>
    public static IReadOnlyList<CrsInfo> GetAll(string authority = "EPSG")
    {
        lock (s_lock)
        {
            if (s_cache.TryGetValue(authority, out var cached)) return cached;
        }

        int count;
        var list = GdalNative.OSRGetCRSInfoListFromDatabase(authority, 0, &count);
        var result = new List<CrsInfo>(count);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var info = list[i];
                result.Add(new CrsInfo(
                    Interop.PtrToString(info->pszAuthName) ?? authority,
                    Interop.PtrToString(info->pszCode) ?? string.Empty,
                    Interop.PtrToString(info->pszName) ?? string.Empty,
                    (CrsType)info->eType,
                    info->bDeprecated != 0,
                    info->bBboxValid != 0 ? new Envelope(info->dfWestLongitudeDeg, info->dfSouthLatitudeDeg, info->dfEastLongitudeDeg, info->dfNorthLatitudeDeg) : null,
                    Interop.PtrToString(info->pszAreaName),
                    Interop.PtrToString(info->pszProjectionMethod)));
            }
        }
        finally
        {
            if (list != null) GdalNative.OSRDestroyCRSInfoList(list);
        }

        lock (s_lock)
        {
            s_cache[authority] = result;
        }
        return result;
    }

    /// <summary>Full-text search on code, name and area (case-insensitive, all terms must match).</summary>
    public static IEnumerable<CrsInfo> Search(string query, string authority = "EPSG", bool includeDeprecated = false, CrsType? type = null)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var crs in GetAll(authority))
        {
            if (!includeDeprecated && crs.Deprecated) continue;
            if (type is { } t && crs.Type != t) continue;
            var ok = true;
            foreach (var term in terms)
            {
                if (!crs.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !crs.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !(crs.AreaName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    ok = false;
                    break;
                }
            }
            if (ok) yield return crs;
        }
    }

    /// <summary>CRSs whose area of use contains a WGS84 longitude/latitude.</summary>
    public static IEnumerable<CrsInfo> At(double longitude, double latitude, string authority = "EPSG", CrsType? type = CrsType.Projected)
        => GetAll(authority).Where(c => !c.Deprecated && (type is null || c.Type == type) && c.AreaOfUseBounds is { } b && b.Contains(longitude, latitude));
}
