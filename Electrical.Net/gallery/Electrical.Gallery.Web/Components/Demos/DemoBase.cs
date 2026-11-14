using System.Globalization;
using Electrical.Gallery.Shared;
using Microsoft.AspNetCore.Components;
using static Electrical.Gallery.Web.Components.Shared.Seg;

namespace Electrical.Gallery.Web.Components.Demos;

/// <summary>Base for demo views: language parameter, bilingual helper and formatting.</summary>
public abstract class DemoBase : ComponentBase
{
    [Parameter] public string Lang { get; set; } = "en";
    protected string T(string en, string id) => Lang == "id" ? id : en;
    protected string T(L text) => text[Lang];
    protected static string F(double v, string fmt = "0.0") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    protected IReadOnlyList<Option> CaseOptions(IEnumerable<string> keys) =>
        keys.Select(k => new Option(k, GalleryCases.All.First(c => c.Key == k).Label[Lang])).ToList();
}
