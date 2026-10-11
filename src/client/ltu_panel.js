// Linked Transfer Unit: client script. The server sends it through the "modinjectjs" HUD event
// (which eval()s it) twice over: once at login, so the stock industry panel of a linked unit
// shows this mod's tabs, and again when a player picks Linked Transfer Unit > Configure, with
// that unit's data in BOOT.open. __LTU_BOOT__ is replaced by the server.
//
// Two frames, one set of tabs:
// - stock: the stock industry panel (#industry_panel, gui/hud/industry_panel) of a linked unit.
//   Its sidebar gets LINKS / FILTER / STATUS entries and its own entries and sub
//   panels are hidden by one stylesheet rule keyed on .ltu_mode, so taking the class off
//   restores the stock panel untouched. Which unit the panel shows is asked of the server
//   (action 207) with the ids seen in the HUD events just before it opened.
// - replica: the same layout built by this script, for Configure from the right-click menu.
//
// Written for the HUD's older JavaScript engine: no optional chaining, no "??", no classList
// reliance. Every console line starts with [LTU]. Lines about behaviour not yet confirmed in
// game carry [unproven].
(function () {
  "use strict";
  var BOOT = __LTU_BOOT__;
  var MOD = BOOT.mod;
  var A_SAVE = 201, A_START = 202, A_STOP = 203, A_LOG = 204, A_UNPAIR = 205, A_PAIR = 206,
    A_STOCK = 207, A_HELLO = 208, A_CATALOG = 209, A_FILTER = 210, A_OUTPUT = 214, A_MOVE = 215;
  var TAG = "[LTU] ";

  // Same version already running: only open the requested panel.
  var prev = window.__ltu;
  if (prev && prev.version === BOOT.version) {
    if (BOOT.open) prev.openConfigure(BOOT.open);
    return;
  }
  if (prev && typeof prev.dispose === "function") { try { prev.dispose(); } catch (e) { } }
  // The panel of the previous script generation listened through window.__ltuPanel.
  if (window.__ltuPanel && typeof window.__ltuPanel.close === "function") { try { window.__ltuPanel.close("replaced"); } catch (e) { } }
  window.__ltuPanel = null;

  function log(msg, forward) {
    try { console.log(TAG + msg); } catch (e) { }
    if (forward) sendRaw(A_LOG, [0, 0, 0], msg);
  }
  function sendRaw(id, ids, payload) {
    try { CPPMod.sendModAction(MOD, id, ids, payload || ""); return true; }
    catch (e) { try { console.log(TAG + "CPPMod.sendModAction failed: " + e); } catch (e2) { } return false; }
  }

  // ---------------------------------------------------------------- look
  // Measured from the stock industry panel at 4K. Sizes are 4K pixels scaled by
  // k = innerHeight / 2160, in px only (vh is double-scaled in Coherent views).
  var C = {
    yellow: "rgb(250, 212, 122)", ink: "rgb(16, 13, 8)",
    tabBg: "rgb(13, 24, 28)", teal: "rgb(98, 142, 157)", tealDim: "rgba(98, 142, 157, 0.35)",
    sideTop: "rgb(36, 53, 61)", sideBottom: "rgb(27, 37, 45)", frame: "rgb(28, 37, 43)", frameHi: "rgb(38, 58, 67)",
    hint: "rgb(196, 232, 242)", sub: "rgb(161, 197, 210)", text: "rgb(226, 238, 243)", icon: "rgb(31, 58, 68)",
    black: "rgb(0, 0, 0)", row: "rgba(98, 142, 157, 0.16)", field: "rgb(6, 10, 12)", bad: "rgb(236, 128, 112)",
    good: "rgb(150, 214, 170)", title: "rgb(232, 244, 248)"
  };
  var k = 1;
  function S(v) { return Math.round(v * k); }
  function px(v) { return S(v) + "px"; }
  function css(n, style) { for (var p in style) if (Object.prototype.hasOwnProperty.call(style, p)) n.style[p] = style[p]; return n; }
  function el(parent, tag, style, text) {
    var n = document.createElement(tag);
    if (style) css(n, style);
    if (text !== undefined && text !== null) n.textContent = text;
    if (parent) parent.appendChild(n);
    return n;
  }
  function clear(n) { while (n && n.firstChild) n.removeChild(n.firstChild); }
  function cls(n) { return n && typeof n.className === "string" ? n.className : ""; }
  function hasCls(n, c) { return (" " + cls(n) + " ").indexOf(" " + c + " ") >= 0; }
  function addCls(n, c) { if (n && !hasCls(n, c)) n.className = (cls(n) + " " + c).replace(/^\s+/, ""); }
  function rmCls(n, c) { if (n) n.className = (" " + cls(n) + " ").replace(" " + c + " ", " ").replace(/^\s+|\s+$/g, ""); }
  function describe(n) {
    if (!n || !n.tagName) return "none";
    var c = cls(n).trim();
    return n.tagName.toLowerCase() + (n.id ? "#" + n.id : "") + (c ? "." + c.split(/\s+/).join(".") : "");
  }
  function cs(n) { try { return window.getComputedStyle(n); } catch (e) { return null; } }
  function fontOf(n) { var s = n ? cs(n) : null; return s && s.fontFamily ? s.fontFamily : ""; }
  function q(root, sel) { try { return (root || document).querySelector(sel); } catch (e) { return null; } }
  function plural(n, one, many) { return n + " " + (n === 1 ? one : many); }

  // Fonts: read from the stock panel's own text where it exists (the wide display face for
  // headings, the mixed-case face for body text), else the HUD font found by the Tool Wheel.
  var F = { head: "", body: "", tab: "" };
  function resolveFonts(panel) {
    var hud = "";
    var picks = [".active_container_name", ".context_menu ul li a", "#action_bar"];
    for (var i = 0; i < picks.length && !hud; i++) hud = fontOf(q(document, picks[i]));
    var p = panel || document.getElementById("industry_panel");
    var mask = q(p, ".no_recipe_mask");
    var maskHead = mask && mask.children && mask.children.length > 1 ? mask.children[1] : null;
    F.head = fontOf(maskHead) || fontOf(q(p, ".industry_panel_title")) || hud || "Bank";
    F.body = fontOf(q(p, ".context_tips")) || fontOf(q(p, ".menu_entry")) || "sans-serif";
    F.tab = fontOf(q(p, ".menu_entry")) || F.body;
  }

  // ---------------------------------------------------------------- shared state
  var R = {
    version: BOOT.version, disposed: false, view: null, timers: [], catReq: 0, catWait: {},
    catCache: {}, mouse: undefined, probed: false, events: {}, wrapped: []
  };

  // HUD events from the server, registered once per client session; they dispatch to whichever
  // script generation is current.
  if (!window.__ltuEvents) {
    window.__ltuEvents = true;
    try {
      engine.on("LinkedTransferUnit.update", function (j) { var r = window.__ltu; if (r && r.onUpdate) r.onUpdate(j); });
      engine.on("LinkedTransferUnit.stock", function (j) { var r = window.__ltu; if (r && r.onStock) r.onStock(j); });
      engine.on("LinkedTransferUnit.catalog", function (j) { var r = window.__ltu; if (r && r.onCatalog) r.onCatalog(j); });
      engine.on("HudManager.SetEnhancedMouse", function (v) { window.__ltuMouse = v; });
    } catch (e) { log("engine.on failed: " + e); }
  }
  function parse(j) { try { return typeof j === "string" ? JSON.parse(j) : j; } catch (e) { return null; } }

  // ---------------------------------------------------------------- HUD event sniffing
  // The stock panel's DOM does not say which element it shows. The HUD events that open it
  // probably do, so the latest call of each event name is kept (name, time, arguments), and
  // the ids found in those just before the panel opened go to the server, which picks the one
  // that is a linked unit. [unproven]: whether engine.trigger carries C++ events in this HUD.
  function wrapEngine() {
    if (typeof engine === "undefined") return;
    ["trigger", "_trigger"].forEach(function (name) {
      var orig = engine[name];
      if (typeof orig !== "function" || orig.__ltuWrap) return;
      var w = function () {
        var n = arguments[0];
        if (typeof n === "string" && n.indexOf("LinkedTransferUnit.") !== 0) R.events[n] = { t: Date.now(), a: arguments };
        return orig.apply(this, arguments);
      };
      w.__ltuWrap = true;
      try { engine[name] = w; } catch (e) { }
      if (engine[name] === w) R.wrapped.push({ name: name, orig: orig, w: w });
    });
  }
  function unwrapEngine() {
    R.wrapped.forEach(function (x) { try { if (engine[x.name] === x.w) engine[x.name] = x.orig; } catch (e) { } });
    R.wrapped = [];
  }
  function numbersIn(v, out, depth, budget) {
    if (out.length >= 48 || depth > 5 || budget.n-- <= 0) return;
    if (typeof v === "number") { if (v >= 1 && v % 1 === 0 && v < 9007199254740992) out.push(String(v)); return; }
    if (typeof v === "string") {
      if (/^\d{1,16}$/.test(v)) out.push(v);
      else if (v.length < 20000 && (v.charAt(0) === "{" || v.charAt(0) === "[")) { try { numbersIn(JSON.parse(v), out, depth + 1, budget); } catch (e) { } }
      return;
    }
    if (v && typeof v === "object") {
      var keys = [];
      try { keys = Object.keys(v); } catch (e) { return; }
      // Keys that look like element ids first.
      keys.sort(function (a, b) { return (/element|id$/i.test(b) ? 1 : 0) - (/element|id$/i.test(a) ? 1 : 0); });
      for (var i = 0; i < keys.length && i < 200; i++) { try { numbersIn(v[keys[i]], out, depth + 1, budget); } catch (e) { } }
    }
  }
  // Events seen in the 3 s before `at`, industry-looking names first.
  function recentEvents(at) {
    var list = [];
    for (var n in R.events) if (Object.prototype.hasOwnProperty.call(R.events, n)) {
      var e = R.events[n];
      if (e.t >= at - 3000 && e.t <= at + 500) list.push({ name: n, t: e.t, a: e.a });
    }
    list.sort(function (a, b) { return b.t - a.t; });
    var ind = list.filter(function (e) { return /industr|element|interact/i.test(e.name); });
    return ind.length ? ind.concat(list.filter(function (e) { return ind.indexOf(e) < 0; })) : list;
  }
  function candidatesFrom(events) {
    var out = [];
    events.forEach(function (e) { numbersIn(Array.prototype.slice.call(e.a, 1), out, 0, { n: 600 }); });
    var seen = {}, uniq = [];
    out.forEach(function (x) { if (!seen[x]) { seen[x] = true; uniq.push(x); } });
    return uniq.slice(0, 64);
  }

  // ---------------------------------------------------------------- stock panel watcher
  // A 250 ms poll of one element: cheaper in the HUD than a subtree MutationObserver.
  var watch = { visible: false, session: 0, panel: null };
  function stockPanel() {
    var p = document.getElementById("industry_panel");
    if (!p || !p.offsetWidth || !p.offsetHeight) return null;
    var s = cs(p);
    if (s && (s.visibility === "hidden" || parseFloat(s.opacity) === 0)) return null;
    return p;
  }
  function poll() {
    if (R.disposed) return;
    var p = stockPanel();
    if (p && !watch.visible) {
      watch.visible = true;
      watch.session++;
      watch.panel = p;
      onStockShown(p);
    } else if (!p && watch.visible) {
      watch.visible = false;
      if (R.view && R.view.kind === "stock") R.view.close("stock panel closed");
      watch.panel = null;
    }
  }
  function onStockShown(p) {
    if (R.view && R.view.kind === "replica") R.view.close("stock panel opened");
    var at = Date.now();
    var evs = recentEvents(at);
    var cands = candidatesFrom(evs);
    sendRaw(A_STOCK, [0, 0, 0], JSON.stringify({ candidates: cands, nonce: watch.session }));
    log("[unproven] stock industry panel opened; asked the server with " + cands.length + " candidate id(s) from " + evs.length + " recent HUD event(s)");
    if (BOOT.probe && !R.probed && !window.__ltuProbedV3) {
      R.probed = window.__ltuProbedV3 = true;
      setTimeout(function () { probe(p, evs, cands); }, 400);
    }
  }
  R.onStock = function (j) {
    var d = parse(j);
    if (!d || R.disposed) return;
    var p = stockPanel();
    if (!p || d.nonce !== watch.session) return;   // an answer for a panel since closed
    if (d.none) { log("stock panel is not a linked unit's"); return; }
    if (R.view && R.view.kind === "stock") { R.view.update(d); return; }
    R.view = makeView("stock", d, p);
    log("[unproven] stock panel of unit " + d.elementId + " taken over (matched by " + d.match + ")", true);
  };
  R.onUpdate = function (j) {
    var d = parse(j);
    if (!d || !R.view || d.elementId !== R.view.data.elementId) return;
    R.view.update(d);
  };
  R.openConfigure = function (d) {
    if (R.view && R.view.kind === "stock" && R.view.data.elementId === d.elementId) { R.view.update(d); R.view.select("links"); return; }
    if (R.view) R.view.close("replaced");
    R.view = makeView("replica", d, null);
  };
  R.dispose = function () {
    R.disposed = true;
    if (R.view) { try { R.view.close("script replaced"); } catch (e) { } }
    R.timers.forEach(function (t) { clearInterval(t); });
    unwrapEngine();
  };

  // ---------------------------------------------------------------- probe (once per session)
  // The stock panel's structure, its styles and the HUD events around its opening, to the
  // server log, so the injection can be checked against the real thing.
  function probe(p, evs, cands) {
    var out = [];
    (function walk(n, d) {
      if (!n || d > 6 || out.length > 120) return;
      var t = "";
      if (n.children && n.children.length === 0) t = (n.textContent || "").replace(/\s+/g, " ").trim().substring(0, 40);
      out.push(new Array(d + 1).join(" ") + describe(n) + (n.children && n.children.length ? " [" + n.children.length + "]" : "") + (t ? " \"" + t + "\"" : ""));
      for (var i = 0; n.children && i < n.children.length && i < 14; i++) walk(n.children[i], d + 1);
    })(p, 0);
    var text = out.join("\n");
    for (var part = 0; part < 4 && text.length; part++) {
      log("[probe] stock panel tree " + (part + 1) + ": \n" + text.substring(0, 3800), true);
      text = text.substring(3800);
    }
    var sels = ["#industry_panel", ".menu_fixed", ".menu_stack", ".menu_entry.selected", ".menu_entry:not(.selected)", ".industry_panel_title",
      ".panel_image", ".header_block", ".context_tips", ".close_button", ".subPanel_area", ".sub_panel_content", ".no_recipe_mask",
      ".footer_area", ".footer_area > *"];
    var st = [];
    sels.forEach(function (sel) {
      var n = q(p, sel);
      if (!n) { st.push(sel + ": none"); return; }
      var s = cs(n);
      if (!s) return;
      st.push(sel + " = " + describe(n) + " {font " + s.fontFamily + " " + s.fontSize + " " + s.fontWeight + " ls " + s.letterSpacing +
        " tt " + s.textTransform + "; color " + s.color + "; bg " + s.backgroundColor + " " + (s.backgroundImage || "").substring(0, 120) +
        "; box " + n.offsetLeft + "," + n.offsetTop + " " + n.offsetWidth + "x" + n.offsetHeight + "; pos " + s.position + " disp " + s.display + "}");
    });
    log("[probe] stock panel styles: \n" + st.join("\n").substring(0, 3900), true);
    var img = q(p, "img.panel_image"), logo = q(p, "img.layout_background_logo");
    log("[probe] images: panel_image " + (img ? img.getAttribute("src") : "none") + "; logo " + (logo ? logo.getAttribute("src") : "none") +
      "; engine wrapped " + R.wrapped.map(function (w) { return w.name; }).join(",") + "; CPPMod.raycast " + (typeof CPPMod !== "undefined" ? typeof CPPMod.raycast : "n/a"), true);
    log("[probe] HUD events in the 3 s before the panel: " + (evs.length ? evs.slice(0, 25).map(function (e) {
      var a = [];
      try { a = Array.prototype.slice.call(e.a, 1).map(function (x) { var s = typeof x === "string" ? x : JSON.stringify(x); return (s || String(x)).substring(0, 160); }); } catch (er) { }
      return e.name + "(" + a.join(", ") + ")";
    }).join(" | ").substring(0, 3600) : "none seen") + "; candidates " + cands.join(" "), true);
    try {
      var g = Object.getOwnPropertyNames(window).filter(function (n) { return /industr/i.test(n); });
      log("[probe] window globals /industr/: " + (g.length ? g.map(function (n) {
        var v; try { v = window[n]; } catch (e) { }
        var keys = ""; try { if (v && typeof v === "object") keys = Object.keys(v).slice(0, 30).join(","); } catch (e) { }
        return n + ":" + typeof v + (keys ? "{" + keys + "}" : "");
      }).join(" ").substring(0, 3600) : "none"), true);
    } catch (e) { }
  }

  // ---------------------------------------------------------------- widgets
  // The sidebar title: TRANSFER, with LINK on the line below.
  function titleLines(n) {
    clear(n);
    n.appendChild(document.createTextNode("TRANSFER"));
    n.appendChild(document.createElement("br"));
    n.appendChild(document.createTextNode("LINK"));
    return n;
  }
  function button(parent, text, kind) {
    // kind: "primary" (solid yellow, the stock selected tab) or "secondary" (the stock tab at rest).
    var primary = kind === "primary";
    var b = el(parent, "div", {
      display: "inline-block", minWidth: px(primary ? 300 : 240), height: px(84), lineHeight: px(84), padding: "0 " + px(40),
      boxSizing: "border-box", textAlign: "center", fontFamily: F.tab, fontSize: px(28), letterSpacing: px(1),
      textTransform: "uppercase", cursor: "pointer", whiteSpace: "nowrap",
      backgroundColor: primary ? C.yellow : C.tabBg, color: primary ? C.ink : C.teal,
      border: px(2) + " solid " + (primary ? C.yellow : C.frameHi)
    }, text);
    b.__primary = primary;
    b.addEventListener("mouseenter", function () { if (!b.__off) b.style.borderColor = primary ? C.text : C.teal; });
    b.addEventListener("mouseleave", function () { b.style.borderColor = primary ? C.yellow : C.frameHi; });
    b.addEventListener("mousedown", function (e) { if (e.stopPropagation) e.stopPropagation(); });
    return b;
  }
  function small(b) { return css(b, { minWidth: px(170), height: px(60), lineHeight: px(56), fontSize: px(24), padding: "0 " + px(24) }); }
  function enable(b, on) { b.__off = !on; b.style.opacity = on ? "1" : "0.38"; b.style.cursor = on ? "pointer" : "default"; }
  function onClick(b, f) { b.addEventListener("click", function (e) { if (e.stopPropagation) e.stopPropagation(); if (!b.__off) f(e); }); }
  function label(parent, text, style) {
    var n = el(parent, "div", { fontFamily: F.body, fontSize: px(24), letterSpacing: px(2), textTransform: "uppercase", color: C.teal }, text);
    if (style) css(n, style);
    return n;
  }
  function para(parent, text, style) {
    var n = el(parent, "div", { fontFamily: F.body, fontSize: px(28), lineHeight: px(40), color: C.sub }, text);
    if (style) css(n, style);
    return n;
  }
  function input(parent, maxLen) {
    var i = el(parent, "input", {
      display: "block", width: "100%", height: px(84), boxSizing: "border-box", padding: "0 " + px(26),
      fontFamily: F.body, fontSize: px(32), color: C.text, backgroundColor: C.field,
      border: px(2) + " solid " + C.frameHi, borderRadius: "0px", outline: "none"
    });
    i.type = "text";
    if (maxLen) i.maxLength = maxLen;
    // [unproven] the stock inputs presumably take text the same way.
    i.addEventListener("focus", function () { i.style.borderColor = C.teal; try { CPPInput.setCaptureText(true); } catch (e) { } });
    i.addEventListener("blur", function () { i.style.borderColor = C.frameHi; try { CPPInput.setCaptureText(false); } catch (e) { } });
    i.addEventListener("keydown", function (e) { if (e.stopPropagation) e.stopPropagation(); });
    i.addEventListener("keyup", function (e) { if (e.stopPropagation) e.stopPropagation(); });
    return i;
  }
  // The stock empty state: an outlined info mark, a wide heading, a small uppercase line.
  function emptyState(parent, head, sub, size) {
    var z = size || 1;
    var w = el(parent, "div", { textAlign: "center", padding: px(60 * z) + " " + px(40) });
    var ring = el(w, "div", {
      position: "relative", width: px(130 * z), height: px(130 * z), margin: "0 auto", boxSizing: "border-box",
      border: px(9 * z) + " solid " + C.icon, borderRadius: "50%"
    });
    el(ring, "div", { position: "absolute", left: "50%", top: px(22 * z), width: px(16 * z), height: px(16 * z), marginLeft: px(-8 * z), backgroundColor: C.icon });
    el(ring, "div", { position: "absolute", left: "50%", top: px(46 * z), width: px(16 * z), height: px(48 * z), marginLeft: px(-8 * z), backgroundColor: C.icon });
    el(w, "div", { fontFamily: F.head, fontSize: px(52 * z), letterSpacing: px(6 * z), textTransform: "uppercase", color: C.text, marginTop: px(32 * z) }, head);
    if (sub) el(w, "div", { fontFamily: F.body, fontSize: px(26 * z), textTransform: "uppercase", color: C.sub, marginTop: px(24 * z) }, sub);
    return w;
  }
  function iconImg(parent, path, size) {
    var box = el(parent, "div", {
      width: px(size), height: px(size), flexShrink: "0", backgroundColor: "rgb(10, 18, 21)", border: px(1) + " solid " + C.frame,
      boxSizing: "border-box", overflow: "hidden"
    });
    if (path) {
      var im = el(box, "img", { width: "100%", height: "100%", display: "block" });
      im.onerror = function () { im.style.visibility = "hidden"; };
      im.src = iconBase() + path;
    }
    return box;
  }
  // Icons live under coui://data/resources_generated/; follow wherever the HUD loads its own from.
  var _iconBase = null;
  function iconBase() {
    if (_iconBase) return _iconBase;
    _iconBase = "coui://data/resources_generated/";
    try {
      var imgs = document.getElementsByTagName("img");
      for (var i = 0; i < imgs.length; i++) {
        var s = imgs[i].getAttribute("src") || "", at = s.indexOf("resources_generated/");
        if (at >= 0) { _iconBase = s.substring(0, at) + "resources_generated/"; break; }
      }
    } catch (e) { }
    return _iconBase;
  }

  // ---------------------------------------------------------------- the view
  var TABS = [
    { id: "links", label: "Links", hint: function (d) { return "Moves items from its sources to its destinations, on any construct within " + (d.rangeMeters || 0) + " m"; } },
    { id: "filter", label: "Filter", hint: function () { return "Choose which items this unit moves: all of them, only the listed ones, or all but the listed ones"; } },
    { id: "status", label: "Status", hint: function () { return "What the unit found on its last check, and whose rights it uses"; } }
  ];

  function makeView(kind, data, stock) {
    k = (window.innerHeight || 2160) / 2160;
    resolveFonts(stock);
    var v = { kind: kind, data: data, tab: "links", busy: false, closed: false, nodes: [], restore: [], tabs: {}, entries: {} };
    var frame = kind === "stock" ? stockFrame(v, stock) : replicaFrame(v);
    if (!frame) { log("could not build the " + kind + " frame", true); return { kind: kind, data: data, update: function () { }, close: function () { }, select: function () { } }; }
    v.frame = frame;

    TABS.forEach(function (t) {
      var sub = frame.addTab(t);
      v.tabs[t.id] = sub;
    });
    var ctl = {
      links: linksTab(v, v.tabs.links), filter: filterTab(v, v.tabs.filter), status: statusTab(v, v.tabs.status)
    };
    v.select = function (id) {
      v.tab = id;
      frame.select(id);
      frame.setHint(TABS.filter(function (t) { return t.id === id; })[0].hint(v.data));
      if (ctl[id].shown) ctl[id].shown();
    };
    v.render = function () {
      for (var id in ctl) if (ctl.hasOwnProperty(id)) ctl[id].render(v.data);
      frame.setHint(TABS.filter(function (t) { return t.id === v.tab; })[0].hint(v.data));
    };
    v.update = function (d) {
      v.busy = false;
      var keepInputs = !d.saved;
      v.data = d;
      ctl.links.render(d, keepInputs);
      ctl.filter.render(d, !d.saved);
      ctl.status.render(d);
      v.setMsg(d.msg || "", !!d.error);
    };
    v.send = function (id, payload) {
      if (!sendRaw(id, [v.data.constructId, v.data.elementId, 0], payload)) { v.busy = false; v.setMsg("Could not reach the server.", true); return false; }
      return true;
    };
    v.setMsg = function (text, bad) {
      for (var id in ctl) if (ctl.hasOwnProperty(id) && ctl[id].footer) ctl[id].footer.msg(text, bad);
    };
    v.toggleRun = function () {
      if (v.busy || !v.data.canUse) return;
      v.busy = true;
      v.setMsg(v.data.running ? "Stopping..." : "Starting...", false);
      v.send(v.data.running ? A_STOP : A_START, "");
    };
    v.close = function (why) {
      if (v.closed) return;
      v.closed = true;
      if (v.tick) clearInterval(v.tick);
      frame.close();
      if (R.view === v) R.view = null;
      log("panel closed (" + why + ")");
    };
    // Pairing countdown, local between server updates.
    v.tick = setInterval(function () {
      var p = v.data.pairing;
      if (!p || v.closed) return;
      p.secondsLeft = Math.max(0, (p.secondsLeft || 0) - 1);
      if (p.secondsLeft <= 0) v.data.pairing = null;
      ctl.links.render(v.data, true);
    }, 1000);

    v.update(data);
    v.select("links");
    log("panel open (" + kind + ") for unit " + data.elementId + " (fonts " + F.head + " / " + F.body + ", k " + k.toFixed(3) + ")");
    return v;
  }

  // Footer bar at the bottom of each tab, where the stock panel keeps its run controls:
  // message on the left, the tab's own action, then Start / Stop on the right.
  function footerBar(v, sub, action) {
    var bar = el(sub.root, "div", {
      display: "flex", alignItems: "center", flexShrink: "0", padding: px(28) + " " + px(48),
      borderTop: px(2) + " solid " + C.frame, backgroundColor: C.black
    });
    var msg = el(bar, "div", { flexGrow: "1", fontFamily: F.body, fontSize: px(28), color: C.sub, paddingRight: px(32) });
    var act = null;
    if (action) { act = button(bar, action.text, "secondary"); act.style.marginRight = px(24); onClick(act, action.run); }
    var run = button(bar, "Start", "primary");
    onClick(run, function () { v.toggleRun(); });
    return {
      act: act,
      msg: function (t, bad) { msg.textContent = t; msg.style.color = bad ? C.bad : C.sub; },
      render: function (d) {
        run.textContent = d.running ? "Stop" : "Start";
        enable(run, !!d.canUse && !v.busy);
        run.style.display = d.canUse ? "inline-block" : "none";
      }
    };
  }
  function scrollArea(sub) {
    return el(sub.root, "div", { flexGrow: "1", overflowY: "auto", padding: px(64) + " " + px(80) + " " + px(40), boxSizing: "border-box" });
  }

  // ---------------------------------------------------------------- LINKS
  // Sources and destinations side by side. Each lists its paired elements, with Pair (action
  // 206) and Unpair (205), and under them the name prefix that adds every container whose name
  // starts with it. The server moves from the union of both. One Save for the two prefixes.
  function linksTab(v, sub) {
    var body = scrollArea(sub);
    var state = el(body, "div", { display: "flex", alignItems: "center", marginBottom: px(24) });
    var mark = el(state, "div", { width: px(28), height: px(28), marginRight: px(28), boxSizing: "border-box", flexShrink: "0" });
    var stateText = el(state, "div", { fontFamily: F.head, fontSize: px(52), letterSpacing: px(6), textTransform: "uppercase", color: C.text });
    var summary = para(body, "", { color: C.text, marginBottom: px(48), maxWidth: px(2400) });
    var banner = el(body, "div", {
      display: "none", borderLeft: px(8) + " solid " + C.yellow, backgroundColor: C.tabBg, padding: px(24) + " " + px(32),
      marginBottom: px(48), fontFamily: F.body, fontSize: px(28), lineHeight: px(40), color: C.text
    });
    var cols = el(body, "div", { display: "flex" });
    var sides = {}, outBtns = {}, outText = null;
    ["source", "destination"].forEach(function (role, i) {
      var col = el(cols, "div", { flexGrow: "1", flexBasis: "0", minWidth: "0", marginRight: i === 0 ? px(80) : "0" });
      var head = el(col, "div", { display: "flex", alignItems: "center", paddingBottom: px(20), borderBottom: px(2) + " solid " + C.frame });
      var title = el(head, "div", { flexGrow: "1", fontFamily: F.head, fontSize: px(34), letterSpacing: px(4), textTransform: "uppercase", color: C.text },
        role === "source" ? "Source" : "Destination");
      var count = el(head, "div", { fontFamily: F.body, fontSize: px(26), color: C.sub, marginRight: px(28) });
      var pb = small(button(head, "Pair", "secondary"));
      css(pb, { minWidth: px(240) });
      onClick(pb, function () {
        if (v.busy || !v.data.canEdit) return;
        v.busy = true;
        v.setMsg("Starting pairing...", false);
        v.send(A_PAIR, JSON.stringify({ role: role }));
      });
      if (role === "destination") {
        // Output mode: how the destinations share the items (action 214, saved at once).
        var orow = el(col, "div", { display: "flex", alignItems: "center", padding: px(24) + " 0 " + px(12) });
        label(orow, "Output", { marginRight: px(28), flexShrink: "0" });
        ["inorder", "roundrobin"].forEach(function (m) {
          var ob = small(button(orow, m === "inorder" ? "In order" : "Round robin", "secondary"));
          css(ob, { minWidth: px(250), marginRight: px(4) });
          onClick(ob, function () {
            if (v.busy || !v.data.canEdit || v.data.outputMode === m) return;
            v.busy = true;
            v.setMsg("Saving output...", false);
            v.send(A_OUTPUT, JSON.stringify({ mode: m }));
          });
          outBtns[m] = ob;
        });
        outText = para(col, "", { fontSize: px(23), lineHeight: px(32), paddingBottom: px(16), borderBottom: px(1) + " solid " + C.row });
      }
      var list = el(col, "div", {});
      // The secondary way in: a name prefix, smaller and below the pairs.
      var more = el(col, "div", { marginTop: px(40) });
      label(more, "Also include containers named...", { fontSize: px(22), color: C.sub, marginBottom: px(14) });
      var i2 = input(more, v.data.maxLen);
      css(i2, { height: px(68), fontSize: px(28) });
      i2.placeholder = role === "source" ? "e.g. ore-in" : "e.g. refinery";
      i2.addEventListener("keydown", function (e) { if ((e.keyCode || e.which) === 13) { e.preventDefault(); save(); } });
      para(more, role === "source"
        ? "Any container whose name starts with this, ignoring case, is a source too. Leave it empty to use only the paired ones."
        : "Any container, hub or fuel tank whose name starts with this, ignoring case, is filled too. Leave it empty to use only the paired ones.",
        { fontSize: px(23), lineHeight: px(32), marginTop: px(12) });
      sides[role] = { title: title, count: count, pb: pb, list: list, input: i2 };
    });
    var note = para(body, "", { marginTop: px(48), maxWidth: px(2400) });
    var foot = footerBar(v, sub, { text: "Save prefixes", run: save });
    function save() {
      if (v.busy || !v.data.canEdit) return;
      v.busy = true;
      v.setMsg("Saving...", false);
      v.send(A_SAVE, JSON.stringify({ source: (sides.source.input.value || "").trim(), destination: (sides.destination.input.value || "").trim() }));
    }
    function summaryOf(d) {
      var paired = (d.sources || []).length + (d.destinations || []).length;
      if (!d.route && !paired) return "Nothing linked yet. Pair a source and a destination below, or name the containers to include.";
      var s = d.sourcesFound || 0, t = d.destinationsFound || 0;
      var found = "Found " + plural(s, "source", "sources") + " and " + plural(t, "destination", "destinations") + " within " + d.rangeMeters + " m.";
      if (s === 0 || t === 0) found += " It waits until it has at least one of each.";
      if (d.deniedByRights) found += " " + plural(d.deniedByRights, "container is", "containers are") + " skipped: the owner lacks the right to use them.";
      if (d.lastMove) found += " Last move: " + d.lastMove.litres + " L at " + d.lastMove.at + ".";
      return found;
    }
    var CHIP = {
      ok: { text: "OK", color: C.good }, dormant: { text: "Dormant", color: C.teal },
      missing: { text: "Missing", color: C.bad }, repair: { text: "Re-pair needed", color: C.yellow }
    };
    function chip(parent, status) {
      var c = CHIP[status] || { text: status, color: C.sub };
      return el(parent, "div", {
        flexShrink: "0", marginLeft: px(24), height: px(44), lineHeight: px(40), padding: "0 " + px(16), boxSizing: "border-box",
        border: px(2) + " solid " + c.color, color: c.color, fontFamily: F.body, fontSize: px(22), letterSpacing: px(1),
        textTransform: "uppercase", whiteSpace: "nowrap"
      }, c.text);
    }
    return {
      footer: foot,
      render: function (d, keepInputs) {
        var running = !!d.running;
        stateText.textContent = running ? "Running" : "Stopped";
        css(mark, running ? { backgroundColor: C.yellow, border: px(3) + " solid " + C.yellow } : { backgroundColor: "transparent", border: px(3) + " solid " + C.teal });
        summary.textContent = summaryOf(d);
        if (d.pairing) {
          banner.style.display = "block";
          banner.textContent = "Pairing a " + d.pairing.role + " for " + d.pairing.secondsLeft + " s. Close this panel, right-click a container, hub or fuel tank within " +
            d.rangeMeters + " m and choose Linked Transfer Unit > Pair with transfer unit.";
        } else banner.style.display = "none";
        var om = d.outputMode === "roundrobin" ? "roundrobin" : "inorder";
        for (var m in outBtns) if (outBtns.hasOwnProperty(m)) {
          var on = om === m;
          css(outBtns[m], { backgroundColor: on ? C.yellow : C.tabBg, color: on ? C.ink : C.teal, borderColor: on ? C.yellow : C.frameHi });
          outBtns[m].__primary = on;
          enable(outBtns[m], on || (!!d.canEdit && !v.busy));
        }
        if (outText) outText.textContent = om === "roundrobin"
          ? "Each cycle is shared evenly across the destinations that still have room, starting one further along every cycle."
          : "The first destination fills before the next: paired ones top to bottom, then containers matching the name below, A to Z.";
        ["source", "destination"].forEach(function (role) {
          var b = sides[role], items = (role === "source" ? d.sources : d.destinations) || [];
          var active = d.pairing && d.pairing.role === role;
          b.count.textContent = items.length + " / " + (d.maxPairs || 0) + " paired";
          b.pb.textContent = active ? "Pairing " + d.pairing.secondsLeft + " s" : "Pair";
          css(b.pb, active ? { backgroundColor: C.yellow, color: C.ink, borderColor: C.yellow } : { backgroundColor: C.tabBg, color: C.teal, borderColor: C.frameHi });
          b.pb.style.display = d.canEdit ? "inline-block" : "none";
          enable(b.pb, !v.busy && items.length < (d.maxPairs || 0));
          if (!keepInputs) b.input.value = (role === "source" ? d.source : d.destination) || "";
          b.input.disabled = !d.canEdit;
          clear(b.list);
          if (!items.length) {
            emptyState(b.list, role === "source" ? "No sources paired" : "No destinations paired",
              role === "source" ? "Pair the containers this unit takes from" : "Pair the containers or tanks this unit fills", 0.6);
            return;
          }
          items.forEach(function (p, idx) {
            var r = el(b.list, "div", { display: "flex", alignItems: "center", padding: px(20) + " 0", borderBottom: px(1) + " solid " + C.row });
            if (role === "destination")
              el(r, "div", { flexShrink: "0", width: px(56), fontFamily: F.head, fontSize: px(30), color: C.teal }, String(idx + 1));
            var t = el(r, "div", { flexGrow: "1", minWidth: "0" });
            el(t, "div", { fontFamily: F.body, fontSize: px(30), color: C.text, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }, p.name || p.typeName);
            el(t, "div", { fontFamily: F.body, fontSize: px(24), color: p.note ? C.bad : C.sub, marginTop: px(6), whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" },
              (p.name ? p.typeName + "  ·  " : "") + (p.constructName || ("construct " + p.constructId)) + (p.note ? "  ·  " + p.note : ""));
            chip(r, p.status);
            if (d.canEdit && role === "destination" && items.length > 1) {
              [-1, 1].forEach(function (dir) {
                var mb = small(button(r, dir < 0 ? "Up" : "Down", "secondary"));
                css(mb, { minWidth: px(120), marginLeft: px(dir < 0 ? 24 : 4) });
                var can = dir < 0 ? idx > 0 : idx < items.length - 1;
                enable(mb, !v.busy && can);
                onClick(mb, function () {
                  if (v.busy || !can) return;
                  v.busy = true;
                  v.setMsg("Saving order...", false);
                  v.send(A_MOVE, JSON.stringify({ elementId: p.elementKey || String(p.elementId), dir: dir }));
                });
              });
            }
            if (d.canEdit) {
              var ub = small(button(r, "Unpair", "secondary"));
              ub.style.marginLeft = px(24);
              enable(ub, !v.busy);
              onClick(ub, function () {
                if (v.busy) return;
                v.busy = true;
                v.setMsg("Unpairing...", false);
                v.send(A_UNPAIR, JSON.stringify({ role: role, elementId: p.elementKey || String(p.elementId) }));
              });
            }
          });
        });
        var n = [];
        if (d.origin === "name") n.push("Nothing is saved yet, so the unit takes its route from its own name (" + d.route + "). Saving prefixes or pairing replaces it.");
        if (!d.requireRunning) n.push("This server runs every unit that has a route: Start and Stop only change the state shown.");
        if (!d.canEdit) n.push("You need the Element Edit right on this unit to change it. You can still view it.");
        note.textContent = n.join(" ");
        note.style.display = n.length ? "block" : "none";
        foot.act.style.display = d.canEdit ? "inline-block" : "none";
        enable(foot.act, !v.busy);
        foot.render(d);
      }
    };
  }

  // ---------------------------------------------------------------- FILTER
  // The picker browses the item bank one group at a time, or searches it; both are asked of
  // the server (action 209) and cached for the session. Edits are a local draft until Save.
  function filterTab(v, sub) {
    var body = el(sub.root, "div", { flexGrow: "1", display: "flex", flexDirection: "column", padding: px(56) + " " + px(80) + " " + px(32), boxSizing: "border-box", minHeight: "0" });
    var draft = { mode: "off", items: [], dirty: false };

    var modeRow = el(body, "div", { display: "flex", alignItems: "center", marginBottom: px(40), flexShrink: "0" });
    label(modeRow, "Mode", { marginRight: px(32) });
    var modeBtns = {};
    ["off", "whitelist", "blacklist"].forEach(function (m) {
      var b = button(modeRow, m === "off" ? "Off" : m === "whitelist" ? "Whitelist" : "Blacklist", "secondary");
      css(b, { minWidth: px(260), marginRight: px(4) });
      onClick(b, function () { if (!v.data.canEdit) return; draft.mode = m; dirty(); renderDraft(); });
      modeBtns[m] = b;
    });
    var modeText = para(modeRow, "", { marginLeft: px(40), flexGrow: "1" });

    var panes = el(body, "div", { display: "flex", flexGrow: "1", minHeight: "0" });
    // Picker
    var left = el(panes, "div", { flexGrow: "3", flexBasis: "0", display: "flex", flexDirection: "column", marginRight: px(64), minHeight: "0" });
    label(left, "Item bank", { marginBottom: px(18), flexShrink: "0" });
    var search = input(left, 64);
    search.style.flexShrink = "0";
    search.placeholder = "Search items by name";
    var crumbs = el(left, "div", { display: "flex", flexWrap: "wrap", alignItems: "center", margin: px(22) + " 0 " + px(14), flexShrink: "0", minHeight: px(40) });
    var list = el(left, "div", { flexGrow: "1", overflowY: "auto", borderTop: px(2) + " solid " + C.frame, minHeight: "0" });
    // Selected
    var right = el(panes, "div", { flexGrow: "2", flexBasis: "0", display: "flex", flexDirection: "column", minHeight: "0" });
    var selTitle = label(right, "", { marginBottom: px(18), flexShrink: "0" });
    var selHint = para(right, "", { fontSize: px(24), lineHeight: px(34), marginBottom: px(22), flexShrink: "0" });
    var selList = el(right, "div", { flexGrow: "1", overflowY: "auto", borderTop: px(2) + " solid " + C.frame, minHeight: "0" });

    var foot = footerBar(v, sub, { text: "Save filter", run: save });
    var page = null, group = "", query = "", searchTimer = null;

    function dirty() { draft.dirty = true; }
    function selected(id) { for (var i = 0; i < draft.items.length; i++) if (draft.items[i].id === id) return true; return false; }
    function add(e) {
      if (!v.data.canEdit || selected(e.id)) return;
      if (draft.items.length >= (v.data.filter ? v.data.filter.max : 200)) { v.setMsg("The filter is full (" + v.data.filter.max + " entries).", true); return; }
      draft.items.push({ id: e.id, label: e.label, icon: e.icon, group: e.group, count: e.count, path: e.path || pathLabel() });
      if (draft.mode === "off") draft.mode = "whitelist";
      dirty();
      renderDraft();
      renderPage();
    }
    function remove(id) {
      draft.items = draft.items.filter(function (x) { return x.id !== id; });
      dirty();
      renderDraft();
      renderPage();
    }
    function pathLabel() {
      if (!page || !page.path) return "";
      return page.path.slice(1).map(function (p) { return p.label; }).join(" / ");
    }
    function save() {
      if (v.busy || !v.data.canEdit) return;
      v.busy = true;
      v.setMsg("Saving filter...", false);
      v.send(A_FILTER, JSON.stringify({ mode: draft.mode, items: draft.items.map(function (x) { return x.id; }) }));
    }

    function ask(req) {
      var key = req.q !== undefined ? "q:" + req.q.toLowerCase() : "g:" + (req.group || "");
      if (R.catCache[key]) { page = R.catCache[key]; renderPage(); return; }
      var n = ++R.catReq;
      R.catWait[n] = function (d) { R.catCache[key] = d; if ((req.q !== undefined ? "q:" + query.toLowerCase() : "g:" + group) === key) { page = d; renderPage(); } };
      clear(list);
      para(list, "Loading...", { padding: px(24) + " 0" });
      req.req = n;
      sendRaw(A_CATALOG, [v.data.constructId, v.data.elementId, 0], JSON.stringify(req));
    }
    search.addEventListener("input", function () {
      if (searchTimer) clearTimeout(searchTimer);
      searchTimer = setTimeout(function () {
        query = (search.value || "").trim();
        if (query.length >= 2) ask({ q: query }); else ask({ group: group });
      }, 250);
    });

    function row(parent, e, opts) {
      var r = el(parent, "div", { display: "flex", alignItems: "center", padding: px(14) + " " + px(8), borderBottom: px(1) + " solid " + C.row, cursor: opts.open ? "pointer" : "default" });
      iconImg(r, e.icon, 72);
      var t = el(r, "div", { flexGrow: "1", minWidth: "0", marginLeft: px(26) });
      el(t, "div", { fontFamily: F.body, fontSize: px(30), color: C.text, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }, e.label);
      var subText = e.group ? "Group of " + plural(e.count, "item", "items") + (e.path ? "  ·  " + e.path : "") : (e.path || "");
      if (opts.sub) subText = opts.sub;
      if (subText) el(t, "div", { fontFamily: F.body, fontSize: px(23), color: C.sub, marginTop: px(4), whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" }, subText);
      if (opts.open) {
        el(r, "div", { fontFamily: F.body, fontSize: px(24), color: C.teal, marginLeft: px(20), marginRight: px(20), textTransform: "uppercase", letterSpacing: px(1) }, "Open");
        r.addEventListener("click", function () { opts.open(); });
        r.addEventListener("mouseenter", function () { r.style.backgroundColor = C.tabBg; });
        r.addEventListener("mouseleave", function () { r.style.backgroundColor = "transparent"; });
      }
      if (opts.action) {
        var b = small(button(r, opts.action.text, opts.action.primary ? "primary" : "secondary"));
        b.style.marginLeft = px(16);
        enable(b, opts.action.enabled !== false);
        onClick(b, opts.action.run);
      }
      return r;
    }
    function renderPage() {
      clear(crumbs);
      clear(list);
      if (!page) return;
      if (page.kind === "search") {
        el(crumbs, "div", { fontFamily: F.body, fontSize: px(26), color: C.sub }, (page.total || 0) + " result(s) for “" + page.q + "”" + (page.truncated ? ", showing the first " + (page.groups.length + page.items.length) : ""));
      } else {
        (page.path || []).forEach(function (p, i) {
          if (i > 0) el(crumbs, "div", { fontFamily: F.body, fontSize: px(26), color: C.tealDim, margin: "0 " + px(14) }, "/");
          var last = i === page.path.length - 1;
          var c = el(crumbs, "div", { fontFamily: F.body, fontSize: px(26), color: last ? C.text : C.teal, cursor: last ? "default" : "pointer" }, p.label);
          if (!last) c.addEventListener("click", function () { group = p.id; search.value = ""; query = ""; ask({ group: p.id }); });
        });
      }
      var canEdit = !!v.data.canEdit;
      (page.groups || []).forEach(function (g) {
        row(list, g, {
          open: function () { group = g.id; search.value = ""; query = ""; ask({ group: g.id }); },
          action: canEdit ? { text: selected(g.id) ? "Added" : "Add group", enabled: !selected(g.id), run: function () { add(g); } } : null
        });
      });
      (page.items || []).forEach(function (it) {
        row(list, it, { action: canEdit ? { text: selected(it.id) ? "Added" : "Add", enabled: !selected(it.id), run: function () { add(it); } } : null });
      });
      if (page.kind !== "search" && page.truncated) para(list, "Showing " + page.items.length + " of " + page.total + " items. Search to narrow it down.", { padding: px(24) + " 0" });
      if (!(page.groups || []).length && !(page.items || []).length) emptyState(list, "No items found", page.kind === "search" ? "Try another name" : "", 0.7);
    }
    function renderDraft() {
      var d = v.data, max = d.filter ? d.filter.max : 200;
      for (var m in modeBtns) if (modeBtns.hasOwnProperty(m)) {
        var on = draft.mode === m;
        css(modeBtns[m], { backgroundColor: on ? C.yellow : C.tabBg, color: on ? C.ink : C.teal, borderColor: on ? C.yellow : C.frameHi });
        modeBtns[m].__primary = on;
        enable(modeBtns[m], !!d.canEdit || on);
      }
      var n = draft.items.length;
      modeText.textContent = draft.mode === "off" ? "Every item moves. Pick items below, then choose a whitelist or a blacklist."
        : draft.mode === "whitelist" ? (n ? "Only the items listed move." : "An empty whitelist moves nothing. Add items or groups.")
        : (n ? "Every item moves except the ones listed." : "An empty blacklist lets every item through.");
      selTitle.textContent = (draft.mode === "blacklist" ? "Blacklist" : draft.mode === "whitelist" ? "Whitelist" : "Listed items") + "   " + n + " / " + max;
      selHint.textContent = "A group covers every item in it. Fuel tanks still take only their own fuel." + (draft.dirty ? "  Unsaved changes." : "");
      selHint.style.color = draft.dirty ? C.yellow : C.sub;
      clear(selList);
      if (!n) emptyState(selList, "Nothing listed", d.canEdit ? "Add items or groups from the item bank" : "", 0.7);
      draft.items.forEach(function (it) {
        row(selList, it, {
          sub: it.missing ? "No longer in the item bank" : (it.group ? "Group of " + plural(it.count, "item", "items") : "") + (it.path ? (it.group ? "  ·  " : "") + it.path : ""),
          action: d.canEdit ? { text: "Remove", run: function () { remove(it.id); } } : null
        });
      });
      foot.act.style.display = d.canEdit ? "inline-block" : "none";
      enable(foot.act, !v.busy);
    }
    return {
      footer: foot,
      shown: function () { if (!page) ask({ group: "" }); },
      render: function (d, keepDraft) {
        if (!keepDraft || !draft.dirty) {
          var f = d.filter || { mode: "off", items: [] };
          draft = { mode: f.mode || "off", items: (f.items || []).slice(), dirty: false };
        }
        renderDraft();
        foot.render(d);
      },
      setPage: function (p) { page = p; renderPage(); }
    };
  }
  R.onCatalog = function (j) {
    var d = parse(j);
    if (!d) return;
    var f = R.catWait[d.req];
    delete R.catWait[d.req];
    if (f) f(d);
  };

  // ---------------------------------------------------------------- STATUS
  function statusTab(v, sub) {
    var body = scrollArea(sub);
    var grid = el(body, "div", { maxWidth: px(2400) });
    var foot = footerBar(v, sub, null);
    function line(k2, val) {
      var r = el(grid, "div", { display: "flex", padding: px(22) + " 0", borderBottom: px(1) + " solid " + C.row });
      label(r, k2, { width: px(520), flexShrink: "0", paddingTop: px(4) });
      el(r, "div", { flexGrow: "1", fontFamily: F.body, fontSize: px(30), lineHeight: px(42), color: C.text }, val);
    }
    return {
      footer: foot,
      render: function (d) {
        clear(grid);
        line("State", d.running ? "Running" : "Stopped");
        line("Last check", d.status || "");
        line("Route", d.route ? d.route + (d.origin === "name" ? "  (from the unit's name)" : "") : "None");
        line("Paired", plural((d.sources || []).length, "source", "sources") + ", " + plural((d.destinations || []).length, "destination", "destinations"));
        line("Found", plural(d.sourcesFound || 0, "source", "sources") + ", " + plural(d.destinationsFound || 0, "destination", "destinations") + " within " + d.rangeMeters + " m");
        var rr = d.outputMode === "roundrobin", ord = d.outputOrder || [];
        line("Output", rr ? "Round robin: each cycle is shared across the destinations, starting one further along every cycle"
          : "In order: the first destination fills before the next");
        line(rr ? "Next cycle order" : "Fill order", ord.length ? ord.map(function (n, i) { return (i + 1) + ". " + n; }).join(",  ") : "No destination found");
        if (d.deniedByRights) line("Refused", plural(d.deniedByRights, "container", "containers") + ": the owner lacks the RDMS right");
        var f = d.filter || {};
        line("Filter", f.mode && f.mode !== "off" ? f.mode.charAt(0).toUpperCase() + f.mode.substring(1) + " of " + plural((f.items || []).length, "entry", "entries") : "Off");
        line("Last move", d.lastMove ? d.lastMove.litres + " L at " + d.lastMove.at : "None since the server started");
        line("Last error", d.lastError ? d.lastError.at + ": " + d.lastError.msg : "None since the server started");
        line("Acts as", d.actor ? d.actor + ", the owner of the unit's construct; containers are used only where they hold the rights" : "Not resolved");
        line("Unit", (d.unitName ? d.unitName + ",  " : "") + "element " + d.elementId + " on construct " + d.constructId);
        line("Your rights", (d.canEdit ? "Element Edit" : "No Element Edit") + ", " + (d.canUse ? "can start and stop" : "cannot start or stop"));
        if (d.match) line("Panel matched by", d.match);
        foot.render(d);
      }
    };
  }

  // ---------------------------------------------------------------- frame: stock panel
  var STYLE_ID = "ltu_stock_style";
  function stockFrame(v, p) {
    var stack = q(p, ".menu_stack"), area = q(p, ".subPanel_area");
    if (!stack || !area) { log("[unproven] stock panel without .menu_stack / .subPanel_area; left alone", true); return null; }
    var tpl = null, entries = stack.children;
    for (var i = 0; i < entries.length; i++) if (hasCls(entries[i], "menu_entry") && !hasCls(entries[i], "hide")) { tpl = entries[i]; break; }
    var entryDisplay = (tpl && cs(tpl) ? cs(tpl).display : "") || "block";
    if (entryDisplay === "none") entryDisplay = "block";
    // Where the stock sub panels sit, measured from the one on screen before it is hidden.
    var box = null, vis = null, subs = area.children;
    for (var j = 0; j < subs.length; j++) if (hasCls(subs[j], "sub_panel_content") && subs[j].offsetHeight > 0) { vis = subs[j]; break; }
    if (vis) box = { left: vis.offsetLeft, top: vis.offsetTop, width: vis.offsetWidth, height: vis.offsetHeight, parent: vis.offsetParent };
    var header = q(p, ".header_block");

    var style = document.getElementById(STYLE_ID);
    if (!style) { style = el(document.head || document.body, "style"); style.id = STYLE_ID; }
    style.textContent =
      ".ltu_mode .menu_stack .menu_entry { display: none !important; }\n" +
      ".ltu_mode .menu_stack .menu_entry.ltu_entry { display: " + entryDisplay + " !important; }\n" +
      ".ltu_mode .subPanel_area .sub_panel_content { display: none !important; }\n" +
      ".ltu_mode .subPanel_area .sub_panel_content.ltu_sub.ltu_on { display: flex !important; }\n";
    addCls(p, "ltu_mode");

    var tips = q(p, ".context_tips"), title = q(p, ".industry_panel_title");
    var saved = { tips: tips ? tips.innerHTML : null, title: title ? title.innerHTML : null };
    // The stock title element itself, so the stock stylesheet keeps its font, size, tracking
    // and colour: "INDUSTRY" becomes "TRANSFER" over "LINK".
    if (title) titleLines(title);
    var tipsText = null;
    if (tips) { tips.innerHTML = ""; tipsText = el(tips, "span", {}); }

    var areaPos = cs(area);
    if (areaPos && areaPos.position === "static" && !(box && box.parent && box.parent !== area)) { area.style.position = "relative"; saved.areaPos = true; }
    var top = header ? header.offsetTop + header.offsetHeight : S(130);
    var nodes = [], subsMade = {};
    return {
      addTab: function (t) {
        var e = el(stack, "div", {}, t.label);
        e.className = "menu_entry ltu_entry";
        e.addEventListener("click", function (ev) { if (ev.stopPropagation) ev.stopPropagation(); v.select(t.id); });
        e.addEventListener("mousedown", function (ev) { if (ev.stopPropagation) ev.stopPropagation(); });
        nodes.push(e);
        v.entries[t.id] = e;
        var s = el(area, "div", { position: "absolute", flexDirection: "column", boxSizing: "border-box", backgroundColor: C.black, overflow: "hidden" });
        s.className = "sub_panel_content ltu_sub";
        if (box && box.width > 0) css(s, { left: box.left + "px", top: box.top + "px", width: box.width + "px", height: box.height + "px" });
        else css(s, { left: "0px", right: "0px", top: top + "px", bottom: "0px" });
        s.addEventListener("contextmenu", function (ev) { ev.preventDefault(); });
        nodes.push(s);
        subsMade[t.id] = s;
        return { root: s };
      },
      select: function (id) {
        for (var t in subsMade) if (subsMade.hasOwnProperty(t)) {
          if (t === id) { addCls(subsMade[t], "ltu_on"); addCls(v.entries[t], "selected"); }
          else { rmCls(subsMade[t], "ltu_on"); rmCls(v.entries[t], "selected"); }
        }
      },
      setHint: function (text) { if (tipsText) tipsText.textContent = text; },
      close: function () {
        nodes.forEach(function (n) { try { n.parentNode.removeChild(n); } catch (e) { } });
        rmCls(p, "ltu_mode");
        if (tips && saved.tips !== null) tips.innerHTML = saved.tips;
        if (title && saved.title !== null) title.innerHTML = saved.title;
        if (saved.areaPos) area.style.position = "";
      }
    };
  }

  // ---------------------------------------------------------------- frame: replica
  // The stock panel's layout rebuilt for Configure: sidebar with the unit art, title and the
  // Novean mark (taken from the stock panel's own images when the HUD has them), stacked tabs,
  // the hint bar with its close mark, and a black content area.
  function replicaFrame(v) {
    var stock = document.getElementById("industry_panel");
    var artSrc = stock ? (q(stock, "img.panel_image") || {}).src : "";
    var logoSrc = stock ? (q(stock, "img.layout_background_logo") || {}).src : "";
    var W = 3400, H = 2110, SIDE = 506;
    var root = el(document.body, "div", {
      position: "absolute", left: "50%", top: "50%", width: px(W), height: px(H), marginLeft: px(-W / 2), marginTop: px(-H / 2),
      backgroundColor: C.frame, zIndex: "9101", boxSizing: "border-box", border: px(3) + " solid " + C.frame
    });
    root.className = "ltu_replica";
    root.addEventListener("contextmenu", function (e) { e.preventDefault(); });
    var side = el(root, "div", {
      position: "absolute", left: "0px", top: "0px", bottom: "0px", width: px(SIDE),
      backgroundImage: "linear-gradient(180deg, " + C.sideTop + ", " + C.sideBottom + ")", borderRight: px(4) + " solid " + C.frameHi
    });
    var art = el(side, "div", { position: "absolute", left: "0px", right: "0px", top: px(60), height: px(380), textAlign: "center" });
    if (artSrc) {
      var im = el(art, "img", { maxWidth: px(380), maxHeight: px(330), display: "block", margin: "0 auto" });
      im.src = artSrc;
    }
    // Styled from the stock title when the HUD has one, else from its measured look.
    var st = stock ? cs(q(stock, ".industry_panel_title")) : null;
    titleLines(el(side, "div", {
      position: "absolute", left: "0px", right: "0px", top: px(362), textAlign: "center", textTransform: "uppercase",
      fontFamily: (st && st.fontFamily) || F.head, fontSize: (st && st.fontSize) || px(33),
      letterSpacing: (st && st.letterSpacing && st.letterSpacing !== "normal") ? st.letterSpacing : px(5),
      color: (st && st.color) || C.title, lineHeight: (st && st.lineHeight && st.lineHeight !== "normal") ? st.lineHeight : "normal"
    }));
    if (logoSrc) {
      var lg = el(side, "img", { position: "absolute", left: px(60), bottom: px(30), width: px(386), opacity: "0.85" });
      lg.src = logoSrc;
    } else {
      el(side, "div", {
        position: "absolute", left: px(60), right: px(60), bottom: px(48), height: px(64), lineHeight: px(64), border: px(2) + " solid " + C.tealDim,
        fontFamily: F.head, fontSize: px(24), letterSpacing: px(6), color: C.tealDim, textAlign: "center", textTransform: "uppercase"
      }, "Novean Technology");
    }
    var stack = el(side, "div", { position: "absolute", left: px(21), width: px(466), top: px(565) });
    var head = el(root, "div", {
      position: "absolute", left: px(SIDE + 2), right: "0px", top: px(12), height: px(105), backgroundColor: C.black, display: "flex", alignItems: "center",
      borderBottom: px(10) + " solid " + C.frame
    });
    var hint = el(head, "div", { flexGrow: "1", paddingLeft: px(28), fontFamily: F.body, fontSize: px(26), color: C.hint, whiteSpace: "nowrap", overflow: "hidden", textOverflow: "ellipsis" });
    var x = el(head, "div", { position: "relative", width: px(112), height: "100%", borderLeft: px(4) + " solid " + C.frame, cursor: "pointer", flexShrink: "0" });
    [45, -45].forEach(function (deg) {
      el(x, "div", { position: "absolute", left: "50%", top: "50%", width: px(46), height: px(4), marginLeft: px(-23), marginTop: px(-2), backgroundColor: C.hint, transform: "rotate(" + deg + "deg)" });
    });
    x.addEventListener("click", function () { v.close("close button"); });
    var area = el(root, "div", { position: "absolute", left: px(SIDE + 2), right: "0px", top: px(127), bottom: "0px", backgroundColor: C.black });

    var subs = {};
    function entryStyle(n, on) {
      css(n, {
        height: px(107), lineHeight: px(107), marginBottom: px(5), paddingRight: px(48), textAlign: "right", fontFamily: F.tab, fontSize: px(29),
        fontWeight: "bold", letterSpacing: px(1), textTransform: "uppercase", cursor: "pointer",
        backgroundColor: on ? C.yellow : C.tabBg, color: on ? C.ink : C.teal
      });
    }
    // Cursor and HUD page, as the earlier panel did: a stock MousePage, plus
    // CPPHud.toggleEnhancedMouse when the cursor is off.
    var page = null, toggled = false;
    if (typeof MousePage === "function") {
      try {
        var LtuPage = class extends MousePage {
          constructor() { super(); this.wrapperNode = root; }
          _onVisibilityChange() { super._onVisibilityChange(); if (!this.isVisible && !v.closed) v.close("hidden by the HUD"); }
          _close() { v.close("HUD close"); }
        };
        page = new LtuPage();
        page.show(true);
      } catch (e) { log("MousePage subclass failed: " + e); }
    }
    function mouseOn() {
      if (window.__ltuMouse === true) return true;
      if (window.__ltuMouse === undefined && window.__toolWheel && window.__toolWheel.mouseMode === true) return true;
      return false;
    }
    setTimeout(function () {
      if (v.closed || mouseOn()) return;
      try { CPPHud.toggleEnhancedMouse(); toggled = true; } catch (e) { log("cursor: toggleEnhancedMouse failed: " + e); }
    }, 120);
    function onKey(e) { if ((e.keyCode || e.which) === 27 && !v.closed) { e.preventDefault(); v.close("escape"); } }
    document.addEventListener("keydown", onKey, true);

    return {
      addTab: function (t) {
        var e = el(stack, "div", {}, t.label);
        e.className = "menu_entry ltu_entry";
        entryStyle(e, false);
        e.addEventListener("click", function () { v.select(t.id); });
        v.entries[t.id] = e;
        var s = el(area, "div", { position: "absolute", left: "0px", top: "0px", right: "0px", bottom: "0px", display: "none", flexDirection: "column" });
        s.className = "ltu_sub";
        subs[t.id] = s;
        return { root: s };
      },
      select: function (id) {
        for (var t in subs) if (subs.hasOwnProperty(t)) {
          subs[t].style.display = t === id ? "flex" : "none";
          entryStyle(v.entries[t], t === id);
        }
      },
      setHint: function (text) { hint.textContent = text; },
      close: function () {
        document.removeEventListener("keydown", onKey, true);
        if (page) { try { page.show(false); } catch (e) { } }
        if (toggled && mouseOn()) { try { CPPHud.toggleEnhancedMouse(); } catch (e) { } }
        try { root.parentNode.removeChild(root); } catch (e) { }
      }
    };
  }

  // ---------------------------------------------------------------- start
  window.__ltu = R;
  if (BOOT.inject) {
    wrapEngine();
    R.timers.push(setInterval(poll, 250));
  }
  sendRaw(A_HELLO, [0, 0, 0], BOOT.version);
  if (BOOT.open) R.openConfigure(BOOT.open);
  log("script " + BOOT.version + " loaded (stock panel tabs " + (BOOT.inject ? "on" : "off") + ")");
})();
