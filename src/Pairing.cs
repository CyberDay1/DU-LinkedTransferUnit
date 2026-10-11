using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace LinkedTransferUnit
{
    public enum PairRole { Source, Destination }

    /// One element paired to a unit. The element id is the key; (ConstructId, LocalId,
    /// ElementTypeId) is the fallback key when the id is gone, and Name is a sanity check
    /// only.
    public sealed class PairedElement
    {
        public ulong ElementId { get; set; }
        public ulong ConstructId { get; set; }
        public ulong LocalId { get; set; }
        public ulong ElementTypeId { get; set; }
        public string Name { get; set; } = "";
        public ulong PairedBy { get; set; }
        public DateTime PairedAt { get; set; }

        public PairedElement Copy() => (PairedElement)MemberwiseClone();
    }

    /// Runtime state of a pair, recomputed every cycle (never stored).
    public enum PairStatus
    {
        /// Resolved this cycle. It may still be skipped (out of range, broken, no rights): see the note.
        Ok,
        /// The element exists but is in an inventory, or its construct is not live. Retried.
        Dormant,
        /// The element is gone and nothing matches its fallback key. Retried.
        Missing,
        /// The element is gone and the fallback found a candidate that cannot be accepted
        /// automatically (several copies, or the name differs). The player must pair again.
        RepairNeeded,
    }

    /// One `element` row as the resolve query returns it.
    public readonly struct ElementRow
    {
        public ElementRow(ulong id, ulong constructId, ulong localId, ulong typeId, bool live)
        {
            Id = id; ConstructId = constructId; LocalId = localId; TypeId = typeId; Live = live;
        }
        public ulong Id { get; }
        /// 0 when the element is in an inventory.
        public ulong ConstructId { get; }
        public ulong LocalId { get; }
        public ulong TypeId { get; }
        /// In a construct that is not deleted.
        public bool Live { get; }
    }

    /// A fallback candidate: an element with the stored (local_id, type) on the stored
    /// construct or on a construct whose originConstructId is the stored construct.
    public sealed class Candidate
    {
        public ulong ElementId;
        public ulong ConstructId;
        public ulong LocalId;
        public ulong TypeId;
        public string Name = "";
    }

    public enum ByIdOutcome
    {
        /// Same row, same construct, same local id.
        Live,
        /// Same row, but its construct or local id changed (a damaged element picked up and
        /// placed again). Accept, refresh the stored values, re-check range and rights.
        Moved,
        /// Row exists but not in a live construct.
        Dormant,
        /// No row with this id, or the type changed: try the fallback key.
        Gone,
    }

    public enum FallbackOutcome { Accepted, NoMatch, Ambiguous }

    /// Pure resolution rules. No server types, so they are unit-tested.
    public static class PairResolver
    {
        public static ByIdOutcome ById(PairedElement stored, ElementRow? row)
        {
            if (row == null) return ByIdOutcome.Gone;
            var r = row.Value;
            if (r.TypeId != stored.ElementTypeId) return ByIdOutcome.Gone;
            if (!r.Live || r.ConstructId == 0) return ByIdOutcome.Dormant;
            if (r.ConstructId != stored.ConstructId || r.LocalId != stored.LocalId) return ByIdOutcome.Moved;
            return ByIdOutcome.Live;
        }

        /// The name check of a fallback match: the same name (trimmed, exact), or the stored
        /// name was the default (empty), since a pristine element loses its name on pickup.
        public static bool NameOk(string? stored, string? candidate)
        {
            var s = (stored ?? "").Trim();
            if (s.Length == 0) return true;
            return string.Equals(s, (candidate ?? "").Trim(), StringComparison.Ordinal);
        }

        /// Fallback after the id is gone. Only ever accepts a single unambiguous match;
        /// never matches by name alone.
        /// 1. Same construct, same (local_id, type): accept when the name passes.
        /// 2. Otherwise, a construct reborn from the stored one (originConstructId): exactly
        ///    one such construct may hold a (local_id, type) match, and its name must pass.
        public static (FallbackOutcome outcome, Candidate? match) Fallback(PairedElement stored, IEnumerable<Candidate> candidates)
        {
            var c = candidates.Where(x => x.LocalId == stored.LocalId && x.TypeId == stored.ElementTypeId
                                          && x.ElementId != stored.ElementId).ToList();
            var same = c.Where(x => x.ConstructId == stored.ConstructId).ToList();
            if (same.Count > 1) return (FallbackOutcome.Ambiguous, null);
            if (same.Count == 1)
                return NameOk(stored.Name, same[0].Name) ? (FallbackOutcome.Accepted, same[0]) : (FallbackOutcome.Ambiguous, null);
            var reborn = c.Where(x => x.ConstructId != stored.ConstructId).ToList();
            if (reborn.Count == 0) return (FallbackOutcome.NoMatch, null);
            if (reborn.Select(x => x.ConstructId).Distinct().Count() > 1 || reborn.Count > 1)
                return (FallbackOutcome.Ambiguous, null);
            return NameOk(stored.Name, reborn[0].Name) ? (FallbackOutcome.Accepted, reborn[0]) : (FallbackOutcome.Ambiguous, null);
        }
    }

    /// Rules for adding and removing pairs, and the panel's pair payloads.
    public static class PairRules
    {
        public static string RoleName(PairRole r) => r == PairRole.Source ? "source" : "destination";

        public static bool TryParseRole(string? s, out PairRole role)
        {
            role = PairRole.Source;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "source": case "src": role = PairRole.Source; return true;
                case "destination": case "dest": case "dst": role = PairRole.Destination; return true;
                default: return false;
            }
        }

        /// Null when the element can be added to `existing` (the list for one role), else a
        /// player-facing reason.
        public static string? CanAdd(IReadOnlyCollection<PairedElement> existing, ulong elementId, PairRole role, int cap)
        {
            if (existing.Any(p => p.ElementId == elementId))
                return $"This element is already paired as a {RoleName(role)}.";
            if (existing.Count >= cap)
                return $"This unit already has {cap} paired {RoleName(role)}s, the maximum.";
            return null;
        }

        /// Everything "Pair with transfer unit" checks, gathered by the caller.
        public sealed class PairCheck
        {
            public PairRole Role;
            public bool IsUnitItself;
            public bool OnLiveConstruct;
            public bool IsContainer;
            public bool UnitEdit;           // pairing player: Element Edit on the unit
            public bool TargetRight;        // pairing player: Retrieve (source) / Put (destination) on the element
            public double? DistanceMeters;  // null = position unknown
            public double RangeMeters;
            public IReadOnlyCollection<PairedElement> Existing = Array.Empty<PairedElement>();
            public ulong ElementId;
            public int Cap;
        }

        /// Null when the pair may be added, else the player-facing reason. Order: what the
        /// element is, rights, range, then the unit's list.
        public static string? Check(PairCheck c)
        {
            if (c.IsUnitItself) return "Right-click the container, hub or fuel tank to pair, not the unit.";
            if (!c.OnLiveConstruct) return "That element is not on a construct.";
            if (!c.IsContainer) return "Only containers, hubs and fuel tanks can be paired.";
            if (!c.UnitEdit) return "You need the Element Edit right on the unit to pair it.";
            if (!c.TargetRight)
                return $"You need the Container {(c.Role == PairRole.Source ? "Retrieve" : "Put")} right on that element to pair it as a {RoleName(c.Role)}.";
            if (c.DistanceMeters == null) return "Could not locate that element. Try again.";
            if (c.DistanceMeters.Value > c.RangeMeters)
                return $"That element is {c.DistanceMeters.Value:F0} m from the unit; the range is {c.RangeMeters:F0} m.";
            return CanAdd(c.Existing, c.ElementId, c.Role, c.Cap);
        }

        /// Reads {"role": "source"|"destination", "elementId": n} (unpair), or {"role": ...} when
        /// `needElement` is false (start pairing from the panel).
        public static bool TryParseRolePayload(string? payload, bool needElement, out PairRole role, out ulong elementId, out string error)
        {
            role = PairRole.Source;
            elementId = 0;
            error = "";
            if (string.IsNullOrWhiteSpace(payload) || payload.Length > UnitSettingsRules.MaxPayloadLength)
            {
                error = "The request is not readable.";
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(payload);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty("role", out var r) || r.ValueKind != JsonValueKind.String
                    || !TryParseRole(r.GetString(), out role))
                {
                    error = "The request names no role.";
                    return false;
                }
                if (!needElement) return true;
                if (!o.TryGetProperty("elementId", out var e)) { error = "The request names no element."; return false; }
                if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out elementId) && elementId != 0) return true;
                // Ids above 2^53 cannot be JS numbers, so the panel may send them as strings.
                if (e.ValueKind == JsonValueKind.String && ulong.TryParse(e.GetString(), out elementId) && elementId != 0) return true;
                error = "The request names no element.";
                return false;
            }
            catch (JsonException)
            {
                error = "The request is not readable.";
                return false;
            }
        }
    }

    /// Players in pairing mode: one pending (unit, role) per player, for a limited time.
    /// Thread-safe.
    public sealed class PairingSessions
    {
        public sealed class Session
        {
            public ulong UnitId;
            public ulong UnitConstructId;
            public PairRole Role;
            public DateTime Expires;
        }

        private readonly Dictionary<ulong, Session> map = new();
        private readonly object gate = new();

        public void Begin(ulong playerId, ulong unitId, ulong unitConstructId, PairRole role, DateTime now, TimeSpan window)
        {
            lock (gate) map[playerId] = new Session { UnitId = unitId, UnitConstructId = unitConstructId, Role = role, Expires = now + window };
        }

        /// The player's live session, or null (expired sessions are dropped).
        public Session? Get(ulong playerId, DateTime now)
        {
            lock (gate)
            {
                if (!map.TryGetValue(playerId, out var s)) return null;
                if (now >= s.Expires) { map.Remove(playerId); return null; }
                return new Session { UnitId = s.UnitId, UnitConstructId = s.UnitConstructId, Role = s.Role, Expires = s.Expires };
            }
        }

        /// Pushes the expiry out after a successful pair, so several can be paired in a row.
        public void Extend(ulong playerId, DateTime now, TimeSpan window)
        {
            lock (gate)
                if (map.TryGetValue(playerId, out var s)) s.Expires = now + window;
        }

        public void End(ulong playerId) { lock (gate) map.Remove(playerId); }
    }

    /// A placed unit as the rescan sees it, for re-keying its saved config.
    public sealed class PlacedUnit
    {
        public ulong ElementId;
        public ulong ConstructId;
        public ulong LocalId;
        public ulong TypeId;
        /// originConstructId of the unit's construct (0 when none).
        public ulong OriginConstructId;
        public string Name = "";
    }

    /// Moves a saved unit config to the unit's new element id when the old id is gone (a
    /// pristine unit picked up and placed back, a Repair Unit, a compaction or blueprint
    /// redeploy). Same rules as pairs: exactly one match on (construct, local_id, type), or on
    /// (local_id, type) in exactly one construct reborn from the stored one; name as a check.
    public static class UnitRekey
    {
        /// Returns old id -> new id for every orphaned config that has one unambiguous home.
        /// `orphans`: configs whose id is not placed. `fresh`: placed units with no config.
        public static Dictionary<ulong, ulong> Match(IReadOnlyDictionary<ulong, UnitSettings> orphans, IReadOnlyCollection<PlacedUnit> fresh)
        {
            var result = new Dictionary<ulong, ulong>();
            var claims = new Dictionary<ulong, int>();
            foreach (var kv in orphans.OrderBy(k => k.Key))
            {
                var s = kv.Value;
                if (s.ConstructId == 0 || s.ElementTypeId == 0) continue;   // saved before the fallback key existed
                var stored = new PairedElement
                {
                    ElementId = kv.Key, ConstructId = s.ConstructId, LocalId = s.LocalId, ElementTypeId = s.ElementTypeId, Name = s.UnitName,
                };
                var candidates = fresh
                    .Where(u => u.ConstructId == s.ConstructId || (u.OriginConstructId != 0 && u.OriginConstructId == s.ConstructId))
                    .Select(u => new Candidate { ElementId = u.ElementId, ConstructId = u.ConstructId, LocalId = u.LocalId, TypeId = u.TypeId, Name = u.Name });
                var (outcome, match) = PairResolver.Fallback(stored, candidates);
                if (outcome != FallbackOutcome.Accepted || match == null) continue;
                claims[match.ElementId] = claims.TryGetValue(match.ElementId, out var n) ? n + 1 : 1;
                result[kv.Key] = match.ElementId;
            }
            // Two old configs want the same new unit: give it to neither.
            return result.Where(r => claims[r.Value] == 1).ToDictionary(r => r.Key, r => r.Value);
        }
    }
}
