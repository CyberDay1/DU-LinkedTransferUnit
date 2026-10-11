using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace LinkedTransferUnit
{
    /// How a unit shares its items among its destinations.
    public enum OutputMode
    {
        /// Fill the first destination until it takes no more, then the next.
        InOrder,
        /// Spread each cycle across every destination that still takes items, starting one
        /// destination further along each cycle.
        RoundRobin,
    }

    /// Pure output-mode rules: names, panel payloads, the destination order and the
    /// distribution of one cycle's budget. No server types, so it is unit-testable.
    public static class OutputRules
    {
        public const int MaxPayloadLength = 256;
        /// Round-robin passes per cycle (each pass gives every active destination one share).
        public const int MaxPasses = 32;
        /// A round-robin cursor is written to the store at most this often; a restart in
        /// between only replays at most this much rotation.
        public static readonly TimeSpan CursorSaveInterval = TimeSpan.FromSeconds(30);

        public static string Name(OutputMode m) => m == OutputMode.RoundRobin ? "roundrobin" : "inorder";
        public static string Label(OutputMode m) => m == OutputMode.RoundRobin ? "Round robin" : "In order";

        /// "inorder" | "roundrobin" (also "in-order", "round robin", ...). Anything else,
        /// including null, is null.
        public static OutputMode? Parse(string? s)
        {
            var t = new string((s ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
            return t switch
            {
                "inorder" => OutputMode.InOrder,
                "roundrobin" => OutputMode.RoundRobin,
                _ => null,
            };
        }

        /// The stored value: unknown or missing reads as In order.
        public static OutputMode ParseStored(string? s) => Parse(s) ?? OutputMode.InOrder;

        /// {"mode": "inorder" | "roundrobin"} from the panel.
        public static bool TryParseModePayload(string? payload, out OutputMode mode, out string error)
        {
            mode = OutputMode.InOrder;
            error = "";
            if (!TryObject(payload, out var doc, out error)) return false;
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.String || Parse(m.GetString()) is not { } p)
                {
                    error = "Unknown output mode.";
                    return false;
                }
                mode = p;
                return true;
            }
        }

        /// {"elementId": "123" | 123, "dir": -1 | 1} from the panel: move one paired destination up or down.
        public static bool TryParseMovePayload(string? payload, out ulong elementId, out int dir, out string error)
        {
            elementId = 0;
            dir = 0;
            error = "";
            if (!TryObject(payload, out var doc, out error)) return false;
            using (doc)
            {
                var r = doc.RootElement;
                if (r.TryGetProperty("elementId", out var e))
                {
                    if (e.ValueKind == JsonValueKind.String) ulong.TryParse(e.GetString(), out elementId);
                    else if (e.ValueKind == JsonValueKind.Number) e.TryGetUInt64(out elementId);
                }
                if (r.TryGetProperty("dir", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var di))
                    dir = Math.Sign(di);
                if (elementId == 0 || dir == 0)
                {
                    error = "The request is missing the element or the direction.";
                    return false;
                }
                return true;
            }
        }

        private static bool TryObject(string? payload, out JsonDocument doc, out string error)
        {
            doc = null!;
            error = "";
            if (string.IsNullOrWhiteSpace(payload)) { error = "Nothing was sent."; return false; }
            if (payload.Length > MaxPayloadLength) { error = "The request is too long."; return false; }
            try
            {
                doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { doc.Dispose(); error = "The request is not readable."; return false; }
                return true;
            }
            catch (JsonException)
            {
                error = "The request is not readable.";
                return false;
            }
        }

        /// Moves the pair with `elementId` one place up (dir -1) or down (dir +1). False when
        /// it is not in the list or already at that end.
        public static bool MovePair(List<PairedElement> list, ulong elementId, int dir)
        {
            var i = list.FindIndex(p => p.ElementId == elementId);
            var j = i + Math.Sign(dir);
            if (i < 0 || j < 0 || j >= list.Count || i == j) return false;
            (list[i], list[j]) = (list[j], list[i]);
            return true;
        }

        /// The destination order both modes use: paired destinations in their saved order
        /// (pairIndex 0, 1, ...), then the prefix matches that are not paired (pairIndex < 0)
        /// by name ignoring case, then by element id. Duplicates by id keep their first place.
        public static List<T> Order<T>(IEnumerable<T> items, Func<T, int> pairIndex, Func<T, string> name, Func<T, ulong> id)
        {
            var seen = new HashSet<ulong>();
            var list = items.Where(x => seen.Add(id(x))).ToList();
            return list
                .OrderBy(x => pairIndex(x) >= 0 ? 0 : 1)
                .ThenBy(x => pairIndex(x) >= 0 ? pairIndex(x) : 0)
                .ThenBy(x => (name(x) ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => (name(x) ?? "").Trim(), StringComparer.Ordinal)
                .ThenBy(id)
                .ToList();
        }

        /// Index of the first destination of a round-robin cycle.
        public static int StartIndex(long cursor, int count) =>
            count <= 0 ? 0 : (int)(((cursor % count) + count) % count);

        /// The list rotated so it starts at StartIndex(cursor).
        public static List<T> Rotate<T>(IReadOnlyList<T> ordered, long cursor)
        {
            var n = ordered.Count;
            var s = StartIndex(cursor, n);
            var r = new List<T>(n);
            for (int i = 0; i < n; i++) r.Add(ordered[(s + i) % n]);
            return r;
        }

        /// Shares one cycle's budget among the destinations. `fill(dest, maxLitres)` moves what
        /// it can into one destination (from every source) and returns the litres moved; it is
        /// the only thing that touches storages, so every per-move guarantee of the caller holds.
        ///
        /// In order: each destination in turn gets the whole remaining budget.
        /// Round robin: starting at StartIndex(cursor), every destination still taking items gets
        /// an equal share of what is left (the budget, or `available` when that is smaller: the
        /// litres the sources hold that may move), pass after pass, until a pass moves nothing.
        /// A destination that takes nothing in a pass (full, filtered, wrong fuel) drops out.
        /// Whatever the shares could not place (an item bigger than a share) is offered, once, to
        /// the dropped destinations in the same rotated order.
        ///
        /// Returns the litres moved and the next cursor: one past the first destination that
        /// received anything, so a full or filtered destination never holds the start and the
        /// next one in line is not served twice. Unchanged when nothing moved (and in order).
        public static async Task<(double moved, long next)> Distribute<T>(OutputMode mode, IReadOnlyList<T> ordered, long cursor,
            double budget, double available, Func<T, double, Task<double>> fill)
        {
            double moved = 0;
            if (ordered.Count == 0 || budget <= 0) return (0, cursor);
            if (mode == OutputMode.InOrder)
            {
                foreach (var d in ordered)
                {
                    if (budget - moved <= 0) break;
                    moved += await fill(d, budget - moved);
                }
                return (moved, cursor);
            }

            var start = StartIndex(cursor, ordered.Count);
            var first = -1;     // rotated index of the first destination that received items

            var rotated = Rotate(ordered, cursor);
            var active = new List<T>(rotated);
            var dropped = new HashSet<int>();
            var pool = available > 0 ? Math.Min(budget, available) : budget;
            for (int pass = 0; pass < MaxPasses && active.Count > 0; pass++)
            {
                var left = Math.Min(budget - moved, pool - moved);
                if (left <= 1e-9) break;
                var share = left / active.Count;
                double passMoved = 0;
                var next = new List<T>(active.Count);
                foreach (var d in active)
                {
                    var cap = Math.Min(share, budget - moved);
                    if (cap <= 1e-9) { next.Add(d); continue; }
                    var m = await fill(d, cap);
                    moved += m;
                    passMoved += m;
                    if (m > 1e-9 && (first < 0 || rotated.IndexOf(d) < first)) first = rotated.IndexOf(d);
                    // Only a destination that took nothing drops out: one that took part of its
                    // share may just have hit whole-item rounding, and is asked again next pass.
                    if (m > 1e-9) next.Add(d);
                    else dropped.Add(rotated.IndexOf(d));
                }
                active = next;
                if (passMoved <= 1e-9) break;
            }
            // Leftovers the shares could not place: offered once to each dropped destination.
            for (int i = 0; i < rotated.Count && budget - moved > 1e-9; i++)
                if (dropped.Contains(i))
                {
                    var m = await fill(rotated[i], budget - moved);
                    moved += m;
                    if (m > 1e-9 && (first < 0 || i < first)) first = i;
                }
            return (moved, first < 0 ? cursor : (start + first + 1) % ordered.Count);
        }

        /// True when a round-robin cursor that changed should be written now.
        public static bool CursorDue(DateTime? lastSaved, DateTime now) =>
            lastSaved == null || now - lastSaved.Value >= CursorSaveInterval;
    }
}
