using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LinkedTransferUnit
{
    /// Everything saved for one unit: prefixes, pairs, item filter and running state. Keyed by the unit's
    /// element id; (ConstructId, LocalId, ElementTypeId) is the fallback key that lets a unit
    /// placed back, repaired by a Repair Unit or redeployed from a blueprint keep its config.
    /// Instances in the store are never mutated after they are published: UnitStore.Update
    /// works on a copy, so readers need no lock.
    public sealed class UnitSettings
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
        public ulong UpdatedBy { get; set; }
        public DateTime UpdatedAt { get; set; }

        // Fallback key of the unit itself.
        public ulong ConstructId { get; set; }
        public ulong LocalId { get; set; }
        public ulong ElementTypeId { get; set; }
        public string UnitName { get; set; } = "";

        /// Last running state set through Start/Stop. The element's industry status is the live
        /// value; this copy is applied when the config moves to a new element id.
        public bool Running { get; set; }

        public List<PairedElement> SourcePairs { get; set; } = new();
        public List<PairedElement> DestinationPairs { get; set; } = new();

        /// Item filter: off, or a whitelist / blacklist of item and group ids.
        public ItemFilterSpec Filter { get; set; } = new();

        /// How the unit shares items among its destinations: "inorder" (default) or "roundrobin".
        public string OutputMode { get; set; } = "inorder";

        /// Round robin: the cycle counter that picks the first destination. Saved at most every
        /// OutputRules.CursorSaveInterval; the engine keeps the live value.
        public long OutputCursor { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public OutputMode OutputModeValue => OutputRules.ParseStored(OutputMode);

        /// First time the unit was absent from a scan (null while placed). Persisted, so the
        /// grace period survives restarts.
        public DateTime? MissingSince { get; set; }

        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPrefixes => Source.Length > 0 || Destination.Length > 0;
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasPairs => SourcePairs.Count > 0 || DestinationPairs.Count > 0;

        public List<PairedElement> Pairs(PairRole role) => role == PairRole.Source ? SourcePairs : DestinationPairs;

        public UnitSettings Copy()
        {
            var c = (UnitSettings)MemberwiseClone();
            c.SourcePairs = (SourcePairs ?? new()).Select(p => p.Copy()).ToList();
            c.DestinationPairs = (DestinationPairs ?? new()).Select(p => p.Copy()).ToList();
            c.Filter = (Filter ?? new()).Copy();
            return c;
        }
    }

    /// Where a unit's route came from.
    public enum RouteOrigin { None, Panel, Name }

    /// Pure rules for the config panel: payload parsing, validation and route choice.
    /// No server types, so it is unit-testable.
    public static class UnitSettingsRules
    {
        /// Longest payload accepted from the client, in characters.
        public const int MaxPayloadLength = 2048;

        /// Reads {"source": "...", "destination": "..."} sent by the panel.
        /// Returns false with a player-facing error when it is not that shape.
        public static bool TryParsePayload(string? payload, out string source, out string destination, out string error)
        {
            source = destination = "";
            error = "";
            if (string.IsNullOrWhiteSpace(payload)) { error = "Nothing was sent."; return false; }
            if (payload.Length > MaxPayloadLength) { error = "The request is too long."; return false; }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { error = "The request is not readable."; return false; }
                if (!TryString(doc.RootElement, "source", out source) || !TryString(doc.RootElement, "destination", out destination))
                {
                    error = "The request is missing the source or the destination.";
                    return false;
                }
                return true;
            }
            catch (JsonException)
            {
                error = "The request is not readable.";
                return false;
            }
        }

        private static bool TryString(JsonElement obj, string name, out string value)
        {
            value = "";
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String)
                return false;
            value = p.GetString() ?? "";
            return true;
        }

        /// Trims both prefixes and checks them. Either may be empty (that side then uses only its
        /// paired elements); both empty clears the prefixes. On success the trimmed values are returned.
        public static bool Validate(string? source, string? destination, int maxLength, bool caseInsensitive,
            out string src, out string dst, out string error)
        {
            src = (source ?? "").Trim();
            dst = (destination ?? "").Trim();
            error = "";
            if (src.Length > maxLength) { error = $"Source prefix is longer than {maxLength} characters."; return false; }
            if (dst.Length > maxLength) { error = $"Destination prefix is longer than {maxLength} characters."; return false; }
            if (src.Any(char.IsControl) || dst.Any(char.IsControl)) { error = "Prefixes cannot contain control characters."; return false; }
            var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (src.Length > 0 && string.Equals(src, dst, cmp)) { error = "Source and destination are the same prefix."; return false; }
            return true;
        }

        /// The prefix route a unit runs: its saved prefixes first (one side may be empty), then,
        /// for a unit with no saved prefixes and no pairs, its name if allowed.
        public static (Route? route, RouteOrigin origin) Resolve(UnitSettings? saved, string? unitName,
            bool allowNameRoutes, string separator)
        {
            if (saved != null && saved.HasPrefixes)
                return (new Route(saved.Source, saved.Destination), RouteOrigin.Panel);
            if (allowNameRoutes && (saved == null || !saved.HasPairs))
            {
                var r = Routing.Parse(unitName, separator);
                if (r != null)
                    return (r, RouteOrigin.Name);
            }
            return (null, RouteOrigin.None);
        }

        /// Marks settings missing, once. Returns false (no change) when it was already marked,
        /// so repeated sightings of the same gone unit neither rewrite the timestamp (which
        /// would keep pushing back UnitConfigKeepHours) nor cause a repeated save.
        public static bool MarkMissing(UnitSettings s, DateTime now)
        {
            if (s.MissingSince != null) return false;
            s.MissingSince = now;
            return true;
        }
    }

    /// Per-unit settings, kept in one JSON file keyed by element id. Survives restarts;
    /// written atomically (temp file, then rename).
    public sealed class UnitStore
    {
        private readonly string path;
        private readonly object gate = new();
        private Dictionary<ulong, UnitSettings> map = new();

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public UnitStore(string path) { this.path = path; }

        public string Path => path;
        public int Count { get { lock (gate) return map.Count; } }

        /// Loads the file. A missing file is an empty store.
        public void Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) { map = new(); return; }
                var raw = JsonSerializer.Deserialize<Dictionary<string, UnitSettings>>(File.ReadAllText(path)) ?? new();
                var m = new Dictionary<ulong, UnitSettings>();
                foreach (var kv in raw)
                    if (ulong.TryParse(kv.Key, out var id) && kv.Value != null)
                    {
                        kv.Value.Source ??= "";
                        kv.Value.Destination ??= "";
                        kv.Value.UnitName ??= "";
                        kv.Value.SourcePairs ??= new();
                        kv.Value.DestinationPairs ??= new();
                        kv.Value.Filter ??= new();
                        kv.Value.Filter.Items ??= new();
                        kv.Value.Filter.Mode ??= "off";
                        kv.Value.OutputMode ??= "inorder";
                        m[id] = kv.Value;
                    }
                map = m;
            }
        }

        public UnitSettings? Get(ulong elementId)
        {
            lock (gate)
                return map.TryGetValue(elementId, out var s) ? s : null;
        }

        public void Set(ulong elementId, UnitSettings s)
        {
            lock (gate)
            {
                map[elementId] = s;
                SaveLocked();
            }
        }

        /// Applies `mutate` to a copy of the unit's settings (a new record when there is none)
        /// and publishes the copy. `mutate` returns false to change nothing. Returns the
        /// published settings, or the unchanged ones.
        public UnitSettings? Update(ulong elementId, Func<UnitSettings, bool> mutate)
        {
            lock (gate)
            {
                map.TryGetValue(elementId, out var cur);
                var copy = cur != null ? cur.Copy() : new UnitSettings();
                if (!mutate(copy)) return cur;
                map[elementId] = copy;
                SaveLocked();
                return copy;
            }
        }

        /// All saved unit ids with their settings (a snapshot).
        public Dictionary<ulong, UnitSettings> Snapshot()
        {
            lock (gate) return new Dictionary<ulong, UnitSettings>(map);
        }

        /// Moves a config to a new element id. Refuses when the target already has one.
        public bool Rekey(ulong oldId, ulong newId)
        {
            lock (gate)
            {
                if (oldId == newId || !map.TryGetValue(oldId, out var s) || map.ContainsKey(newId)) return false;
                var c = s.Copy();
                c.MissingSince = null;
                map.Remove(oldId);
                map[newId] = c;
                SaveLocked();
                return true;
            }
        }

        public bool Remove(ulong elementId)
        {
            lock (gate)
            {
                if (!map.Remove(elementId)) return false;
                SaveLocked();
                return true;
            }
        }

        /// Drops settings of units that have been absent from every scan for at least
        /// `grace`, so one bad scan never wipes the store and a unit coming back (by id or by
        /// its fallback key) keeps its config meanwhile. Returns the ids dropped.
        public List<ulong> Prune(ICollection<ulong> placed, DateTime now, TimeSpan grace)
        {
            var dropped = new List<ulong>();
            lock (gate)
            {
                bool changed = false;
                foreach (var id in map.Keys.ToList())
                {
                    var s = map[id];
                    if (placed.Contains(id))
                    {
                        if (s.MissingSince != null) { var c = s.Copy(); c.MissingSince = null; map[id] = c; changed = true; }
                        continue;
                    }
                    if (s.MissingSince == null) { var c = s.Copy(); c.MissingSince = now; map[id] = c; changed = true; continue; }
                    if (now - s.MissingSince.Value >= grace)
                    {
                        map.Remove(id);
                        dropped.Add(id);
                        changed = true;
                    }
                }
                if (changed) SaveLocked();
            }
            return dropped;
        }

        private void SaveLocked()
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var raw = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(raw, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
