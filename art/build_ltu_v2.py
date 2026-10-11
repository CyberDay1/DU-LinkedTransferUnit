# Linked Transfer Unit: concept round v2 (2026-10-10), in Dual Universe's industry language.
# User feedback on v1 (art/v1/): "more in the DU style rather than the voxel mmo style... a bit more futuristic
# industrial and less of the scifi style". So: no levitators, crystals or orbs. Grounded, heavy machinery like the
# stock industry units (studied read-only from the client's loose icons and the stock TU albedo): light-grey panels over
# a gunmetal housing and a black chassis, seams you can see, orange accent bands, small blue status strips, one
# mechanism per unit that explains "remote linking" industrially.
#   D "Relay Hub"      : container-hub housing (gunmetal frame, grey panels, orange top band), a sealed coupler port on
#                        the front, a dish linker on a yoke mast on the roof.
#   E "Gantry Transfer": assembly-line gantry (two clad towers with orange inner faces, a crossbeam), a carriage with a
#                        telescopic transfer arm and gripper over an intake hopper, a blade uplink on the beam.
#   F "Linked Vessel"  : a horizontal pressure vessel on saddles (refiner/smelter language), a sealed coupler on one end
#                        cap, a service housing on top carrying a beacon mast.
# Each concept is ONE rigid body (no moving or floating parts). Gates as v1: orphans, floating_islands (2 mm),
# coplanar cross-zone, inverted islands, footprint <= stock TU (2.11 x 1.97 x 2.56 m), base at z = 0.
# Render: Cycles + denoise, ground plane for contact shadows/AO, three material tones + orange + emissive strips.
# Headless:  blender -b --factory-startup --python build_ltu_v2.py
#   -> art/LTUConceptsV2.blend, art/build_report_v2.json, art/renders/v2_*.png
import sys, os, json, time
import bpy
from math import radians, pi, sin, cos
from mathutils import Vector, Matrix

KIT = "F:/VoxelMMO/Prototype/Art/ProcKit/kit"
sys.path.insert(0, KIT)
import hsk, hsk_check, hsk_preview as P

ART = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ART, "renders")
LIM = (2.11, 1.97, 2.56)
BV = 0.012
CREATED = []


def T(o):
    CREATED.append(o); return o


def bx(x0, x1, y0, y1, z0, z1, zone="Secondary", bv=BV, name="bx"):
    o = T(hsk.box((x1 - x0, y1 - y0, z1 - z0), Matrix.Translation(((x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2)), zone, name))
    return hsk.chamfer(o, bv, 40) if bv else o


def plate_on(face, c, w, h, t=0.028, zone="Main", inset=0.004, corner=0.03, bv=0.006, name="pl"):
    """Panel on an axis-aligned face. face = (axis, sign, coord); c = (u, v) centre in the face's other two axes
    (x-faces: (y, z); y-faces: (x, z); z-faces: (x, y)). Back face buried `inset` into the housing."""
    ax, sg, co = face
    n = Vector([1 if i == ax else 0 for i in range(3)]) * sg
    o_ = [0, 0, 0]; o_[ax] = co - sg * inset
    rest = [i for i in range(3) if i != ax]
    o_[rest[0]] = c[0]; o_[rest[1]] = c[1]
    tang = Vector([1 if i == rest[0] else 0 for i in range(3)])
    M = hsk.frame(o_, n, tang)
    return T(hsk.plate(w, h, t, M, zone, corner, bv, name))


def clad(face, u0, u1, v0, v1, nu, nv, gap=0.035, skip=(), zone="Main", t=0.028, corner=0.03):
    out = []
    du = (u1 - u0 - gap * (nu + 1)) / nu; dv = (v1 - v0 - gap * (nv + 1)) / nv
    for i in range(nu):
        for j in range(nv):
            if (i, j) in skip: continue
            cu = u0 + gap + du / 2 + i * (du + gap); cv = v0 + gap + dv / 2 + j * (dv + gap)
            out.append(plate_on(face, (cu, cv), du, dv, t, zone, corner=corner))
    return out


def strip(face, c, w, h, depth=0.014, zone="Lightstrip"):
    """Status-light bar seated on the housing face inside a panel seam (top below the panel faces)."""
    ax, sg, co = face
    lo = [0, 0, 0]; hi = [0, 0, 0]
    rest = [i for i in range(3) if i != ax]
    lo[ax], hi[ax] = sorted((co - sg * 0.003, co + sg * depth))
    lo[rest[0]], hi[rest[0]] = c[0] - w / 2, c[0] + w / 2
    lo[rest[1]], hi[rest[1]] = c[1] - h / 2, c[1] + h / 2
    return bx(lo[0], hi[0], lo[1], hi[1], lo[2], hi[2], zone, 0.002, "strip")


def oct_along(axis, flat, a0, a1, c, zone, flat2=None, bv=0.006, name="oc"):
    o = T(hsk.octo(flat, a0, a1, axis, c, zone, flat2, name))
    return hsk.chamfer(o, bv, 40) if bv else o


def ring_along(axis, n, a_out, a_in, a0, a1, c, zone, k=0.08, name="rg"):
    """Faceted collar along X or Y (built along Z, then turned)."""
    o = T(hsk.tube_z(hsk.chpoly(n, a_out, a_out * k), hsk.chpoly(n, a_in, a_in * k), a0, a1, zone, name))
    R = {"X": Matrix.Rotation(pi / 2, 4, "Y"), "Y": Matrix.Rotation(-pi / 2, 4, "X"), "Z": Matrix.Identity(4)}[axis]
    o.data.transform(Matrix.Translation(c) @ R); return o


def coupler(axis, sign, face_coord, c, size=0.56, depth=0.12):
    """Sealed coupler port: gunmetal octagonal flange, orange bezel, recessed dark cap, four clamp lugs.
    axis 'X' or 'Y', sign = outward direction, c = centre (the two other coords as a 3-vector with the axis coord ignored)."""
    ai = "XYZ".index(axis); p = []
    def at(d):
        v = list(c); v[ai] = face_coord + sign * d; return tuple(v)
    a0, a1 = sorted((face_coord - sign * 0.01, face_coord + sign * depth))
    cc = list(c); cc[ai] = 0
    p.append(ring_along(axis, 8, size / 2, size / 2 - 0.07, a0, a1, tuple(cc), "Secondary", name="cp_flange"))
    b0, b1 = sorted((face_coord + sign * (depth - 0.03), face_coord + sign * (depth + 0.012)))
    p.append(ring_along(axis, 8, size / 2 - 0.06, size / 2 - 0.11, b0, b1, tuple(cc), "Accent", name="cp_bezel"))
    e0, e1 = sorted((face_coord - sign * 0.01, face_coord + sign * (depth - 0.035)))
    p.append(oct_along(axis, size - 0.20, e0, e1, tuple(cc), "Secondary", name="cp_cap"))
    # clamp lugs at 45/135/225/315 deg on the flange face
    r = size / 2 - 0.035
    for k in range(4):
        ang = pi / 4 + k * pi / 2
        u, v = r * cos(ang), r * sin(ang)
        q = [0, 0, 0]; others = [i for i in range(3) if i != ai]
        q[others[0]] = c[others[0]] + u; q[others[1]] = c[others[1]] + v
        lo = list(q); hi = list(q)
        for i in others: lo[i] -= 0.035; hi[i] += 0.035
        lo[ai], hi[ai] = sorted((face_coord + sign * (depth - 0.01), face_coord + sign * (depth + 0.03)))
        p.append(bx(*[lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]], "Secondary", 0.004, "cp_lug"))
    return p


def lathe_z(profile, n, zones, M, cham_k=0.0, name="lz"):
    o = T(hsk.revolve(profile, n=n, cham_k=cham_k, zones=zones, name=name))
    hsk.x_to_z(o); o.data.transform(M); return o


# ---------------------------------------------------------------- D: Relay Hub
def concept_D():
    p = []
    W, D_, H = 0.92, 0.87, 1.42                       # housing half-width (x), half-depth (y), top
    p.append(bx(-1.0, 1.0, -0.95, 0.95, 0.0, 0.12, "Secondary", 0.02, "D_skid"))
    p.append(bx(-W, W, -D_, D_, 0.11, H, "Secondary", 0.03, "D_core"))
    for sx in (-1, 1):
        for sy in (-1, 1):
            p.append(bx(sx * W - 0.10, sx * W + 0.10, sy * D_ - 0.10, sy * D_ + 0.10, 0.11, H + 0.04, "Secondary", 0.02, "D_post"))
    # cladding: sides 2x2, back 2x2, front = four corner panels around the coupler
    for sx in (-1, 1): p += clad((0, sx, sx * W), -D_ + 0.10, D_ - 0.10, 0.16, H - 0.10, 2, 2)
    p += clad((1, 1, D_), -W + 0.10, W - 0.10, 0.16, H - 0.10, 2, 2)
    for cu in (-0.62, 0.62):
        for cv, hh in ((0.40, 0.42), (1.08, 0.42)):
            p.append(plate_on((1, -1, -D_), (cu, cv), 0.42, hh))
    p += coupler("Y", -1, -D_, (0.0, 0.0, 0.74), size=0.62, depth=0.08)
    for sx in (-1, 1): p.append(strip((1, -1, -D_), (sx * 0.355, 0.74), 0.018, 0.5))
    # orange top band (frame ring) and a roof plate
    o = T(hsk.tube_z(hsk.rect(-W - 0.02, W + 0.02, -D_ - 0.02, D_ + 0.02, 0.06), hsk.rect(-W + 0.12, W - 0.12, -D_ + 0.12, D_ - 0.12, 0.04),
                     H - 0.05, H + 0.03, "Accent", "D_band"))
    p.append(o)
    p += clad((2, 1, H), -W + 0.12, W - 0.12, -D_ + 0.12, D_ - 0.12, 2, 2, t=0.022)
    # roof linker: pedestal, mast, yoke, dish with a feed horn
    p.append(bx(-0.30, 0.30, -0.30, 0.30, H, H + 0.20, "Secondary", 0.02, "D_ped"))
    p.append(oct_along("Z", 0.20, H + 0.19, 2.02, (0, 0, 0), "Secondary", name="D_mast"))
    p.append(bx(-0.54, 0.54, -0.09, 0.09, 1.98, 2.06, "Secondary", 0.01, "D_yokebase"))
    for sx in (-1, 1): p.append(bx(sx * 0.50 - 0.04, sx * 0.50 + 0.04, -0.07, 0.07, 2.02, 2.24, "Secondary", 0.008, "D_yokearm"))
    tilt = Matrix.Translation((0, 0, 2.16)) @ Matrix.Rotation(radians(-28), 4, "X")      # dish faces up and toward -Y
    p.append(lathe_z([(-0.14, 0.0), (-0.14, 0.12), (0.04, 0.44), (0.08, 0.44), (0.08, 0.40), (-0.08, 0.10), (-0.08, 0.0)], 16,
                     ["Secondary", "Main", "Secondary", "Secondary", "Main", "Main", "Main"], tilt, cham_k=0.03, name="D_dish"))
    p.append(T(hsk.cyl(0.035, -0.53, 0.53, "X", (0, 0, 2.16), "Secondary", 8, name="D_trunnion")))
    for z0, z1, r0, r1, zn in ((-0.05, 0.18, 0.025, 0.025, "Secondary"), (0.18, 0.26, 0.05, 0.03, "Accent")):
        h = T(hsk.cyl(r0, z0, z1, "Z", (0, 0, 0), zn, 8, r2=r1, name="D_feed")); h.data.transform(tilt); p.append(h)
    return {"body": hsk.join(p, "LTU_D_Body")}


# ---------------------------------------------------------------- E: Gantry Transfer
def concept_E():
    p = []
    p.append(bx(-1.0, 1.0, -0.95, 0.95, 0.0, 0.14, "Secondary", 0.02, "E_chassis"))
    for sx in (-1, 1):
        x0, x1 = sorted((sx * 0.62, sx * 0.98))
        p.append(bx(x0, x1, -0.70, 0.70, 0.13, 2.08, "Secondary", 0.03, "E_tower"))
        outer = sx * 0.98; inner = sx * 0.62
        p += clad((0, sx, outer), -0.68, 0.68, 0.18, 2.04, 2, 3)
        p += clad((0, -sx, inner), -0.62, 0.62, 0.40, 1.90, 1, 2, zone="Accent", t=0.02)
        for sy in (-1, 1): p += clad((1, sy, sy * 0.70), x0, x1, 0.18, 2.04, 1, 3)
        p.append(strip((1, -1, -0.70), ((x0 + x1) / 2, 0.806), 0.20, 0.016))
        # foot gussets tying tower to chassis, inner side
        g = T(hsk.prism_x([(-0.55, 0.13), (0.55, 0.13), (0.45, 0.36), (-0.45, 0.36)], -0.04, 0.04, "Secondary", "E_gusset"))
        g.data.transform(Matrix.Translation((inner - sx * 0.04, 0, 0)))
        p.append(hsk.chamfer(g, 0.006, 40))
    # crossbeam with two rails underneath
    p.append(bx(-0.99, 0.99, -0.16, 0.16, 2.06, 2.26, "Secondary", 0.02, "E_beam"))
    p += clad((1, -1, -0.16), -0.95, 0.95, 2.08, 2.24, 4, 1, t=0.02, gap=0.03)
    for sy in (-1, 1): p.append(bx(-0.62, 0.62, sy * 0.10 - 0.025, sy * 0.10 + 0.025, 2.02, 2.07, "Main", 0.006, "E_rail"))
    # carriage on the rails, telescopic arm, gripper over the hopper
    cx = 0.0
    p.append(bx(cx - 0.20, cx + 0.20, -0.17, 0.17, 1.82, 2.03, "Main", 0.015, "E_carriage"))
    p.append(strip((1, -1, -0.17), (cx, 1.925), 0.24, 0.018))
    p.append(bx(cx - 0.075, cx + 0.075, -0.075, 0.075, 1.30, 1.83, "Secondary", 0.008, "E_arm1"))
    p.append(bx(cx - 0.055, cx + 0.055, -0.055, 0.055, 0.98, 1.31, "Main", 0.006, "E_arm2"))
    p.append(bx(cx - 0.20, cx + 0.20, -0.16, 0.16, 0.88, 0.99, "Secondary", 0.012, "E_head"))
    for sy in (-1, 1):
        p.append(bx(cx - 0.16, cx + 0.16, sy * 0.13 - 0.025, sy * 0.13 + 0.025, 0.72, 0.89, "Secondary", 0.006, "E_jaw"))
        p.append(bx(cx - 0.165, cx + 0.165, sy * 0.13 - 0.028, sy * 0.13 + 0.028, 0.715, 0.76, "Accent", 0.004, "E_jawtip"))
    # intake hopper: clad frustum with a dark recessed mouth and grate bars
    hop = T(hsk.loft_z([(0.13, hsk.rect(-0.30, 0.30, -0.30, 0.30, 0.05)), (0.52, hsk.rect(-0.44, 0.44, -0.44, 0.44, 0.07))], "Main", "E_hopper"))
    p.append(hsk.chamfer(hop, 0.01, 40))
    p.append(T(hsk.tube_z(hsk.rect(-0.46, 0.46, -0.46, 0.46, 0.07), hsk.rect(-0.36, 0.36, -0.36, 0.36, 0.05), 0.50, 0.58, "Secondary", "E_lip")))
    p.append(bx(-0.37, 0.37, -0.37, 0.37, 0.44, 0.53, "Secondary", 0.0, "E_mouth"))
    for i in range(5):
        y = -0.28 + i * 0.14
        p.append(bx(-0.37, 0.37, y - 0.015, y + 0.015, 0.52, 0.56, "Secondary", 0.003, "E_grate"))
    # blade uplink on the beam
    p.append(bx(0.52, 0.80, -0.12, 0.12, 2.25, 2.33, "Secondary", 0.01, "E_upbase"))
    p.append(oct_along("Z", 0.08, 2.32, 2.42, (0.66, 0, 0), "Secondary", name="E_upmast"))
    p.append(bx(0.64, 0.68, -0.26, 0.26, 2.40, 2.52, "Main", 0.006, "E_blade"))
    p.append(bx(0.635, 0.685, -0.27, 0.27, 2.50, 2.54, "Accent", 0.004, "E_bladetip"))
    return {"body": hsk.join(p, "LTU_E_Body")}


# ---------------------------------------------------------------- F: Linked Vessel
def concept_F():
    p = []
    zc, R = 1.04, 0.54
    p.append(bx(-1.0, 1.0, -0.95, 0.95, 0.0, 0.12, "Secondary", 0.02, "F_chassis"))
    for sx in (-1, 1):
        sad = T(hsk.prism_x([(-0.66, 0.11), (0.66, 0.11), (0.66, 0.50), (0.48, 0.74), (0.30, 0.60), (0.0, 0.52), (-0.30, 0.60), (-0.48, 0.74), (-0.66, 0.50)],
                            sx * 0.55 - 0.13, sx * 0.55 + 0.13, "Secondary", "F_saddle"))
        p.append(hsk.chamfer(sad, 0.012, 40))
        p.append(plate_on((1, -1, -0.66), (sx * 0.55, 0.30), 0.20, 0.30, t=0.022))
        p.append(plate_on((1, 1, 0.66), (sx * 0.55, 0.30), 0.20, 0.30, t=0.022))
    # vessel: 16-gon shell in three panel courses (seams = small gaps on a dark liner), dark domed end caps
    liner = T(hsk.loft_x([(x, hsk.chpoly(16, R - 0.02, 0.0, c=(0, zc))) for x in (-0.80, 0.80)], "Secondary", "F_liner"))
    p.append(liner)
    for x0, x1 in ((-0.78, -0.30), (-0.27, 0.27), (0.30, 0.78)):
        p.append(T(hsk.tube_x(hsk.chpoly(16, R, R * 0.03, c=(0, zc)), hsk.chpoly(16, R - 0.03, R * 0.03, c=(0, zc)), x0, x1, "Main", "F_shell")))
    for xm in (-0.62, 0.62):
        p.append(T(hsk.tube_x(hsk.chpoly(16, R + 0.022, (R + 0.022) * 0.03, c=(0, zc)), hsk.chpoly(16, R - 0.01, (R - 0.01) * 0.03, c=(0, zc)),
                              xm - 0.04, xm + 0.04, "Accent", "F_band")))
    for sx in (-1, 1):
        cap = T(hsk.revolve([(0.0, 0.0), (0.0, R + 0.01), (0.07, R - 0.01), (0.12, R - 0.10), (0.14, 0.30), (0.14, 0.0)], n=16, cham_k=0.03,
                            zones=["Secondary"] * 6, name="F_cap"))
        cap.data.transform(Matrix.Translation((sx * 0.79, 0, zc)) @ (Matrix.Rotation(pi, 4, "Z") if sx < 0 else Matrix.Identity(4)))
        p.append(cap)
    p += coupler("X", 1, 0.93, (0.0, 0.0, zc), size=0.50, depth=0.06)
    # service housing on the crown, beacon mast on it
    p.append(bx(-0.36, 0.36, -0.30, 0.30, 1.44, 1.86, "Secondary", 0.02, "F_house"))
    for sy in (-1, 1): p += clad((1, sy, sy * 0.30), -0.34, 0.34, zc + R + 0.02, 1.84, 2, 1, t=0.022)
    for sx in (-1, 1): p += clad((0, sx, sx * 0.36), -0.28, 0.28, zc + R + 0.02, 1.84, 1, 1, t=0.022)
    p.append(strip((1, -1, -0.30), (0.0, 1.62), 0.62, 0.018))
    p.append(T(hsk.tube_z(hsk.rect(-0.38, 0.38, -0.32, 0.32, 0.04), hsk.rect(-0.28, 0.28, -0.22, 0.22, 0.03), 1.84, 1.90, "Accent", "F_hband")))
    p.append(bx(-0.30, 0.30, -0.24, 0.24, 1.84, 1.88, "Secondary", 0.0, "F_roof"))
    p.append(oct_along("Z", 0.13, 1.87, 2.26, (0.0, 0.0, 0.0), "Secondary", name="F_mast"))
    p.append(oct_along("Z", 0.26, 2.25, 2.29, (0.0, 0.0, 0.0), "Secondary", name="F_beaconbase"))
    p.append(oct_along("Z", 0.20, 2.28, 2.42, (0.0, 0.0, 0.0), "Lightstrip", bv=0.0, name="F_lamp"))
    for k in range(8):                                  # lamp cage bars
        a = pi / 8 + k * pi / 4; r = 0.118
        p.append(bx(r * cos(a) - 0.012, r * cos(a) + 0.012, r * sin(a) - 0.012, r * sin(a) + 0.012, 2.28, 2.43, "Secondary", 0.0, "F_cage"))
    p.append(oct_along("Z", 0.26, 2.42, 2.47, (0.0, 0.0, 0.0), "Secondary", name="F_beacontop"))
    p.append(oct_along("Z", 0.16, 2.46, 2.52, (0.0, 0.0, 0.0), "Accent", flat2=0.10, name="F_cap2"))
    return {"body": hsk.join(p, "LTU_F_Body")}


# ---------------------------------------------------------------- gates
def bbox(objs):
    pts = [o.matrix_world @ v.co for o in objs for v in o.data.vertices]
    return [min(q[i] for q in pts) for i in range(3)], [max(q[i] for q in pts) for i in range(3)]


def gates(c):
    o = c["body"]
    fl = hsk_check.floating_islands(o)
    cp = hsk_check.coplanar_overlaps(o, occluders=[o])
    inv = hsk_check.inverted_islands(o)
    lo, hi = bbox([o])
    r = {"tris": hsk.tris(o), "floating": fl["floating"], "floating_items": fl["items"][:5], "islands": fl["islands"],
         "coplanar_cross_visible_cm2": round(cp.get("cross_visible_cm2", cp["cross_zone_cm2"]), 3),
         "coplanar_same_zone_cm2": round(cp["same_zone_cm2"], 3), "inverted_islands": inv,
         "bbox_m": [round(hi[i] - lo[i], 3) for i in range(3)], "min_z": round(lo[2], 4)}
    r["floating"] = int(r["floating"]); r["inverted_islands"] = [int(x) for x in inv]
    r["ok"] = bool(r["floating"] == 0 and r["coplanar_cross_visible_cm2"] == 0 and inv[0] == 0 and abs(lo[2]) < 1e-4
               and all(r["bbox_m"][i] <= LIM[i] + 1e-6 for i in range(3)))
    return r


# ---------------------------------------------------------------- render (Cycles, preview-cheap)
LOOK = {  # linear base, metallic, roughness, emission
    "Main":       ((0.46, 0.47, 0.48), 0.55, 0.40, 0.0),     # DU light-grey brushed panel
    "Secondary":  ((0.040, 0.042, 0.046), 0.85, 0.36, 0.0),  # gunmetal housing / chassis
    "Accent":     ((0.75, 0.17, 0.025), 0.10, 0.42, 0.0),    # DU orange
    "Glow":       ((0.10, 0.55, 1.00), 0.0, 0.3, 5.0),
    "Lightstrip": ((0.12, 0.60, 1.00), 0.0, 0.3, 6.0),       # blue status strip
}


def render_mats():
    for z, (rgb, met, rough, em) in LOOK.items():
        m = bpy.data.materials.get("HSK_" + z)
        if m is None: continue
        b = next(n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        b.inputs["Base Color"].default_value = (*rgb, 1); b.inputs["Metallic"].default_value = met
        b.inputs["Roughness"].default_value = rough
        b.inputs["Emission Strength"].default_value = em
        if em: b.inputs["Emission Color"].default_value = (*rgb, 1)
        if z in ("Main", "Secondary"):               # subtle roughness breakup so panels read as metal, not plastic
            nt = m.node_tree; nz = nt.nodes.new("ShaderNodeTexNoise"); nz.inputs["Scale"].default_value = 18.0
            mr = nt.nodes.new("ShaderNodeMapRange"); mr.inputs["To Min"].default_value = rough - 0.08; mr.inputs["To Max"].default_value = rough + 0.08
            nt.links.new(nz.outputs["Fac"], mr.inputs["Value"]); nt.links.new(mr.outputs["Result"], b.inputs["Roughness"])


def render_scene(res):
    sc = bpy.context.scene
    sc.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    dev = "NONE"
    for t in ("OPTIX", "CUDA"):
        try:
            prefs.compute_device_type = t; prefs.get_devices()
            if any(d.type == t for d in prefs.devices):
                for d in prefs.devices: d.use = d.type == t
                dev = t; break
        except TypeError: pass
    sc.cycles.device = "GPU" if dev != "NONE" else "CPU"
    sc.cycles.samples = 64; sc.cycles.use_denoising = True
    sc.render.resolution_x, sc.render.resolution_y = res; sc.render.resolution_percentage = 100
    sc.render.image_settings.file_format = "PNG"; sc.render.image_settings.color_mode = "RGB"
    try: sc.view_settings.view_transform = "AgX"; sc.view_settings.look = "AgX - Medium High Contrast"
    except TypeError: pass
    w = bpy.data.worlds.new("LTU_World"); w.use_nodes = True
    bg = w.node_tree.nodes["Background"]; bg.inputs[0].default_value = (0.30, 0.32, 0.35, 1); bg.inputs[1].default_value = 0.6
    sc.world = w
    coll = bpy.data.collections.new("LTU_Render"); sc.collection.children.link(coll)
    gm = bpy.data.materials.new("LTU_Ground"); gm.use_nodes = True
    gb = next(n for n in gm.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    gb.inputs["Base Color"].default_value = (0.16, 0.165, 0.17, 1); gb.inputs["Roughness"].default_value = 0.8
    bpy.ops.mesh.primitive_plane_add(size=60, location=(0, 0, 0)); g = bpy.context.active_object; g.name = "LTU_Ground"
    g.data.materials.append(gm)
    for c in g.users_collection: c.objects.unlink(g)
    coll.objects.link(g)
    sun = bpy.data.objects.new("LTU_Sun", bpy.data.lights.new("LTU_Sun", "SUN")); coll.objects.link(sun)
    sun.data.energy = 3.2; sun.data.angle = radians(4)
    sun.rotation_euler = Vector((-0.55, 0.65, -0.75)).to_track_quat("-Z", "Y").to_euler()
    fill = bpy.data.objects.new("LTU_Fill", bpy.data.lights.new("LTU_Fill", "AREA")); coll.objects.link(fill)
    fill.data.energy = 900; fill.data.size = 6; fill.location = (-6, -7, 5)
    fill.visible_camera = False; fill.visible_glossy = False
    fill.rotation_euler = (-Vector((-6, -7, 5))).to_track_quat("-Z", "Y").to_euler()
    cam = bpy.data.objects.new("LTU_Cam", bpy.data.cameras.new("LTU_Cam")); coll.objects.link(cam)
    cam.data.lens = 50; sc.camera = cam
    return sc, cam, coll, dev


def shot(sc, cam, objs, d, path, pad=1.08):
    lo, hi = P._box(objs)
    P._frame(cam, lo, hi, d, sc.render.resolution_x / sc.render.resolution_y, pad)
    cam.data.clip_start = 0.05; cam.data.clip_end = 200
    sc.render.filepath = path; bpy.ops.render.render(write_still=True); return path


def main():
    t0 = time.time()
    for n in ("Cube", "Light", "Camera"):                # factory-startup default scene
        if n in bpy.data.objects: bpy.data.objects.remove(bpy.data.objects[n])
    hsk.begin("LTU_V2")
    concepts = {"D": concept_D(), "E": concept_E(), "F": concept_F()}
    bodies = {k: c["body"] for k, c in concepts.items()}
    live = [o for o in hsk.COLL.objects if o.type == "MESH"]
    orphans = [o.name for o in live if o not in bodies.values()]
    rep = {"date": "2026-10-10", "round": "v2 (DU industry language)", "limit_m": LIM, "orphans": orphans, "concepts": {}}
    for k, c in concepts.items(): rep["concepts"][k] = gates(c)
    rep["ok"] = not orphans and all(v["ok"] for v in rep["concepts"].values())
    print("LTU V2 GATES", json.dumps({k: (v["ok"], v["tris"], v["bbox_m"], v["floating"], v["coplanar_cross_visible_cm2"]) for k, v in rep["concepts"].items()}), "orphans", orphans)
    render_mats()
    os.makedirs(OUT, exist_ok=True); files = []
    sc, cam, coll, dev = render_scene((1000, 720)); rep["render_device"] = dev
    views = {"front34": (0.95, -1.30, 0.70), "side": (1.45, 0.40, 0.32), "back34": (-0.95, 1.25, 0.85)}
    for k, o in bodies.items():
        for kk, oo in bodies.items(): oo.hide_render = kk != k
        for vn, v in views.items():
            files.append(shot(sc, cam, [o], v, os.path.join(OUT, f"v2_{vn}_{k}.png")))
    # lineup with the 1.8 m figure
    x = 0.0; gap = 0.9
    for k, o in bodies.items():
        o.hide_render = False; lo, hi = bbox([o]); o.location.x = x - lo[0]; x += (hi[0] - lo[0]) + gap
    fig = P.figure(coll, x + 0.2, 0.0)
    bpy.context.view_layer.update()
    sc.render.resolution_x, sc.render.resolution_y = 1800, 900
    files.append(shot(sc, cam, list(bodies.values()) + [fig], (0.35, -1.4, 0.50), os.path.join(OUT, "v2_lineup.png"), pad=1.04))
    for o in bodies.values(): o.location.x = 0.0
    rep["renders"] = files; rep["seconds"] = round(time.time() - t0, 1)
    with open(os.path.join(ART, "build_report_v2.json"), "w") as f: json.dump(rep, f, indent=1)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(ART, "LTUConceptsV2.blend"))
    print("LTU V2 DONE", "OK" if rep["ok"] else "GATE FAIL", rep["seconds"], "s", dev)


main()
