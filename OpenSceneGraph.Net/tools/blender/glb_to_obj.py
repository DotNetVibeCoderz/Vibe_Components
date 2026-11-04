# Converts a Rodin/any GLB into a game-ready OBJ for OpenSceneGraph.Net:
#   Z-up, longest horizontal axis -> +Y (forward), base on z=0, scaled to --size metres,
#   decimated to --faces triangles, texture baked out as PNG (<= 1024 px).
# Usage: blender -b --python tools/blender/glb_to_obj.py -- in.glb out.obj --size 15 --faces 6000 [--yaw 0]
import bpy, sys, os, math, mathutils
argv = sys.argv[sys.argv.index("--") + 1:]
src, dst = argv[0], argv[1]
opt = {argv[i]: argv[i + 1] for i in range(2, len(argv) - 1, 2)}
size = float(opt.get("--size", 10)); faces = int(opt.get("--faces", 6000)); yaw = float(opt.get("--yaw", 0))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
meshes = [o for o in bpy.context.scene.objects if o.type == 'MESH']
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1: bpy.ops.object.join()
obj = bpy.context.view_layer.objects.active
bpy.ops.object.parent_clear(type='CLEAR_KEEP_TRANSFORM')
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

# orient: longest horizontal extent along +Y
d = obj.dimensions
angle = (90 if d.x > d.y else 0) + yaw
if angle: obj.data.transform(mathutils.Matrix.Rotation(math.radians(angle), 4, 'Z')); obj.data.update()
# scale + ground + centre
s = size / max(obj.dimensions)
obj.scale = (s, s, s); bpy.ops.object.transform_apply(scale=True)
bb = [obj.matrix_world @ mathutils.Vector(c) for c in obj.bound_box]
cx = (min(v.x for v in bb) + max(v.x for v in bb)) / 2; cy = (min(v.y for v in bb) + max(v.y for v in bb)) / 2; mz = min(v.z for v in bb)
obj.location = (-cx, -cy, -mz); bpy.ops.object.transform_apply(location=True)

tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
if tris > faces:
    m = obj.modifiers.new("dec", 'DECIMATE'); m.ratio = faces / tris
    bpy.ops.object.modifier_apply(modifier=m.name)

# save textures as PNG next to the OBJ, downscaled
out_dir = os.path.dirname(os.path.abspath(dst)); base = os.path.splitext(os.path.basename(dst))[0]
for i, img in enumerate(bpy.data.images):
    if img.size[0] == 0: continue
    if max(img.size) > 1024:
        k = 1024 / max(img.size); img.scale(int(img.size[0] * k), int(img.size[1] * k))
    img.filepath_raw = os.path.join(out_dir, f"{base}_tex{i}.png"); img.file_format = 'PNG'; img.save()

bpy.ops.wm.obj_export(filepath=dst, export_selected_objects=False, forward_axis='Y', up_axis='Z',
                      export_materials=True, path_mode='STRIP', export_triangulated_mesh=True, export_normals=True, export_uv=True)
print("EXPORTED", dst, "tris", sum(len(p.vertices) - 2 for p in obj.data.polygons), "dims", tuple(round(v, 2) for v in obj.dimensions))
# keep only the base-colour map (OpenSceneGraph.Net uses map_Kd); drop normal/ORM maps and their MTL lines
mtl = os.path.splitext(dst)[0] + ".mtl"
if os.path.exists(mtl):
    lines = open(mtl).read().splitlines()
    keep = [l for l in lines if not l.startswith(("map_Bump", "map_Ks", "map_Ns", "map_d", "map_refl", "map_Pr", "map_Pm", "norm", "map_Ke"))]
    used = {l.split()[-1] for l in keep if l.startswith("map_Kd")}
    open(mtl, "w").write("\n".join(keep) + "\n")
    for f in os.listdir(out_dir):
        if f.startswith(base + "_tex") and f not in used: os.remove(os.path.join(out_dir, f))
    print("TEXTURES", used)
