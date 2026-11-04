using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>Localised text markup extension: <c>Text="{local:L Search}"</c>.</summary>
public sealed class L(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"T[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
}
