# Component 3D (Blazor.AI.ThreeD)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [English](../en/threed.md)

`Blazor.AI.ThreeD` menambahkan component 3D bertenaga AI di atas Three.js: avatar yang bisa berbicara dengan lip sync, chart dan graph 3D, visualisasi agent dan keputusan, arena reinforcement learning, digital twin, anatomi dan molekul, copilot teknik, scene edukasi, serta tool untuk game design. Kamu cukup mendeskripsikan scene di C#; sebuah engine JavaScript kecil yang akan me-render-nya.

![3D gallery page](../screenshots/3d-aiavatar3d.jpg)

## Persiapan

```bash
dotnet add package Blazor.AI.ThreeD
```

```csharp
builder.Services.AddBlazorAI(builder.Configuration).AddSimulated();
builder.Services.AddBlazorAIComponents();
builder.Services.AddBlazorAIThreeD();
// AgentStudio3D dan MultiAgentArena menjalankan agen sungguhan, jadi butuh juga:
builder.Services.AddBlazorAIAgents();      // agent studio, arenas
```

```html
<link rel="stylesheet" href="_content/Blazor.AI.Components/blazor-ai.css" />
<link rel="stylesheet" href="_content/Blazor.AI.ThreeD/blazor-ai-3d.css" />
```

Three.js r170 beserta loader-nya dan Draco decoder sudah dibundel di dalam package, jadi tidak ada yang dimuat dari CDN dan kamu tidak perlu import map. Scene membutuhkan WebGL; jika WebGL tidak tersedia, component akan menampilkan pesan alih-alih gagal.

## Model scene

Setiap component dibangun di atas `Scene3D`, yang juga bisa kamu pakai secara langsung:

```razor
<Scene3D @ref="scene" Objects="@objects" Options="@options" Height="420px" OnPick="Picked">
    <div class="bai3d-panel"><h4>HUD</h4>Overlay HTML goes here.</div>
</Scene3D>

@code {
    Scene3D? scene;
    Scene3DOptions options = new() { Camera = new() { Position = [4, 3, 6] }, Ground = new(), Lights = "studio" };
    Object3D[] objects =
    [
        Object3D.Box("crate", [0, 0.5, 0], [1, 1, 1], ThreeDPalette.Soga) with { Tooltip = "A crate", Animate = Animate3D.Spin() },
        Object3D.Model("guide", ThreeDAssets.GuideBot, [2, 0, 0], fitHeight: 1.7, clip: "Wave"),
        Object3D.Line("path", [[0, 0, 0], [2, 0, 0]], ThreeDPalette.Nila, dashed: true),
    ];

    async Task Picked(PickEvent e) => await scene!.FocusAsync(e.Id);
}
```

- **Deklaratif dan di-diff.** Berikan array `Objects` yang baru dan engine akan memperbarui object berdasarkan `Id`: transform, warna, dan label berubah di tempat; perubahan geometri hanya membangun ulang object tersebut.
- **Jenis:** `box`, `sphere`, `cylinder`, `cone`, `torus`, `icosahedron`, `capsule`, `plane`, `ring`, `line`, `tube`, `points`, `arrow`, `surface` (height field dengan warna per vertex), `text` (hanya label), `group` dan `model` (glTF/GLB).
- **Gerakan bawaan** (`Animate3D`): `Spin`, `Bob`, `Pulse`, `Orbit`, `Path` (mengikuti titik-titik, menghadap arah gerak), `Wander`. Gerakan menghormati `prefers-reduced-motion`.
- **Update langsung tanpa mengirim ulang scene:** `UpdateAsync(patches, durationMs)` melakukan tween pada posisi, skala, dan warna.
- **Model:** `PlayClipAsync(id, "Wave")`, `SetMorphAsync(id, "Smile", 1)`, `OnModelLoaded` melaporkan nama clip, nama morph, dan jumlah segitiga.
- **Kamera:** `FlyToAsync`, `FocusAsync`. **Utilitas:** `ScreenshotAsync` (data URL PNG), `StatsAsync`, `SimplifyAsync` (decimation mesh), `SetWireframeAsync`.
- **Suara dengan lip sync:** `SpeakAsync(objectId, text, "id-ID")` memakai speech synthesis bawaan browser dan menggerakkan morph `Talk`/`Open`/`Jaw` apa pun pada model selama berbicara.
- **Performa:** scene yang berada di luar layar berhenti di-render (scene akan menggambar ulang satu frame ketika kontennya berubah); model dimuat sekali per URL lalu di-clone; pixel ratio dibatasi maksimal 2.

`Layout3D` menyediakan layout deterministik untuk scene buatanmu sendiri: `Force` (graph force-directed 3-D), `Tree`, `Fibonacci` (titik-titik pada bola), `Pca3` (memproyeksikan vektor ke 3-D), `Normalize` dan `Arc` (edge melengkung). `ThreeDPalette` berisi warna kawung beserta skala `Heat` dan `Ramp`.

## Pola AI

Component mewarisi `ThreeDComponentBase`. Geometri, fisika, statistik, training, dan simulasi berjalan di .NET; model bertugas membuat rencana (JSON terstruktur via `PlanAsync`) atau menarasikan (`NarrateAsync`) berdasarkan fakta yang sudah dihitung. Setiap component generatif memiliki fallback lokal yang deterministik, sehingga tetap berfungsi dengan simulated model offline.

## Aset bawaan

| Konstanta | Aset |
|---|---|
| `ThreeDAssets.GuideBot` | Robot pemandu yang sudah di-rig. Clip: Idle, Wave, Talk, Nod, Point, Walk, Happy, Think. Morph visor: Talk, Smile, Sad |
| `ThreeDAssets.Fish` | Ikan yang sudah di-rig dengan loop Swim |
| `ThreeDAssets.RobotArm` | Lengan robot industri dengan siklus PickPlace |
| `ThreeDAssets.Heart`, `Stupa`, `Corn`, `Car`, `Mannequin` | Model statis dengan skala dunia nyata |
| `ThreeDAssets.HeroArt`, `WorldConcept`, `NpcBlacksmith`, `NpcMerchant`, `NpcGuardian` | Art 2-D (WebP) |

Model-model ini dibuat dengan Rodin, lalu di-rig dan dianimasikan di Blender melalui MCP; lihat [tools/blender](../../tools/blender/README.md) untuk membuat ulang atau menyesuaikannya. Model menghadap ke +Z.

## Component

Lihat [katalog component](components.md#blazoraithreed) untuk parameter setiap component. Gallery (halaman `/3d…`) menampilkan masing-masing component secara langsung beserta kode yang siap disalin.
