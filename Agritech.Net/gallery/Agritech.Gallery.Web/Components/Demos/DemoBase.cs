using Microsoft.AspNetCore.Components;

namespace Agritech.Gallery.Web.Components.Demos;

/// <summary>Base for gallery views: language parameter and bilingual helper.</summary>
public abstract class DemoBase : ComponentBase
{
    [Parameter] public string Lang { get; set; } = "en";
    protected string T(string en, string id) => Lang == "id" ? id : en;
    protected static string F(double v, string fmt = "0.0") => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
}
