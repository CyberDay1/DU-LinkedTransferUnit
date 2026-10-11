"""Checks the v2 skinned export against the v3b scene, by rendering.

    blender -b LTUConceptV3b.blend --python verify_ltu_v2.py

For each test pose, the REFERENCE is the untouched v3b parts posed in Blender, and the
CANDIDATE is ltu_mesh_v2.mesh (LOD0) deformed by ltu_mesh_v2_pump.anim at a clip frame, with
the skinning done here from the files alone (bone matrix(frame) * inverse(bind) * vertex). Both
are drawn with Workbench, flat zone colours, no anti-aliasing, from 18 views; differing pixels
are counted. A v1 baseline (the static v1 export vs the reference at mid-stroke) gives the
exporter's own noise floor, so culling + skinning must not add to it.

Writes art/verify_report_v2.json. LTU_VIEW=az,el renders one view only (debugging).
"""
import json
import math
import os
import struct
import sys

import bpy
from mathutils import Matrix, Quaternion, Vector

HERE = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "unigine_mesh"))
import an10  # noqa: E402
import ms11  # noqa: E402

CLIENT = os.path.join(HERE, "..", "assets", "client")
RENDERS = os.path.join(HERE, "renders")
PARTS = ["LTU_Body", "LTU_Frame", "LTU_CanA", "LTU_CanB"]
ZONE_MAT = {"white": "DU_WhiteEnamel", "grey": "DU_GreyEnamel", "chassis": "DU_BlackChassis",
            "gunmetal": "DU_Gunmetal", "steel": "DU_Steel", "orange": "DU_Orange",
            "redtag": "DU_RedTag", "status": "DU_BlueStatus"}
RES = 384
STROKE = 0.26

sc = bpy.context.scene
for o in list(bpy.data.objects):
    if o.type == "MESH" and o.name not in PARTS:
        bpy.data.objects.remove(o)
    elif o.type == "LIGHT":
        bpy.data.objects.remove(o)
sc.render.engine = "BLENDER_WORKBENCH"
sc.display.shading.light = "FLAT"
sc.display.shading.color_type = "MATERIAL"
sc.display.render_aa = "OFF"
# The client culls back faces (mesh_base materials are one-sided), so the reference must too:
# without this, the backs of the panel bevels showed through the panel gaps in the reference only.
sc.display.shading.show_backface_culling = True
sc.render.resolution_x = sc.render.resolution_y = RES
sc.render.film_transparent = True
sc.view_settings.view_transform = "Standard"
sc.render.image_settings.file_format = "PNG"
sc.render.image_settings.color_mode = "RGBA"
# Distinct flat colours per zone so a missing face shows as a colour change.
PALETTE = {"DU_WhiteEnamel": (0.9, 0.9, 0.9), "DU_GreyEnamel": (0.5, 0.5, 0.55), "DU_BlackChassis": (0.1, 0.1, 0.1),
           "DU_Gunmetal": (0.25, 0.3, 0.4), "DU_Steel": (0.6, 0.7, 0.6), "DU_Orange": (0.95, 0.45, 0.05),
           "DU_RedTag": (0.8, 0.05, 0.3), "DU_BlueStatus": (0.1, 0.5, 1.0)}
for n, c in PALETTE.items():
    bpy.data.materials[n].diffuse_color = (*c, 1)

cam_data = bpy.data.cameras.new("vcam")
cam_data.type = "ORTHO"
cam_data.ortho_scale = 3.2
cam = bpy.data.objects.new("vcam", cam_data)
sc.collection.objects.link(cam)
sc.camera = cam
VIEWS = [(az, el) for el in (15, 50) for az in range(0, 360, 45)] + [(0, 89), (30, -20)]
if os.environ.get("LTU_VIEW"):
    VIEWS = [tuple(int(v) for v in os.environ["LTU_VIEW"].split(","))]


def aim(az, el):
    a, e = math.radians(az), math.radians(el)
    target = Vector((0, 0, 1.28))
    cam.location = target + 6 * Vector((math.cos(e) * math.cos(a), math.cos(e) * math.sin(a), math.sin(e)))
    cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()


def render(tag):
    path = os.path.join(os.environ.get("TEMP", HERE), f"ltu_verify_{tag}.png")
    sc.render.filepath = path
    bpy.ops.render.render(write_still=True)
    img = bpy.data.images.load(path, check_existing=False)
    px = list(img.pixels)
    bpy.data.images.remove(img)
    return path, px


def show(objs, on):
    for o in objs:
        o.hide_render = not on


def import_skinned(path, anim=None, frame=None, lod="lod0"):
    """ms11 surfaces of one LOD as Blender objects, skinned to a clip frame from the files."""
    m = ms11.read_file(path)
    mats = {}
    if m.bones:
        a = an10.read_file(anim)
        clip = a.clips[0]
        pose = an10.decode(a, clip)[frame]
        for i, b in enumerate(clip.bones):
            bx = m.bone_xforms[b]
            bind = Matrix.Translation(bx[0:3]) @ Quaternion((bx[6], *bx[3:6])).to_matrix().to_4x4()
            t, q, _ = pose[i]
            cur = Matrix.Translation(t) @ Quaternion((q[3], *q[0:3])).normalized().to_matrix().to_4x4()
            mats[b] = cur @ bind.inverted()
    objs = []
    for s in m.surfaces:
        if not s.name.decode().endswith(lod):
            continue
        P = s.targets[0].positions
        verts = []
        for i in range(len(P) // 3):
            v = Vector(P[3 * i:3 * i + 3])
            if mats:
                v = mats[s.weights[i][0][0]] @ v
            verts.append(v)
        faces = [tuple(s.cindices[i:i + 3]) for i in range(0, len(s.cindices), 3)]
        me = bpy.data.meshes.new(s.name.decode())
        me.from_pydata(verts, [], faces)
        zone = s.name.decode().split("_")[1]
        me.materials.append(bpy.data.materials[ZONE_MAT[zone]])
        o = bpy.data.objects.new(me.name, me)
        sc.collection.objects.link(o)
        objs.append(o)
    return objs


def diff(pa, pb):
    n = 0
    for i in range(0, len(pa), 4):
        if (abs(pa[i] - pb[i]) + abs(pa[i + 1] - pb[i + 1]) + abs(pa[i + 2] - pb[i + 2]) + abs(pa[i + 3] - pb[i + 3])) > 0.08:
            n += 1
    return n


def coverage(p):
    return sum(1 for i in range(3, len(p), 4) if p[i] > 0.5)


def compare(tag, za, zb, cand):
    ref = [bpy.data.objects[n] for n in PARTS]
    bpy.data.objects["LTU_CanA"].location.z = za
    bpy.data.objects["LTU_CanB"].location.z = zb
    bpy.context.view_layer.update()
    rows = []
    for az, el in VIEWS:
        aim(az, el)
        show(cand, False); show(ref, True)
        _, pr = render(f"{tag}_ref")
        show(ref, False); show(cand, True)
        _, pc = render(f"{tag}_cand")
        d = diff(pr, pc)
        rows.append({"az": az, "el": el, "diff_px": d, "cover_px": coverage(pr),
                     "diff_pct": round(100 * d / max(1, coverage(pr)), 3)})
    show(cand, False)
    show(ref, True)
    return rows


report = {}
# baseline: v1 static (mid-stroke) vs reference at mid-stroke
v1 = os.path.join(CLIENT, "ltu_mesh_v1.mesh")
if os.path.exists(v1):
    objs = import_skinned(v1)
    report["v1_mid_baseline"] = compare("v1mid", STROKE / 2, STROKE / 2, objs)
    for o in objs:
        bpy.data.objects.remove(o)

mesh = os.path.join(CLIENT, "ltu_mesh_v2.mesh")
anim = os.path.join(CLIENT, "ltu_mesh_v2_pump.anim")
a = an10.read_file(anim)
frames = len(a.clips[0].frames)
dec = an10.decode(a, a.clips[0])
bind = ms11.read_file(mesh).bone_xforms
for f in (0, frames // 8, frames // 4, 3 * frames // 8, frames // 2, 3 * frames // 4):
    za = STROKE / 2 + dec[f][1][0][2] - bind[1][2]
    zb = STROKE / 2 + dec[f][2][0][2] - bind[2][2]
    objs = import_skinned(mesh, anim, f)
    report[f"v2_frame{f:02d}_A{za:.3f}_B{zb:.3f}"] = compare(f"v2f{f}", za, zb, objs)
    for o in objs:
        bpy.data.objects.remove(o)

summary = {k: {"max_diff_pct": max(r["diff_pct"] for r in v), "sum_diff_px": sum(r["diff_px"] for r in v),
               "sum_cover_px": sum(r["cover_px"] for r in v)} for k, v in report.items()}
json.dump({"summary": summary, "views": report}, open(os.path.join(HERE, "verify_report_v2.json"), "w"), indent=1)
print(json.dumps(summary, indent=1))
