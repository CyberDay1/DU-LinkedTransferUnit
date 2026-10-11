# Linked Transfer Unit: v3, one concept built on the stock Transfer Unit's own language (2026-10-10).
# User: v1 too sci-fi, v2 "still trying to make one in the voxelmmo style"; "give it some photos of the DU transfer unit
# to use as a base". So this is modelled from scratch with plain bpy/bmesh (NO ProcKit kit helpers or zone
# conventions; hsk_check is imported only as a validation gate) against art/reference/ (stock icons x4, the stock TU's
# loose _c/_e textures).
# Stock TU read (icon + textures): glossy white/light-grey enamel panels with rounded vertical edges, slightly separated,
# thin dark vertical seam slots with a light bar and a small red tag; a matte black plinth; on top a short metal
# canister with an orange shallow-domed bolted lid and one diagonal strap; emission only in a few small dots.
#   STOCK : an approximate rebuild of the stock TU (comparison only, never shipped).
#   LTU   : same family (panelled white body, orange-capped canisters, black base) as a premium "linked" variant: a
#           black deck with an orange lip, TWIN slimmer canisters strapped to a central black frame (slim posts,
#           diagonal braces, orange strips, blue status dots), and a small relay dish on a short mast above the frame.
# v3b (user on v3: "I sort of like it"): nothing interpenetrates (new part-vs-part intersection gate), each canister
# rides in a WELL (sleeve sunk into the deck and body, black guide collar with an orange band, three guide rails), and
# the canisters stroke up/down in OPPOSITE phase (alternating pump). The stroke is swept: 9 poses, each canister checked
# against the body, the frame/antenna and the other canister.
# Headless:  blender -b --factory-startup --python build_ltu_v3b.py
#   -> art/LTUConceptV3b.blend, art/build_report_v3b.json, art/renders/v3b_*.png
import sys, os, json, time
import bpy, bmesh
from math import radians, pi, sin, cos, atan2, sqrt
from mathutils import Vector, Matrix

sys.path.insert(0, "F:/VoxelMMO/Prototype/Art/ProcKit/kit")
import hsk_check          # gates only

ART = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ART, "renders")
LIM = (2.11, 1.97, 2.56)
COLL = None
PARTS = {}


# ---------------------------------------------------------------- materials (glossy PBR, DU-icon look)
def material(name, rgb, metal, rough, coat=0.0, emit=0.0):
    m = bpy.data.materials.new(name); m.use_nodes = True
    b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    b.inputs["Base Color"].default_value = (*rgb, 1); b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    if coat:
        b.inputs["Coat Weight"].default_value = coat; b.inputs["Coat Roughness"].default_value = 0.08
    if emit:
        b.inputs["Emission Color"].default_value = (*rgb, 1); b.inputs["Emission Strength"].default_value = emit
    m.diffuse_color = (*rgb, 1)
    return m


def make_mats():
    return {
        "white":  material("DU_WhiteEnamel", (0.78, 0.78, 0.77), 0.0, 0.24, coat=0.35),
        "grey":   material("DU_GreyEnamel", (0.42, 0.43, 0.44), 0.0, 0.28, coat=0.30),
        "steel":  material("DU_Steel", (0.36, 0.37, 0.38), 1.0, 0.34),
        "black":  material("DU_BlackChassis", (0.018, 0.018, 0.02), 0.25, 0.42),
        "gun":    material("DU_Gunmetal", (0.06, 0.062, 0.066), 0.9, 0.33),
        "orange": material("DU_Orange", (0.72, 0.10, 0.025), 0.0, 0.30, coat=0.4),
        "red":    material("DU_RedTag", (0.65, 0.04, 0.03), 0.0, 0.35),
        "blue":   material("DU_BlueStatus", (0.10, 0.55, 1.0), 0.0, 0.3, emit=12.0),
    }


# ---------------------------------------------------------------- geometry (own, no kit)
def _obj(bm, name, mat):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    o = bpy.data.objects.new(name, me); COLL.objects.link(o)
    return o


def bevel(o, w, seg=3, ang=35, only_vertical=False):
    if w <= 0: return o
    md = o.modifiers.new("bv", "BEVEL"); md.width = w; md.segments = seg; md.limit_method = "ANGLE"
    md.angle_limit = radians(ang); md.use_clamp_overlap = True; md.profile = 0.6
    return apply(o)


def apply(o):
    dg = bpy.context.evaluated_depsgraph_get()
    me = bpy.data.meshes.new_from_object(o.evaluated_get(dg), preserve_all_data_layers=True, depsgraph=dg)
    old = o.data; o.modifiers.clear(); o.data = me; bpy.data.meshes.remove(old)
    return o


def box(name, x0, x1, y0, y1, z0, z1, mat, bv=0.0, seg=3, M=None):
    bm = bmesh.new(); bmesh.ops.create_cube(bm, size=1.0)
    bmesh.ops.scale(bm, vec=(x1 - x0, y1 - y0, z1 - z0), verts=bm.verts)
    bmesh.ops.translate(bm, vec=((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2), verts=bm.verts)
    if M is not None: bm.transform(M)
    return bevel(_obj(bm, name, mat), bv, seg)


def lathe(name, prof, mat, seg=48, M=None):
    """Closed (r, z) profile revolved about Z; r == 0 points collapse to the axis."""
    bm = bmesh.new(); rings = []
    for r, z in prof:
        if r < 1e-7: rings.append([bm.verts.new((0, 0, z))])
        else: rings.append([bm.verts.new((r * cos(2 * pi * k / seg), r * sin(2 * pi * k / seg), z)) for k in range(seg)])
    n = len(prof)
    for i in range(n):
        a, b = rings[i], rings[(i + 1) % n]
        if len(a) == 1 and len(b) == 1: continue
        if len(a) == 1 or len(b) == 1:
            c, R = (a[0], b) if len(a) == 1 else (b[0], a)
            for j in range(seg):
                try: bm.faces.new([c, R[j], R[(j + 1) % seg]])
                except ValueError: pass
        else:
            for j in range(seg):
                try: bm.faces.new([a[j], a[(j + 1) % seg], b[(j + 1) % seg], b[j]])
                except ValueError: pass
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-7)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    if M is not None: bm.transform(M)
    return _obj(bm, name, mat)


def cyl(name, r, z0, z1, mat, seg=48, M=None, r2=None):
    return lathe(name, [(0, z0), (r, z0), (r if r2 is None else r2, z1), (0, z1)], mat, seg, M)


def shade(o, ang=38):
    me = o.data
    me.polygons.foreach_set("use_smooth", [True] * len(me.polygons))
    me.set_sharp_from_angle(angle=radians(ang)); me.update()
    return o


def join(objs, name):
    with bpy.context.temp_override(active_object=objs[0], selected_editable_objects=objs, selected_objects=objs):
        bpy.ops.object.join()
    o = objs[0]; o.name = name; o.data.name = name
    return shade(o)


# ---------------------------------------------------------------- the panelled body (stock language)
def panel_face(p, M, w, h, z0, mats, t=0.05, gap=0.012, cols=2, corner=0.16, tags=True, dots=False):
    """One body face in its own frame M (local X along the face, local Y = outward normal, local Z up, origin = face
    centre at the face plane, z0 = panel bottom). Corner strips + `cols` big panels with rounded vertical edges, dark
    seam slots between them carrying a light bar and a red tag (and optional blue status dots)."""
    xs = [-w / 2, -w / 2 + corner] + [-w / 2 + corner + (w - 2 * corner) * (k + 1) / cols for k in range(cols - 1)] + [w / 2 - corner, w / 2]
    slot = 0.07
    for i, (a, b) in enumerate(zip(xs, xs[1:])):
        big = 1 <= i <= cols
        a2 = a + (slot / 2 if big and i > 1 else gap / 2); b2 = b - (slot / 2 if big and i < cols else gap / 2)
        mat = mats["white"] if big else mats["grey"]
        p.append(box("pnl", a2, b2, -0.006, t, z0, z0 + h, mat, bv=0.028 if big else 0.018, seg=4, M=M))
    for k in range(cols - 1):                    # seam slots between the big panels
        x = xs[2 + k]
        p.append(box("slot", x - slot / 2 + 0.004, x + slot / 2 - 0.004, -0.006, t * 0.45, z0 + 0.04, z0 + h - 0.04, mats["black"], M=M))
        p.append(box("bar", x - 0.012, x + 0.012, 0.0, t * 0.75, z0 + h * 0.18, z0 + h * 0.70, mats["grey"], bv=0.006, seg=2, M=M))
        if tags: p.append(box("tag", x - 0.016, x + 0.016, 0.0, t * 0.62, z0 + h - 0.16, z0 + h - 0.10, mats["red"], bv=0.004, seg=2, M=M))
        if dots: p.append(box("dot", x - 0.014, x + 0.014, 0.0, t * 0.66, z0 + h - 0.26, z0 + h - 0.22, mats["blue"], bv=0.004, seg=2, M=M))


def body(p, mats, X, Y, z0, z1, dots=False):
    """Core + plinth + four panelled faces. X, Y = half extents of the panel faces."""
    p.append(box("plinth", -X + 0.06, X - 0.06, -Y + 0.06, Y - 0.06, 0.0, z0 + 0.01, mats["black"], bv=0.02))
    p.append(box("core", -X + 0.04, X - 0.04, -Y + 0.04, Y - 0.04, z0 - 0.005, z1, mats["gun"], bv=0.02))
    h = z1 - z0
    for ang, w, d in ((0, 2 * X, Y), (pi / 2, 2 * Y, X), (pi, 2 * X, Y), (-pi / 2, 2 * Y, X)):
        # frame: local Y outward. Rotation about Z maps local -Y face (front) around.
        M = Matrix.Rotation(ang, 4, "Z") @ Matrix.Translation((0, -(d - 0.055), 0)) @ Matrix.Rotation(pi, 4, "Z")
        panel_face(p, M, w, h - 0.01, z0, mats, dots=dots)
    p.append(box("lid", -X + 0.05, X - 0.05, -Y + 0.05, Y - 0.05, z1 - 0.02, z1 + 0.03, mats["grey"], bv=0.02))


def canister(p, mats, cx, cy, r, z0, z1, strap=True, port_ang=-pi / 2, lid_h=0.13, flush=False):
    M = Matrix.Translation((cx, cy, 0))
    p.append(cyl("can_base", r + 0.03, z0 - 0.01, z0 + 0.07, mats["black"], M=M))
    p.append(cyl("can", r, z0 + 0.06, z1, mats["steel"], M=M))
    p.append(cyl("can_band", r + 0.012, z1 - 0.10, z1 - 0.05, mats["gun"], M=M))
    # orange shallow-domed lid with a lip
    p.append(lathe("can_lid", [(0, z1 - 0.01), (r + 0.045, z1 - 0.01), (r + 0.045, z1 + 0.035), (r * 0.80, z1 + lid_h * 0.75),
                                (r * 0.40, z1 + lid_h * 0.97), (0, z1 + lid_h)], mats["orange"], M=M))
    for k in range(10):                            # lid bolts on the lip
        a = 2 * pi * k / 10
        p.append(cyl("bolt", 0.014, z1 + 0.025, z1 + 0.048, mats["gun"], seg=10,
                     M=Matrix.Translation((cx + (r + 0.02) * cos(a), cy + (r + 0.02) * sin(a), 0))))
    if strap:                                      # one diagonal strap across the camera side, wrapped on the drum
        a0, a1 = port_ang - 0.55, port_ang + 0.55; n = 14
        bm = bmesh.new(); rows = []
        for i in range(n + 1):
            t = i / n; a = a0 + (a1 - a0) * t; z = z0 + 0.12 + (z1 - z0 - 0.26) * t
            ring = []
            for dr, dz in ((-0.004, -0.022), (0.016, -0.022), (0.016, 0.022), (-0.004, 0.022)):
                ring.append(bm.verts.new((cx + (r + dr) * cos(a), cy + (r + dr) * sin(a), z + dz)))
            rows.append(ring)
        for ra, rb in zip(rows, rows[1:]):
            for j in range(4): bm.faces.new([ra[j], ra[(j + 1) % 4], rb[(j + 1) % 4], rb[j]])
        bm.faces.new(rows[0][::-1]); bm.faces.new(rows[-1])
        bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
        p.append(_obj(bm, "strap", mats["gun"]))
    # round inspection port
    pa = port_ang + 0.95
    R = Matrix.Translation((cx + r * cos(pa), cy + r * sin(pa), z0 + 0.22)) @ Matrix.Rotation(pa - pi / 2, 4, "Z") @ Matrix.Rotation(-pi / 2, 4, "X")
    pt = 0.010 if flush else 0.022                 # flush port when the drum has to pass through a well bore
    p.append(cyl("port", 0.055, -0.01, pt, mats["gun"], seg=24, M=R))
    p.append(cyl("port_glass", 0.036, 0.0, pt + 0.004 if flush else 0.028, mats["grey"], seg=24, M=R))


# ---------------------------------------------------------------- the two units
def build_stock():
    p = []
    X, Y = 1.055, 0.985
    body(p, MATS, X, Y, 0.06, 1.52)
    canister(p, MATS, 0.0, 0.06, 0.80, 1.55, 2.40, lid_h=0.155)
    return join(p, "STOCK_TU")


# LTU layout (m). Canister: base-ring bottom b, drum 0.85, lid 0.11 -> 0.97 tall. Low pose b = B_LOW, stroke STROKE.
R_CAN, CX, B_LOW, STROKE = 0.35, 0.56, 1.08, 0.26
BORE, SLEEVE, COLLAR = 0.39, 0.43, 0.45           # well inner / sleeve outer / collar outer radius
FLOOR = 1.07                                       # well floor top (1 cm under the canister at the low pose)


def cut(o, r, cx, z0, z1):
    c = cyl("cutter", r, z0, z1, MATS["gun"], seg=48, M=Matrix.Translation((cx, 0, 0)))
    md = o.modifiers.new("b", "BOOLEAN"); md.operation = "DIFFERENCE"; md.solver = "EXACT"; md.object = c
    apply(o); bpy.data.objects.remove(c)


def build_ltu():
    X, Y = 1.055, 0.985
    # ---- static body: panelled box, deck, two wells
    p = []
    body(p, MATS, X, Y, 0.06, 1.34, dots=True); core, lid = p[1], p[-1]
    deck = box("deck", -0.98, 0.98, -0.80, 0.80, 1.36, 1.42, MATS["black"], bv=0.02); p.append(deck)
    p.append(box("deck_lip", -0.86, 0.86, -0.815, -0.79, 1.375, 1.405, MATS["orange"], bv=0.006, seg=2))
    for s_ in (-1, 1):
        cx = s_ * CX
        # the well IS the bore cut through core / lid / deck (stepped radii: no coplanar hole walls)
        for o, r in ((core, BORE), (lid, BORE + 0.005), (deck, BORE + 0.010)):
            cut(o, r, cx, FLOOR - 0.06, 1.50)
        M = Matrix.Translation((cx, 0, 0))
        p.append(cyl("well_floor", BORE - 0.003, FLOOR - 0.065, FLOOR, MATS["black"], M=M))
        p.append(lathe("collar", [(BORE, 1.41), (COLLAR, 1.41), (COLLAR, 1.50), (COLLAR - 0.015, 1.52), (BORE, 1.52)], MATS["black"], M=M))
        p.append(lathe("collar_band", [(COLLAR - 0.01, 1.455), (COLLAR + 0.006, 1.455), (COLLAR + 0.006, 1.475), (COLLAR - 0.01, 1.475)], MATS["orange"], M=M))
        for k in range(3):                           # guide rails: the canister visibly rides between them
            a = pi / 2 + k * 2 * pi / 3
            Rr = Matrix.Translation((cx + (BORE + 0.022) * cos(a), (BORE + 0.022) * sin(a), 0)) @ Matrix.Rotation(a, 4, "Z")
            p.append(box("rail", -0.016, 0.016, -0.016, 0.016, 1.50, 1.88, MATS["gun"], bv=0.005, M=Rr))
            p.append(box("rail_cap", -0.022, 0.022, -0.02, 0.02, 1.86, 1.90, MATS["orange"], bv=0.005, M=Rr))
    body_o = join(p, "LTU_Body")
    # ---- static frame + relay antenna (between the wells, clear of both canisters at every pose)
    f = []
    fx = 0.065
    for s_ in (-1, 1):
        f.append(box("post", s_ * fx - 0.035, s_ * fx + 0.035, -0.07, 0.07, 1.42, 2.40, MATS["black"], bv=0.008))
        f.append(box("post_strip", s_ * fx - 0.010, s_ * fx + 0.010, -0.078, -0.06, 1.55, 2.30, MATS["orange"], bv=0.004, seg=2))
        for z in (1.70, 2.05):
            f.append(box("post_dot", s_ * fx - 0.014, s_ * fx + 0.014, -0.081, -0.066, z, z + 0.035, MATS["blue"], bv=0.004, seg=2))
    for zb, zt in ((1.46, 1.92), (1.92, 2.37)):
        for sy in (-1, 1):
            for d in (-1, 1):
                L = sqrt((2 * fx) ** 2 + (zt - zb) ** 2); a = atan2(zt - zb, 2 * fx * d)
                M = Matrix.Translation((0, sy * 0.045, (zb + zt) / 2)) @ Matrix.Rotation(-a, 4, "Y")
                f.append(box("brace", -L / 2, L / 2, -0.012, 0.012, -0.015, 0.015, MATS["black"], M=M))
    f.append(box("crossbar", -0.13, 0.13, -0.09, 0.09, 2.38, 2.44, MATS["black"], bv=0.012))
    f.append(cyl("mast", 0.03, 2.43, 2.46, MATS["gun"], seg=16))
    Md = Matrix.Translation((0, 0, 2.46)) @ Matrix.Rotation(radians(32), 4, "X") @ Matrix.Scale(0.62, 4)
    f.append(lathe("dish", [(0, -0.02), (0.06, -0.02), (0.19, 0.035), (0.205, 0.05), (0.19, 0.055), (0.06, 0.005), (0, 0.005)], MATS["black"], seg=40, M=Md))
    f.append(lathe("dish_rim", [(0.185, 0.035), (0.21, 0.04), (0.21, 0.058), (0.185, 0.058)], MATS["orange"], seg=40, M=Md))
    f.append(cyl("feed", 0.012, 0.0, 0.10, MATS["gun"], seg=10, M=Md))
    f.append(cyl("feed_tip", 0.02, 0.09, 0.115, MATS["blue"], seg=12, M=Md))
    frame_o = join(f, "LTU_Frame")
    # ---- the two moving canisters, built at the LOW pose; posed by object location z
    cans = []
    for s_, nm in ((-1, "LTU_CanA"), (1, "LTU_CanB")):
        c = []
        canister(c, MATS, s_ * CX, 0.0, R_CAN, B_LOW + 0.01, B_LOW + 0.01 + 0.85, strap=True, port_ang=-pi / 2 + s_ * 0.35, lid_h=0.11, flush=True)
        cans.append(join(c, nm))
    return {"body": body_o, "frame": frame_o, "cans": cans}


def pose(ltu, t):
    """t = 0: A up / B down; t = 1: A down / B up (opposite phase)."""
    a, b = ltu["cans"]
    a.location.z = STROKE * (1 - t); b.location.z = STROKE * t
    bpy.context.view_layer.update()


# ---------------------------------------------------------------- gates
def verts_world(o): return [o.matrix_world @ v.co for v in o.data.vertices]


def gates(o):
    fl = hsk_check.floating_islands(o)
    cp = hsk_check.coplanar_overlaps(o, occluders=[o])
    inv = hsk_check.inverted_islands(o)
    vs = verts_world(o)
    lo = [min(v[i] for v in vs) for i in range(3)]; hi = [max(v[i] for v in vs) for i in range(3)]
    me = o.data; me.calc_loop_triangles()
    r = {"tris": len(me.loop_triangles), "floating": int(fl["floating"]), "floating_items": fl["items"][:5],
         "coplanar_cross_visible_cm2": round(float(cp.get("cross_visible_cm2", cp["cross_zone_cm2"])), 3),
         "inverted_islands": [int(x) for x in inv], "bbox_m": [round(hi[i] - lo[i], 3) for i in range(3)], "min_z": round(lo[2], 4)}
    r["ok"] = bool(r["floating"] == 0 and r["coplanar_cross_visible_cm2"] == 0 and inv[0] == 0 and abs(lo[2]) < 1e-4
                   and all(r["bbox_m"][i] <= LIM[i] + 1e-6 for i in range(3)))
    return r


def _bvh(o):
    from mathutils.bvhtree import BVHTree
    me = o.data; me.calc_loop_triangles()
    co = [o.matrix_world @ v.co for v in me.vertices]
    return BVHTree.FromPolygons(co, [t.vertices[:] for t in me.loop_triangles], all_triangles=True), co


def clearance(a, b, stride=2):
    """(intersecting triangle pairs, approx min clearance mm). Clearance = nearest vertex-to-surface, both ways."""
    ta, ca = _bvh(a); tb, cb = _bvh(b)
    hits = len(ta.overlap(tb))
    g = 1e9
    for t, pts in ((tb, ca[::stride]), (ta, cb[::stride])):
        for q in pts:
            h = t.find_nearest(q, 0.2)
            if h[0] is not None: g = min(g, h[3])
    return hits, round(min(g, 0.2) * 1000, 1)


def sweep(ltu, steps=9, min_mm=3.0):
    rows = []; ok = True
    a, b = ltu["cans"]
    for i in range(steps):
        t = i / (steps - 1); pose(ltu, t); row = {"t": round(t, 3)}
        for nm, x, y in (("A_body", a, ltu["body"]), ("B_body", b, ltu["body"]), ("A_frame", a, ltu["frame"]),
                         ("B_frame", b, ltu["frame"]), ("A_B", a, b)):
            hits, mm = clearance(x, y); row[nm] = [hits, mm]
            if hits or mm < min_mm: ok = False
        rows.append(row)
    return {"ok": ok, "min_clearance_mm_gate": min_mm, "poses": rows}


# ---------------------------------------------------------------- render: soft studio like the stock icons
def studio(res):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences; dev = "CPU"
    for t in ("OPTIX", "CUDA"):
        try:
            prefs.compute_device_type = t; prefs.get_devices()
            if any(d.type == t for d in prefs.devices):
                for d in prefs.devices: d.use = d.type == t
                dev = t; break
        except TypeError: pass
    sc.cycles.device = "GPU" if dev != "CPU" else "CPU"
    sc.cycles.samples = 160; sc.cycles.use_denoising = True
    sc.render.resolution_x, sc.render.resolution_y = res; sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGB"
    sc.view_settings.view_transform = "AgX"
    try: sc.view_settings.look = "AgX - Base Contrast"
    except TypeError: pass
    w = bpy.data.worlds.new("Studio"); w.use_nodes = True
    bg = w.node_tree.nodes["Background"]; bg.inputs[0].default_value = (0.86, 0.87, 0.88, 1); bg.inputs[1].default_value = 0.38
    sc.world = w
    rc = bpy.data.collections.new("Studio"); sc.collection.children.link(rc)
    gm = material("StudioFloor", (0.80, 0.80, 0.81), 0.0, 0.55)
    bm = bmesh.new(); bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=40)
    me = bpy.data.meshes.new("Floor"); bm.to_mesh(me); bm.free(); me.materials.append(gm)
    fl = bpy.data.objects.new("Floor", me); rc.objects.link(fl)

    def area(name, loc, size, energy, color=(1, 1, 1)):
        L = bpy.data.objects.new(name, bpy.data.lights.new(name, "AREA")); rc.objects.link(L)
        L.data.size = size; L.data.energy = energy; L.data.color = color; L.location = loc
        L.rotation_euler = (-Vector(loc)).to_track_quat("-Z", "Y").to_euler()
        L.visible_camera = False
        return L
    area("Key", (-5.0, -6.0, 7.0), 6.0, 1300)
    area("Fill", (7.0, -3.0, 3.0), 7.0, 420, (0.92, 0.95, 1.0))
    area("Rim", (2.0, 7.0, 5.0), 5.0, 700)
    area("Top", (0.0, 0.0, 9.0), 8.0, 350)
    cam = bpy.data.objects.new("Cam", bpy.data.cameras.new("Cam")); rc.objects.link(cam)
    cam.data.lens = 70; sc.camera = cam
    return sc, cam, dev


def frame(sc, cam, objs, d, pad=1.10):
    vs = [v for o in objs for v in verts_world(o)]
    lo = Vector([min(v[i] for v in vs) for i in range(3)]); hi = Vector([max(v[i] for v in vs) for i in range(3)])
    c = (lo + hi) / 2; d = Vector(d).normalized(); fwd = -d
    q = fwd.to_track_quat("-Z", "Y"); right = q @ Vector((1, 0, 0)); up = q @ Vector((0, 1, 0))
    asp = sc.render.resolution_x / sc.render.resolution_y
    th = cam.data.sensor_width / (2 * cam.data.lens); tv = th / asp
    corners = [Vector((x, y, z)) - c for x in (lo.x, hi.x) for y in (lo.y, hi.y) for z in (lo.z, hi.z)]
    dist = max(max(abs(p.dot(right)) * pad / th, abs(p.dot(up)) * pad / tv) - p.dot(fwd) for p in corners)
    cam.location = c - fwd * dist; cam.rotation_euler = q.to_euler()
    cam.data.clip_start = 0.05; cam.data.clip_end = 500


def shot(sc, cam, objs, d, path, pad=1.10):
    frame(sc, cam, objs, d, pad); sc.render.filepath = path
    bpy.ops.render.render(write_still=True); return path


def main():
    global COLL, MATS
    t0 = time.time()
    for n in ("Cube", "Light", "Camera"):
        if n in bpy.data.objects: bpy.data.objects.remove(bpy.data.objects[n])
    COLL = bpy.data.collections.new("LTU_V3b"); bpy.context.scene.collection.children.link(COLL)
    MATS = make_mats()
    stock = build_stock(); ltu = build_ltu()
    parts = [ltu["body"], ltu["frame"]] + ltu["cans"]
    rep = {"date": "2026-10-10", "round": "v3b (wells, opposite-phase stroke, intersection gate)", "limit_m": LIM,
           "stroke_m": STROKE, "orphans": [o.name for o in COLL.objects if o not in parts + [stock]]}
    rep["parts"] = {o.name: gates(o) for o in parts}
    for o in parts:      # per-part gate; the assembly bbox is checked over both stroke ends below
        r = rep["parts"][o.name]
        r["ok"] = bool(r["floating"] == 0 and r["coplanar_cross_visible_cm2"] == 0 and r["inverted_islands"][0] == 0)
    rep["static_frame_vs_body"] = dict(zip(("tri_pairs", "clearance_mm"), clearance(ltu["frame"], ltu["body"])))
    rep["sweep"] = sweep(ltu)
    allv = []
    for t in (0.0, 1.0):
        pose(ltu, t); allv += [v for o in parts for v in verts_world(o)]
    lo = [min(v[i] for v in allv) for i in range(3)]; hi = [max(v[i] for v in allv) for i in range(3)]
    rep["assembly_bbox_all_poses_m"] = [round(hi[i] - lo[i], 3) for i in range(3)]
    rep["ok"] = bool(not rep["orphans"] and all(r["ok"] for r in rep["parts"].values()) and rep["sweep"]["ok"]
                     and all(rep["assembly_bbox_all_poses_m"][i] <= LIM[i] + 1e-6 for i in range(3)) and abs(lo[2]) < 1e-4)
    worst = min(((row["t"], k, v) for row in rep["sweep"]["poses"] for k, v in row.items() if k != "t"), key=lambda x: (-x[2][0], x[2][1]))
    print("LTU V3b GATES", json.dumps({o: (r["ok"], r["tris"], r["floating"], r["coplanar_cross_visible_cm2"]) for o, r in rep["parts"].items()}),
          "sweep_ok", rep["sweep"]["ok"], "worst", worst, "bbox", rep["assembly_bbox_all_poses_m"], "frame_vs_body", rep["static_frame_vs_body"])
    os.makedirs(OUT, exist_ok=True); files = []
    sc, cam, dev = studio((1600, 1000)); rep["render_device"] = dev
    icon = (1.0, -1.15, 0.40)
    pose(ltu, 0.0)
    for o in parts: o.location.x = 1.45
    stock.location.x = -1.45; bpy.context.view_layer.update()
    files.append(shot(sc, cam, [stock] + parts, icon, os.path.join(OUT, "v3b_compare_stock_vs_ltu.png"), pad=1.06))
    for o in parts: o.location.x = 0.0
    stock.location.x = 0.0; stock.hide_render = True; bpy.context.view_layer.update()
    sc.render.resolution_x, sc.render.resolution_y = 1000, 1100
    for vn, d in (("icon34", icon), ("side", (1.0, 0.15, 0.25)), ("back34", (-0.95, 1.1, 0.7))):
        files.append(shot(sc, cam, parts, d, os.path.join(OUT, f"v3b_ltu_{vn}.png")))
    # motion strip: one fixed camera framed on the canisters' full travel
    pose(ltu, 0.0); bpy.context.view_layer.update()
    a_up = [v for v in verts_world(ltu["cans"][0])]
    frame(sc, cam, parts, icon, pad=1.14)
    for k, t in (("1_Aup_Bdown", 0.0), ("2_mid", 0.5), ("3_Adown_Bup", 1.0)):
        pose(ltu, t); sc.render.filepath = os.path.join(OUT, f"v3b_motion_{k}.png"); bpy.ops.render.render(write_still=True)
        files.append(sc.render.filepath)
    pose(ltu, 0.0)
    rep["renders"] = files; rep["seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(ART, "build_report_v3b.json"), "w") as f: json.dump(rep, f, indent=1)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ART, "LTUConceptV3b.blend"))
    print("LTU V3b DONE", "OK" if rep["ok"] else "GATE FAIL", rep["seconds"], "s", dev)


MATS = None
main()
