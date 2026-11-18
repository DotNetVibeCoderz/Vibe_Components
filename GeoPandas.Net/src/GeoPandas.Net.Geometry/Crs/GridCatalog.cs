using System.Collections.Concurrent;

namespace GeoPandas.Net.Geometry.Crs;

/// <summary>
/// Finds and caches grid files referenced by <c>+nadgrids=</c> (and geoid grids): absolute/relative paths, then the directories in
/// <c>GEOPANDAS_NET_GRIDS</c>, <c>PROJ_DATA</c> and <c>PROJ_LIB</c>. A leading <c>@</c> marks a grid as optional; <c>@null</c> means no shift.
/// </summary>
public static class GridCatalog
{
    private static readonly ConcurrentDictionary<string, HorizontalGridShift> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers an in-memory grid under a name usable in <c>+nadgrids=name</c>.</summary>
    public static void Register(string name, HorizontalGridShift grid) => s_cache[name] = grid;

    public static IEnumerable<string> SearchDirectories()
    {
        foreach (var v in new[] { "GEOPANDAS_NET_GRIDS", "PROJ_DATA", "PROJ_LIB" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } dirs)
                foreach (var d in dirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)) yield return d;
    }

    /// <summary>Resolves a nadgrids list ("a.gsb,@b.gsb"); returns the first grid found, or null for <c>@null</c> / optional misses.</summary>
    public static HorizontalGridShift? Resolve(string list)
    {
        foreach (var raw in list.Split(','))
        {
            bool optional = raw.StartsWith('@');
            var name = raw.TrimStart('@');
            if (name.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
            if (s_cache.TryGetValue(name, out var cached)) return cached;
            var path = Locate(name);
            if (path is null)
            {
                if (optional) continue;
                throw new FileNotFoundException($"Grid '{name}' not found. Put it next to the program, give an absolute path, or set GEOPANDAS_NET_GRIDS / PROJ_DATA.");
            }
            var grid = path.EndsWith(".gsb", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                ? HorizontalGridShift.LoadNtv2(path)
                : throw new NotSupportedException($"Grid format of '{name}' is not supported (use NTv2 .gsb).");
            return s_cache.GetOrAdd(name, grid);
        }
        return null;
    }

    public static string? Locate(string name)
    {
        if (File.Exists(name)) return Path.GetFullPath(name);
        foreach (var d in SearchDirectories())
        {
            var p = Path.Combine(d, name);
            if (File.Exists(p)) return p;
        }
        var local = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(local) ? local : null;
    }
}
