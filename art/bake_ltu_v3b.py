"""Low-poly game mesh + texture bake for the v3b LTU (variant "baked"), step 1 of 2 (Blender).

    blender -b LTUConceptV3b.blend --python bake_ltu_v3b.py
    python bake_ltu_v3b_post.py          (step 2: composite, DU texture set, DDS, .nqz)

Builds a low-poly copy of each part (hidden faces culled as in v2, planar dissolve, collapse
decimate, sharp edges by angle), unwraps the four parts into one atlas (the canisters are not
copies: CanB's port and straps face the other way), then bakes the full-detail v3b parts onto it with Cycles,
part by part (selected to active, each low only sees its own high, so a canister never picks
up the bore wall):
    normal   tangent space (Blender's convention, checked against the stock _n in step 2)
    ao       ambient occlusion with every part present (the wells darken)
    albedo   emission bake of DU zone colours sampled from the stock TU _c
    mr       emission bake: R = metalness, G = roughness (stock TU _mrao values)
    emask    emission bake: 1 on the blue status zone (drives the _e map)
Raw float passes and the UV triangles go to C:/Claude/scratch/ltu-bake/ for step 2.

Also writes the skinned game mesh (same bones and pump clip as v2), LOD1 and collision:
  ../assets/client/ltu_baked_v1.mesh, ltu_baked_v1_col.mesh, ltu_baked_v1_pump.anim
  ../assets/ltu_baked_v1.surfaces.json
"""
import json
import math
import os
import sys
import time

import bmesh
import bpy
import numpy as np
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "unigine_mesh"))
import an10  # noqa: E402
import blender_io  # noqa: E402
import ltu_v3b_lib as lib  # noqa: E402
import ms11  # noqa: E402
from ltu_v3b_lib import BIND_Z, BONE_OF, BONES, CLIP, FRAMES, LOD_SWITCH_M, OUT, PARTS, TU_MAT  # noqa: E402

VER = "v1"
NAME = f"ltu_baked_{VER}"
META = os.path.join(HERE, "..", "assets", f"{NAME}.surfaces.json")
SCRATCH = "C:/Claude/scratch/ltu-bake"
BAKE_RES = 2048            # baked at 2x, written at 1024 by step 2
TARGET = {"LTU_Body": 1700, "LTU_Frame": 450, "LTU_CanA": 450, "LTU_CanB": 450}
LOD1_TARGET = 650          # whole unit
PLANAR_DEG = 2.0
SHARP_DEG = 40.0
EXTRUSION = 0.015          # bake cage push-out (m); decimated cylinders sit up to ~7 mm inside
MAX_RAY = 0.045

# Zone values: albedo sRGB bytes (white/grey/dark/orange sampled from the stock TU _c at the
# uniform windows used in v1), metalness and roughness bytes (stock _mrao R and G there).
ZONE = {
    "DU_WhiteEnamel":  ((175, 174, 175), 0, 88),
    "DU_GreyEnamel":   ((160, 160, 162), 0, 120),     # lighter than the stock patch (116): the approved v3b drums
    "DU_BlackChassis": ((36, 35, 36), 0, 97),
    "DU_Gunmetal":     ((58, 60, 64), 230, 85),
    "DU_Steel":        ((150, 153, 158), 255, 90),
    "DU_Orange":       ((181, 81, 38), 0, 84),
    "DU_RedTag":       ((160, 30, 28), 0, 95),
    "DU_BlueStatus":   ((205, 218, 235), 0, 70),
}


def log(*a):
    print("[bake]", *a, flush=True)


# ---------------------------------------------------------------- low poly

def tris(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)


def make_low(src, target, name):
    me = src.data.copy()
    me.name = name
    ob = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(ob)
    ob.matrix_world = src.matrix_world.copy()
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-5)
    bmesh.ops.dissolve_limit(bm, angle_limit=math.radians(PLANAR_DEG), use_dissolve_boundaries=False,
                             verts=bm.verts[:], edges=bm.edges[:], delimit={"MATERIAL"})
    bmesh.ops.triangulate(bm, faces=bm.faces[:])
    bm.to_mesh(me)
    bm.free()
    n = tris(ob)
    if n > target:
        mod = ob.modifiers.new("dec", "DECIMATE")
        mod.ratio = target / n
        mod.use_collapse_triangulate = True
        dg = bpy.context.evaluated_depsgraph_get()
        me2 = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
        ob.modifiers.clear()
        ob.data = me2
        me2.name = name
    sharpen(ob)
    return ob


def sharpen(ob):
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    lim = math.radians(SHARP_DEG)
    for f in bm.faces:
        f.smooth = True
    for e in bm.edges:
        e.smooth = not (len(e.link_faces) != 2 or e.calc_face_angle(0) > lim)
    bm.to_mesh(ob.data)
    bm.free()


def unwrap(objs):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in objs:
        o.select_set(True)
        if not o.data.uv_layers:
            o.data.uv_layers.new(name="UV0")
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.003, area_weight=0.0,
                             correct_aspect=True, scale_to_bounds=False)
    bpy.ops.uv.select_all(action="SELECT")
    bpy.ops.uv.pack_islands(udim_source="CLOSEST_UDIM", rotate=True, margin_method="SCALED", margin=0.004)
    bpy.ops.object.mode_set(mode="OBJECT")


# ---------------------------------------------------------------- bake

def setup_cycles():
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    dev = "CPU"
    for t in ("OPTIX", "CUDA"):
        try:
            prefs.compute_device_type = t
            prefs.get_devices()
            if any(d.type == t for d in prefs.devices):
                for d in prefs.devices:
                    d.use = d.type == t
                dev = t
                break
        except TypeError:
            pass
    sc.cycles.device = "GPU" if dev != "CPU" else "CPU"
    sc.cycles.use_denoising = False
    b = sc.render.bake
    b.use_selected_to_active = True
    b.use_cage = False
    b.cage_extrusion = EXTRUSION
    b.max_ray_distance = MAX_RAY
    b.margin = 4
    b.use_clear = True
    b.normal_space = "TANGENT"
    log("cycles device", dev)


def emission_mat(name, rgb):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    em = nt.nodes.new("ShaderNodeEmission")
    em.inputs["Color"].default_value = (*rgb, 1)
    em.inputs["Strength"].default_value = 1.0
    out = nt.nodes.new("ShaderNodeOutputMaterial")
    nt.links.new(em.outputs[0], out.inputs[0])
    return m


def target_mat(low, img):
    m = bpy.data.materials.new(f"bake_{low.name}")
    m.use_nodes = True
    nt = m.node_tree
    node = nt.nodes.new("ShaderNodeTexImage")
    node.image = img
    nt.nodes.active = node
    low.data.materials.clear()
    low.data.materials.append(m)


def with_override(highs, kind, fn):
    """Temporarily swap every high part's zone materials for flat emission ones."""
    saved = {h.name: [s.material for s in h.material_slots] for h in highs}
    if kind:
        for h in highs:
            for s in h.material_slots:
                rgb, metal, rough = ZONE[saved[h.name][s.slot_index].name]
                col = {"albedo": tuple(c / 255 for c in rgb),
                       "mr": (metal / 255, rough / 255, 0.0),
                       "emask": (1.0, 1.0, 1.0) if s.material.name == "DU_BlueStatus" else (0.0, 0.0, 0.0),
                       "cover": (1.0, 1.0, 1.0)}[kind]
                s.material = emission_mat(f"bk_{kind}_{'%.4f_%.4f_%.4f' % col}", col)
    try:
        fn()
    finally:
        for h in highs:
            for s, m in zip(h.material_slots, saved[h.name]):
                s.material = m


def bake_part(low, high, highs, pass_name, img):
    sc = bpy.context.scene
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    high.select_set(True)
    low.select_set(True)
    bpy.context.view_layer.objects.active = low
    target_mat(low, img)
    kind = {"normal": None, "ao": None}.get(pass_name, pass_name)
    btype = {"normal": "NORMAL", "ao": "AO"}.get(pass_name, "EMIT")
    sc.cycles.samples = 128 if pass_name == "ao" else 4

    def run():
        bpy.ops.object.bake(type=btype)
    with_override(highs, kind, run)


def save_pass(img, path, ch):
    a = np.empty(img.size[0] * img.size[1] * 4, dtype=np.float32)
    img.pixels.foreach_get(a)
    a = a.reshape(img.size[1], img.size[0], 4)[::-1, :, :ch]      # row 0 = top (file v = 0)
    np.save(path, a.astype(np.float16))


def uv_tris(o):
    me = o.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    out = np.empty((len(me.loop_triangles), 3, 2), dtype=np.float32)
    for i, lt in enumerate(me.loop_triangles):
        for k, li in enumerate(lt.loops):
            u, v = uv[li].uv
            out[i, k] = (u, 1.0 - v)                              # file uv (v down)
    return out


# ---------------------------------------------------------------- run

t0 = time.time()
os.makedirs(SCRATCH, exist_ok=True)
lib.assembly_frame()
vis = lib.visible_faces()
log("visible faces", {n: len(v) for n, v in vis.items()}, f"{time.time() - t0:.0f}s")
culled = {n: lib.culled(n, vis[n]) for n in PARTS}

# The canisters are not copies of each other (the v3b builder turns CanB's port and straps
# to face outward), so each gets its own islands and its own bake.
lows = {n: make_low(culled[n], TARGET[n], n + "_low") for n in PARTS}
unwrap([lows[n] for n in PARTS])
log("low tris", {n: tris(o) for n, o in lows.items()})

# hide the low parts from every ray so AO and projection only see the high detail
for o in lows.values():
    o.visible_camera = o.visible_diffuse = o.visible_glossy = False
    o.visible_transmission = o.visible_volume_scatter = o.visible_shadow = False
for o in bpy.data.objects:
    if o.name.endswith("_culled") or o.name in ("Floor", "STOCK_TU"):
        o.hide_render = True
setup_cycles()
highs = [bpy.data.objects[n] for n in PARTS]
CH = {"normal": 3, "ao": 1, "albedo": 3, "mr": 2, "emask": 1, "cover": 1}
for part in PARTS:
    for pname, ch in CH.items():
        img = bpy.data.images.new(f"{part}_{pname}", BAKE_RES, BAKE_RES, float_buffer=True, alpha=False)
        img.colorspace_settings.name = "Non-Color"
        t1 = time.time()
        bake_part(lows[part], bpy.data.objects[part], highs, pname, img)
        save_pass(img, os.path.join(SCRATCH, f"{part}_{pname}.npy"), ch)
        bpy.data.images.remove(img)
        log(part, pname, f"{time.time() - t1:.1f}s")
    np.save(os.path.join(SCRATCH, f"{part}_uvtris.npy"), uv_tris(lows[part]))

# ---------------------------------------------------------------- game mesh
for n, o in lows.items():
    o["ms11_bone"] = BONE_OF[n]
bind = {0: (0.0, 0.0, 0.0), 1: lib.can_base("LTU_CanA"), 2: lib.can_base("LTU_CanB")}
bones = [(BONES[i], -1, (*bind[i], 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0)) for i in range(3)]

lod1 = {}
total0 = sum(tris(o) for o in lows.values())
for n, o in lows.items():
    c = o.copy()
    c.data = o.data.copy()
    c.name = n + "_lod1"
    bpy.context.scene.collection.objects.link(c)
    mod = c.modifiers.new("dec", "DECIMATE")
    mod.ratio = LOD1_TARGET / total0
    mod.use_collapse_triangulate = True
    dg = bpy.context.evaluated_depsgraph_get()
    me2 = bpy.data.meshes.new_from_object(c.evaluated_get(dg))
    c.modifiers.clear()
    c.data = me2
    sharpen(c)
    c["ms11_bone"] = BONE_OF[n]
    lod1[n] = c

for o in list(lows.values()) + list(lod1.values()):
    o.data.materials.clear()
MESH, COLF, ANIM = f"{NAME}.mesh", f"{NAME}_col.mesh", f"{NAME}_pump.anim"
spec = [("ltu_baked_lod0", list(lows.values())), ("ltu_baked_lod1", list(lod1.values()))]
mesh = blender_io.export_mesh(os.path.join(OUT, MESH), spec, bones=bones)
col, hull_tris = lib.collision()
colmesh = blender_io.export_mesh(os.path.join(OUT, COLF), [(f"{NAME}_col", [col])])

poses = []
for f in range(FRAMES):
    za, zb = lib.heights(f)
    row = [(bind[0], (0.0, 0.0, 0.0, 1.0))]
    for b, z in ((1, za), (2, zb)):
        x, y, z0 = bind[b]
        row.append(((x, y, z0 + (z - BIND_Z)), (0.0, 0.0, 0.0, 1.0)))
    poses.append(row)
clip = an10.encode_clip(CLIP.encode(), [0, 1, 2], poses)
an10.write_file(os.path.join(OUT, ANIM), an10.Anim(tuple(mesh.bounds), list(mesh.bones), list(mesh.bone_xforms), [clip]))
for fn, mod in ((MESH, ms11), (COLF, ms11), (ANIM, an10)):
    raw = open(os.path.join(OUT, fn), "rb").read()
    assert mod.write(mod.read(raw)) == raw, fn

report = {
    "lod0_tris": sum(len(s.cindices) // 3 for s in mesh.surfaces if s.name.endswith(b"lod0")),
    "lod1_tris": sum(len(s.cindices) // 3 for s in mesh.surfaces if s.name.endswith(b"lod1")),
    "low_tris": {n: tris(o) for n, o in lows.items()},
    "lod1_part_tris": {n: tris(o) for n, o in lod1.items()},
    "bounds": [round(x, 4) for x in mesh.bounds],
    "bake": {"res": BAKE_RES, "extrusion": EXTRUSION, "max_ray": MAX_RAY},
    "col_tris": len(colmesh.surfaces[0].cindices) // 3,
    "bytes": {fn: os.path.getsize(os.path.join(OUT, fn)) for fn in (MESH, COLF, ANIM)},
    "seconds": round(time.time() - t0),
}
json.dump({"name": NAME, "surfaces": [
    {"name": "ltu_baked_lod0", "material": "ltu_baked_body", "max": LOD_SWITCH_M, "min": None},
    {"name": "ltu_baked_lod1", "material": "ltu_baked_body", "max": None, "min": LOD_SWITCH_M}],
    "col_surface": f"{NAME}_col", "skinned": True, "anim": {"file": ANIM, "name": "running", "loop": True},
    "parent_material": TU_MAT, "report": report}, open(META, "w"), indent=2)
bpy.ops.wm.save_as_mainfile(filepath=os.path.join(SCRATCH, "LTUBakedV1.blend"), copy=True)
log(json.dumps(report, indent=1))
