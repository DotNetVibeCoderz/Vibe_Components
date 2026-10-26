namespace GDAL.Net.Avalonia;

/// <summary>
/// GDAL datasets are not thread-safe. The GDAL.Net Avalonia controls read datasets on background threads while holding
/// <see cref="Lock"/>; take the same lock when your code touches a dataset that is displayed by a control
/// (or when disposing it).
/// </summary>
public static class GdalUiSync
{
    public static readonly System.Threading.Lock Lock = new();
}
