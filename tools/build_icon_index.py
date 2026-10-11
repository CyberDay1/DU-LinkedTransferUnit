"""Build src/IconIndex.json: item-bank name -> icon path, from a local DU client install.

The client keeps its item icons as loose files under data/resources_generated and names them in
plain JSON .nqdef files (elements/*/defs, voxel/materials, partsicons, ammoicons, ...). The HUD
loads them as coui://data/resources_generated/<path>. The index holds only those paths (no image
data), with the "resources_generated/" prefix dropped.

    python tools/build_icon_index.py ["C:/ProgramData/My Dual Universe/Game/data"]
"""
import json
import pathlib
import re
import sys

DATA = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else r"C:\ProgramData\My Dual Universe\Game\data")
ROOT = DATA / "resources_generated"
OUT = pathlib.Path(__file__).resolve().parent.parent / "src" / "IconIndex.json"
PREFIX = "resources_generated/"


def load(path):
    text = path.read_text(encoding="utf-8", errors="replace")
    text = re.sub(r",(\s*[}\]])", r"\1", text)  # a few files carry trailing commas
    try:
        return json.loads(text)
    except ValueError:
        return None


def walk(node, icons, inherits):
    """Every {Name: {"icon": path}} / {Name: {"inherit": Other}} and {"icons": {cat: {Name: path}}}."""
    if not isinstance(node, dict):
        return
    for key, val in node.items():
        if isinstance(val, dict):
            if isinstance(val.get("icon"), str):
                icons.setdefault(key, val["icon"])
            elif isinstance(val.get("inherit"), str):
                inherits.setdefault(key, val["inherit"])
            walk(val, icons, inherits)
        elif isinstance(val, str) and val.startswith(PREFIX) and val.endswith(".png"):
            icons.setdefault(key, val)


def main():
    icons, inherits = {}, {}
    files = sorted(ROOT.rglob("*.nqdef"))
    for f in files:
        d = load(f)
        if d is not None:
            walk(d, icons, inherits)
    for name, base in inherits.items():
        seen, b = set(), base
        while b not in icons and b in inherits and b not in seen:
            seen.add(b)
            b = inherits[b]
        if b in icons:
            icons.setdefault(name, icons[b])
    out = {k: (v[len(PREFIX):] if v.startswith(PREFIX) else v) for k, v in sorted(icons.items())}
    missing = [k for k, v in out.items() if not (ROOT / v).exists()]
    for k in missing:
        del out[k]
    OUT.write_text(json.dumps(out, separators=(",", ":")), encoding="utf-8")
    print(f"{len(files)} def files, {len(out)} icons ({len(missing)} dropped: file missing) -> {OUT}")


if __name__ == "__main__":
    main()
