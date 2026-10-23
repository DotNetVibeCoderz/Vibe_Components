using System.ComponentModel;
using System.Reflection;

namespace CesiumNet.Gallery.Avalonia.ViewModels;

/// <summary>Bindable localized strings: <c>{Binding L[Overview]}</c> resolves <see cref="GalleryText"/> fields.</summary>
public sealed class Strings : INotifyPropertyChanged
{
    private static readonly Dictionary<string, Text> s_texts = typeof(GalleryText)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(Text))
        .ToDictionary(f => f.Name, f => (Text)f.GetValue(null)!);

    private GalleryLanguage _language;

    public event PropertyChangedEventHandler? PropertyChanged;

    public GalleryLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public string this[string key] => s_texts.TryGetValue(key, out var t) ? t[_language] : key;

    public string Get(Text text) => text[_language];
}
