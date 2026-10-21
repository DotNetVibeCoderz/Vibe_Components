"""Rig and animate a Rodin-generated humanoid robot for Blazor.AI.ThreeD (Blender 5.x).

Run inside Blender (Scripting tab, or via MCP for Blender's execute_blender_code) after importing the
Rodin GLB as the only object. Produces guide-bot.glb with 8 clips (Idle, Wave, Talk, Nod, Point, Walk,
Happy, Think) on NLA tracks and a visor "VoiceBar" with Talk/Smile/Sad shape keys used for lip sync.

Joint positions are for a 1.71 m robot facing -Y in an A-pose; adjust JOINTS for other models.
Dibuat oleh Gravicode Studios dipimpin oleh Kang Fadhil.
"""
import math
import bmesh
import bpy
from mathutils import Matrix, Vector

OUT = r"guide-bot.glb"
r = math.radians

body = next(o for o in bpy.context.scene.objects if o.type == "MESH")
body.name = "GuideBot"

# Stand on the ground, reduce to ~15k faces.
zmin = min((body.matrix_world @ v.co).z for v in body.data.vertices)
body.location.z -= zmin
bpy.context.view_layer.objects.active = body
body.select_set(True)
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
dec = body.modifiers.new("dec", "DECIMATE")
dec.ratio = 0.4
bpy.ops.object.modifier_apply(modifier="dec")

# ---- armature
arm = bpy.data.armatures.new("GuideRig")
rig = bpy.data.objects.new("GuideRig", arm)
bpy.context.scene.collection.objects.link(rig)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="EDIT")


def bone(name, head, tail, parent=None, connect=False):
    b = arm.edit_bones.new(name)
    b.head, b.tail = Vector(head), Vector(tail)
    if parent:
        b.parent = arm.edit_bones[parent]
        b.use_connect = connect


bone("Hips", (0, 0.01, 0.80), (0, 0.01, 1.0))
bone("Spine", (0, 0.01, 1.0), (0, 0.01, 1.18), "Hips", True)
bone("Chest", (0, 0.01, 1.18), (0, 0.01, 1.30), "Spine", True)
bone("Neck", (0, 0.01, 1.30), (0, 0.01, 1.40), "Chest", True)
bone("Head", (0, 0.01, 1.40), (0, 0.01, 1.71), "Neck", True)
for s, x in (("L", 1), ("R", -1)):
    bone(f"Shoulder.{s}", (0.06 * x, 0.01, 1.24), (0.22 * x, 0.01, 1.19), "Chest")
    bone(f"UpperArm.{s}", (0.22 * x, 0.01, 1.19), (0.47 * x, 0.01, 1.08), f"Shoulder.{s}", True)
    bone(f"LowerArm.{s}", (0.47 * x, 0.01, 1.08), (0.66 * x, 0.01, 1.01), f"UpperArm.{s}", True)
    bone(f"Hand.{s}", (0.66 * x, 0.01, 1.01), (0.77 * x, 0.01, 0.95), f"LowerArm.{s}", True)
    bone(f"UpperLeg.{s}", (0.12 * x, 0.01, 0.80), (0.13 * x, 0.01, 0.40), "Hips")
    bone(f"LowerLeg.{s}", (0.13 * x, 0.01, 0.40), (0.13 * x, 0.02, 0.10), f"UpperLeg.{s}", True)
    bone(f"Foot.{s}", (0.13 * x, 0.02, 0.10), (0.13 * x, -0.17, 0.03), f"LowerLeg.{s}", True)
bpy.ops.object.mode_set(mode="OBJECT")
bpy.ops.object.select_all(action="DESELECT")
body.select_set(True)
rig.select_set(True)
bpy.context.view_layer.objects.active = rig
bpy.ops.object.parent_set(type="ARMATURE_AUTO")

# ---- clips (axes: UpperArm X raises/lowers, Z swings forward; legs forward on -X; head nods on X, turns on Y)
pb = rig.pose.bones
REST = {"UpperArm.L": (-55, 0, 0), "UpperArm.R": (-55, 0, 0), "LowerArm.L": (0, 0, -15), "LowerArm.R": (0, 0, 15)}
rig.animation_data_create()


def pose(d):
    for b in pb:
        b.rotation_mode = "XYZ"
        b.rotation_euler = (0, 0, 0)
        b.location = (0, 0, 0)
    for k, v in {**REST, **{k: v for k, v in d.items() if k != "HipsZ"}}.items():
        pb[k].rotation_euler = tuple(r(a) for a in v)
    if "HipsZ" in d:
        pb["Hips"].location = (0, d["HipsZ"], 0)


def make(name, frames):
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    rig.animation_data.action = act
    for f, d in frames:
        pose(d)
        for b in pb:
            b.keyframe_insert("rotation_euler", frame=f)
            b.keyframe_insert("location", frame=f)
    rig.animation_data.action = None
    track = rig.animation_data.nla_tracks.new()
    track.name = name
    track.strips.new(name, int(frames[0][0]), act)


make("Idle", [(1, {}), (24, {"Chest": (-2, 0, 0), "Head": (2, 4, 0), "UpperArm.L": (-53, 0, 0), "UpperArm.R": (-53, 0, 0), "HipsZ": 0.006}), (48, {})])
wave_up = {"UpperArm.R": (95, 0, 0), "Head": (0, -8, 4)}
make("Wave", [(1, {}), (10, {**wave_up, "LowerArm.R": (25, 0, 0)}), (18, {**wave_up, "LowerArm.R": (-20, 0, 0)}),
              (26, {**wave_up, "LowerArm.R": (25, 0, 0)}), (34, {**wave_up, "LowerArm.R": (-20, 0, 0)}), (48, {})])
make("Talk", [(1, {}), (12, {"Head": (4, 3, 0), "LowerArm.L": (0, 0, -45), "UpperArm.L": (-45, 0, -10)}),
              (24, {"Head": (-2, -3, 0), "LowerArm.R": (0, 0, 45), "UpperArm.R": (-45, 0, 10)}),
              (36, {"Head": (3, 0, 2), "LowerArm.L": (0, 0, -35), "LowerArm.R": (0, 0, 35)}), (48, {})])
make("Nod", [(1, {}), (7, {"Head": (16, 0, 0)}), (13, {"Head": (-2, 0, 0)}), (19, {"Head": (12, 0, 0)}), (26, {})])
point = {"UpperArm.R": (15, 0, 70), "LowerArm.R": (0, 0, 5), "Head": (0, -15, 0), "Chest": (0, -8, 0)}
make("Point", [(1, {}), (12, point), (36, point), (48, {})])
step_a = {"UpperLeg.L": (-25, 0, 0), "UpperLeg.R": (20, 0, 0), "LowerLeg.R": (30, 0, 0), "UpperArm.L": (-55, 0, -20), "UpperArm.R": (-55, 0, -20)}
step_b = {"UpperLeg.L": (20, 0, 0), "LowerLeg.L": (30, 0, 0), "UpperLeg.R": (-25, 0, 0), "UpperArm.L": (-55, 0, 20), "UpperArm.R": (-55, 0, 20)}
make("Walk", [(1, step_a), (9, {"HipsZ": 0.02}), (17, step_b), (25, {"HipsZ": 0.02}), (33, step_a)])
cheer = {"UpperArm.L": (100, 0, 0), "UpperArm.R": (100, 0, 0), "HipsZ": 0.06, "Head": (-10, 0, 0)}
make("Happy", [(1, {}), (8, cheer), (14, {"UpperArm.L": (90, 0, 0), "UpperArm.R": (90, 0, 0)}), (20, cheer), (36, {})])
think = {"UpperArm.R": (-30, 0, 45), "LowerArm.R": (0, 0, 120), "Head": (6, 0, -10)}
make("Think", [(1, {}), (14, think), (40, {**think, "Head": (6, 6, -10)}), (52, {})])
pose({})

# ---- visor voice bar with shape keys (lip sync and emotion)
bm = bmesh.new()
bmesh.ops.create_cube(bm, size=1.0)
bmesh.ops.scale(bm, vec=(0.085, 0.006, 0.012), verts=bm.verts)
bmesh.ops.subdivide_edges(bm, edges=[e for e in bm.edges if abs(e.verts[0].co.x - e.verts[1].co.x) > 0.01], cuts=8)
me = bpy.data.meshes.new("VoiceBar")
bm.to_mesh(me)
bm.free()
bar = bpy.data.objects.new("VoiceBar", me)
bpy.context.scene.collection.objects.link(bar)
bar.location = (0, -0.207, 1.50)
bar.shape_key_add(name="Basis")
for name, fn in (("Talk", lambda c: (c.x * 0.75, c.y, c.z * 4.2)),
                 ("Smile", lambda c: (c.x * 1.05, c.y, c.z + (c.x / 0.0425) ** 2 * 0.012)),
                 ("Sad", lambda c: (c.x * 0.9, c.y, c.z - (c.x / 0.0425) ** 2 * 0.010))):
    key = bar.shape_key_add(name=name, from_mix=False)
    for i, v in enumerate(key.data):
        v.co = fn(me.vertices[i].co)
    key.value = 0.0
glow = bpy.data.materials.new("VoiceGlow")
glow.use_nodes = True
bsdf = next(n for n in glow.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
bsdf.inputs["Base Color"].default_value = (0.24, 0.77, 0.69, 1)
bsdf.inputs["Emission Color"].default_value = (0.24, 0.9, 0.78, 1)
bsdf.inputs["Emission Strength"].default_value = 3.0
me.materials.append(glow)
bar.parent = rig
bar.parent_type = "BONE"
bar.parent_bone = "Head"
head = rig.pose.bones["Head"]
bar.matrix_parent_inverse = (rig.matrix_world @ head.matrix @ Matrix.Translation((0, head.length, 0))).inverted()

# ---- export
for img in bpy.data.images:
    if img.size[0] > 1024:
        img.scale(1024, 1024)
for o in bpy.data.objects:
    o.select_set(o.name in ("GuideBot", "GuideRig", "VoiceBar"))
bpy.ops.export_scene.gltf(filepath=OUT, export_format="GLB", use_selection=True, export_animation_mode="NLA_TRACKS",
                          export_morph=True, export_skins=True, export_draco_mesh_compression_enable=True,
                          export_draco_mesh_compression_level=6, export_image_format="WEBP")
print("exported", OUT)
