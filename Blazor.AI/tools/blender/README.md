# 3D asset pipeline

*Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.*

The models and art in `src/Blazor.AI.ThreeD/wwwroot` were made with AI generators and Blender, driven through MCP from Claude Code:

| Asset | Made with | Notes |
|---|---|---|
| `models/guide-bot.glb` | Rodin text-to-3D → Blender rig + 8 clips + visor shape keys | `rig_guide_bot.py` |
| `models/fish.glb` | Rodin → Blender 4-bone spine + travelling-wave "Swim" loop | bones `Head, Spine1, Spine2, Tail`; amplitude grows towards the tail |
| `models/robot-arm.glb` | Procedural in Blender (bmesh) + object-level "PickPlace" animation | joints `J1_Turret … J4_Wrist`; crate hand-off via constant scale keys |
| `models/heart.glb`, `stupa.glb`, `corn.glb`, `car.glb`, `mannequin.glb` | Rodin → Blender clean-up | real-world size, origin at the base, decimated, Draco + WebP |
| `art/*.webp` | Nano Banana 2 (Rodin MCP) | hero banner, NPC portraits, world concept art |

## Export settings

All GLBs use Draco mesh compression (level 6) and WebP textures at most 1024 px. The engine loads them with the
vendored `DRACOLoader`, so no CDN is needed. Blender's −Y becomes glTF +Z, so models face **+Z** in the browser;
`Animate3D.Path` and `Wander` turn objects so +Z points along the direction of travel.

## Regenerating the guide robot

1. Generate a humanoid in an A-pose with Rodin (prompt in `rig_guide_bot.py`'s history: friendly guide robot, white and indigo panels, amber accents, teal visor).
2. Import the GLB into an empty Blender scene.
3. Run `rig_guide_bot.py` (adjust the joint coordinates if your model's proportions differ; it prints nothing until export).
4. Copy `guide-bot.glb` to `src/Blazor.AI.ThreeD/wwwroot/models/`.

Clips are stored as NLA tracks and exported with `export_animation_mode="NLA_TRACKS"`, so each becomes a named glTF
animation that `Scene3D.PlayClipAsync(objectId, "Wave")` can play. Shape keys on the visor bar drive lip sync
(`Talk`, animated by the engine while speech is playing) and emotion (`Smile`, `Sad`).
