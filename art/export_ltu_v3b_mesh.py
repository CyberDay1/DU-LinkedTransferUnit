"""Exports the approved v3b LTU as a SKINNED myDU element mesh plus its pump clip (v2).

    blender -b LTUConceptV3b.blend --python export_ltu_v3b_mesh.py

Writes (then run ../assets/build_client_assets.py to make the node, mat and nqdef):
  ../assets/client/ltu_mesh_v2.mesh       LOD0 + LOD1, one surface per zone, skinned: bones
                                          ltu_root (body, frame), ltu_can_a, ltu_can_b
  ../assets/client/ltu_mesh_v2_pump.anim  an10 clip: the canisters stroke in opposite phase
  ../assets/client/ltu_mesh_v2_col.mesh   convex hull, decimated, like the stock _col.mesh
  ../assets/ltu_mesh_v2.surfaces.json     surfaces, materials, LOD distances, report

Skinning follows the stock refiner (env_industry-refiner_001.mesh, bones cylindre02/03):
every bone a root (parent -1), bind pose in mesh space, one influence of 65535 per position
vertex. The bind pose is mid-stroke for both canisters, so a unit that is not animating looks
like the v1 static export.

Weight (v2): faces that never cover a pixel in an ID render from 400+ directions at five
stroke poses are dropped before export (panel backs inside the core, rail feet inside
collars, ...). Nothing visible changes:
verify_ltu_v2.py renders before/after and diffs the pixels.

Texturing is still the interim patch sampling of the stock TU texture set (see v1 notes in
du-ltu-asset.md); a real bake is a later step.
"""
import json
import os
import sys

import bpy

HERE = os.path.dirname(os.path.abspath(bpy.data.filepath or __file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "unigine_mesh"))
import an10  # noqa: E402
import blender_io  # noqa: E402
import ltu_v3b_lib as lib  # noqa: E402
import ms11  # noqa: E402
from ltu_v3b_lib import (BIND_Z, BONES, CLIP, FRAMES, HOLD, LOD1_RATIO, LOD_SWITCH_M, OUT, PARTS, RISE,  # noqa: E402
                         SWEEP, TU_MAT, VER, VIEW_DIRS, VIEW_RES, ZONES, META, can_base, collision, culled,
                         heights, tris_of, visible_faces, zone_objects)

# ---------------------------------------------------------------- run

CENTRE, EXTENT = lib.assembly_frame()   # same patch mapping as v1 (bind pose = v1's static pose)

vis = visible_faces()
sources = {n: culled(n, vis[n]) for n in PARTS}
cull_report = {n: {"tris_before": tris_of(bpy.data.objects[n]), "tris_after": tris_of(sources[n])} for n in PARTS}

bind = {0: (0.0, 0.0, 0.0), 1: can_base("LTU_CanA"), 2: can_base("LTU_CanB")}
bones = [(BONES[i], -1, (*bind[i], 0.0, 0.0, 0.0, 1.0, 1.0, 1.0, 1.0)) for i in range(3)]

os.makedirs(OUT, exist_ok=True)
lod0 = zone_objects(sources, "lod0", 1.0)
lod1 = zone_objects(sources, "lod1", LOD1_RATIO)
spec, surfaces, materials = [], [], {}
for lod, zones in (("lod0", lod0), ("lod1", lod1)):
    for suffix in sorted(zones):
        sname = f"ltu_{suffix}_{lod}"
        spec.append((sname, zones[suffix]))
        surfaces.append({"name": sname, "material": f"ltu_mesh_{suffix}",
                         "max": LOD_SWITCH_M if lod == "lod0" else None,
                         "min": LOD_SWITCH_M if lod == "lod1" else None})
for mname, (suffix, patch, tint) in ZONES.items():
    materials[f"ltu_mesh_{suffix}"] = {"name": f"ltu_mesh_{suffix}", "parent": TU_MAT, "params": {"albedo_color": tint}}

MESH = f"ltu_mesh_{VER}.mesh"
COLF = f"ltu_mesh_{VER}_col.mesh"
ANIM = f"ltu_mesh_{VER}_pump.anim"
mesh = blender_io.export_mesh(os.path.join(OUT, MESH), spec, bones=bones)
col, hull_tris = collision()
colmesh = blender_io.export_mesh(os.path.join(OUT, COLF), [(f"ltu_mesh_{VER}_col", [col])])

# ---- the clip: absolute bone transforms per frame (all bones are roots, as in the refiner)
poses = []
for f in range(FRAMES):
    za, zb = heights(f)
    row = [(bind[0], (0.0, 0.0, 0.0, 1.0))]
    for b, z in ((1, za), (2, zb)):
        x, y, zb0 = bind[b]
        row.append(((x, y, zb0 + (z - BIND_Z)), (0.0, 0.0, 0.0, 1.0)))
    poses.append(row)
clip = an10.encode_clip(CLIP.encode(), [0, 1, 2], poses)
anim = an10.Anim(tuple(mesh.bounds), list(mesh.bones), list(mesh.bone_xforms), [clip])
an10.write_file(os.path.join(OUT, ANIM), anim)

# re-read every file: it must parse and round-trip byte-identical
for fn, mod in ((MESH, ms11), (COLF, ms11), (ANIM, an10)):
    raw = open(os.path.join(OUT, fn), "rb").read()
    assert mod.write(mod.read(raw)) == raw, fn

# top of each canister over the clip vs the assembly top (bounds must still hold)
can_top = {n: max((bpy.data.objects[n].matrix_world @ v.co).z for v in bpy.data.objects[n].data.vertices)
           for n in ("LTU_CanA", "LTU_CanB")}
dec = an10.decode(anim, clip)
za_all = [fr[1][0][2] - bind[1][2] for fr in dec]
zb_all = [fr[2][0][2] - bind[2][2] for fr in dec]

report = {
    "pose": "bind = canisters at mid-stroke (0.13 m); clip strokes each 0..0.26 m",
    "bounds": [round(x, 4) for x in mesh.bounds],
    "bones": [{"name": n, "bind_pos": [round(v, 4) for v in bind[i]]} for i, n in enumerate(BONES)],
    "surfaces": [{"name": s.name.decode(), "tris": len(s.cindices) // 3,
                  "verts": len(s.targets[0].positions) // 3, "attr_verts": len(s.targets[0].tangents) // 4,
                  "bones": sorted({w[0][0] for w in s.weights})}
                 for s in mesh.surfaces],
    "lod0_tris": sum(len(s.cindices) // 3 for s in mesh.surfaces if s.name.endswith(b"lod0")),
    "lod1_tris": sum(len(s.cindices) // 3 for s in mesh.surfaces if s.name.endswith(b"lod1")),
    "culling": {"method": "ID render", "views": len(VIEW_DIRS), "res": VIEW_RES, "sweep_poses": len(SWEEP), "parts": cull_report},
    "col": {"hull_tris": hull_tris, "tris": len(colmesh.surfaces[0].cindices) // 3,
            "bounds": [round(x, 4) for x in colmesh.bounds]},
    "clip": {"name": CLIP, "frames": FRAMES, "seconds_at_30fps": round(FRAMES / 30, 3),
             "rise_fraction": RISE, "hold_fraction": HOLD,
             "can_a_dz_range": [round(min(za_all), 4), round(max(za_all), 4)],
             "can_b_dz_range": [round(min(zb_all), 4), round(max(zb_all), 4)],
             "can_top_at_bind": {k: round(v, 4) for k, v in can_top.items()},
             "tmin": [round(v, 4) for v in clip.tmin], "trange": [round(v, 4) for v in clip.trange]},
    "bytes": {fn: os.path.getsize(os.path.join(OUT, fn)) for fn in (MESH, COLF, ANIM)},
    "zero_area_tris_dropped": {k: v for k, v in blender_io.DROPPED.items() if v},
}
json.dump({"materials": list(materials.values()), "surfaces": surfaces, "col_surface": f"ltu_mesh_{VER}_col",
           "skinned": True, "anim": {"file": ANIM, "name": "running", "loop": True},
           "extra_libraries": [], "report": report}, open(META, "w"), indent=2)
print(json.dumps(report, indent=1))
