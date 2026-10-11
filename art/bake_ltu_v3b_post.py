"""Step 2 of the LTU bake (plain Python): composites the per-part Cycles passes written by
bake_ltu_v3b.py into the DU texture set and writes the client files of the two baked variants.

    python bake_ltu_v3b_post.py

DU texture set, as the stock transfer unit's (read-only, measured):
    _c     DXT1   albedo, sRGB
    _n     BC5U   tangent-space normal X in R, Y in G (no Z); green follows the file +v
                  bitangent, i.e. image DOWN, the opposite of Blender's bake, so G is flipped.
                  Checked with an integrability test, corr(dR/d(down), dG/d(right)) on island
                  interiors: +0.25..+0.41 on the stock industry maps, negative on Blender's.
    _mrao  DXT5   R metalness, G roughness, B ambient occlusion, A 255
    _e     DXT1   white emission mask; the element states tint and pulse it
Full mip chains, legacy DDS headers byte-identical in layout to the stock files (dds_bc.py).

Outputs (assets/client):
    baked     ltu_baked.nqdef + ltu_baked_v1.{mat,node} + the binaries as NQ-compressed .nqz
              (client 1.4.10+ inflates a .nqz before it enters the file system, ModLoader README)
    bakedraw  the same art uncompressed (fallback if a client does not take .nqz)
plus art/renders/baked_v1_textures.png (a board of the four maps).
"""
import json
import os
import shutil
import struct
import sys
import zlib

import numpy as np
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "..", "tools", "unigine_mesh"))
import dds_bc  # noqa: E402

SCRATCH = "C:/Claude/scratch/ltu-bake"
CLIENT = os.path.join(HERE, "..", "assets", "client")
NAME = "ltu_baked_v1"
RAW = "ltu_bakedraw_v1"
PARTS = ("LTU_Body", "LTU_Frame", "LTU_CanA", "LTU_CanB")
TEX = 1024
NQZ_MAGIC = 0x124F0359
rng = np.random.default_rng(7)


def load(part, p):
    a = np.load(os.path.join(SCRATCH, f"{part}_{p}.npy")).astype(np.float32)
    return a if a.ndim == 3 else a[..., None]


def mask_of(part, res):
    tris = np.load(os.path.join(SCRATCH, f"{part}_uvtris.npy"))
    im = Image.new("L", (res, res), 0)
    d = ImageDraw.Draw(im)
    for t in tris:
        d.polygon([(float(u * res), float(v * res)) for u, v in t], fill=255, outline=255)
    return np.asarray(im) > 0


def dilate_fill(img, filled, steps):
    """Grow filled pixels into empty ones (mean of filled 8-neighbours), `steps` times."""
    img = img.copy()
    filled = filled.copy()
    for _ in range(steps):
        acc = np.zeros_like(img)
        cnt = np.zeros(filled.shape, np.float32)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                if dx == dy == 0:
                    continue
                f = np.roll(np.roll(filled, dy, 0), dx, 1)
                acc += np.roll(np.roll(img, dy, 0), dx, 1) * f[..., None]
                cnt += f
        grow = (~filled) & (cnt > 0)
        if not grow.any():
            break
        img[grow] = acc[grow] / cnt[grow][:, None]
        filled |= grow
    return img, filled


def noise(res, octaves=(8, 16, 32, 64, 128, 256), seed_shift=0):
    out = np.zeros((res, res), np.float32)
    amp, tot = 1.0, 0.0
    for o in octaves:
        g = rng.random((o, o)).astype(np.float32)
        up = np.asarray(Image.fromarray(g, "F").resize((res, res), Image.BICUBIC))
        out += amp * (up - 0.5) * 2
        tot += amp
        amp *= 0.6
    return out / tot


def curl_sign(rg, inside):
    """Integrability of the map read as a height-field gradient: the correlation of dR/d(down)
    with dG/d(right), on island interiors only (island borders are jumps, not slopes).
    Positive = green follows the image-DOWN (file +v) bitangent, as every stock industry _n
    measured (+0.25..+0.41); Blender bakes green image-UP, which measures negative."""
    nx, ny = rg[..., 0] * 2 - 1, rg[..., 1] * 2 - 1
    a = np.diff(nx, axis=0)[:, :-1]
    b = np.diff(ny, axis=1)[:-1, :]
    ins = inside[:-1, :-1] & inside[1:, :-1] & inside[:-1, 1:]
    m = ins & ((np.abs(a) > 0.01) | (np.abs(b) > 0.01))
    return float(np.corrcoef(a[m], b[m])[0, 1])


def down(a, normal=False):
    ch = a.shape[2]
    out = np.stack([np.asarray(Image.fromarray(a[..., i].astype(np.float32), "F").resize((TEX, TEX), Image.BOX))
                    for i in range(ch)], -1)
    if normal:
        xy = out[..., :2] * 2 - 1
        ln = np.sqrt((xy ** 2).sum(-1, keepdims=True))
        xy = np.where(ln > 1, xy / np.maximum(ln, 1e-6), xy)
        out[..., :2] = (xy + 1) / 2
    return out


def to8(a):
    return np.clip(a * 255 + 0.5, 0, 255).astype(np.uint8)


def nqz(src, dst):
    data = open(src, "rb").read()
    with open(dst, "wb") as f:
        f.write(struct.pack("<IQ", NQZ_MAGIC, len(data)) + zlib.compress(data, 9))


# ---------------------------------------------------------------- composite
res = np.load(os.path.join(SCRATCH, "LTU_Body_ao.npy")).shape[0]
passes = {p: np.zeros((res, res, c), np.float32) for p, c in
          (("normal", 3), ("ao", 1), ("albedo", 3), ("mr", 2), ("emask", 1), ("cover", 1))}
filled = np.zeros((res, res), bool)
report = {"bake_res": res, "tex_res": TEX, "miss_pct": {}}
for part in PARTS:
    m = mask_of(part, res)
    cov = load(part, "cover")[..., 0] > 0.5
    report["miss_pct"][part] = round(100 * float((m & ~cov).sum()) / max(1, int(m.sum())), 3)
    good = m & cov
    for p in passes:
        a = load(part, p)
        a, _ = dilate_fill(np.where(good[..., None], a, 0), good, 6)     # close ray misses inside islands
        passes[p][m] = a[m]
    filled |= m
report["atlas_fill_pct"] = round(100 * float(filled.mean()), 1)
NEUTRAL = {"normal": (0.5, 0.5, 1.0), "ao": (1.0,), "emask": (0.0,), "cover": (1.0,)}
for p in passes:
    passes[p], reach = dilate_fill(passes[p], filled, 24)
    # gaps no island reaches (the bore holes in the deck): a flat normal, the mean elsewhere,
    # so the small mips never average in a black (invalid) normal
    fillv = NEUTRAL.get(p) or tuple(passes[p][filled].mean(0))
    passes[p][~reach] = np.array(fillv, np.float32)

ao = passes["ao"][..., 0]
nrm = passes["normal"]
edge = np.clip((1 - (nrm[..., 2] * 2 - 1)) * 6, 0, 1)                   # bevels and seams
n1, n2 = noise(res), noise(res)
scratch = np.clip(noise(res, (64, 128, 256, 512)) * 3 - 2.2, 0, 1)

alb = passes["albedo"]
alb = alb * (0.9 + 0.1 * ao[..., None])                                   # cavity dirt
alb = alb * (1 + 0.035 * n1[..., None])                                   # grunge
alb = alb + (1 - alb) * (0.07 * edge)[..., None]                          # worn bevel edges
alb = alb * (1 - 0.05 * scratch)[..., None]

metal = passes["mr"][..., 0]
rough = np.clip(passes["mr"][..., 1] + 0.05 * n2 - 0.06 * edge + 0.08 * scratch, 0.08, 1)
aoc = np.clip(ao, 0, 1) ** 0.8
mrao = np.stack([metal, rough, aoc, np.ones_like(ao)], -1)
emask = (passes["emask"][..., 0] > 0.5).astype(np.float32)
emask = np.asarray(Image.fromarray(emask, "F").resize((res // 2, res // 2), Image.BOX).resize((res, res), Image.BILINEAR))

normal_rg = np.concatenate([nrm[..., :2], np.zeros_like(nrm[..., :1])], -1)
from PIL import ImageFilter  # noqa: E402
inside = np.asarray(Image.fromarray(filled.astype(np.uint8) * 255).filter(ImageFilter.MinFilter(7))) > 0
report["normal_curl_corr_blender"] = round(curl_sign(nrm, inside), 3)
if report["normal_curl_corr_blender"] < 0:
    normal_rg[..., 1] = 1 - normal_rg[..., 1]
    report["normal_green_flipped"] = True
report["normal_curl_corr_out"] = round(curl_sign(normal_rg, inside), 3)

maps = {
    "c": (to8(down(alb)), "DXT1", False),
    "n": (to8(down(normal_rg, normal=True)), "BC5U", True),
    "mrao": (to8(down(mrao)), "DXT5", False),
    "e": (to8(down(np.repeat(emask[..., None], 3, -1))), "DXT1", False),
}

# ---------------------------------------------------------------- write both variants
sizes = {"baked": {}, "bakedraw": {}}
for k, (arr, fmt, isn) in maps.items():
    mode = "RGBA" if arr.shape[2] == 4 else "RGB"
    raw_path = os.path.join(CLIENT, f"{RAW}_{k}.dds")
    dds_bc.write(raw_path, Image.fromarray(arr, mode), fmt, normal=isn)
    sizes["bakedraw"][f"{RAW}_{k}.dds"] = os.path.getsize(raw_path)
    nqz(raw_path, os.path.join(CLIENT, f"{NAME}_{k}.dds.nqz"))
    sizes["baked"][f"{NAME}_{k}.dds.nqz"] = os.path.getsize(os.path.join(CLIENT, f"{NAME}_{k}.dds.nqz"))
for suffix in (".mesh", "_col.mesh", "_pump.anim"):
    plain, raw_copy = os.path.join(CLIENT, NAME + suffix), os.path.join(CLIENT, RAW + suffix)
    if os.path.exists(plain):           # fresh from bake_ltu_v3b.py; a rerun reads the raw copy
        shutil.move(plain, raw_copy)
    sizes["bakedraw"][RAW + suffix] = os.path.getsize(raw_copy)
    nqz(raw_copy, plain + ".nqz")
    sizes["baked"][NAME + suffix + ".nqz"] = os.path.getsize(plain + ".nqz")
report["bytes"] = {v: {"total": sum(s.values()), **s} for v, s in sizes.items()}

# check: the written DDS decode, and the normal convention survived compression
n_back = np.asarray(Image.open(os.path.join(CLIENT, f"{RAW}_n.dds")).convert("RGB")).astype(np.float32) / 255
inside_t = np.asarray(Image.fromarray(inside.astype(np.uint8) * 255).resize((TEX, TEX), Image.NEAREST)
                      .filter(ImageFilter.MinFilter(3))) > 0
report["normal_curl_corr_dds"] = round(curl_sign(n_back, inside_t), 3)

# preview board
tiles = [Image.fromarray(maps["c"][0]), Image.fromarray(maps["n"][0]),
         Image.fromarray(maps["mrao"][0][..., :3]), Image.fromarray(maps["e"][0])]
board = Image.new("RGB", (TEX * 2, TEX * 2))
for i, t in enumerate(tiles):
    board.paste(t.convert("RGB"), ((i % 2) * TEX, (i // 2) * TEX))
board.resize((TEX, TEX)).save(os.path.join(HERE, "renders", "baked_v1_textures.png"))
for k, (arr, _, _) in maps.items():
    Image.fromarray(arr[..., :3] if arr.shape[2] == 4 else arr).save(os.path.join(SCRATCH, f"tex_{k}.png"))

meta_path = os.path.join(HERE, "..", "assets", f"{NAME}.surfaces.json")
meta = json.load(open(meta_path))
meta["textures"] = {"albedo": "c", "normal": "n", "shading": "mrao", "emission": "e"}
meta["post"] = report
json.dump(meta, open(meta_path, "w"), indent=2)
print(json.dumps(report, indent=1))
