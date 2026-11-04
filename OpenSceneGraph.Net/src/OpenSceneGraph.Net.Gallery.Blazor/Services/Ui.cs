using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace OpenSceneGraph.Net.Gallery.Web.Services;

/// <summary>Current UI language ("en" or "id"), persisted in localStorage and overridable with <c>?lang=</c>.</summary>
public sealed class LanguageState(IJSRuntime js)
{
    public string Current { get; private set; } = "en";
    public event Action? Changed;

    public bool IsIndonesian => Current == "id";

    public async Task InitializeAsync(string? queryLang)
    {
        string? lang = Normalize(queryLang);
        if (lang is null)
        {
            try { lang = Normalize(await js.InvokeAsync<string?>("osgGallery.getLang")); }
            catch (JSException) { }
        }
        if (lang is not null && lang != Current) { Current = lang; Changed?.Invoke(); }
    }

    public async Task SetAsync(string lang)
    {
        lang = Normalize(lang) ?? "en";
        if (lang == Current) return;
        Current = lang;
        Changed?.Invoke();
        try { await js.InvokeVoidAsync("osgGallery.setLang", lang); } catch (JSException) { }
    }

    private static string? Normalize(string? l) => l?.ToLowerInvariant() switch
    {
        "id" or "id-id" or "in" => "id",
        "en" or "en-us" or "en-gb" => "en",
        _ => null,
    };
}

/// <summary>Base class for components that render bilingual text.</summary>
public abstract class LocalizedComponent : ComponentBase
{
    [CascadingParameter(Name = "Lang")] public string Lang { get; set; } = "en";

    protected string T(string key) => Ui.Get(key, Lang);
    protected string T(Text text) => text.Get(Lang);
}

/// <summary>All UI strings, English and Bahasa Indonesia.</summary>
public static class Ui
{
    public const string Credit = "credit";

    private static readonly Dictionary<string, Text> s = new(StringComparer.Ordinal)
    {
        ["nav.gallery"] = new("Gallery", "Galeri"),
        ["nav.about"] = new("About", "Tentang"),
        ["nav.lang"] = new("Language", "Bahasa"),
        [Credit] = new("Created by Gravicode Studios, led by Kang Fadhil", "Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil"),

        ["hero.eyebrow"] = new("OpenSceneGraph.Net · Web gallery", "OpenSceneGraph.Net · Galeri web"),
        ["hero.title.a"] = new("A scene graph,", "Scene graph,"),
        ["hero.title.b"] = new("running live in this tab.", "berjalan langsung di tab ini."),
        ["hero.body"] = new(
            "Armoured platoons, CT volumes, flight models and night cities, each one culled, lit and sorted by the managed OpenSceneGraph.Net core in .NET WebAssembly, then drawn by WebGL2 with the same shaders as the desktop renderer.",
            "Peleton lapis baja, volume CT, model penerbangan dan kota malam: semuanya di-cull, diberi cahaya, dan diurutkan oleh inti OpenSceneGraph.Net di .NET WebAssembly, lalu digambar WebGL2 dengan shader yang sama seperti renderer desktop."),
        ["hero.cta"] = new("Browse the samples", "Jelajahi sampel"),
        ["hero.open"] = new("Open this sample", "Buka sampel ini"),
        ["hero.live"] = new("Live", "Langsung"),
        ["hero.next"] = new("Next scene", "Adegan berikutnya"),
        ["hero.facts"] = new("{0} samples across {1} domains · no plug-ins · no external CDNs",
                             "{0} sampel dari {1} bidang · tanpa plug-in · tanpa CDN eksternal"),

        ["filter.all"] = new("All", "Semua"),
        ["filter.search"] = new("Search samples, tags, features", "Cari sampel, tag, fitur"),
        ["filter.heading"] = new("Use cases", "Kasus penggunaan"),
        ["filter.empty"] = new("No sample matches “{0}”. Clear the search or pick another domain.",
                               "Tidak ada sampel yang cocok dengan “{0}”. Hapus pencarian atau pilih bidang lain."),
        ["filter.clear"] = new("Clear filters", "Hapus filter"),
        ["filter.count"] = new("{0} of {1} samples", "{0} dari {1} sampel"),

        ["card.open"] = new("Run sample", "Jalankan sampel"),

        ["sample.back"] = new("All samples", "Semua sampel"),
        ["sample.prev"] = new("Previous", "Sebelumnya"),
        ["sample.next"] = new("Next", "Berikutnya"),
        ["sample.overview"] = new("Overview", "Ringkasan"),
        ["sample.code"] = new("Code", "Kode"),
        ["sample.features"] = new("OSG features", "Fitur OSG"),
        ["sample.controls"] = new("Controls", "Kontrol"),
        ["sample.telemetry"] = new("Live telemetry", "Telemetri langsung"),
        ["sample.telemetry.none"] = new("This sample publishes no telemetry.", "Sampel ini tidak memublikasikan telemetri."),
        ["sample.copy"] = new("Copy code", "Salin kode"),
        ["sample.copied"] = new("Copied", "Tersalin"),
        ["sample.building"] = new("Building scene graph…", "Menyusun scene graph…"),
        ["sample.focus"] = new("Click the view to give it keyboard focus.", "Klik tampilan agar keyboard aktif."),
        ["sample.notfound"] = new("There is no sample called “{0}”.", "Tidak ada sampel bernama “{0}”."),
        ["sample.codeNote"] = new("Excerpt of the sample's Build method. The full source lives in OpenSceneGraph.Net.Gallery.Core.",
                                  "Cuplikan metode Build sampel. Kode lengkap ada di OpenSceneGraph.Net.Gallery.Core."),
        ["sample.webglError"] = new("WebGL2 could not start: {0}", "WebGL2 gagal dijalankan: {0}"),

        ["ctl.orbit"] = new("Orbit", "Memutar kamera"),
        ["ctl.pan"] = new("Pan", "Geser"),
        ["ctl.zoom"] = new("Zoom", "Perbesar"),
        ["ctl.home"] = new("Reset view", "Atur ulang tampilan"),
        ["ctl.drag"] = new("Drag", "Seret"),
        ["ctl.shiftDrag"] = new("Shift + drag", "Shift + seret"),
        ["ctl.wheel"] = new("Wheel / right-drag", "Roda / seret kanan"),
        ["ctl.space"] = new("Space", "Spasi"),

        ["aria.primary"] = new("Primary", "Utama"),
        ["aria.sampleNav"] = new("Sample navigation", "Navigasi sampel"),
        ["hero.draws"] = new("draws", "item"),
        ["hero.tris"] = new("tris", "segitiga"),
        ["stat.fps"] = new("FPS", "FPS"),
        ["stat.draws"] = new("Draw items", "Item"),
        ["stat.calls"] = new("GL calls", "Call GL"),
        ["stat.tris"] = new("Triangles", "Segitiga"),
        ["stat.culled"] = new("Culled", "Ter-cull"),
        ["stat.lights"] = new("Lights", "Lampu"),
        ["stat.cull"] = new(".NET ms", ".NET ms"),
        ["stat.renderer"] = new("Renderer", "Renderer"),

        ["about.title"] = new("About this gallery", "Tentang galeri ini"),
        ["about.lead"] = new(
            "OpenSceneGraph.Net is a .NET 10 port and wrapper of OpenSceneGraph: nodes, transforms, LOD and paging, culling, manipulators, animation, particles, terrain, GIS and simulation, with renderers for desktop OpenGL, WebGL2 and native OSG.",
            "OpenSceneGraph.Net adalah port dan wrapper OpenSceneGraph untuk .NET 10: node, transformasi, LOD dan paging, culling, manipulator, animasi, partikel, terrain, GIS dan simulasi, dengan renderer untuk OpenGL desktop, WebGL2 dan OSG native."),
        ["about.how.title"] = new("How a frame reaches the screen", "Bagaimana satu frame sampai ke layar"),
        ["about.how.1"] = new("The browser's requestAnimationFrame calls into .NET with the canvas size and any pointer and keyboard input since the last frame.",
                              "requestAnimationFrame milik browser memanggil .NET dengan ukuran kanvas serta input pointer dan keyboard sejak frame terakhir."),
        ["about.how.2"] = new("Viewer.Frame() runs the event, update and cull traversals and produces a RenderList: passes, sorted draw items, lights and environment.",
                              "Viewer.Frame() menjalankan traversal event, update dan cull, lalu menghasilkan RenderList: pass, item gambar terurut, lampu dan lingkungan."),
        ["about.how.3"] = new("The RenderList is packed into one binary buffer. Vertex data is sent only when a geometry is new or its version changes.",
                              "RenderList dikemas menjadi satu buffer biner. Data verteks hanya dikirim saat geometri baru atau versinya berubah."),
        ["about.how.4"] = new("JavaScript reads the buffer through typed-array views and draws it with WebGL2: shadow map, main pass, HUD cameras, then bloom and tone mapping.",
                              "JavaScript membaca buffer lewat typed array dan menggambarnya dengan WebGL2: shadow map, pass utama, kamera HUD, lalu bloom dan tone mapping."),
        ["about.use.title"] = new("Use it in your own Blazor app", "Pakai di aplikasi Blazor Anda"),
        ["about.supported.title"] = new("What the WebGL2 renderer supports", "Yang didukung renderer WebGL2"),
        ["about.supported"] = new(
            "Up to 8 lights, vertex colours, textures, fog, sky gradient, transparency with alpha, additive and multiply blending, cull face, depth test and write, points with world-space sprite size, lines, render-to-texture and HUD cameras, PCF shadow maps, and bloom, HDR tone mapping and vignette. Custom GLSL programs and stereo are desktop-only; wireframe is drawn as triangle edges.",
            "Hingga 8 lampu, warna verteks, tekstur, kabut, gradien langit, transparansi dengan blending alpha, aditif dan multiply, cull face, depth test dan write, titik dengan ukuran sprite dunia, garis, render-to-texture dan kamera HUD, shadow map PCF, serta bloom, tone mapping HDR dan vignette. Program GLSL kustom dan stereo hanya untuk desktop; wireframe digambar sebagai tepi segitiga."),
        ["about.credit.title"] = new("Credits", "Kredit"),
        ["about.credit.body"] = new(
            "OpenSceneGraph is © Robert Osfield and contributors (OSGPL). Typefaces: Chakra Petch (Cadson Demak) and IBM Plex (IBM), both under the SIL Open Font License, served from this site.",
            "OpenSceneGraph © Robert Osfield dan kontributor (OSGPL). Huruf: Chakra Petch (Cadson Demak) dan IBM Plex (IBM), keduanya berlisensi SIL Open Font License, disajikan dari situs ini."),

        ["notfound.title"] = new("Nothing at this address", "Tidak ada apa pun di alamat ini"),
        ["notfound.body"] = new("The page you asked for does not exist. Go back to the gallery to pick a sample.",
                                "Halaman yang Anda minta tidak ada. Kembali ke galeri untuk memilih sampel."),
    };

    public static string Get(string key, string lang) => s.TryGetValue(key, out var t) ? t.Get(lang) : key;
}
