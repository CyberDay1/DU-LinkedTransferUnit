# Linked Transfer Unit

A myDU server mod: a transfer unit that needs no links. Pair it with source and
destination containers, hubs or fuel tanks on any construct within range (1 km
by default), or give it name prefixes, and it moves items between them every
cycle.

- Works across constructs, which the stock Link tool can't do.
- One item filter per unit: a whitelist or blacklist of items and item groups.
- Output modes: in order, or round robin.
- Its settings appear as tabs in the unit's own industry panel.
- Its own model and animation, pushed to clients by the server.

## Install

1. Build it (see `GUIDE.md`) and copy `ModLinkedTransferUnit.dll` into the
   server's `Mods` folder.
2. Optional: copy `ModLinkedTransferUnit.json` next to it to change the defaults.
3. Restart the `orleans` service.

Players need **Enable MOD JS** on in the client (`game.enableModJS: true`).

The NQ toolkit assemblies are not included (toolkit licence); copy them into
`lib/` before building.

## More

`GUIDE.md` covers use, configuration, client art, building, item safety and
known limits.

## Licence

MIT, see `LICENSE`. The NQ toolkit assemblies the build needs are not covered
and not included.
