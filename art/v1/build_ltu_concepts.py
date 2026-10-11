# Linked Transfer Unit: exotic-look concept round (2026-10-10). PREVIEW ONLY (ProcKit PROCESS.md step 2b): build +
# gates + flat-zone renders. No UV / opt / bake / export to DU. Uses the VoxelMMO ProcKit kit read-only (imported,
# never edited); every output stays in this folder.
#
# Scale: the stock DU Transfer Unit mesh (resources_generated/.../env_industry-transfer-unit_001.mesh, header bounds)
# is 2.11 x 1.97 x 2.56 m, Z-up, origin on the mount face. Every concept here keeps a 1.9 m footprint (M class,
# 4x4 voxels) and a 1.7-2.4 m height, so it drops into any slot a stock TU fits.
# Zones follow the DU read of the stock TU: white armour (Main), gunmetal frame (Secondary), orange cap (Accent),
# and Glow = the faces that would carry the emission texture, which the inherited TransferUnit nqdef states recolour
# (idle red, running green pulse, jammed orange pulse).
#   A "Gate Spire"      : a hex energy conduit rising through two levitating 12-gon gate rings, held by four field posts.
#   B "Lattice Resonator": a long hex crystal seated in a socket, cradled by three tines tied by two lattice bands.
#   C "Gravitic Node"   : a glowing field bowl with a levitating core sphere and three orbiting collector pods.
# Levitating pieces are separate objects on purpose (in DU they would be separate ObjectMeshStatic nodes). The
# floating gate runs per object (each must be ONE connected cluster); levitators are then checked for clearance
# instead (no overlap, min gap >= 20 mm to the body).
# Headless:
#   blender -b --factory-startup --python build_ltu_concepts.py
#   -> art/LTUConcepts.blend, art/build_report.json, art/renders/*.png
import sys, os, json, time
import bpy
from math import radians, pi, sin, cos
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

KIT = "F:/VoxelMMO/Prototype/Art/ProcKit/kit"
sys.path.insert(0, KIT)
import hsk, hsk_check, hsk_preview as P
hsk.SEAT_INSETS = True

ART = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ART, "renders")
FOOT = 2.0          # M class footprint (m)
MIN_GAP = 0.020     # levitator clearance gate (m)
CREATED = []        # orphan audit: every object built


def T(o):
    CREATED.append(o); return o


def oprism(n, a, cham, z0, z1, zone, rot=None, name="op"):
    return T(hsk.prism_z(hsk.chpoly(n, a, cham, rot), z0, z1, zone, name))


def ofrustum(n, a0, a1, z0, z1, zone, k=0.12, rot=None, name="of"):
    return T(hsk.loft_z([(z0, hsk.chpoly(n, a0, a0 * k, rot)), (z1, hsk.chpoly(n, a1, a1 * k, rot))], zone, name))


def oring(n, a_out, a_in, z0, z1, zone, k=0.10, rot=None, name="or"):
    return T(hsk.tube_z(hsk.chpoly(n, a_out, a_out * k, rot), hsk.chpoly(n, a_in, a_in * k, rot), z0, z1, zone, name))


def lathe_z(profile, n, zones, z_off=0.0, cham_k=0.0, name="lz"):
    """Faceted lathe about world Z: profile = [(z, r)] closed, revolved as chpoly(n) rings."""
    o = hsk.revolve(profile, n=n, cham_k=cham_k, zones=zones, name=name)
    hsk.x_to_z(o, z_off); return T(o)


def place(o, M):
    o.data.transform(M); return o


def join(parts, name, bevel=0.0):
    o = hsk.join(parts, name)
    if bevel: hsk.chamfer(o, bevel, 40)
    return o


# ---------------------------------------------------------------- A: Gate Spire
def concept_A():
    p = []
    p.append(oprism(8, 0.95, 0.14, 0.0, 0.30, "Secondary", name="A_plinth"))
    p.append(ofrustum(8, 0.90, 0.74, 0.29, 0.52, "Main", name="A_armour"))
    p.append(oprism(8, 0.70, 0.08, 0.51, 0.60, "Glow", name="A_band"))            # recessed glow band
    p.append(ofrustum(8, 0.74, 0.58, 0.59, 0.72, "Main", name="A_collar"))
    p.append(oprism(6, 0.27, 0.04, 0.71, 0.96, "Secondary", name="A_sleeve"))
    p.append(oprism(6, 0.16, 0.025, 0.95, 1.98, "Glow", name="A_conduit"))       # the energy conduit
    p.append(oprism(6, 0.21, 0.03, 1.34, 1.42, "Secondary", name="A_clamp"))
    p.append(ofrustum(6, 0.25, 0.20, 1.97, 2.06, "Secondary", name="A_capbase"))
    p.append(ofrustum(6, 0.22, 0.09, 2.05, 2.24, "Accent", name="A_cap"))
    # four field posts on the plinth corners-of-flats (45 deg): chamfered blocks leaning in, glow face toward the ring
    for k in range(4):
        ang = pi / 4 + k * pi / 2
        R = Matrix.Rotation(ang, 4, "Z")
        lean = Matrix.Translation((0.84, 0, 0.30)) @ Matrix.Rotation(radians(-5), 4, "Y")
        post = T(hsk.prism_x(hsk.rect(-0.07, 0.07, 0.0, 0.80, 0.03), -0.09, 0.09, "Secondary", "A_post"))
        post.data.transform(lean)
        tip = T(hsk.prism_x(hsk.rect(-0.05, 0.05, 0.0, 0.10, 0.02), -0.095, -0.07, "Glow", "A_posttip"))
        tip.data.transform(lean @ Matrix.Translation((0, 0, 0.62)))
        for o in (post, tip): o.data.transform(R)
        p += [post, tip]
    body = join(p, "LTU_A_Body")
    # levitating gate rings, twisted 15 deg against each other, glow lining on the inside
    r1 = [oring(12, 0.60, 0.50, 1.06, 1.18, "Main", name="A_r1"), oring(12, 0.505, 0.47, 1.08, 1.16, "Glow", name="A_r1g")]
    ring1 = join(r1, "LTU_A_Ring1")
    r2 = [oring(12, 0.46, 0.37, 1.58, 1.68, "Main", rot=pi / 2 - pi / 12 + radians(15), name="A_r2"),
          oring(12, 0.375, 0.345, 1.60, 1.66, "Glow", rot=pi / 2 - pi / 12 + radians(15), name="A_r2g")]
    ring2 = join(r2, "LTU_A_Ring2")
    return {"body": body, "levitators": [ring1, ring2]}


# ---------------------------------------------------------------- B: Lattice Resonator
def concept_B():
    p = []
    p.append(oprism(4, 0.95, 0.26, 0.0, 0.24, "Secondary", name="B_base"))
    p.append(ofrustum(4, 0.90, 0.70, 0.23, 0.44, "Main", k=0.26, name="B_armour"))
    p.append(oprism(4, 0.66, 0.16, 0.43, 0.50, "Glow", name="B_band"))             # recessed under the collar
    p.append(ofrustum(4, 0.70, 0.56, 0.49, 0.56, "Main", k=0.24, name="B_collar"))
    p.append(ofrustum(6, 0.40, 0.31, 0.55, 0.62, "Secondary", name="B_socket"))
    p.append(oring(6, 0.31, 0.24, 0.61, 0.78, "Secondary", name="B_cup"))
    # the crystal: long hex bipyramid, tip buried in the socket floor
    p.append(lathe_z([(0.58, 0.0), (0.86, 0.22), (1.80, 0.22), (2.38, 0.0)], 6, ["Glow"] * 4, name="B_crystal"))
    # three tines: chamfered bars along a bent spine, base on the armour, tips curling in toward the crystal
    spine = [(0.66, 0.40), (0.62, 0.95), (0.52, 1.55), (0.36, 2.02)]          # (radius, z)
    sec = hsk.rect(-0.05, 0.05, -0.04, 0.04, 0.015)                            # (u = tangential, v = radial)

    def tine_r(z):
        for (r0, z0), (r1, z1) in zip(spine, spine[1:]):
            if z0 <= z <= z1: return r0 + (r1 - r0) * (z - z0) / (z1 - z0)
        return spine[-1][0]
    for k in range(3):
        ang = pi / 2 + k * 2 * pi / 3
        rad = Vector((cos(ang), sin(ang), 0)); tan_ = Vector((-sin(ang), cos(ang), 0))
        pts = [rad * r + Vector((0, 0, z)) for r, z in spine]
        st = []
        for i, q in enumerate(pts):
            t = (pts[min(i + 1, 3)] - pts[max(i - 1, 0)]).normalized()
            nrm = tan_.cross(t).normalized()
            st.append((q, tan_, nrm, 1.0, 1.0))
        tine = T(hsk.loft_spine(st, sec, ["Main"] * len(sec), name="B_tine"))
        tip = T(hsk.loft_z([(0, hsk.chpoly(6, 0.055, 0.008)), (0.10, hsk.chpoly(6, 0.03, 0.005))], "Accent", "B_tip"))
        tip.data.transform(Matrix.Translation(pts[-1] - Vector((0, 0, 0.02))))
        p += [tine, tip]
    # two lattice bands: hex rings whose corners run through the tine centres (vertices at 90/210/330 deg)
    for z, h in ((1.05, 0.07), (1.62, 0.06)):
        a = tine_r(z + h / 2) * cos(pi / 6)
        p.append(oring(6, a + 0.025, a - 0.025, z, z + h, "Secondary", k=0.06, rot=pi / 2, name="B_lattice"))
    body = join(p, "LTU_B_Body")
    return {"body": body, "levitators": []}


# ---------------------------------------------------------------- C: Gravitic Node
def concept_C():
    p = []
    p.append(oprism(8, 0.95, 0.14, 0.0, 0.22, "Secondary", name="C_plinth"))
    p.append(ofrustum(8, 0.90, 0.62, 0.21, 0.46, "Main", name="C_armour"))
    p.append(oprism(8, 0.50, 0.06, 0.45, 0.66, "Secondary", name="C_neck"))
    # field bowl (z, r): white outer shell, gunmetal lip and inner face, one glow ring channel inside (the emitter)
    bowl = lathe_z([(0.62, 0.0), (0.62, 0.40), (1.00, 0.84), (1.06, 0.84), (1.06, 0.76), (0.94, 0.62), (0.92, 0.56),
                    (0.86, 0.48), (0.84, 0.42), (0.74, 0.28), (0.70, 0.0)], 12,
                   ["Main", "Main", "Secondary", "Secondary", "Secondary", "Secondary", "Glow", "Secondary", "Secondary",
                    "Secondary", "Secondary"], cham_k=0.04, name="C_bowl")
    p.append(bowl)
    body = join(p, "LTU_C_Body")
    zc = 1.62
    core = lathe_z([(zc - 0.36, 0.0), (zc - 0.27, 0.22), (zc - 0.10, 0.34), (zc - 0.05, 0.31), (zc + 0.05, 0.31),
                    (zc + 0.10, 0.34), (zc + 0.27, 0.22), (zc + 0.36, 0.0)],
                   10, ["Main", "Main", "Secondary", "Glow", "Secondary", "Main", "Main", "Main"], cham_k=0.04, name="C_core")
    core = join([core], "LTU_C_Core")
    pods = []
    tilt = Matrix.Rotation(radians(16), 4, "X")
    for k in range(3):
        ang = k * 2 * pi / 3 + radians(20)
        pod = T(hsk.revolve([(-0.26, 0.0), (-0.20, 0.06), (-0.12, 0.11), (0.12, 0.11), (0.20, 0.06), (0.26, 0.0)], n=6,
                            cham_k=0.06, zones=["Accent", "Main", "Glow", "Main", "Accent", "Main"], name="C_pod"))
        o = join([pod], f"LTU_C_Pod{k + 1}")
        # pod axis tangent to the orbit, orbit radius 0.68 around the core, plane tilted 16 deg
        o.data.transform(Matrix.Translation((0, 0, zc)) @ tilt @ Matrix.Rotation(ang, 4, "Z") @ Matrix.Translation((0.68, 0, 0))
                         @ Matrix.Rotation(pi / 2, 4, "Z"))
        pods.append(o)
    return {"body": body, "levitators": [core] + pods}


# ---------------------------------------------------------------- gates
def _bvh(o):
    me = o.data; me.calc_loop_triangles()
    co = [o.matrix_world @ v.co for v in me.vertices]
    return BVHTree.FromPolygons(co, [t.vertices[:] for t in me.loop_triangles], all_triangles=True), co


def min_gap(a, b):
    ta, ca = _bvh(a); tb, cb = _bvh(b)
    if ta.overlap(tb): return 0.0
    g = 1e9
    for t, pts in ((tb, ca), (ta, cb)):
        for q in pts:
            h = t.find_nearest(q)
            if h[0] is not None: g = min(g, h[3])
    return g


def bbox(objs):
    pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]      # not bound_box: stale after data.transform
    lo = [min(q[i] for q in pts) for i in range(3)]; hi = [max(q[i] for q in pts) for i in range(3)]
    return lo, hi


def gates(name, c):
    objs = [c["body"]] + c["levitators"]
    rep = {"parts": {}, "ok": True}
    for o in objs:
        fl = hsk_check.floating_islands(o)
        cp = hsk_check.coplanar_overlaps(o, occluders=objs)
        r = {"tris": hsk.tris(o), "floating": fl["floating"], "islands": fl["islands"],
             "coplanar_cross_visible_cm2": round(cp.get("cross_visible_cm2", cp["cross_zone_cm2"]), 3),
             "coplanar_same_zone_cm2": round(cp["same_zone_cm2"], 3),
             "inverted_islands": hsk_check.inverted_islands(o) if hasattr(hsk_check, "inverted_islands") else None}
        if o in c["levitators"]:
            r["gap_to_others_mm"] = round(min(min_gap(o, x) for x in objs if x is not o) * 1000, 1)
            if r["gap_to_others_mm"] < MIN_GAP * 1000: rep["ok"] = False
        if r["floating"] or r["coplanar_cross_visible_cm2"] > 0 or (r["inverted_islands"] and r["inverted_islands"][0]): rep["ok"] = False
        rep["parts"][o.name] = r
    lo, hi = bbox(objs)
    rep["bbox_m"] = [round(hi[i] - lo[i], 3) for i in range(3)]
    rep["min_z"] = round(lo[2], 4)
    rep["tris_total"] = sum(v["tris"] for v in rep["parts"].values())
    if rep["bbox_m"][0] > FOOT + 1e-6 or rep["bbox_m"][1] > FOOT + 1e-6 or abs(lo[2]) > 1e-4: rep["ok"] = False
    return rep


def main():
    t0 = time.time()
    hsk.begin("LTU_Concepts")
    concepts = {"A": concept_A(), "B": concept_B(), "C": concept_C()}
    final = {o for c in concepts.values() for o in [c["body"]] + c["levitators"]}
    # orphan audit: every mesh object in the build collection must be a final part (joins remove the pieces)
    live = [o for o in hsk.COLL.objects if o.type == "MESH"]
    orphans = [o.name for o in live if o not in final]
    report = {"date": "2026-10-10", "stock_tu_bbox_m": [2.111, 1.970, 2.557], "orphans": orphans, "concepts": {}}
    ok = not orphans
    for k, c in concepts.items():
        r = gates(k, c); report["concepts"][k] = r; ok &= r["ok"]
    report["ok"] = ok
    os.makedirs(OUT, exist_ok=True)
    groups = {k: [c["body"]] + c["levitators"] for k, c in concepts.items()}
    files = P.preview_only(groups, OUT, "ltu", shots=("lineup",), res=1600)["files"]
    views = {"front34": (1.0, -1.25, 0.75), "side": (1.4, 0.35, 0.30), "high": (-0.6, -0.9, 1.6)}
    for k, objs in groups.items():
        for vn, v in views.items():
            files += P.preview_only({k: objs}, OUT, f"ltu_{vn}", shots=("34",), res=900, view=v)["files"]
    report["renders"] = files
    report["seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(ART, "build_report.json"), "w") as f: json.dump(report, f, indent=1)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ART, "LTUConcepts.blend"))
    print("LTU REPORT", json.dumps({k: (v["ok"], v["tris_total"], v["bbox_m"]) for k, v in report["concepts"].items()}),
          "orphans", orphans, "OK" if ok else "GATE FAIL")


main()
