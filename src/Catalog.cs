using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LinkedTransferUnit
{
    /// One item-bank definition as the catalogue sees it. Built from IGameplayBank on the
    /// server; plain data so the catalogue is unit-testable.
    public sealed class CatalogNode
    {
        public ulong Id;
        public string Name = "";
        public string DisplayName = "";
        public ulong ParentId;
        public bool Hidden;
        public string AssetAlias = "";
    }

    /// The item picker's view of the item bank: the subtree under one root (BaseItem), minus
    /// hidden definitions and excluded subtrees. Leaves are items; inner nodes are groups
    /// ("Ore", "Pure", "Fuels"). A group with no visible item under it is dropped.
    public sealed class Catalog
    {
        public sealed class Entry
        {
            public ulong Id;
            public string Name = "";        // item-bank name
            public string Label = "";       // display name, or the name when it has none
            public ulong ParentId;
            public bool IsGroup;
            public string Icon = "";        // path under resources_generated/, "" = none
            public int ItemCount;           // items under a group (1 for an item)
            public readonly List<ulong> Children = new();
        }

        private readonly Dictionary<ulong, Entry> byId = new();
        public ulong RootId { get; }
        public int ItemCount { get; private set; }
        public int GroupCount { get; private set; }
        /// The root's ancestor chain, for the boot log.
        public string Ancestors { get; private set; } = "";

        public Entry? Get(ulong id) => byId.TryGetValue(id, out var e) ? e : null;
        public bool Contains(ulong id) => byId.ContainsKey(id);

        private Catalog(ulong rootId) { RootId = rootId; }

        /// Builds the catalogue. `icons` maps item-bank names to client icon paths; an entry with
        /// no icon of its own takes its asset alias's, then its nearest ancestor's. A group takes
        /// the icon of its first item (by label) when it has none.
        public static Catalog Build(IEnumerable<CatalogNode> nodes, string rootName, IReadOnlyDictionary<string, string> icons,
            IEnumerable<string>? excludeNames = null)
        {
            var all = new Dictionary<ulong, CatalogNode>();
            foreach (var n in nodes)
                if (n != null) all[n.Id] = n;
            var root = all.Values.FirstOrDefault(n => n.Name == rootName);
            if (root == null) return new Catalog(0);
            var exclude = new HashSet<string>(excludeNames ?? Array.Empty<string>(), StringComparer.Ordinal);
            var kids = new Dictionary<ulong, List<CatalogNode>>();
            foreach (var n in all.Values)
            {
                if (n.Id == n.ParentId) continue;
                if (!kids.TryGetValue(n.ParentId, out var l)) kids[n.ParentId] = l = new();
                l.Add(n);
            }

            string? OwnIcon(CatalogNode n)
            {
                if (icons.TryGetValue(n.Name, out var p) && !string.IsNullOrEmpty(p)) return p;
                if (!string.IsNullOrEmpty(n.AssetAlias) && icons.TryGetValue(n.AssetAlias, out p) && !string.IsNullOrEmpty(p)) return p;
                return null;
            }

            // A node is visited at most once, and the root's ancestors never: the live bank links
            // GameplayObject (BaseItem's parent) back under BaseItem's own subtree. When the
            // ancestor chain loops back like that, only the root's direct parent is a real ancestor.
            var chain = new List<ulong>();
            var seen = new HashSet<ulong> { root.Id };
            bool loops = false;
            for (var a = all.TryGetValue(root.ParentId, out var p0) ? p0 : null; a != null; a = all.TryGetValue(a.ParentId, out var pa) ? pa : null)
            {
                if (!seen.Add(a.Id)) { loops = true; break; }
                chain.Add(a.Id);
            }
            var visited = new HashSet<ulong>(loops ? chain.Take(1) : chain);
            string NameOf(ulong id) => all.TryGetValue(id, out var x) ? x.Name : id.ToString();

            var cat = new Catalog(root.Id);
            cat.Ancestors = string.Join(" > ", chain.Select(NameOf)) + (loops ? " (loops back into the tree)" : "");
            int items = 0, groups = 0;
            // Depth-first; returns the entry, or null when nothing visible is under it.
            Entry? Visit(CatalogNode n, string inheritedIcon, int depth)
            {
                if (depth > 64 || n.Hidden || exclude.Contains(n.Name) || !visited.Add(n.Id)) return null;
                var icon = OwnIcon(n) ?? inheritedIcon;
                var e = new Entry
                {
                    Id = n.Id, Name = n.Name, Label = string.IsNullOrWhiteSpace(n.DisplayName) ? n.Name : n.DisplayName.Trim(),
                    ParentId = n.ParentId,
                };
                if (!kids.TryGetValue(n.Id, out var children) || children.Count == 0)
                {
                    e.IsGroup = false;
                    e.Icon = icon;
                    e.ItemCount = 1;
                    cat.byId[e.Id] = e;
                    items++;
                    return e;
                }
                var visible = new List<Entry>();
                foreach (var c in children)
                {
                    var ce = Visit(c, icon, depth + 1);
                    if (ce != null) visible.Add(ce);
                }
                if (visible.Count == 0) return null;
                e.IsGroup = true;
                visible.Sort(CompareEntries);
                e.Children.AddRange(visible.Select(v => v.Id));
                e.ItemCount = visible.Sum(v => v.ItemCount);
                e.Icon = OwnIcon(n) ?? FirstItemIcon(visible) ?? icon;
                cat.byId[e.Id] = e;
                groups++;
                return e;
            }
            string? FirstItemIcon(List<Entry> sorted)
            {
                foreach (var v in sorted)
                    if (v.Icon.Length > 0) return v.Icon;
                return null;
            }

            Visit(root, "", 0);
            cat.ItemCount = items;
            cat.GroupCount = groups;
            return cat;
        }

        /// Groups first, then items, each by label.
        private static int CompareEntries(Entry a, Entry b)
        {
            if (a.IsGroup != b.IsGroup) return a.IsGroup ? -1 : 1;
            var c = string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }

        /// Groups from the root down to `id` (inclusive), for the picker's breadcrumb.
        public List<Entry> PathTo(ulong id)
        {
            var path = new List<Entry>();
            for (var e = Get(id); e != null && path.Count < 64; e = e.Id == RootId ? null : Get(e.ParentId))
                path.Add(e);
            path.Reverse();
            return path;
        }

        /// "Materials / Refined Materials / Pure", without the root, for an entry's subtitle.
        public string PathLabel(ulong id)
        {
            var p = PathTo(id);
            return string.Join(" / ", p.Where(e => e.Id != RootId && e.Id != id).Select(e => e.Label));
        }

        public sealed class Page
        {
            public Entry? Group;
            public List<Entry> Path = new();
            public List<Entry> Groups = new();
            public List<Entry> Items = new();
            public int Total;
            public bool Truncated;
        }

        /// One group's children: its subgroups and its items (at most `maxItems` items).
        public Page Browse(ulong groupId, int maxItems)
        {
            var g = Get(groupId);
            if (g == null || !g.IsGroup) g = Get(RootId);
            var page = new Page { Group = g };
            if (g == null) return page;
            page.Path = PathTo(g.Id);
            foreach (var cid in g.Children)
            {
                var c = Get(cid);
                if (c == null) continue;
                if (c.IsGroup) page.Groups.Add(c);
                else
                {
                    page.Total++;
                    if (page.Items.Count < maxItems) page.Items.Add(c);
                    else page.Truncated = true;
                }
            }
            return page;
        }

        /// Items and groups whose label (or item-bank name) contains every word of `query`,
        /// ignoring case. Label prefix matches rank first, then shorter labels.
        public Page Search(string query, int maxResults)
        {
            var page = new Page();
            var words = (query ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return page;
            var hits = new List<(Entry e, int rank)>();
            foreach (var e in byId.Values)
            {
                if (e.Id == RootId) continue;
                var label = e.Label.ToLowerInvariant();
                var name = e.Name.ToLowerInvariant();
                if (!words.All(w => label.Contains(w) || name.Contains(w))) continue;
                int rank = label.StartsWith(words[0]) ? 0 : label.Contains(" " + words[0]) ? 1 : 2;
                hits.Add((e, rank));
            }
            page.Total = hits.Count;
            foreach (var h in hits.OrderBy(h => h.rank).ThenBy(h => h.e.IsGroup ? 0 : 1).ThenBy(h => h.e.Label.Length)
                         .ThenBy(h => h.e.Label, StringComparer.OrdinalIgnoreCase).Take(maxResults))
                (h.e.IsGroup ? page.Groups : page.Items).Add(h.e);
            page.Truncated = hits.Count > maxResults;
            return page;
        }
    }

    // ---------------------------------------------------------------------- item filter

    public enum FilterMode { Off, Whitelist, Blacklist }

    /// One filter per unit: a whitelist or a blacklist of item-bank ids. An id may be an item or
    /// a group; a group covers every item under it.
    public sealed class ItemFilterSpec
    {
        public string Mode { get; set; } = "off";
        public List<ulong> Items { get; set; } = new();

        public ItemFilterSpec Copy() => new() { Mode = Mode ?? "off", Items = new List<ulong>(Items ?? new()) };

        [System.Text.Json.Serialization.JsonIgnore]
        public FilterMode ModeValue => ItemFilter.ParseMode(Mode) ?? FilterMode.Off;
    }

    public static class ItemFilter
    {
        /// Longest filter payload accepted from the client, in characters.
        public const int MaxPayloadLength = 32768;

        public static FilterMode? ParseMode(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
        {
            "off" or "" => FilterMode.Off,
            "whitelist" => FilterMode.Whitelist,
            "blacklist" => FilterMode.Blacklist,
            _ => null,
        };

        public static string ModeName(FilterMode m) => m switch
        {
            FilterMode.Whitelist => "whitelist",
            FilterMode.Blacklist => "blacklist",
            _ => "off",
        };

        /// Does an item of a type whose self-and-ancestors are `lineage` pass the filter?
        /// Off passes everything; a whitelist passes only items at or under an entry (an empty
        /// whitelist passes nothing); a blacklist passes everything else.
        public static bool Passes(ItemFilterSpec? f, IEnumerable<ulong> lineage)
        {
            var mode = f?.ModeValue ?? FilterMode.Off;
            if (mode == FilterMode.Off) return true;
            var set = f!.Items ?? new List<ulong>();
            bool listed = set.Count > 0 && lineage.Any(set.Contains);
            return mode == FilterMode.Whitelist ? listed : !listed;
        }

        /// Reads {"mode": "off"|"whitelist"|"blacklist", "items": ["123", 456, ...]}.
        public static bool TryParsePayload(string? payload, out FilterMode mode, out List<ulong> items, out string error)
        {
            mode = FilterMode.Off;
            items = new List<ulong>();
            error = "";
            if (string.IsNullOrWhiteSpace(payload)) { error = "Nothing was sent."; return false; }
            if (payload.Length > MaxPayloadLength) { error = "The filter is too long."; return false; }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) { error = "The request is not readable."; return false; }
                if (!root.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String || ParseMode(m.GetString()) is not FilterMode pm)
                {
                    error = "The filter mode must be off, whitelist or blacklist.";
                    return false;
                }
                mode = pm;
                if (root.TryGetProperty("items", out var arr))
                {
                    if (arr.ValueKind != JsonValueKind.Array) { error = "The filter items are not a list."; return false; }
                    foreach (var v in arr.EnumerateArray())
                    {
                        ulong id;
                        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt64(out id)) { }
                        else if (v.ValueKind == JsonValueKind.String && ulong.TryParse(v.GetString(), out id)) { }
                        else { error = "A filter item is not an item id."; return false; }
                        items.Add(id);
                    }
                }
                return true;
            }
            catch (JsonException)
            {
                error = "The request is not readable.";
                return false;
            }
        }

        /// Checks a parsed filter: every id must be in the catalogue (an item or a group), the
        /// list is de-duplicated and at most `maxEntries` long. Returns the cleaned spec.
        public static bool Validate(FilterMode mode, IEnumerable<ulong> ids, Func<ulong, bool> known, int maxEntries,
            out ItemFilterSpec spec, out string error)
        {
            spec = new ItemFilterSpec { Mode = ModeName(mode) };
            error = "";
            var seen = new HashSet<ulong>();
            foreach (var id in ids)
            {
                if (!seen.Add(id)) continue;
                if (!known(id)) { error = $"Item {id} is not in the item bank, or cannot be stored."; return false; }
                spec.Items.Add(id);
            }
            if (spec.Items.Count > maxEntries)
            {
                error = $"A filter holds at most {maxEntries} entries; this one has {spec.Items.Count}.";
                return false;
            }
            return true;
        }
    }
}
