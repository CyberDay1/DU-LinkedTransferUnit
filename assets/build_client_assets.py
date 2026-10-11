"""Builds the files the LTU mod sends to clients (LinkedTransferUnit/assets/client/).

    python build_client_assets.py ["<client>/Game/data/resources_generated"]

Variants (the server picks one with AssetVariant in ModLinkedTransferUnit.json):
  kit   ltu_kit.nqdef  -> ltu_kit_v1.node (+ ltu_kit_v1.mat): the stock TU body, retinted, with
        two stock radial atmo fuel tanks (XS) stood on the rear deck corners. Text only.
  mesh  ltu_mesh.nqdef -> ltu_mesh_v2.node (+ .mat, skinned .mesh, _col.mesh, _pump.anim):
        the approved v3b model exported by tools/unigine_mesh (run art/export_ltu_v3b_mesh.py
        first); the canisters pump in opposite phase while the unit runs.
  baked / bakedraw  ltu_baked.nqdef / ltu_bakedraw.nqdef: the low-poly skinned mesh with its own
        baked DU texture set (art/bake_ltu_v3b.py then bake_ltu_v3b_post.py); "baked" sends
        the binaries as .nqz (zlib, client 1.4.10+), "bakedraw" the same files uncompressed.

The stock files are only READ (from the client's loose resources_generated), never changed.
Every name is flat and lower-case, as NQ's ModLoader README requires; the client sees them
under resources_generated/. Bump the _vN suffix when a file's content changes, so a client
that cached an older copy in its RAM filesystem cannot mix versions.
"""
import copy
import hashlib
import json
import math
import os
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "client")
RES = sys.argv[1] if len(sys.argv) > 1 else r"C:/ProgramData/My Dual Universe/Game/data/resources_generated"
P = "resources_generated/"

TU = "elements/industries/industry-transfer-unit_001/"
TU_NODE = TU + "nodes/env_industry-transfer-unit_001.node"
TU_MAT = TU + "env_industry-transfer-unit_001.mat"
TU_BODY_MAT = "env_core-round-side-industry_001_01"
TU_ICON = TU + "icons/env_industry-transfer-unit_001_icon.png"
TANK = "elements/fueltanks/fueltank-atmospheric-radial_001_xs/"
TANK_MESH = TANK + "meshes/env_fueltank-atmospheric-radial_001_xs.mesh"
TANK_MAT_LIB = "elements/fueltanks/fueltank_001/env_fueltank_001.mat"
TANK_MAT = "RadialAtmofuelTankXS"

DECK_Z = 1.504            # top of the stock TU body deck (measured from its mesh)
TANK_HALF = 0.507         # tank half-length along its own Y axis (mesh header)
TANK_XY = [(-0.80, 0.73), (0.80, 0.73)]   # rear deck corners: 0.11 m clear of the stock canister

ITEM = "LinkedTransferUnit"


def node_id(*parts):
    return str(int(hashlib.sha1("|".join(parts).encode()).hexdigest()[:7], 16))


def mat4(cols):
    """Unigine .node <transform>: 16 numbers, column by column (translation last)."""
    return " ".join(f"{v:.9g}" for c in cols for v in c)


def tank_transform(x, y):
    # stand the tank up: its Y (length) axis -> world Z
    z = DECK_Z + TANK_HALF + 0.004
    return mat4([(1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (x, y, z, 1)])


def write(name, text):
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", name, len(text.encode()), "bytes")


def nqdef(node_file, icon):
    return json.dumps({"elements": {ITEM: {
        "inherit": "TransferUnit",
        "node": P + node_file,
        "icon": icon,
    }}}, indent=2) + "\n"


def node_text(tree):
    ET.indent(tree, "\t")
    body = ET.tostring(tree.getroot(), encoding="unicode").replace(" />", "/>")
    return '<?xml version="1.0" encoding="utf-8"?>\n' + body + "\n"


def remap_ids(root, tag):
    for i, n in enumerate(root.iter("node")):
        if "id" in n.attrib:
            n.set("id", node_id(tag, str(i), n.get("name", "")))


def build_kit():
    mat = f"""<?xml version="1.0" encoding="utf-8"?>
<materials version="2.3">
\t<material name="ltu_kit_body" parent="{TU_BODY_MAT}">
\t\t<parameter name="albedo_color">0.72 0.82 1 1</parameter>
\t</material>
\t<material name="ltu_kit_tank" parent="{TANK_MAT}">
\t\t<parameter name="albedo_color">1 0.62 0.32 1</parameter>
\t</material>
</materials>
"""
    write("ltu_kit_v1.mat", mat)

    tree = ET.parse(os.path.join(RES, TU_NODE))
    root = tree.getroot()
    libs = root.find("materials")
    for lib in (TANK_MAT_LIB,):
        e = ET.SubElement(libs, "library")
        e.text = P + lib
    e = ET.SubElement(libs, "library")
    e.text = P + "ltu_kit_v1.mat"
    body = root.find("node")
    body.set("name", "ltu_kit_body")
    for s in body.findall("surface"):
        s.set("material", "ltu_kit_body")
    for i, (x, y) in enumerate(TANK_XY):
        t = ET.Element("node", {"type": "ObjectMeshStatic", "id": "0", "name": f"ltu_kit_tank_{i}"})
        ET.SubElement(t, "mesh_name").text = P + TANK_MESH
        for lod, lo, hi in (("lod0", None, 75), ("lod1", 75, 150), ("lod2", 150, 250)):
            a = {"name": lod, "material": "ltu_kit_tank", "property": "surface_base"}
            if lo is not None:
                a["min_visible_distance"] = str(lo)
            a["max_visible_distance"] = str(hi)
            ET.SubElement(t, "surface", a)
        ET.SubElement(t, "transform").text = tank_transform(x, y)
        body.insert(list(body).index(body.find("transform")) + 1, t)
    remap_ids(root, "ltu_kit_v1")
    write("ltu_kit_v1.node", node_text(tree))
    write("ltu_kit.nqdef", nqdef("ltu_kit_v1.node", P + TU_ICON))


MESH_VER = "v2"
TU_NQDEF = TU + "defs/env_industry-transfer-unit_001.nqdef"

# State wiring of the pump clip, after the stock refiner (animation_play "running", scoped,
# on the running state) and the mining units (animation_pause on jammed, animation_stop_loop
# on idle). Appended to copies of the stock TU's own state actions, so the sounds, vfx and
# status-light material_params stay exactly the TU's whatever the client's inherit merge does.
PUMP_ACTIONS = {
    "running": [{"action": "animation_play", "name": "running", "scoped": True}],
    "jammed": [{"action": "animation_pause", "name": "running"}],
    "idle": [{"action": "animation_stop_loop", "name": "running"}],
}


def mesh_states():
    tu = json.load(open(os.path.join(RES, TU_NQDEF), encoding="utf-8"))["elements"]["TransferUnit"]
    states = copy.deepcopy(tu["states"])
    for state, actions in PUMP_ACTIONS.items():
        states.setdefault(state, {}).setdefault("start", []).extend(copy.deepcopy(actions))
    return states


def build_mesh():
    """Node for the exported v3b mesh (ltu_mesh_v2.mesh skinned + _col.mesh + _pump.anim,
    written by art/export_ltu_v3b_mesh.py). Keeps the stock TU's contact surface and vfx
    nodes; the body becomes an ObjectMeshSkinned, as the refiner's is."""
    v = MESH_VER
    if not os.path.exists(os.path.join(OUT, f"ltu_mesh_{v}.mesh")):
        print("skip mesh variant: run art/export_ltu_v3b_mesh.py in Blender first")
        return
    meta = json.load(open(os.path.join(OUT, "..", f"ltu_mesh_{v}.surfaces.json")))
    mat_lines = ['<?xml version="1.0" encoding="utf-8"?>', '<materials version="2.3">']
    for m in meta["materials"]:
        mat_lines.append(f'\t<material name="{m["name"]}" parent="{m["parent"]}">')
        for k, val in m.get("params", {}).items():
            mat_lines.append(f'\t\t<parameter name="{k}">{val}</parameter>')
        mat_lines.append("\t</material>")
    mat_lines.append("</materials>")
    write(f"ltu_mesh_{v}.mat", "\n".join(mat_lines) + "\n")

    tree = ET.parse(os.path.join(RES, TU_NODE))
    root = tree.getroot()
    libs = root.find("materials")
    for lib in meta.get("extra_libraries", []):
        ET.SubElement(libs, "library").text = P + lib
    ET.SubElement(libs, "library").text = P + f"ltu_mesh_{v}.mat"
    body = root.find("node")
    body.set("name", "ltu_mesh_body")
    if meta.get("skinned"):
        body.set("type", "ObjectMeshSkinned")
    body.find("mesh_name").text = P + f"ltu_mesh_{v}.mesh"
    for s in body.findall("surface"):
        body.remove(s)
    at = list(body).index(body.find("mesh_name")) + 1
    for i, s in enumerate(meta["surfaces"]):
        a = {"name": s["name"]}
        if s.get("min"):
            a["min_visible_distance"] = str(s["min"])
        if s.get("max"):
            a["max_visible_distance"] = str(s["max"])
        a["material"] = s["material"]
        a["property"] = "surface_base"
        body.insert(at + i, ET.Element("surface", a))
    col = next(n for n in body.iter("node") if n.get("name") == "col_mesh")
    col.find("mesh_name").text = P + f"ltu_mesh_{v}_col.mesh"
    col.find("surface").set("name", meta["col_surface"])
    remap_ids(root, f"ltu_mesh_{v}")
    write(f"ltu_mesh_{v}.node", node_text(tree))

    entry = {"inherit": "TransferUnit", "node": P + f"ltu_mesh_{v}.node", "icon": P + TU_ICON}
    if meta.get("anim"):
        an = meta["anim"]
        entry["animations"] = [{"animationPath": P + an["file"], "loop": an["loop"], "name": an["name"]}]
        entry["states"] = mesh_states()
    write("ltu_mesh.nqdef", json.dumps({"elements": {ITEM: entry}}, indent=2) + "\n")


def build_baked(variant, prefix):
    """Node, mat and nqdef of a baked variant (art/bake_ltu_v3b.py + bake_ltu_v3b_post.py):
    the low-poly skinned mesh with its own DU texture set. "baked" ships every binary as an
    NQ-compressed .nqz (the client inflates it and files it under the name without .nqz), and
    "bakedraw" ships the same art uncompressed. The files referenced here are the inflated
    names in both cases. One material: a child of the stock TU material, so the TU's states,
    gloss and mrao handling apply and only the four textures change."""
    meta_path = os.path.join(OUT, "..", "ltu_baked_v1.surfaces.json")
    if not os.path.exists(meta_path) or "textures" not in json.load(open(meta_path)):
        print(f"skip {variant}: run art/bake_ltu_v3b.py and bake_ltu_v3b_post.py first")
        return
    meta = json.load(open(meta_path))
    mat_name = "ltu_baked_body"
    lines = ['<?xml version="1.0" encoding="utf-8"?>', '<materials version="2.3">',
             f'\t<material name="{mat_name}" parent="{meta["parent_material"]}">']
    for slot, suffix in meta["textures"].items():
        lines.append(f'\t\t<texture name="{slot}">{P}{prefix}_{suffix}.dds</texture>')
    lines += ["\t</material>", "</materials>"]
    write(f"{prefix}.mat", "\n".join(lines) + "\n")

    tree = ET.parse(os.path.join(RES, TU_NODE))
    root = tree.getroot()
    ET.SubElement(root.find("materials"), "library").text = P + f"{prefix}.mat"
    body = root.find("node")
    body.set("name", "ltu_baked_body")
    body.set("type", "ObjectMeshSkinned")
    body.find("mesh_name").text = P + f"{prefix}.mesh"
    for s in body.findall("surface"):
        body.remove(s)
    at = list(body).index(body.find("mesh_name")) + 1
    for i, s in enumerate(meta["surfaces"]):
        a = {"name": s["name"]}
        if s.get("min"):
            a["min_visible_distance"] = str(s["min"])
        if s.get("max"):
            a["max_visible_distance"] = str(s["max"])
        a["material"] = mat_name
        a["property"] = "surface_base"
        body.insert(at + i, ET.Element("surface", a))
    col = next(n for n in body.iter("node") if n.get("name") == "col_mesh")
    col.find("mesh_name").text = P + f"{prefix}_col.mesh"
    col.find("surface").set("name", meta["col_surface"])
    remap_ids(root, prefix)
    write(f"{prefix}.node", node_text(tree))

    an = meta["anim"]
    entry = {"inherit": "TransferUnit", "node": P + f"{prefix}.node", "icon": P + TU_ICON,
             "animations": [{"animationPath": P + f"{prefix}_pump.anim", "loop": an["loop"], "name": an["name"]}],
             "states": mesh_states()}
    write(f"ltu_{variant}.nqdef", json.dumps({"elements": {ITEM: entry}}, indent=2) + "\n")


if __name__ == "__main__":
    build_kit()
    build_mesh()
    build_baked("baked", "ltu_baked_v1")
    build_baked("bakedraw", "ltu_bakedraw_v1")
