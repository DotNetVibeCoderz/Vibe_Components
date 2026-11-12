using GDAL.Net.Native;

namespace GDAL.Net.Tests;

/// <summary>
/// A managed assembly loaded before GDAL with the same file name as one of GDAL's native dependencies (Parquet.Net's Parquet.dll versus
/// GDAL's parquet.dll) makes Windows bind GDAL to the managed file. The loader must detect that case and explain it.
/// </summary>
public class NativeShadowingTests
{
    [Fact]
    public void Detects_a_loaded_managed_assembly_named_like_a_native_dependency()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), $"gdalnet-shadow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // GDAL.Net.dll is a managed assembly loaded in this process; pretend the native folder ships a file with the same name
            File.WriteAllBytes(Path.Combine(dir, "GDAL.Net.dll"), [0]);
            // kernel32.dll is loaded too, but it is native: not a conflict
            File.WriteAllBytes(Path.Combine(dir, "kernel32.dll"), [0]);
            var hits = NativeLibraryLoader.FindShadowingManagedModules(dir);
            var hit = Assert.Single(hits);
            Assert.Equal("GDAL.Net.dll", hit.NativeName);
            Assert.EndsWith("GDAL.Net.dll", hit.LoadedFrom, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_shipped_native_folder_is_not_shadowed_in_a_clean_process()
    {
        if (!OperatingSystem.IsWindows()) return;
        Gdal.Initialize();
        foreach (var dir in NativeLibraryLoader.SearchDirectories().Where(Directory.Exists))
            Assert.Empty(NativeLibraryLoader.FindShadowingManagedModules(dir));
    }
}
