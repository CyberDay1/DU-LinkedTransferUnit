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
# Headless:  blender -b --factory-startup --python build_ltu_v3.py
#   -> art/LTUConceptV3.blend, art/build_report_v3.json, art/renders/v3_*.png
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


def canister(p, mats, cx, cy, r, z0, z1, strap=True, port_ang=-pi / 2, lid_h=0.13):
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
    p.append(cyl("port", 0.055, -0.01, 0.022, mats["gun"], seg=24, M=R))
    p.append(cyl("port_glass", 0.036, 0.0, 0.028, mats["grey"], seg=24, M=R))


# ---------------------------------------------------------------- the two units
def build_stock():
    p = []
    X, Y = 1.055, 0.985
    body(p, MATS, X, Y, 0.06, 1.52)
    canister(p, MATS, 0.0, 0.06, 0.80, 1.55, 2.40, lid_h=0.155)
    return join(p, "STOCK_TU")


def build_ltu():
    p = []
    X, Y = 1.055, 0.985
    body(p, MATS, X, Y, 0.06, 1.34, dots=True)
    # black deck with an orange front lip
    p.append(box("deck", -0.98, 0.98, -0.80, 0.80, 1.36, 1.42, MATS["black"], bv=0.02))
    p.append(box("deck_lip", -0.86, 0.86, -0.815, -0.79, 1.375, 1.405, MATS["orange"], bv=0.006, seg=2))
    zc0, zc1, r, cx = 1.42, 2.20, 0.40, 0.56
    for s in (-1, 1):
        canister(p, MATS, s * cx, 0.0, r, zc0 + 0.01, zc1, strap=True, port_ang=-pi / 2 + s * 0.35, lid_h=0.11)
    # central black frame: two slim posts, diagonal braces, top crossbar, orange strips, blue status dots
    fx = 0.10
    for s in (-1, 1):
        p.append(box("post", s * fx - 0.045, s * fx + 0.045, -0.07, 0.07, 1.40, 2.37, MATS["black"], bv=0.01))
        p.append(box("post_strip", s * fx - 0.012, s * fx + 0.012, -0.078, -0.06, 1.55, 2.26, MATS["orange"], bv=0.004, seg=2))
        for z in (1.66, 2.00):
            p.append(box("post_dot", s * fx - 0.016, s * fx + 0.016, -0.081, -0.066, z, z + 0.035, MATS["blue"], bv=0.004, seg=2))
        # clamp arms: each canister strapped to its post (the visible "link")
        for z in (1.62, 2.04):
            p.append(box("clamp_arm", min(s * fx, s * (cx - r + 0.02)), max(s * fx, s * (cx - r + 0.02)), -0.04, 0.04, z, z + 0.06, MATS["gun"], bv=0.008))
            p.append(cyl("clamp_band", r + 0.016, z - 0.005, z + 0.065, MATS["gun"], M=Matrix.Translation((s * cx, 0, 0))))
    for zb, zt in ((1.45, 1.90), (1.90, 2.33)):          # X braces between the posts (front and back)
        for sy in (-1, 1):
            for d in (-1, 1):
                L = sqrt((2 * fx) ** 2 + (zt - zb) ** 2); a = atan2(zt - zb, 2 * fx * d)
                M = Matrix.Translation((0, sy * 0.045, (zb + zt) / 2)) @ Matrix.Rotation(-a, 4, "Y")
                p.append(box("brace", -L / 2, L / 2, -0.012, 0.012, -0.015, 0.015, MATS["black"], M=M))
    p.append(box("crossbar", -0.20, 0.20, -0.09, 0.09, 2.34, 2.40, MATS["black"], bv=0.012))
    # relay mast + small dish (black, orange rim, blue feed tip) facing up/forward
    p.append(cyl("mast", 0.03, 2.39, 2.43, MATS["gun"], seg=16))
    Md = Matrix.Translation((0, 0, 2.43)) @ Matrix.Rotation(radians(32), 4, "X") @ Matrix.Scale(0.78, 4)
    p.append(lathe("dish", [(0, -0.02), (0.06, -0.02), (0.19, 0.035), (0.205, 0.05), (0.19, 0.055), (0.06, 0.005), (0, 0.005)], MATS["black"], seg=40, M=Md))
    p.append(lathe("dish_rim", [(0.185, 0.035), (0.21, 0.04), (0.21, 0.058), (0.185, 0.058)], MATS["orange"], seg=40, M=Md))
    p.append(cyl("feed", 0.012, 0.0, 0.10, MATS["gun"], seg=10, M=Md))
    p.append(cyl("feed_tip", 0.02, 0.09, 0.115, MATS["blue"], seg=12, M=Md))
    return join(p, "LTU_V3")


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
    COLL = bpy.data.collections.new("LTU_V3"); bpy.context.scene.collection.children.link(COLL)
    MATS = make_mats()
    stock = build_stock(); ltu = build_ltu()
    rep = {"date": "2026-10-10", "round": "v3 (stock TU as base, no ProcKit modelling)", "limit_m": LIM,
           "orphans": [o.name for o in COLL.objects if o not in (stock, ltu)],
           "stock_lookalike": gates(stock), "ltu": gates(ltu)}
    rep["ok"] = not rep["orphans"] and rep["ltu"]["ok"]
    print("LTU V3 GATES", json.dumps({k: rep[k] for k in ("orphans",)}), "LTU", json.dumps({k: rep["ltu"][k] for k in ("ok", "tris", "bbox_m", "floating", "coplanar_cross_visible_cm2", "inverted_islands")}),
          "STOCK", json.dumps({k: rep["stock_lookalike"][k] for k in ("tris", "bbox_m")}))
    os.makedirs(OUT, exist_ok=True); files = []
    sc, cam, dev = studio((1600, 1000)); rep["render_device"] = dev
    icon = (1.0, -1.15, 0.40)                      # the stock icon's 3/4-from-slightly-above view
    stock.location.x = -1.45; ltu.location.x = 1.45; bpy.context.view_layer.update()
    files.append(shot(sc, cam, [stock, ltu], icon, os.path.join(OUT, "v3_compare_stock_vs_ltu.png"), pad=1.06))
    stock.location.x = 0.0; ltu.location.x = 0.0; stock.hide_render = True; bpy.context.view_layer.update()
    sc.render.resolution_x, sc.render.resolution_y = 1000, 1100
    for vn, d in (("icon34", icon), ("side", (1.0, 0.15, 0.25)), ("back34", (-0.95, 1.1, 0.7))):
        files.append(shot(sc, cam, [ltu], d, os.path.join(OUT, f"v3_ltu_{vn}.png")))
    rep["renders"] = files; rep["seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(ART, "build_report_v3.json"), "w") as f: json.dump(rep, f, indent=1)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ART, "LTUConceptV3.blend"))
    print("LTU V3 DONE", "OK" if rep["ok"] else "GATE FAIL", rep["seconds"], "s", dev)


MATS = None
main()
