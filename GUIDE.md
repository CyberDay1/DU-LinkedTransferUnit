# Linked Transfer Unit (myDU server mod)

A transfer unit that needs no links. Pair it with specific source and
destination elements (containers, hubs, fuel tanks) on any construct within
range (1 km by default), or give it name prefixes, and press Start. While it
runs, every cycle it moves items from its sources into its destinations. The
stock Link tool cannot do this: links belong to one construct.

Each unit can also carry one item filter, a whitelist or a blacklist of items
and item groups, so it moves only what you want.

It is an embedded DLL mod (`ModLinkedTransferUnit.dll`) for the myDU server's
Orleans service. It needs no client changes: the item reuses the stock Transfer
Unit's model, and its settings appear as tabs in the unit's own industry panel
(through the HUD's mod script event, so players need `game.enableModJS: true`).

## Install (server admin)

1. Build it, or take a prebuilt `ModLinkedTransferUnit.dll` (see *Build*).
2. Copy `ModLinkedTransferUnit.dll` into the `Mods` folder at the root of your
   myDU server install. That folder is mounted into the `orleans` container as
   `/OrleansGrains/Mods`.
3. Optional: copy `ModLinkedTransferUnit.json` next to it and edit it. Without
   it, the defaults below apply.
4. Restart the stack (`scripts/down` then `scripts/up`, or at least restart
   `orleans`, then `front` and `node` so they pick up the new item).

On first boot the mod adds an item `LinkedTransferUnit` to the item bank, as a
child of `TransferUnit` with `assetAlias: TransferUnit`. Expected log lines in
`logs/Grains_dev.log`:

```
INF MyDuMod] LinkedTransferUnit: initializing, config from /OrleansGrains/Mods/ModLinkedTransferUnit.json
INF MyDuMod] LinkedTransferUnit: created item LinkedTransferUnit under TransferUnit (id ...)
INF MyDuMod] LinkedTransferUnit: 0 unit config(s) loaded from /OrleansGrains/Mods/LinkedTransferUnit/units.json
INF MyDuMod] LinkedTransferUnit: stock industry Start/Stop hooked, Status patched
INF MyDuMod] LinkedTransferUnit: running state required (units move items only while RUNNING), name routes allowed when no prefixes are saved
INF ModManagerGrain] Mod /OrleansGrains/Mods/ModLinkedTransferUnit.dll initialized
INF MyDuMod] LinkedTransferUnit: transfer loop started (cycle 5s, max range 1000 m, cap 1000 L/unit/cycle, 16 pairs per role)
INF MyDuMod] LinkedTransferUnit: heartbeat cycle 1: 0 unit(s) placed, 0 processed this cycle
INF MyDuMod] LinkedTransferUnit: item catalogue built: 3164 items in 367 groups under BaseItem (5587 definitions in the bank; above the root: GameplayObject > InvalidItem (loops back into the tree))
```

The catalogue counts depend on the item bank (these are the local server's). The
live bank parents GameplayObject under `InvalidItem`, inside BaseItem's own
subtree; the catalogue visits every node once and skips the root's parent.
When a player connects:

```
INF MyDuMod] LinkedTransferUnit: injected panel script v<hash> into player <id> (login, attempt 1 at +3s)
INF MyDuMod] LinkedTransferUnit: player <id> loaded panel script <hash>
```

On later boots the second line reads `item LinkedTransferUnit present in item bank`.

If you would rather add the item yourself, set `CreateItemIfMissing` to
`false` and add it in the backoffice Item Hierarchy ("Add a new child" on
`TransferUnit`, name `LinkedTransferUnit`, set `assetAlias` to `TransferUnit`).

The mod adds no recipe. Give units to players from the backoffice, or add a
recipe for `LinkedTransferUnit` in your item bank.

## Use (players)

1. Place a Linked Transfer Unit anywhere on a construct you own.
2. Open the unit as you would any industry unit. Its panel shows three tabs in
   place of Production / Configuration / Containers: **LINKS**, **FILTER** and
   **STATUS** (see *The panel*). Right-click **Linked Transfer Unit >
   Configure** opens the same panel.
3. Pick its sources and destinations on the LINKS tab: pair specific elements
   (**Pair** under SOURCE or DESTINATION, see *Pairing* below), name a prefix,
   or both.
   For prefixes, fill in **Also include containers named...** under SOURCE
   and/or DESTINATION (for example `outbound-cargo` and `ship-fuel`) and press
   **Save prefixes**. The unit's own name is not touched. Then rename the
   containers. A prefix matches any container whose name **starts with** it,
   ignoring case: `outbound-cargo` matches `outbound-cargo`, `outbound-cargo-2`
   and `Outbound-Cargo B`.
4. Optional: limit what it moves on the FILTER tab (see *Item filter*).
5. Press **Start** at the bottom right of any tab. Within 5 seconds items start
   moving. **Stop** pauses it. The STATUS tab, and **Linked Transfer Unit >
   Status** in the right-click menu, show running or stopped, the route, what it
   found and why it is idle.

### Pairing

1. Right-click the unit and choose **Linked Transfer Unit > Pair source** or
   **Pair destination**. The unit waits 60 s (`PairingWindowSeconds`) and a HUD
   message says so. The **Pair** button under SOURCE or DESTINATION on the
   LINKS tab does the same, and counts the window down.
2. Close the panel if it is open. Right-click a container, hub or fuel tank, on any construct within range,
   and choose **Linked Transfer Unit > Pair with transfer unit**. The HUD
   message confirms it (`paired ore-in on Sanc Factory as source 3/16`).
3. The 60 s restart after each pair, so you can pair several in a row.

A pair is refused, with the reason in the HUD message, when the element is
not a container, hub or fuel tank, is not on a construct, is out of range, is
already paired in that role, or the unit already has `MaxPairsPerRole` (16)
of that role. You need RDMS **Element Edit** on the unit, plus **Container
Retrieve** on a source or **Container Put** on a destination. Those are your
own rights at pairing time; while running, the unit still checks its owner's
rights every cycle (see *Rules*), so pairing grants nothing.

The LINKS tab lists the paired sources and destinations with name, type,
construct and a status chip, each with an **Unpair** button:

| Status | Meaning |
|---|---|
| OK | Resolved this cycle. A note says why it is skipped anyway: out of range, broken (0 hit points), or the owner lacks the RDMS right. |
| Dormant | The element is in an inventory, or its construct is not live. Retried every cycle. |
| Missing | The element is gone and nothing matches it yet. Retried every `PairRetrySeconds`. |
| Re-pair needed | The element is gone and the candidates are ambiguous (several redeployed copies, or a different name). Unpair it and pair again. |

Paired elements and prefix matches are combined: a unit with a source prefix
and two paired destinations takes from every matching container and fills the
two destinations. Either prefix may be left empty.

Upgrading from the name-only version: units placed before it start out
STOPPED and move nothing until someone presses Start. Their name routes keep
working once started.

The panel needs `game.enableModJS: true` in the client's `settings.yaml`, the
same setting the Tool Wheel needs.

### Item filter

One filter per unit, on the FILTER tab:

- **Mode:** **Off** (every item moves), **Whitelist** (only the listed items
  move) or **Blacklist** (every item moves except the listed ones). An empty
  whitelist moves nothing; an empty blacklist lets everything through. Adding
  the first entry while the mode is Off switches it to Whitelist.
- **Entries:** items, or item groups from the item bank's own hierarchy
  ("Ore", "Pure", "Fuels", "Tier 1 Ores" and so on). A group covers every item
  under it, so one entry can cover a whole family.
- **Picker:** the left pane browses the item bank one group at a time
  (breadcrumb at the top; click a group to go in, **Add group** to list the
  whole group) or searches it by name (two letters or more; every word must
  match the display name or the item-bank name). Each row shows the game's own
  icon. **Add** puts an item on the list.
- **List:** the right pane shows the entries with their group path and a
  **Remove** button. Changes stay local until **Save filter**; the pane says
  "Unsaved changes" meanwhile.

Rules, checked by the server on save: Element Edit on the unit (as for
prefixes), every id must be in the picker's catalogue, duplicates are dropped,
and at most `MaxFilterEntries` (200) entries.

The transfer loop applies the filter to every stack before it moves it: an
item passes when it, or one of its groups, is on a whitelist, or when neither
is on a blacklist. Fuel tanks still take only their own fuel family on top of
that, and a container's own stock item filter still applies.

The picker offers every item-bank definition under `CatalogRoot` (`BaseItem`,
"All items") that is not hidden, minus `CatalogExclude` (`DataItem`:
blueprints and schematic copies are instanced items, which the unit never
moves). **Elements are included**: they sit in containers and move like any
other stackable item. Hidden definitions (deprecated parts and the like) are
left out, and groups left with no item are dropped.

Icons: the item bank has no icon paths; the client does. `src/IconIndex.json`
maps item-bank names to the client's own icon files
(`coui://data/resources_generated/...`). It is built from a local client
install by `python tools/build_icon_index.py` and holds paths only, no image
data. An item with no icon of its own takes its asset alias's, then its nearest
group's. On the local server, 4097 of the 4124 items under `BaseItem` resolve
to an icon.

### Output mode

How a unit shares items among its destinations. Pick it under **Output** in the
DESTINATION column of the LINKS tab; a click saves it at once (Element Edit on
the unit, as for prefixes).

- **In order** (the default): the first destination fills until it takes no
  more, then the next. A destination that refuses an item (a fuel tank and ore,
  a container's own item filter) is skipped for that item.
- **Round robin:** each cycle is split evenly across every destination that
  still takes items, and the next cycle starts one destination further along.
  Full or refusing destinations drop out of the split, and their share goes to
  the others. An item bigger than one share (a part, say) still moves, to the
  first destination in the rotation that takes it.

The order, for both modes: paired destinations as listed (move them with **Up**
and **Down** on the LINKS tab; the number in front of each row is its place),
then the containers matched by the destination prefix, by name ignoring case,
then by element id. A hub counts once, at its first place. The STATUS tab shows
the mode and the destinations found on the last check in that order ("Fill
order", or "Next cycle order" for round robin, rotated to where the next cycle
starts).

Round robin keeps a cursor per unit: one past the first destination that
received items last cycle, so a full destination never holds the start. It is
saved in `units.json` (`OutputCursor`) at most every 30 s and on Start, Stop and
mode changes, so a restart replays at most a few cycles of rotation. The split
is sized from what the sources hold that the unit may move (its filter
applied), so a source smaller than the budget is still shared evenly.

Before output modes existed, destinations were filled nearest first. Units saved
before then load as In order, with the new order (paired as listed, then prefix
matches by name).

### Running state

A unit moves items only while its industry state is **RUNNING**
(`RequireRunning`). Stopped, pending, jammed and every other state mean idle.
The state is the element's own industry `status` property, the one the stock
industry panel shows, so it survives restarts and needs nothing new on the client.

The stock grain cannot run this unit by itself: its Start refuses a unit with no
recipe, its SetRecipe refuses every item for this unit (its transfer-unit case
matches the exact `TransferUnit` id, not children of it), and a started run with
no linked containers only ever jams. So the mod hooks `IndustryUnitGrain`
(`HookStockStartStop`):

- **Start** on a linked unit sets its state to RUNNING, after checking that the
  player has RDMS Element Use or Element Edit on it.
- **StopSoft** and **StopHard** set it to STOPPED.
- **SetRecipe** is accepted and ignored, so the stock panel lets you press
  Start after picking any item.
- **Status** (`PatchIndustryStatus`) reports RUNNING or STOPPED from the same
  state.

Every other industry unit on the server goes straight to the stock grain. The
server logs one `intercepting IndustryUnitGrain.<call>` line per hooked call,
for every industry unit. If another mod also replaces one of these calls, the
one loaded last wins (the server logs `Replacing replace hook`).

The panel's own Start and Stop write the same state, so they work even if the
stock panel will not send Start for this unit.

### Rules

- **Prefixes:** saved per unit on the server. Each may be empty, is at most
  `MaxPrefixLength` characters, without control characters, and the two must
  differ. Only a player with RDMS **Element Edit** on the unit can save them;
  others see them read-only.
- **Pairs:** up to `MaxPairsPerRole` sources and as many destinations per unit.
  Only a player with Element Edit on the unit can pair or unpair.
- **Old name routes:** a unit with no saved prefixes and no pairs still takes
  its route from its name, `source > destination`, as before
  (`AllowNameRoutes`). It still needs to be started.
- **Range:** containers within `MaxRangeMeters` of the unit (element to
  element), on the unit's own construct or any other construct. Paired
  elements are re-checked every cycle; one that has moved out of range is
  skipped until it is back.
- **Hubs:** a container linked to a hub counts as the hub's whole pool. Naming
  the hub or any member selects that pool once.
- **Fuel tanks:** atmospheric, space and rocket tanks are valid destinations.
  They only receive their own fuel family and fill up to `FuelFillPercent`.
- **Rights:** the unit acts as its owner, which is the owner of the construct it
  sits on. For an organisation-owned construct it acts as the organisation's
  effective super legate. Every cycle the mod checks that this player holds
  RDMS **Container Retrieve** on each source and **Container Put** on each
  destination. Containers that fail the check are skipped. Containers owned by
  someone else are used only when RDMS grants those rights.
- **Throughput:** at most `MaxLitresPerUnitPerCycle` litres per unit per cycle.
- **Items:** only plain stackable items move. Items with per-instance
  properties stay put (`SkipInstancedItems`).

### Where the settings live

Each unit's config (prefixes, pairs, filter, last running state) is kept in one JSON
file on the server, `StorePath` (`Mods/LinkedTransferUnit/units.json` in the
install), keyed by the unit's element id and written atomically. A custom
element property would need a gameplay-bank DLL and a `docker-compose` change
(see the toolkit's ModPinCode sample), so the mod uses its own file.

The output mode is stored as `"OutputMode": "inorder"|"roundrobin"` with the
round-robin cursor `"OutputCursor"`; configs without them load as In order.

The filter is stored as `"Filter": {"Mode": "off"|"whitelist"|"blacklist", "Items": [itemTypeId, ...]}`;
an id may be an item or a group. Configs saved before filters existed load with
the filter off.

Every pair stores `{elementId, constructId, localId, elementTypeId, name}`.
The element id is the key; the rest is the fallback key, following what the
server does to element ids (measured on a local server, 2026-10-09):

- Each cycle, every pair of every unit in the cycle is resolved by element id
  in one query.
- Same id on another construct or with a new local id (a damaged element
  picked up and placed again): accepted, the stored values are refreshed, and
  range and rights are re-checked.
- Same id in an inventory: dormant, retried.
- Id gone: looked up by (construct, local id, type) with the name as a check,
  then by (local id, type) in a construct whose `originConstructId` is the
  stored construct (a compacted or blueprint-redeployed construct). Exactly one
  match is accepted and written back; anything else is "re-pair needed". A
  name alone never matches. A stored name only passes if the candidate has the
  same name, or the stored name was the default (empty).

The unit itself carries the same fallback key (`constructId`, `localId`,
`elementTypeId`, `unitName`). When a unit's id disappears and a new unit with
no config appears in the same slot (a pristine unit picked up and placed back,
a Repair Unit, or a redeployed copy of the construct), the config moves to the
new id, and the unit is restarted if it was running. Two copies of the
construct mean no move. A config whose unit stays missing for
`UnitConfigKeepHours` (7 days) is dropped.

### The panel

The panel follows the stock industry panel: a teal-grey sidebar with the unit's
art, its title and the Novean mark, stacked tabs (the active one solid yellow
with dark text), a hint bar with the close mark, and a black content area. Body
text is mixed case; headings use the HUD's wide display face. Start / Stop sits
at the bottom right of every tab, where the stock panel keeps its run controls.
Details such as the owner the unit acts as and raw element ids are on the
STATUS tab only.

- **LINKS:** state (RUNNING / STOPPED) with a one-line summary of what was
  found, then SOURCE and DESTINATION side by side. Each lists its paired
  elements (name, type, construct, a status chip, **Unpair**) under a **Pair**
  button, which shows the countdown while that side is pairing (a banner says
  what to do next). Below each list, a smaller field, **Also include
  containers named...**, holds that side's name prefix. **Save prefixes** in
  the footer saves both (Enter in either field does too). Under the
  DESTINATION heading, **Output** picks In order or Round robin (see *Output
  mode*); paired destinations are numbered in that order and have **Up** /
  **Down** to reorder them.
- **FILTER:** see *Item filter*.
- **STATUS:** state, last check, route, pairs, what was found, rights refusals,
  filter, output mode and fill order, last move, the owner it acts as, element and construct ids, your
  rights on the unit, and how the panel matched the unit.

**Inside the stock panel.** The script is sent to every client at login
(`InjectStockPanel`) and watches for the stock industry panel
(`div#industry_panel`; its layout as the probe logged it: `.menu_fixed >
.menu_stack > .menu_entry`, `.subPanel_area > .header_block > .context_tips`, and
one `.sub_panel_content` per tab). When it opens for a linked unit, the script:

- adds its three `.menu_entry` tabs to the stock sidebar, and one
  `.sub_panel_content` per tab sized from the stock sub panel on screen, so the
  stock stylesheet styles the tabs;
- hides the stock entries and sub panels with one stylesheet rule keyed on an
  `ltu_mode` class on the panel. PRODUCTION, CONFIGURATION and CONTAINERS all
  mean nothing for this unit: it has no recipe and no links;
- writes its own hint into `.context_tips`, and "TRANSFER" with "LINK" on the
  line below into the stock title element (`.industry_panel_title`, the
  "INDUSTRY" under the sidebar art), so the stock stylesheet keeps its font,
  size, tracking and colour.

Closing the panel removes all of it, so the same panel opened on any other
industry unit is untouched.

Which unit the panel shows is not in its DOM. The script keeps the latest call
of every HUD event (`engine.trigger`) and, when the panel opens, sends the
server the ids found in the events of the 3 s before (action 207). The server
takes the first that is a placed linked unit. Without one, it takes the industry
unit nearest the player (any kind) and uses it only if that is a linked unit
within `StockPanelMatchMeters` (12 m). Anything else leaves the stock panel
alone. The server logs every match and how it was made (`matched by hud event`
or `nearest industry unit, 2.4 m`).

**From the right-click menu.** **Configure** builds the same layout itself,
using the stock panel's fonts and sidebar images when the HUD has them. Its
sidebar title is the same TRANSFER / LINK, styled from the stock title's
computed style when the HUD has one. Escape
or the close mark closes it.

Sizes are 4K pixel values scaled by `window.innerHeight / 2160` (px only: `vh` is
double-scaled in DU's views).

**Previews.** `mockups/ltu_stock_links.png` (with a destination pairing
window open), `ltu_stock_filter.png` and `ltu_stock_status.png` are the real script rendered at 3840×2160 by
`python mockups/render_stock.py [stock_panel_screenshot.png]`. It takes over a
stand-in stock panel built from the probed layout and styled after a screenshot
of the real one (the real stylesheet is packed in the client). "Play" and
"Michroma" stand in for the panel's two faces, item icons come from the local
client install, and the item bank pages come from `mockups/catalog_fixture.json`
(a slice of the local server's bank). The harness also audits each tab for text
overflow, boxes escaping the tab and icons that fail to load.

**Probe.** With `ProbeStockPanel` on, the first time a client sees the stock
panel after login, the script logs to `Grains_dev.log` as `[client <id>]
[probe]` lines: the panel's tree six levels deep with leaf texts, the computed
fonts, colours and boxes of its parts, its image paths, the HUD events of the
3 s before it opened with their arguments, and window globals named like
"industry". That is what to check the takeover against if it misbehaves.

## Configuration (`ModLinkedTransferUnit.json`)

| Key | Default | Meaning |
|---|---|---|
| `ItemName` | `LinkedTransferUnit` | Item-bank name of the unit. Children of it count too. |
| `CreateItemIfMissing` | `true` | Add the item to the item bank on boot if missing. |
| `ItemParent` | `TransferUnit` | Parent of the created item. |
| `ItemAssetAlias` | `TransferUnit` | Item whose art the unit reuses while `ClientAssets` is off. With it on, the item's `assetAlias` is cleared at boot so clients use the unit's own element definition. |
| `ClientAssets` | `true` | Send the unit's own element definition and art to each client at login (see *Client art*). `false` = the stock transfer unit look; restart orleans so the alias is restored. |
| `ClientAssetDir` | `/OrleansGrains/Mods/LinkedTransferUnit/client` | The client files: flat, lower-case names. |
| `AssetVariant` | `kit` | The look to send: `ltu_<variant>.nqdef`. `kit` = stock meshes combined, `mesh` = the exported v3b mesh. Read at every login and by *Reload unit art*, so no restart is needed. |
| `ClientAssetMinResendSeconds` | `20` | A player is sent the art at most once in this time (Reload unit art ignores it). |
| `ItemDisplayName`, `ItemDescription` | | Shown in game. |
| `RouteSeparator` | `>` | Separator in a unit name route. |
| `CaseInsensitive` | `true` | Case-insensitive tag matching. |
| `AllowNameRoutes` | `true` | A unit with no saved prefixes takes its route from its name, `source > destination`. |
| `MaxPrefixLength` | `64` | Longest prefix the panel accepts (1-256). |
| `StorePath` | `/OrleansGrains/Mods/LinkedTransferUnit/units.json` | Per-unit config: prefixes, pairs, filter, running state. |
| `UnitConfigKeepHours` | `168` | How long a missing unit's config is kept for it to come back. |
| `MaxPairsPerRole` | `16` | Paired sources per unit, and paired destinations per unit. |
| `PairingWindowSeconds` | `60` | How long a unit waits in pairing mode (5-3600). |
| `PairRetrySeconds` | `30` | Seconds between fallback lookups for a pair whose element is gone. |
| `RequireRunning` | `true` | Move items only while the unit's industry state is RUNNING. `false` = every unit with a route runs. |
| `HookStockStartStop` | `true` | Hook the stock industry Start/Stop/SetRecipe for this unit (see *Running state*). |
| `PatchIndustryStatus` | `true` | Also hook the stock industry Status call. |
| `ProbeStockPanel` | `true` | Log the stock industry panel's layout, styles and opening events once per client session. |
| `InjectStockPanel` | `true` | Send the script at login so a linked unit's stock industry panel shows the mod's tabs. `false` = right-click Configure only. |
| `InjectDelaysSeconds` | `3, 8, 15, 30, 60, 120` | When the script is (re)sent after login, until the client confirms it loaded. |
| `StockPanelMatchMeters` | `12` | Fallback match for the stock panel: the industry unit nearest the player, if it is a linked unit this close. `0` = event ids only. |
| `MaxFilterEntries` | `200` | Most entries (items or groups) in one unit's filter (1-5000). |
| `CatalogRoot` | `BaseItem` | Item-bank node whose subtree the filter picker offers. |
| `CatalogExclude` | `["DataItem"]` | Subtrees left out of the picker, by item-bank name. |
| `CatalogBrowseMax`, `CatalogSearchMax` | `400`, `120` | Most items in one picker page, and in one search result. |
| `MaxRangeMeters` | `1000` | Hard range cap, metres. |
| `CycleSeconds` | `5` | Seconds between cycles (min 1). |
| `MaxLitresPerUnitPerCycle` | `1000` | Throughput cap per unit. |
| `MaxUnitsPerCycle` | `200` | Units processed per cycle; extra units take turns. |
| `RescanSeconds` | `30` | How often the database is scanned for placed units. A newly placed unit starts within this time. |
| `ContainerCacheSeconds` | `15` | How long a construct's container list is cached. A renamed container is seen within this time. |
| `FuelFillPercent` | `100` | Fuel tanks fill to this percent. |
| `RequireMatchingFuelType` | `true` | Only matching fuel goes into a tank. |
| `FuelTypeFamilies` | Atmospheric→AtmoFuel, Space→SpaceFuel, Rocket→RocketFuel | Tank `fuelType` to item-bank fuel family. |
| `AllowOtherOwnersWithRights` | `true` | `false` = only containers on constructs with the unit's owner. |
| `SkipInstancedItems` | `true` | Leave instanced items alone. |
| `JournalStaleSeconds` | `120` | An in-flight journal entry older than this counts as interrupted and is checked. Entries from before a restart are checked at startup. |
| `FaultInjection` | `false` | DEBUG ONLY. Lets an admin arm injected faults (action 212) for the test harness. Read from the file each time, so it can be switched without a restart. Keep it `false`. |
| `LogEveryMove` | `true` | Log each move to `Grains_dev.log`. |
| `HeartbeatMinutes` | `10` | Interval for the "still running" log line. |

### Open defaults (not yet ruled on by the server owner)

These were chosen as defaults and are all configurable:

- Tag matching is case-insensitive (`CaseInsensitive`).
- The route is set in the config panel; old name routes still work
  (`AllowNameRoutes`).
- Transfers run as the unit's owner, with an RDMS check every cycle; other
  owners' containers are allowed only through RDMS grants
  (`AllowOtherOwnersWithRights`).
- Fuel tanks fill to 100 %, with matching fuel only (`FuelFillPercent`,
  `RequireMatchingFuelType`).
- Throughput cap of 1000 L per unit per 5 s cycle (`MaxLitresPerUnitPerCycle`,
  `CycleSeconds`).

## Client art

The unit has its own look, delivered by NQ's documented resource-loading protocol
(`ModLoader/README.md` in the official `mydu-server-mods` repo). At login, the mod sends the
client one `.nqdef` (flag 1), then the variant's files into the client's RAM filesystem as
`resources_generated/<name>` (flag 2), then an element load request for `LinkedTransferUnit`
(flag 3). This is the loader's "raw" mode: nothing is cached on the client, and nothing in
the client install changes. The cached and synchronous modes are not used because the
client sends its reply to a mod literally named `Loader` or `SyncLoader`; raw mode lets this
mod run beside a server's own loader.

Files: `assets/client/` (built by `assets/build_client_assets.py`; the mesh by
`art/export_ltu_v3b_mesh.py`). Copy them to `Mods/LinkedTransferUnit/client/` on the server.
Each `ltu_<name>.nqdef` is a variant; files named `ltu_<name>_...` belong to it and are only
sent with it. The definition is `"LinkedTransferUnit": {"inherit": "TransferUnit", ...}`, so the
unit keeps the transfer unit's sounds, damage effects and idle/running/jammed lights.

Right-click anywhere > **Linked Transfer Unit > Reload unit art** sends the art again to you,
for example after switching `AssetVariant`.

## Panel messages

The panel is plain injected JavaScript (`src/client/ltu_panel.js`). Server to
client, HUD events:

- `modinjectjs`: the whole script, with `__LTU_BOOT__` replaced by
  `{mod, version, probe, inject, open}`. Sent at login with `open: null` (the
  stock panel watcher), and on **Configure** with `open` set to the unit data
  below. A script of the same version already running only opens the panel.
- `LinkedTransferUnit.update`: the unit data as a JSON string, after every
  panel action and after a pair made from the right-click menu. The panel
  ignores it unless `elementId` matches.
- `LinkedTransferUnit.stock`: the answer to action 207: the unit data plus
  `match` (how it was matched) and `nonce` (echoed), or `{none: true, nonce}`.
- `LinkedTransferUnit.catalog`: the answer to action 209 (below).

Unit data (`version: 2`):

```
{ version, mod, elementId, constructId, unitName,
  source, destination,              // saved prefixes ("" = none)
  origin: "panel"|"name"|"none", route,   // the prefix route in effect
  running, requireRunning, status,  // status = one-line text from a dry run
  canEdit, canUse,                  // Element Edit / Element Use on the unit
  maxLen, maxPairs, rangeMeters, probe,
  pairing: null | { role: "source"|"destination", secondsLeft },  // this player's pairing mode for this unit
  sources: [Pair], destinations: [Pair],
  filter: { mode: "off"|"whitelist"|"blacklist", max, items: [Entry] },
  actor,                            // "player 10000": whose rights the unit uses ("" = none)
  sourcesFound, destinationsFound, deniedByRights,
  lastMove: null | { litres, at },
  msg, error, saved,
  match, nonce }                    // stock panel answers only

Pair = { role, elementId, elementKey /* id as a string */, constructId, constructName,
         localId, name /* "" = default */, typeName,
         status: "ok"|"dormant"|"missing"|"repair", note }

Entry = { id /* string */, label, icon /* path under resources_generated/, "" = none */,
          group, count /* items under a group */, path /* "Materials / Refined Materials" */, missing }
```

Catalogue page (`LinkedTransferUnit.catalog`):

```
{ req /* echoed */, kind: "browse"|"search", q,
  group: Entry, path: [Entry],      // browse: the group shown, and the breadcrumb from the root
  groups: [Entry], items: [Entry],  // groups first, then items, by label (search: best match first)
  total, truncated }
```

The client caches every page for the session, so each group or search is asked
once.

Client to server, `CPPMod.sendModAction("LinkedTransferUnit", id, [constructId, unitElementId, 0], payload)`:

| id | Action | Payload |
|---|---|---|
| 201 | Save prefixes | `{"source": "...", "destination": "..."}` (either may be `""`) |
| 202 | Start | `""` |
| 203 | Stop | `""` |
| 204 | Client log line (to `Grains_dev.log`, 30/min) | text |
| 205 | Unpair | `{"role": "source"\|"destination", "elementId": "<elementKey>"}` (number or string) |
| 206 | Enter pairing mode | `{"role": "source"\|"destination"}` |
| 207 | The stock industry panel opened (ids `[0, 0, 0]`) | `{"candidates": ["<id>", ...], "nonce": n}` |
| 208 | Script loaded (ids `[0, 0, 0]`) | script version |
| 209 | Item picker page | `{"req": n, "group": "<id>"}` (`""` = the root) or `{"req": n, "q": "text"}` |
| 210 | Save filter | `{"mode": "off"\|"whitelist"\|"blacklist", "items": ["<id>", ...]}` |
| 214 | Set output mode | `{"mode": "inorder"\|"roundrobin"}` |
| 215 | Move a paired destination | `{"elementId": "<elementKey>", "dir": -1\|1}` (up / down) |

Right-click menu actions (`ModActionContext.Element`, on every element): 1
Status, 2 Configure, 3 Pair source, 4 Pair destination, 5 Pair with transfer
unit.

## Build

Requirements: .NET SDK 6 or later.

1. Copy the NQ assemblies from the myDU mod toolkit's `ModAdmin` folder
   (`NQutils.dll`, `Backend.dll`, `Backend.PubSub.dll`, `Interfaces.dll`,
   `Router.Orleans.dll`) into `lib/`. They are not in this repository, because
   the toolkit licence does not allow redistributing them. You can point
   elsewhere with `-p:NQLibDir=<folder>/`.
2. `dotnet build -c Release`
3. The mod is `bin/Release/net6.0/ModLinkedTransferUnit.dll`. The panel script
   (`src/client/ltu_panel.js`) and the icon index (`src/IconIndex.json`) are
   embedded in it.

After a client update that adds items, rebuild the icon index:
`python tools/build_icon_index.py ["<client>/Game/data"]`.

Unit tests for the parsing, matching, quantity, panel validation, store, pair
resolution, pairing rules, item catalogue and filter need no NQ assemblies:
`dotnet test tests/`.

## Item safety

Every move is one storage transaction holding both the debit from the source
and the credit to the destination. The server commits the two in one SQL
transaction or neither, and rolls back any transaction that is not committed.
A move cannot lose or duplicate items on its own; a refused move leaves both
storages as they were.

Around that, the mod:

- locks each source and destination pool for the length of a move (ascending
  id order, so no deadlock);
- moves only what fits the destination's measured free room, rounded down
  (bulky parts included);
- writes each move to `Mods/LinkedTransferUnit/moves-inflight.json` before its
  commit and clears it after. An entry left by a crash is checked against the
  storages at startup and logged as `INTERRUPTED MOVE ...` (applied, not
  applied, or unknown). It is never re-sent, because re-sending a move that
  already happened would duplicate items. A unit with a leftover entry pauses
  until the entry is resolved;
- re-checks the storages when a commit itself throws, since the SQL step may
  have succeeded before a later step failed;
- keeps a per-unit last error, shown in Status and on the STATUS tab.

The audit and the fault-injection results are in
`C:\Claude\docs\voxelmmo\du-ltu-item-safety.md`.

## Known limits

- The stock panel takeover is built against the probed layout and a screenshot,
  and has not been seen in game yet. Open points: whether `engine.trigger`
  carries the event that opens the panel (so the unit is matched by id rather
  than by distance), and whether the stock stylesheet styles the added tabs
  exactly like its own. The first stock panel of each session is probed into
  the log to check both.
- The nearest-unit fallback can pick the wrong unit when a linked unit stands
  closer to the player than the industry unit they opened; that panel then
  shows the linked unit's tabs. Matching by event id avoids it when it works.
- Text input in the HUD is unconfirmed: the inputs ask for text capture
  (`CPPInput.setCaptureText`) on focus.
- Units are discovered by scanning the database every `RescanSeconds`.
- The menu entries show on every element: a mod cannot limit an Element
  action to one item type. On anything else they answer with a HUD message.
- Pairing and every fallback case are unit-tested; none has been run in game
  yet. The Repair Unit path in the element-id doc is code-read only.
- The server logs `No bounding box for object LinkedTransferUnit` at boot, as it
  does for some stock items. Placement and damage on the unit are untested.
