"""Render the client script (src/client/ltu_panel.js) taking over a stock industry panel.

Headless Chromium at 3840x2160. The page holds a stand-in for the stock panel, built from the
structure the in-game probe logged (div#industry_panel > .menu_fixed / .subPanel_area, ...)
and styled after a 4K screenshot of the real panel; the real stylesheet is packed in the
client and not readable here. "Play" stands in for the panel's mixed-case face and "Michroma"
for its wide display face. The sidebar art is cropped from that screenshot when it is given.

The script itself runs unmodified, compacted as the server sends it, with the HUD globals
(engine, CPPMod, CPPHud) stubbed and the server's replies (actions 207 and 209) answered from
mockups/catalog_fixture.json. Item icons load from the local client install.

    python render_stock.py [stock_screenshot.png]
    # writes ltu_stock_links.png, ltu_stock_filter.png, ltu_stock_status.png
"""
import json
import pathlib
import sys

from playwright.sync_api import sync_playwright

HERE = pathlib.Path(__file__).resolve().parent
SCRIPT = HERE.parent / "src" / "client" / "ltu_panel.js"
FIXTURE = HERE / "catalog_fixture.json"
BACKDROP = HERE.parent.parent / "ToolWheel" / "mockups" / "assets" / "backdrop.jpg"
CLIENT = pathlib.Path(r"C:\ProgramData\My Dual Universe\Game\data\resources_generated")
SHOT = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else None

# Screenshot of the stock panel (2000x1253, panel 1223 px tall) -> 4K panel 2110 px tall.
SCALE = 2110 / 1223


def crops(tmp):
    """Sidebar art and Novean mark cut from the stock screenshot, or None."""
    if not SHOT or not SHOT.exists():
        return None, None
    from PIL import Image
    im = Image.open(SHOT).convert("RGB")
    art = im.crop((48, 72, 252, 212))
    logo = im.crop((40, 1100, 262, 1218))
    out = []
    for name, c in (("art", art), ("logo", logo)):
        c = c.resize((round(c.width * SCALE), round(c.height * SCALE)), Image.LANCZOS)
        p = tmp / f"_stock_{name}.png"
        c.save(p)
        out.append(p.as_uri())
    return out[0], out[1]


PAGE = """<!DOCTYPE html><html><head><meta charset="utf-8">
<link href="https://fonts.googleapis.com/css2?family=Play:wght@400;700&family=Michroma&display=block" rel="stylesheet">
<style>
 html, body { margin: 0; width: 3840px; height: 2160px; overflow: hidden; background: #000; }
 #bg { position: absolute; inset: 0; background: url(%(bg)s) center / cover; filter: brightness(0.55); }
 .active_container_name { position: absolute; opacity: 0; font-family: "Michroma"; }
 .hide { display: none !important; }
 #industry_panel { position: absolute; left: 220px; top: 25px; width: 3400px; height: 2110px; background: rgb(28, 37, 43);
   box-sizing: border-box; border: 4px solid rgb(28, 37, 43); display: none; }
 #industry_panel .layout_background_logo { position: absolute; left: 34px; bottom: 16px; width: 382px; z-index: 2; }
 #industry_panel .menu_fixed { position: absolute; left: 0; top: 0; bottom: 0; width: 506px;
   background: linear-gradient(180deg, rgb(36, 53, 61), rgb(30, 43, 51) 55%%, rgb(27, 37, 45)); }
 #industry_panel .image { position: absolute; left: 0; right: 0; top: 0; height: 450px; }
 #industry_panel .panel_image { position: absolute; left: 70px; top: 117px; width: 352px; }
 #industry_panel .industry_panel_title { position: absolute; left: 0; right: 0; top: 362px; text-align: center;
   font: 33px "Michroma"; letter-spacing: 5px; color: rgb(232, 244, 248); text-transform: uppercase; }
 #industry_panel .menu_stack { position: absolute; left: 21px; top: 565px; width: 466px; }
 #industry_panel .menu_entry { height: 107px; line-height: 107px; margin-bottom: 5px; padding-right: 48px; text-align: right;
   background: rgb(13, 24, 28); color: rgb(98, 142, 157); font: 700 29px "Play"; text-transform: uppercase; cursor: pointer; }
 #industry_panel .menu_entry.selected { background: rgb(250, 212, 122); color: rgb(16, 13, 8); }
 #industry_panel .subPanel_area { position: absolute; left: 510px; right: 0; top: 0; bottom: 0; }
 #industry_panel .header_block { position: absolute; left: 0; right: 0; top: 10px; height: 105px; background: #000;
   border-bottom: 10px solid rgb(28, 37, 43); display: flex; }
 #industry_panel .context_tips { flex: 1; padding-left: 28px; line-height: 105px; font: 26px "Play"; color: rgb(196, 232, 242); }
 #industry_panel .close_button { position: relative; width: 112px; border-left: 4px solid rgb(28, 37, 43); }
 #industry_panel .close_button i { position: absolute; left: 33px; top: 50px; width: 46px; height: 4px; background: rgb(196, 232, 242); }
 #industry_panel .sub_panel_content { position: absolute; left: 0; right: 0; top: 125px; bottom: 0; background: #000; }
 #industry_panel .no_recipe_mask { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; }
 #industry_panel .no_recipe_mask .ring { width: 130px; height: 130px; border: 9px solid rgb(31, 58, 68); border-radius: 50%%; box-sizing: border-box; }
 #industry_panel .no_recipe_mask .title { font: 52px "Michroma"; letter-spacing: 6px; color: #fff; margin-top: 32px; }
 #industry_panel .no_recipe_mask .subtitle { font: 26px "Play"; color: rgb(161, 197, 210); margin-top: 24px; }
</style></head><body><div id="bg"></div><div class="active_container_name">x</div>
<img src="%(iconprobe)s" style="display:none">
<div id="industry_panel" class="industry_panel">
 <img class="layout_background_logo" src="%(logo)s">
 <div class="menu_fixed">
  <div class="image"><img class="panel_image" src="%(art)s"><div class="industry_panel_title">Industry</div></div>
  <div class="menu_stack">
   <div class="menu_entry selected">Production</div><div class="menu_entry">Configuration</div><div class="menu_entry">Containers</div>
   <div class="menu_entry recipe_bank_menu hide"><span>Recipes</span></div>
  </div>
 </div>
 <div class="subPanel_area">
  <div class="header_block"><div class="context_tips"><span>This is the Industry Unit's control panel, producing batches of the item you configured</span></div>
   <div id="industryPanel_closeButton" class="close_button"><i style="transform: rotate(45deg)"></i><i style="transform: rotate(-45deg)"></i></div></div>
  <div id="industryPanel_productionSubPanel_wrapper" class="sub_panel_content sub_panel_industry sub_panel_production">
   <div id="industryPanel_productionSubPanel_noRecipeMask" class="no_recipe_mask"><div class="ring"></div>
    <div class="title">NO ITEM SELECTED</div><div class="subtitle">PLEASE SELECT ONE FROM THE 'CONFIGURATION' TAB</div></div>
  </div>
  <div id="industryPanel_recipeSubPanel_wrapper" class="hide sub_panel_content sub_panel_industry sub_panel_recipes"></div>
  <div id="industryPanel_containersSubPanel_wrapper" class="hide sub_panel_content sub_panel_industry sub_panel_container"></div>
 </div>
</div></body></html>"""

STUBS = """
window.__logs = []; window.__sent = [];
var _h = {};
window.engine = {
  on: function (n, f) { (_h[n] = _h[n] || []).push(f); },
  trigger: function (n) { var a = Array.prototype.slice.call(arguments, 1); (_h[n] || []).forEach(function (f) { f.apply(null, a); }); }
};
window.CPPHud = { toggleEnhancedMouse: function () { engine.trigger("HudManager.SetEnhancedMouse", true); } };
window.CPPInput = { setCaptureText: function () { } };
window.CPPMod = { sendModAction: function (m, id, a, p) { __sent.push([m, id, a, p]); if (window.__server) setTimeout(function () { __server(id, a, p); }, 30); } };
var _log = console.log; console.log = function (s) { __logs.push(String(s)); _log.apply(console, arguments); };
"""

# The server side, enough for the shots: 207 -> the unit's data, 209 -> catalogue pages.
SERVER = """
(function () {
  var CAT = __CATALOG__, UNIT = __UNIT__;
  var byName = {}, byId = {}, kids = {};
  CAT.forEach(function (n) { byName[n.name] = n; byId[n.id] = n; });
  CAT.forEach(function (n) { var p = byName[n.parent]; if (p) (kids[p.id] = kids[p.id] || []).push(n); });
  function count(n) { var c = kids[n.id]; if (!c) return 1; var s = 0; c.forEach(function (x) { s += count(x); }); return s; }
  function icon(n) { if (n.icon) return n.icon; var c = (kids[n.id] || []).slice().sort(cmp); for (var i = 0; i < c.length; i++) { var x = icon(c[i]); if (x) return x; } return ""; }
  function cmp(a, b) { var ga = !!kids[a.id], gb = !!kids[b.id]; if (ga !== gb) return ga ? -1 : 1; return a.label.toLowerCase() < b.label.toLowerCase() ? -1 : 1; }
  function entry(n) { return { id: n.id, label: n.label, icon: icon(n), group: !!kids[n.id], count: count(n) }; }
  var root = byName.BaseItem;
  function path(n) { var p = []; while (n) { p.unshift(entry(n)); if (n === root) break; n = byName[n.parent]; } return p; }
  function pathLabel(n) { return path(n).slice(1, -1).map(function (e) { return e.label; }).join(" / "); }
  window.__server = function (id, ids, payload) {
    var p = {}; try { p = JSON.parse(payload || "{}"); } catch (e) { }
    if (id === 207) { var d = JSON.parse(JSON.stringify(UNIT)); d.nonce = p.nonce; engine.trigger("LinkedTransferUnit.stock", JSON.stringify(d)); }
    if (id === 209) {
      var g = byId[p.group] || root, r = { req: p.req, kind: "browse", q: "", group: entry(g), path: path(g), groups: [], items: [], total: 0, truncated: false };
      (kids[g.id] || []).filter(function (n) { return !n.hidden && n.name !== "DataItem"; }).sort(cmp).forEach(function (n) {
        if (kids[n.id]) r.groups.push(entry(n)); else { r.total++; r.items.push(entry(n)); }
      });
      engine.trigger("LinkedTransferUnit.catalog", JSON.stringify(r));
    }
  };
})();
"""


def fixture_unit(cat):
    ids = {n["name"]: n for n in cat}

    def entry(name, path):
        n = ids[name]
        return {"id": n["id"], "label": n["label"], "icon": n["icon"] or "", "group": False, "count": 1, "path": path}

    pure = dict(entry("PureMaterial", "Materials / Refined Materials"), group=True, count=28,
                icon="env/voxel/ingots/aluminium-ingot/icons/env_aluminium-ingot_icon.png")
    return {
        "version": 2, "mod": "LinkedTransferUnit", "elementId": 322708, "constructId": 1000007, "unitName": "Ore feed",
        "source": "ore-in", "destination": "refinery", "origin": "panel", "route": "ore-in > refinery",
        "running": True, "requireRunning": True,
        "status": "running: route \"ore-in > refinery\" + 2 paired source(s), 1 paired destination(s), filter: whitelist of 3, moved 812.5 L last cycle",
        "canEdit": True, "canUse": True, "maxLen": 64, "maxPairs": 16, "rangeMeters": 1000, "probe": False,
        "pairing": None,
        "sources": [
            {"role": "source", "elementId": 322711, "elementKey": "322711", "constructId": 1000007, "constructName": "Sanc Factory",
             "localId": 41, "name": "ore-in", "typeName": "Container M", "status": "ok", "note": ""},
            {"role": "source", "elementId": 321971, "elementKey": "321971", "constructId": 1000008, "constructName": "Hauler Mk2",
             "localId": 7, "name": "", "typeName": "Container Hub", "status": "dormant", "note": ""},
            {"role": "source", "elementId": 320455, "elementKey": "320455", "constructId": 1000011, "constructName": "Outpost Kappa",
             "localId": 19, "name": "kappa-ore", "typeName": "Container L", "status": "repair", "note": ""},
        ],
        "destinations": [
            {"role": "destination", "elementId": 322013, "elementKey": "322013", "constructId": 1000007, "constructName": "Sanc Factory",
             "localId": 52, "name": "refinery-in", "typeName": "Container Hub", "status": "ok", "note": ""},
            {"role": "destination", "elementId": 322090, "elementKey": "322090", "constructId": 1000007, "constructName": "Sanc Factory",
             "localId": 60, "name": "smelter-buffer", "typeName": "Container S", "status": "ok", "note": "out of range"},
            {"role": "destination", "elementId": 319877, "elementKey": "319877", "constructId": 1000009, "constructName": "Dock 2",
             "localId": 4, "name": "dock-fuel", "typeName": "Space Fuel Tank M", "status": "missing", "note": ""},
        ],
        "filter": {"mode": "whitelist", "max": 200, "items": [
            entry("AluminiumOre", "Materials / Minable materials / Ore / Tier 1 Ores"),
            entry("CarbonOre", "Materials / Minable materials / Ore / Tier 1 Ores"),
            pure,
        ]},
        "outputMode": "roundrobin", "outputOrder": ["refinery-in", "refinery-overflow", "refinery-spare"],
        "actor": "player 10000", "sourcesFound": 3, "destinationsFound": 1, "deniedByRights": 0,
        "lastMove": {"litres": 812.5, "at": "01:22:40 UTC"},
        "msg": "", "error": False, "saved": False, "match": "hud event",
    }


AUDIT = """
() => {
  var out = [], sub = Array.prototype.filter.call(document.querySelectorAll('.ltu_sub'), function (s) { return s.offsetHeight > 0; })[0];
  if (!sub) return ['no visible tab'];
  var sb = sub.getBoundingClientRect();
  var imgs = sub.querySelectorAll('img'), broken = 0;
  for (var i = 0; i < imgs.length; i++) if (!imgs[i].naturalWidth) broken++;
  if (broken) out.push(broken + ' of ' + imgs.length + ' icon(s) failed to load');
  sub.querySelectorAll('*').forEach(function (n) {
    if (!n.offsetWidth) return;
    var r = n.getBoundingClientRect(), t = (n.textContent || '').substring(0, 50);
    var clipped = false;
    for (var a = n.parentNode; a && a !== sub; a = a.parentNode) if (getComputedStyle(a).overflowY === 'auto') { clipped = true; break; }
    if (!clipped && (r.right > sb.right + 1 || r.bottom > sb.bottom + 1 || r.left < sb.left - 1)) out.push('outside tab: ' + n.tagName + ' "' + t + '"');
    var cs = getComputedStyle(n);
    if (n.children.length === 0 && cs.textOverflow !== 'ellipsis' && n.scrollWidth > n.clientWidth + 2 && cs.overflow !== 'visible') out.push('text overflow: "' + t + '"');
    if (n.children.length === 0 && n.textContent && n.scrollWidth > n.clientWidth + 2 && cs.whiteSpace === 'nowrap' && cs.textOverflow !== 'ellipsis') out.push('nowrap overflow: "' + t + '" ' + n.scrollWidth + '>' + n.clientWidth);
  });
  return out.length ? out : ['clean'];
}
"""


def save_png(data, name):
    """Write a shot, retrying while another process (a viewer, a scanner) holds the file."""
    import time
    for i in range(20):
        try:
            (HERE / name).write_bytes(data)
            return
        except OSError:
            time.sleep(0.5)
    raise OSError("could not write " + name)


def compact(js):
    # Same as MyDuMod.Initialize: trim, drop blank and whole-line comment lines.
    return "\n".join(l.strip() for l in js.split("\n") if l.strip() and not l.strip().startswith("//"))


def main():
    cat = json.loads(FIXTURE.read_text(encoding="utf-8"))
    unit = fixture_unit(cat)
    tmp = HERE
    art, logo = crops(tmp)
    probe_icon = (CLIENT / "iconsLib" / "misclib" / "x.png").as_uri()
    html = HERE / "_stock_page.html"
    html.write_text(PAGE % {"bg": BACKDROP.as_uri(), "art": art or "", "logo": logo or "", "iconprobe": probe_icon}, encoding="utf-8")
    boot = {"mod": "LinkedTransferUnit", "version": "mock", "probe": False, "inject": True, "open": None}
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=True, args=["--allow-file-access-from-files"])
        page = browser.new_page(viewport={"width": 3840, "height": 2160}, device_scale_factor=1)
        page.goto(html.as_uri())
        page.wait_for_load_state("networkidle")
        page.evaluate("Promise.all([document.fonts.load('40px Play'), document.fonts.load('700 40px Play'), document.fonts.load('40px Michroma')])")
        page.evaluate(STUBS)
        page.evaluate(SERVER.replace("__CATALOG__", json.dumps(cat)).replace("__UNIT__", json.dumps(unit)))
        page.evaluate("src => { (0, eval)(src); }", compact(SCRIPT.read_text(encoding="utf-8")).replace("__LTU_BOOT__", json.dumps(boot)))
        # The HUD opens the panel: an event naming the element, then the panel shows.
        page.evaluate("engine.trigger('IndustryPanel.show', JSON.stringify({ elementId: 322708, constructId: 1000007 }))")
        page.evaluate("document.getElementById('industry_panel').style.display = 'block'")
        page.wait_for_timeout(900)
        page.mouse.move(3700, 2100)

        def tab(name):
            page.evaluate("n => { var e = Array.prototype.filter.call(document.querySelectorAll('.ltu_entry'), x => x.textContent === n)[0]; e.click(); }", name)
            page.wait_for_timeout(300)

        def click_text(text):
            page.evaluate("t => { var r = Array.prototype.filter.call(document.querySelectorAll('.ltu_sub div'), x => x.firstChild && x.firstChild.nodeType === 3 && x.textContent === t)[0]; (r.parentNode.parentNode).click(); }", text)
            page.wait_for_timeout(300)

        audits = {}
        # Links, with a pairing window open for a destination.
        page.evaluate("d => { d.pairing = { role: 'destination', secondsLeft: 48 }; d.nonce = undefined; engine.trigger('LinkedTransferUnit.update', JSON.stringify(d)); }", unit)
        page.wait_for_timeout(300)
        save_png(page.screenshot(), "ltu_stock_links.png")
        audits["links"] = page.evaluate(AUDIT)
        fit = page.evaluate("""() => {
          var t = document.querySelector('.industry_panel_title').getBoundingClientRect(), m = document.querySelector('.menu_stack').getBoundingClientRect();
          var sc = Array.prototype.filter.call(document.querySelectorAll('.ltu_sub > div'), function (x) { return x.offsetHeight > 0 && getComputedStyle(x).overflowY === 'auto'; })[0];
          return 'title ' + Math.round(t.top) + '-' + Math.round(t.bottom) + ' (tabs start ' + Math.round(m.top) + '); links content ' + (sc ? sc.scrollHeight + ' in ' + sc.clientHeight : '?');
        }""")
        page.evaluate("d => { engine.trigger('LinkedTransferUnit.update', JSON.stringify(d)); }", unit)
        tab("Filter")
        page.wait_for_timeout(300)
        for g in ("Consumables", "Materials", "Minable materials", "Ore", "Tier 1 Ores"):
            click_text(g)
        page.mouse.move(3700, 2100)
        page.wait_for_timeout(300)
        save_png(page.screenshot(), "ltu_stock_filter.png")
        audits["filter"] = page.evaluate(AUDIT)
        tab("Status")
        save_png(page.screenshot(), "ltu_stock_status.png")
        audits["status"] = page.evaluate(AUDIT)
        title = page.evaluate("document.querySelector('.industry_panel_title').innerText")
        logs = page.evaluate("__logs")
        sent = page.evaluate("__sent")
        browser.close()
    html.unlink(missing_ok=True)
    for f in tmp.glob("_stock_*.png"):
        f.unlink()
    for l in logs:
        print("   ", l[:200])
    for k2, v2 in audits.items():
        print("    audit", k2, v2[:12])
    print("    sent:", [(s[1], (s[3] or "")[:80]) for s in sent])
    print("    sidebar title:", repr(title), "|", fit)
    print("wrote ltu_stock_links.png, ltu_stock_filter.png, ltu_stock_status.png")


if __name__ == "__main__":
    main()
