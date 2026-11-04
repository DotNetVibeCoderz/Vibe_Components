using System.ComponentModel;

namespace OpenSceneGraph.Net.Gallery.Desktop;

/// <summary>
/// UI strings in English and Bahasa Indonesia. XAML binds to <c>{Binding T[key], Source={x:Static local:Loc.Instance}}</c>;
/// switching language swaps the whole dictionary so every binding re-evaluates.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly Dictionary<string, (string En, string Id)> s_strings = new()
    {
        ["AppTitle"] = ("OpenSceneGraph.Net Gallery", "Galeri OpenSceneGraph.Net"),
        ["Brand"] = ("Gallery", "Galeri"),
        ["Credit"] = ("Created by Gravicode Studios, led by Kang Fadhil", "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil"),
        ["AllSamples"] = ("All samples", "Semua sampel"),
        ["Domains"] = ("Domains", "Bidang"),
        ["Search"] = ("Search samples, tags, features", "Cari sampel, tag, fitur"),
        ["HomeTitle"] = ("Scenes you can run, read and reuse", "Adegan yang bisa dijalankan, dibaca, dan dipakai ulang"),
        ["HomeLead"] = ("Every card is a live OpenSceneGraph.Net scene. Open one to explore it in 3D, watch its telemetry and copy the code behind it.",
                        "Setiap kartu adalah adegan OpenSceneGraph.Net yang hidup. Buka untuk menjelajahinya dalam 3D, memantau telemetri, dan menyalin kodenya."),
        ["Showing"] = ("Showing", "Menampilkan"),
        ["Of"] = ("of", "dari"),
        ["Scenes"] = ("scenes", "adegan"),
        ["NoResults"] = ("No sample matches that search. Try a tag such as \"terrain\" or clear the filter.",
                         "Tidak ada sampel yang cocok. Coba tag seperti \"terrain\" atau hapus filter."),
        ["ClearFilter"] = ("Clear filter", "Hapus filter"),
        ["Open"] = ("Open", "Buka"),
        ["Back"] = ("All samples", "Semua sampel"),
        ["Overview"] = ("Overview", "Ringkasan"),
        ["Code"] = ("Code", "Kode"),
        ["CopyCode"] = ("Copy code", "Salin kode"),
        ["Copied"] = ("Copied", "Tersalin"),
        ["Features"] = ("Features used", "Fitur yang dipakai"),
        ["Telemetry"] = ("Live telemetry", "Telemetri langsung"),
        ["Controls"] = ("Controls", "Kontrol"),
        ["Mouse"] = ("Mouse", "Mouse"),
        ["MouseHelp"] = ("Drag to orbit · Shift+drag to pan · wheel to zoom", "Seret untuk orbit · Shift+seret untuk geser · roda untuk zoom"),
        ["NoTelemetry"] = ("This scene publishes no telemetry.", "Adegan ini tidak menerbitkan telemetri."),
        ["Renderer"] = ("Renderer", "Renderer"),
        ["Draws"] = ("draws", "draw"),
        ["Tris"] = ("tris", "segitiga"),
        ["Fps"] = ("fps", "fps"),
        ["ClickToDrive"] = ("Click the view, then use the keys below", "Klik tampilan, lalu gunakan tombol di bawah"),
        ["RenderFailed"] = ("OpenGL could not start on this machine. Update the graphics driver or run outside a remote session.",
                            "OpenGL tidak dapat berjalan di mesin ini. Perbarui driver grafis atau jalankan di luar sesi remote."),
        ["BuildFailed"] = ("This sample failed to build:", "Sampel ini gagal dibangun:"),
        ["About"] = ("About", "Tentang"),
        ["Close"] = ("Close", "Tutup"),
        ["AboutLead"] = ("A .NET 10 port and wrapper of OpenSceneGraph: a managed scene graph, an OpenGL 3.3 / GLES 3 renderer, Avalonia and Blazor views, and the native OSG 3.6.5 runtime for loading classic model formats.",
                         "Port dan wrapper .NET 10 untuk OpenSceneGraph: scene graph terkelola, renderer OpenGL 3.3 / GLES 3, tampilan Avalonia dan Blazor, serta runtime OSG 3.6.5 native untuk memuat format model klasik."),
        ["AboutGallery"] = ("This gallery runs the shared Gallery.Core samples inside the Avalonia OsgView control.",
                            "Galeri ini menjalankan sampel Gallery.Core bersama di dalam kontrol OsgView Avalonia."),
        ["Version"] = ("Version", "Versi"),
        ["License"] = ("MIT license", "Lisensi MIT"),
        ["Language"] = ("Language", "Bahasa"),
        ["NodePath"] = ("Scene path", "Jalur adegan"),
    };

    // declared after the string table so static initialisation order is correct
    public static Loc Instance { get; } = new();

    private string _lang = "en";

    private Loc() => T = BuildTable("en");

    /// <summary>Current table (key → text). Replaced on every language change.</summary>
    public IReadOnlyDictionary<string, string> T { get; private set; }

    /// <summary>"en" or "id".</summary>
    public string Lang
    {
        get => _lang;
        set
        {
            if (_lang == value) return;
            _lang = value;
            T = BuildTable(value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(T)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lang)));
            LanguageChanged?.Invoke();
        }
    }

    public bool IsIndonesian => _lang == "id";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string this[string key] => T.TryGetValue(key, out var v) ? v : key;

    public string Get(Text text) => text.Get(_lang);

    private static Dictionary<string, string> BuildTable(string lang) =>
        s_strings.ToDictionary(kv => kv.Key, kv => lang == "id" ? kv.Value.Id : kv.Value.En);
}
