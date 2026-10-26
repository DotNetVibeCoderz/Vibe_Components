using System.Globalization;
using Avalonia.Data.Converters;

namespace GDAL.Net.Gallery.Avalonia.Controls;

public static class Converters
{
    public static readonly IValueConverter Upper = new FuncValueConverter<string?, string>(s => s?.ToUpper(CultureInfo.CurrentCulture) ?? string.Empty);
    public static readonly IValueConverter MultilineHeight = new FuncValueConverter<bool, double>(m => m ? 96 : 0);
}
