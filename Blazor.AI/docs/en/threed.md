# 3D components (Blazor.AI.ThreeD)

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.* · [Bahasa Indonesia](../id/threed.md)

`Blazor.AI.ThreeD` adds AI-powered 3D components on top of Three.js: a talking avatar with lip sync, 3D charts and graphs, agent and decision visualisers, reinforcement-learning arenas, digital twins, anatomy and molecules, engineering copilots, education scenes and game-design tools. You describe scenes in C#; a small JavaScript engine renders them.

![3D gallery page](../screenshots/3d-aiavatar3d.jpg)

## Setup

```bash
dotnet add package Blazor.AI.ThreeD
```

```csharp
builder.Services.AddBlazorAI(builder.Configuration).AddSimulated();
builder.Services.AddBlazorAIComponents();
builder.Services.AddBlazorAIThreeD();
// AgentStudio3D and MultiAgentArena run real agents, so they also need:
builder.Services.AddBlazorAIAgents();      // agent studio, arenas
```

```html
<link rel="stylesheet" href="_content/Blazor.AI.Components/blazor-ai.css" />
<link rel="stylesheet" href="_content/Blazor.AI.ThreeD/blazor-ai-3d.css" />
```

Three.js r170, its loaders and the Draco decoder are bundled in the package, so nothing is loaded from a CDN and no import map is needed. Scenes need WebGL; when it is unavailable the component shows a message instead of failing.

## The scene model

Every component is built on `Scene3D`, which you can use directly:

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

- **Declarative and diffed.** Assign a new `Objects` array and the engine updates objects by `Id`: transforms, colours and labels change in place; geometry changes rebuild only that object.
- **Kinds:** `box`, `sphere`, `cylinder`, `cone`, `torus`, `icosahedron`, `capsule`, `plane`, `ring`, `line`, `tube`, `points`, `arrow`, `surface` (height field with per-vertex colours), `text` (label only), `group` and `model` (glTF/GLB).
- **Built-in motion** (`Animate3D`): `Spin`, `Bob`, `Pulse`, `Orbit`, `Path` (follows points, facing travel), `Wander`. Motion respects `prefers-reduced-motion`.
- **Live updates without re-sending the scene:** `UpdateAsync(patches, durationMs)` tweens positions, scales and colours.
- **Models:** `PlayClipAsync(id, "Wave")`, `SetMorphAsync(id, "Smile", 1)`, `OnModelLoaded` reports clip names, morph names and triangle count.
- **Camera:** `FlyToAsync`, `FocusAsync`. **Utilities:** `ScreenshotAsync` (PNG data URL), `StatsAsync`, `SimplifyAsync` (mesh decimation), `SetWireframeAsync`.
- **Speech with lip sync:** `SpeakAsync(objectId, text, "id-ID")` uses the browser's speech synthesis and drives any `Talk`/`Open`/`Jaw` morph on the model while speaking.
- **Performance:** scenes off-screen stop rendering (they redraw one frame when their content changes); models load once per URL and are cloned; pixel ratio is capped at 2.

`Layout3D` gives deterministic layouts for your own scenes: `Force` (3-D force-directed graph), `Tree`, `Fibonacci` (points on a sphere), `Pca3` (project vectors to 3-D), `Normalize` and `Arc` (curved edges). `ThreeDPalette` holds the kawung colours plus `Heat` and `Ramp` scales.

## AI pattern

Components inherit `ThreeDComponentBase`. Geometry, physics, statistics, training and simulation run in .NET; the model plans (structured JSON via `PlanAsync`) or narrates (`NarrateAsync`) from computed facts. Every generative component has a deterministic local fallback, so it works with the offline simulated model.

## Shipped assets

| Constant | Asset |
|---|---|
| `ThreeDAssets.GuideBot` | Rigged guide robot. Clips: Idle, Wave, Talk, Nod, Point, Walk, Happy, Think. Visor morphs: Talk, Smile, Sad |
| `ThreeDAssets.Fish` | Rigged fish with a Swim loop |
| `ThreeDAssets.RobotArm` | Industrial arm with a PickPlace cycle |
| `ThreeDAssets.Heart`, `Stupa`, `Corn`, `Car`, `Mannequin` | Static models at real-world scale |
| `ThreeDAssets.HeroArt`, `WorldConcept`, `NpcBlacksmith`, `NpcMerchant`, `NpcGuardian` | 2-D art (WebP) |

The models were generated with Rodin and rigged and animated in Blender through MCP; see [tools/blender](../../tools/blender/README.md) to regenerate or adapt them. Models face +Z.

## Components

See the [component catalogue](components.md#blazoraithreed) for every component's parameters. The gallery (`/3d…` pages) shows each one live with copy-ready code.
