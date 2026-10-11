"""Shared pieces of the v3b LTU exporters (export_ltu_v3b_mesh.py: the tinted skinned mesh;
bake_ltu_v3b.py: the low-poly baked mesh). Runs inside Blender on LTUConceptV3b.blend.

Set CENTRE and EXTENT (the patch-UV frame, bind pose) before calling zone_objects/collision.
"""
import json
import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

HERE = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "unigine_mesh"))

VER = "v2"
OUT = os.path.join(HERE, "..", "assets", "client")
META = os.path.join(HERE, "..", "assets", f"ltu_mesh_{VER}.surfaces.json")
PARTS = ["LTU_Body", "LTU_Frame", "LTU_CanA", "LTU_CanB"]
BONE_OF = {"LTU_Body": 0, "LTU_Frame": 0, "LTU_CanA": 1, "LTU_CanB": 2}
BONES = ["ltu_root", "ltu_can_a", "ltu_can_b"]
STROKE = 0.26
BIND_Z = STROKE * 0.5
LOD1_RATIO = 0.30
LOD_SWITCH_M = 50          # as the stock TU: lod0 up to 50 m, lod1 beyond
COL_TRIS = 120
TU_MAT = "env_core-round-side-industry_001_01"

# Clip: 30 frames/s is ObjectMeshSkinned's default speed (the refiner node sets none, many
# stock nodes write <speed>30</speed>); 80 frames = 2.67 s per cycle. The refiner's
# cylindre-on is 160 frames (5.3 s): ease up ~1.6 s, hold, ease down ~1.6 s. Ours is the same
# shape at twice the rate, with B half a cycle behind A.
FRAMES = 80
RISE = 0.42                # fraction of the cycle spent rising (and again falling)
HOLD = 0.5 - RISE          # dwell at each end
CLIP = "ltu_pump"

# Uniform windows of the stock TU texture set (Blender uv, v up), 24 px of 2048.
PATCH = {
    "white": (0.2246, 0.9707),   # _c 0.69 grey-white, mrao metal 0 rough 0.34
    "grey": (0.6816, 0.3848),    # _c 0.45
    "dark": (0.0449, 0.1074),    # _c 0.14
    "orange": (0.5410, 0.1230),  # _c (0.71, 0.32, 0.15), the cap
    "emit": (0.4824, 0.5879),    # _e fully lit: status lights follow the TU states
}
HALF_WINDOW = 0.0045            # uv half-size used inside a window (window is 0.0117)

# v3b zone material -> (surface/material suffix, patch, albedo_color tint)
ZONES = {
    "DU_WhiteEnamel": ("white", "white", "1 1 1 1"),
    "DU_GreyEnamel": ("grey", "grey", "1 1 1 1"),
    "DU_BlackChassis": ("chassis", "dark", "1 1 1 1"),
    "DU_Gunmetal": ("gunmetal", "grey", "0.62 0.64 0.68 1"),
    "DU_Steel": ("steel", "white", "0.9 0.92 0.95 1"),
    "DU_Orange": ("orange", "orange", "1 1 1 1"),
    "DU_RedTag": ("redtag", "orange", "1 0.45 0.45 1"),
    "DU_BlueStatus": ("status", "emit", "0.35 0.6 1 1"),
}


# ---------------------------------------------------------------- motion

def smoother(x):
    x = min(1.0, max(0.0, x))
    return x * x * x * (x * (x * 6 - 15) + 10)


def lift(phase):
    """0..1 height of a canister at cycle phase 0..1: rise, hold up, fall, hold down."""
    p = phase % 1.0
    if p < RISE:
        return smoother(p / RISE)
    if p < 0.5:
        return 1.0
    if p < 0.5 + RISE:
        return 1.0 - smoother((p - 0.5) / RISE)
    return 0.0


def heights(frame):
    """(A, B) location.z of the canisters at a frame: A leads, B is half a cycle behind."""
    ph = frame / FRAMES
    return STROKE * lift(ph), STROKE * lift(ph + 0.5)


def set_pose(za, zb):
    bpy.data.objects["LTU_CanA"].location.z = za
    bpy.data.objects["LTU_CanB"].location.z = zb
    bpy.context.view_layer.update()


# ---------------------------------------------------------------- hidden-face culling

def _dirs(n):
    ga = math.pi * (3 - math.sqrt(5))
    out = []
    for i in range(n):
        z = 1 - 2 * (i + 0.5) / n
        r = math.sqrt(1 - z * z)
        out.append(Vector((r * math.cos(ga * i), r * math.sin(ga * i), z)))
    return out


def _axis_dirs():
    """The 26 lattice directions plus 5-degree steps in the axis planes: the body's seams and
    slots run along the axes, so their walls show only from near those planes."""
    out = [Vector((x, y, z)).normalized() for x in (-1, 0, 1) for y in (-1, 0, 1) for z in (-1, 0, 1)
           if (x, y, z) != (0, 0, 0)]
    for k in range(0, 360, 5):
        a = math.radians(k)
        c, s_ = math.cos(a), math.sin(a)
        out += [Vector((c, s_, 0)), Vector((c, 0, s_)), Vector((0, c, s_))]
    return out


VIEW_DIRS = _dirs(160) + _axis_dirs()
VIEW_RES = 1024            # 3.1 mm per pixel over the 3.2 m view
SWEEP = [(STROKE, 0.0), (STROKE * 0.75, STROKE * 0.25), (STROKE * 0.5, STROKE * 0.5),
         (STROKE * 0.25, STROKE * 0.75), (0.0, STROKE)]


def visible_faces():
    """{part: set(polygon index)} of faces that cover at least one pixel in some view at some
    stroke pose. An ID render: every face gets its index as a flat float colour, Workbench
    draws with back faces culled (as the client does), and the EXR is decoded exactly.

    (A first version cast rays from sample points on each face. It culled the big core faces
    that show only through the 5 mm panel gaps, because no sample point sat in a gap.)"""
    import numpy as np
    sc = bpy.context.scene
    saved = {o.name: o.hide_render for o in bpy.data.objects}
    for o in bpy.data.objects:
        o.hide_render = o.name not in PARTS
    owners, base = [], 0
    for n in PARTS:
        me = bpy.data.objects[n].data
        attr = me.color_attributes.get("ltu_id") or me.color_attributes.new("ltu_id", "FLOAT_COLOR", "CORNER")
        for p in me.polygons:
            gid = base + p.index + 1                    # 0 = background
            col = ((gid & 1023) / 1024.0, (gid >> 10) / 1024.0, 0.0, 1.0)
            for li in p.loop_indices:
                attr.data[li].color = col
            owners.append((n, p.index))
        me.color_attributes.active_color = attr
        base += len(me.polygons)
    r, d = sc.render, sc.display
    r.engine = "BLENDER_WORKBENCH"
    d.shading.light = "FLAT"
    d.shading.color_type = "VERTEX"
    d.shading.show_backface_culling = True
    d.render_aa = "OFF"
    r.resolution_x = r.resolution_y = VIEW_RES
    r.resolution_percentage = 100
    r.film_transparent = True
    sc.view_settings.view_transform = "Raw"
    sc.view_settings.look = "None"
    sc.view_settings.exposure = 0
    sc.view_settings.gamma = 1
    r.image_settings.file_format = "OPEN_EXR"
    r.image_settings.color_depth = "32"
    r.image_settings.exr_codec = "ZIP"
    cd = bpy.data.cameras.new("idcam")
    cd.type = "ORTHO"
    cd.ortho_scale = 3.2
    cd.clip_start, cd.clip_end = 0.01, 20
    cam = bpy.data.objects.new("idcam", cd)
    sc.collection.objects.link(cam)
    sc.camera = cam
    path = os.path.join(os.environ.get("TEMP", HERE), f"ltu_id_{os.getpid()}.exr")
    seen = set()
    target = Vector((0, 0, 1.28))
    for za, zb in SWEEP:
        set_pose(za, zb)
        for dv in VIEW_DIRS:
            cam.location = target + dv * 8
            cam.rotation_euler = (-dv).to_track_quat("-Z", "Y" if abs(dv.z) < 0.99 else "X").to_euler()
            r.filepath = path
            bpy.ops.render.render(write_still=True)
            img = bpy.data.images.load(path, check_existing=False)
            px = np.empty(len(img.pixels), dtype=np.float32)
            img.pixels.foreach_get(px)
            bpy.data.images.remove(img)
            px = px.reshape(-1, 4)
            px = px[px[:, 3] > 0.5]
            ids = np.rint(px[:, 0] * 1024).astype(np.int64) + (np.rint(px[:, 1] * 1024).astype(np.int64) << 10)
            seen.update(int(i) for i in np.unique(ids))
    seen.discard(0)
    if max(seen, default=0) > len(owners):
        raise SystemExit("ID decode out of range: colour management is not raw")
    bpy.data.objects.remove(cam)
    for n, h in saved.items():
        if n in bpy.data.objects:
            bpy.data.objects[n].hide_render = h
    for n in PARTS:
        me = bpy.data.objects[n].data
        me.color_attributes.remove(me.color_attributes["ltu_id"])
    set_pose(BIND_Z, BIND_Z)
    vis = {n: set() for n in PARTS}
    for gid in seen:
        n, pi = owners[gid - 1]
        vis[n].add(pi)
    return vis


def culled(name, keep):
    src = bpy.data.objects[name]
    bm = bmesh.new()
    bm.from_mesh(src.data)
    bm.faces.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index not in keep], context="FACES")
    bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
    me = bpy.data.meshes.new(name + "_culled")
    bm.to_mesh(me)
    bm.free()
    for m in src.data.materials:
        me.materials.append(m)
    ob = bpy.data.objects.new(name + "_culled", me)
    bpy.context.scene.collection.objects.link(ob)
    ob.matrix_world = src.matrix_world.copy()
    return ob


# ---------------------------------------------------------------- export helpers (as v1)

def evaluated_copy(ob, ratio):
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    me.transform(ob.matrix_world)
    if ratio < 1.0:
        bm = bmesh.new()
        bm.from_mesh(me)
        bmesh.ops.triangulate(bm, faces=bm.faces[:])
        bm.to_mesh(me)
        bm.free()
        tmp = bpy.data.objects.new("tmp", me)
        bpy.context.scene.collection.objects.link(tmp)
        mod = tmp.modifiers.new("dec", "DECIMATE")
        mod.ratio = ratio
        mod.use_collapse_triangulate = True
        dg = bpy.context.evaluated_depsgraph_get()
        me2 = bpy.data.meshes.new_from_object(tmp.evaluated_get(dg))
        bpy.data.objects.remove(tmp)
        me = me2
    return me


def patch_uvs(bm, uvl, centre, patch_name, scale):
    pu, pv = PATCH[patch_name]
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        a, b = [(1, 2), (0, 2), (0, 1)][ax]
        for loop in f.loops:
            co = loop.vert.co
            loop[uvl].uv = (pu + (co[a] - centre[a]) * scale, pv + (co[b] - centre[b]) * scale)


def zone_objects(sources, lod, ratio):
    """lod objects split by zone: {zone suffix: [objects]}, each tagged with its bone."""
    out = {}
    for name in PARTS:
        src = sources[name]
        me = evaluated_copy(src, ratio)
        mats = [m.name if m else None for m in src.data.materials]
        for mi, mname in enumerate(mats):
            if mname not in ZONES:
                raise SystemExit(f"{name}: material {mname} has no zone mapping")
            suffix, patch, _ = ZONES[mname]
            bm = bmesh.new()
            bm.from_mesh(me)
            bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.material_index != mi], context="FACES")
            if not bm.faces:
                bm.free()
                continue
            bmesh.ops.delete(bm, geom=[v for v in bm.verts if not v.link_faces], context="VERTS")
            uvl = bm.loops.layers.uv.new("UV0")
            bm.normal_update()
            patch_uvs(bm, uvl, CENTRE, patch, HALF_WINDOW / EXTENT)
            zm = bpy.data.meshes.new(f"{name}_{suffix}_{lod}")
            bm.to_mesh(zm)
            bm.free()
            zm.materials.clear()
            ob = bpy.data.objects.new(zm.name, zm)
            bpy.context.scene.collection.objects.link(ob)
            ob["ms11_bone"] = BONE_OF[name]
            out.setdefault(suffix, []).append(ob)
    return out


def collision():
    bm = bmesh.new()
    for name in PARTS:
        o = bpy.data.objects[name]
        for v in o.data.vertices:
            bm.verts.new(o.matrix_world @ v.co)
    hull = bmesh.ops.convex_hull(bm, input=bm.verts[:])
    bmesh.ops.delete(bm, geom=list(set(hull["geom_interior"]) | set(hull["geom_unused"])), context="VERTS")
    me = bpy.data.meshes.new("ltu_col")
    bm.to_mesh(me)
    bm.free()
    ob = bpy.data.objects.new("ltu_col", me)
    bpy.context.scene.collection.objects.link(ob)
    tris = sum(len(p.vertices) - 2 for p in me.polygons)
    mod = ob.modifiers.new("dec", "DECIMATE")
    mod.ratio = min(1.0, COL_TRIS / max(tris, 1))
    dg = bpy.context.evaluated_depsgraph_get()
    me2 = bpy.data.meshes.new_from_object(ob.evaluated_get(dg))
    ob.modifiers.clear()
    ob.data = me2
    bm = bmesh.new()
    bm.from_mesh(me2)
    uvl = bm.loops.layers.uv.new("UV0")
    bm.normal_update()
    patch_uvs(bm, uvl, CENTRE, "dark", HALF_WINDOW / EXTENT)
    bm.to_mesh(me2)
    bm.free()
    return ob, tris


def can_base(name):
    """Bone origin of a canister at the bind pose: centre of its footprint, bottom height."""
    o = bpy.data.objects[name]
    pts = [o.matrix_world @ v.co for v in o.data.vertices]
    lo = [min(p[i] for p in pts) for i in range(3)]
    hi = [max(p[i] for p in pts) for i in range(3)]
    return ((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2])


def tris_of(o):
    return sum(len(p.vertices) - 2 for p in o.data.polygons)



CENTRE = None
EXTENT = None


def assembly_frame():
    """(centre, half extent) of the four parts at the bind pose: the patch-UV frame of v1/v2."""
    global CENTRE, EXTENT
    set_pose(BIND_Z, BIND_Z)
    pts = [o.matrix_world @ v.co for o in (bpy.data.objects[n] for n in PARTS) for v in o.data.vertices]
    lo = Vector([min(p[i] for p in pts) for i in range(3)])
    hi = Vector([max(p[i] for p in pts) for i in range(3)])
    CENTRE = (lo + hi) / 2
    EXTENT = max((hi - lo)[i] for i in range(3)) / 2
    return CENTRE, EXTENT
