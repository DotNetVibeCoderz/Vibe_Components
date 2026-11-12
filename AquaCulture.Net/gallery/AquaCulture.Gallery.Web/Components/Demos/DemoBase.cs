using AquaCulture.Gallery.Shared;
using Microsoft.AspNetCore.Components;

namespace AquaCulture.Gallery.Web.Components.Demos;

/// <summary>Base for gallery views: language parameter and helpers.</summary>
public abstract class DemoBase : ComponentBase
{
    [Parameter] public string Lang { get; set; } = "en";
    protected string T(string en, string id) => Lang == "id" ? id : en;
    protected static string F(double v, string fmt = "0.0") => Fmt.N(v, fmt);
    protected static string Rp(double v) => Fmt.Rp(v);
    protected static string Img(byte[] png) => AquaCulture.Visualization.PngEncoder.DataUri(png);
}
