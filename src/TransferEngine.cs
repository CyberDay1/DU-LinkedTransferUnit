using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Backend;
using Backend.Scenegraph;
using Backend.Storage;
using Microsoft.Extensions.Logging;
using NQ;
using NQ.Interfaces;
using NQ.RDMS;
using NQutils.Sql;
using NQutils.Storage;
using Orleans;

namespace LinkedTransferUnit
{
    /// One placed linked transfer unit.
    public sealed class UnitRef
    {
        public ulong ElementId;
        public ulong ConstructId;
        public ulong LocalId;
        public ulong TypeId;
    }

    /// A container (or fuel tank, or hub pool) a unit can reach.
    public sealed class Endpoint
    {
        public ulong ElementId;     // the element that was matched or paired
        public ulong RootId;        // storage actually used (hub root when linked)
        public ulong ConstructId;
        public string Name = "";
        public bool IsFuelTank;
        public string FuelType = "";
        public double DistanceMeters;
        /// Destinations: index in the unit's saved destination pairs, -1 for a prefix match.
        public int PairIndex = -1;
    }

    /// The acting identity for a unit: the owner of the unit's construct.
    public sealed class Actor
    {
        public EntityId Owner;
        public ulong PlayerId;      // player whose rights are checked and who is the storage requester
        public override string ToString() =>
            Owner.playerId != 0 ? $"player {Owner.playerId}" : $"org {Owner.organizationId} (legate {PlayerId})";
    }

    /// One pair as the panel shows it.
    public sealed class PairView
    {
        public PairRole Role;
        public ulong ElementId;
        public ulong ConstructId;
        public ulong LocalId;
        public string Name = "";
        public string TypeName = "";
        public string ConstructName = "";
        public PairStatus Status;
        /// Why an Ok pair is not used this cycle (out of range, broken, no rights), or detail
        /// for the other states. Empty when it is in use.
        public string Note = "";

        public string StatusName => Status switch
        {
            PairStatus.Ok => "ok",
            PairStatus.Dormant => "dormant",
            PairStatus.Missing => "missing",
            _ => "repair",
        };
    }

    public sealed class UnitReport
    {
        public string Status = "";
        public bool Running;
        public RouteOrigin Origin;
        public string Route = "";
        public int Sources;
        public int Destinations;
        public int DeniedByRights;
        public string Actor = "";        // "player 10000", "" when not resolved
        public string Filter = "";       // "", or "whitelist of 3" / "blacklist of 1"
        public double MovedLitres;
        public DateTime At;
        /// The unit's most recent refused or failed move (or failed cycle), "" when none.
        public string LastError = "";
        public DateTime? LastErrorAt;
        public List<PairView> Pairs = new();
        /// Output mode, and the destinations found this check in the order the mode uses
        /// (round robin: rotated to the destination the next cycle starts at).
        public OutputMode Output;
        public List<string> DestinationOrder = new();
    }

    public sealed class TransferEngine
    {
        private readonly IServiceProvider isp;
        private readonly IClusterClient orleans;
        private readonly IGameplayBank bank;
        private readonly IScenegraph scenegraph;
        private readonly IItemStorageService storage;
        private readonly ISql sql;
        private readonly ILogger logger;
        private readonly LtuConfig cfg;
        private readonly UnitStore store;
        private readonly MoveJournal journal;
        private readonly ContainerLocks locks = new();
        /// Debug-only injected faults (see FaultPlan); empty unless an admin armed one.
        public readonly FaultPlan Faults = new();
        /// Last error per unit: a refused move, a failed cycle, or an interrupted move.
        private readonly ConcurrentDictionary<ulong, (DateTime at, string msg)> lastErrors = new();

        private HashSet<ulong> unitTypeIds = new();
        private ConcurrentDictionary<ulong, UnitRef> placed = new();   // element id -> unit
        private List<UnitRef> units = new();
        // Guards `units` (and the handful of moments `placed` is swapped wholesale): a hooked
        // call can register a unit from any grain-call thread, concurrently with RunCycle's
        // loop and with Rescan. `placed` itself is a ConcurrentDictionary so plain reads
        // (IsKnownUnit, FindUnit) need no lock.
        private readonly object unitsLock = new();
        private DateTime lastScan = DateTime.MinValue;
        private DateTime lastHeartbeat = DateTime.MinValue;
        private int roundRobin;
        private long cycle;

        private readonly ConcurrentDictionary<ulong, (DateTime at, List<ElementInfo> containers)> containerCache = new();
        /// Round-robin cursor per unit: the live value and when it was last written to the store.
        private readonly ConcurrentDictionary<ulong, (long cursor, DateTime? saved)> cursors = new();
        public readonly ConcurrentDictionary<ulong, UnitReport> Reports = new();
        /// Last known running flag per unit (from the element's industry status). Read by the
        /// IndustryUnitGrain.Status hook, which must not make grain calls of its own.
        public readonly ConcurrentDictionary<ulong, bool> RunningCache = new();
        /// Last fallback outcome per (unit, role, paired element id), so a gone pair is looked
        /// up at most every PairRetrySeconds.
        private readonly ConcurrentDictionary<(ulong unit, PairRole role, ulong element), (DateTime at, PairStatus status)> fallbackTried = new();

        public TransferEngine(IServiceProvider isp, IClusterClient orleans, IGameplayBank bank, IScenegraph scenegraph,
            IItemStorageService storage, ISql sql, ILogger logger, LtuConfig cfg, UnitStore store, MoveJournal journal)
        {
            this.store = store;
            this.journal = journal;
            this.isp = isp;
            this.orleans = orleans;
            this.bank = bank;
            this.scenegraph = scenegraph;
            this.storage = storage;
            this.sql = sql;
            this.logger = logger;
            this.cfg = cfg;
        }

        public bool IsUnitType(ulong elementType) => unitTypeIds.Contains(elementType);

        /// True when the element is a placed unit seen by the last scan (no I/O).
        public bool IsKnownUnit(ulong elementId) => placed.ContainsKey(elementId);

        /// The placed unit with this element id, or null when the element is not one.
        /// Uses the last scan, then the database for a unit placed since (and registers it
        /// immediately on a hit, so it is never missing from the active set after this call —
        /// see RegisterUnit).
        public async Task<UnitRef?> FindUnit(ulong elementId)
        {
            if (placed.TryGetValue(elementId, out var hit))
                return hit;
            if (unitTypeIds.Count == 0)
                return null;
            var rows = await sql.Query<long, long, long>(
                "SELECT construct_id, COALESCE(local_id, 0), element_type_id FROM element WHERE id = @1 AND construct_id IS NOT NULL", elementId);
            foreach (var r in rows)
                if (unitTypeIds.Contains((ulong)r.Item3))
                {
                    var u = new UnitRef { ElementId = elementId, ConstructId = (ulong)r.Item1, LocalId = (ulong)r.Item2, TypeId = (ulong)r.Item3 };
                    RegisterUnit(u);
                    return u;
                }
            return null;
        }

        /// Adds a unit resolved outside the normal scan — a hooked Start/Stop/Status/SetRecipe
        /// call via FindUnit, or the cheap ConstructElementsGrain.AddElement placement hook — to
        /// the active set right away, instead of waiting up to RescanSeconds for the next Rescan.
        /// Fixes the stale-Status / stale-getState() bug: without this, IndustryHooks.Status's
        /// IsKnownUnit gate stayed false (and a fresh cycle never ran) for up to RescanSeconds
        /// after a unit was placed and started. No-op when the unit is already known. Benign
        /// race: if this lands in the instant Rescan is swapping `placed` to a new snapshot, the
        /// registration can be lost; the next FindUnit/hook call (or the next Rescan) repeats it.
        public void RegisterUnit(UnitRef u)
        {
            if (!placed.TryAdd(u.ElementId, u)) return;
            lock (unitsLock) units.Add(u);
            logger.LogInformation("LinkedTransferUnit: unit {unit} registered immediately (seen before its first scan)", u.ElementId);
        }

        /// The unit's industry state is RUNNING. The state lives in the element's stock
        /// "status" property (IndustryUnit.d_status), which the stock panel also shows.
        public bool IsRunning(ElementInfo unit)
        {
            var def = bank.GetDefinition(unit.elementType);
            if (def == null) return false;
            var state = Backend.ElementPropertiesHelper.GetPropertyOrDefault(unit.properties, def.BaseObject,
                (NQutils.Def.IDynamicProperty<IndustryState>)NQutils.Def.IndustryUnit.d_status);
            return state == IndustryState.RUNNING;
        }

        /// Writes the unit's industry state (RUNNING or STOPPED) to its "status" property,
        /// and remembers it in the unit's saved config.
        public async Task SetRunning(UnitRef u, bool running)
        {
            var grain = orleans.GetConstructElementsGrain(u.ConstructId);
            var unit = await grain.GetElement(u.ElementId);
            await grain.UpdateElementProperty(ElementPropertyUpdate.Create(unit, NQutils.Def.IndustryUnit.d_status,
                running ? IndustryState.RUNNING : IndustryState.STOPPED));
            RunningCache[u.ElementId] = running;
            FlushCursor(u.ElementId);
            store.Update(u.ElementId, s =>
            {
                if (s.Running == running && s.ConstructId != 0) return false;
                s.Running = running;
                Remember(s, u, unit);
                return true;
            });
        }

        /// Shows a recipe id in the unit's "recipe" property, so the stock panel accepts a
        /// recipe pick. The mod ignores the recipe.
        public async Task SetRecipeShown(UnitRef u, ulong recipeId)
        {
            var grain = orleans.GetConstructElementsGrain(u.ConstructId);
            var unit = await grain.GetElement(u.ElementId);
            await grain.UpdateElementProperty(ElementPropertyUpdate.Create(unit, NQutils.Def.IndustryUnit.d_recipe, (long)recipeId));
        }

        public UnitSettings? Settings(ulong elementId) => store.Get(elementId);

        /// Writes the unit's own fallback key into its settings. True when anything changed.
        public static bool Remember(UnitSettings s, UnitRef u, ElementInfo unit)
        {
            var name = NameOf(unit) ?? "";
            if (s.ConstructId == unit.constructId && s.LocalId == unit.localId && s.ElementTypeId == unit.elementType && s.UnitName == name)
                return false;
            s.ConstructId = unit.constructId;
            s.LocalId = unit.localId;
            s.ElementTypeId = unit.elementType;
            s.UnitName = name;
            return true;
        }

        // ------------------------------------------------------------------ loop

        public async Task RunForever(CancellationToken ct)
        {
            logger.LogInformation("LinkedTransferUnit: transfer loop started (cycle {cycle}s, max range {range} m, cap {cap} L/unit/cycle, {pairs} pairs per role)",
                cfg.CycleSeconds, cfg.MaxRangeMeters, cfg.MaxLitresPerUnitPerCycle, cfg.MaxPairsPerRole);
            // Moves interrupted by the last shutdown or crash: checked and reported, never replayed.
            try { await ResolveJournal(all: false); }
            catch (Exception e) { logger.LogError(e, "LinkedTransferUnit: in-flight journal {path} could not be resolved at startup", journal.Path); }
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RunCycle();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "LinkedTransferUnit: cycle {cycle} failed", cycle);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(cfg.CycleSeconds), ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        public async Task RunCycle()
        {
            cycle++;
            if ((DateTime.UtcNow - lastScan).TotalSeconds >= cfg.RescanSeconds)
                await Rescan();
            if (journal.Count > 0)
            {
                try { await ResolveJournal(all: false); }
                catch (Exception e) { logger.LogWarning(e, "LinkedTransferUnit: in-flight journal check failed this cycle"); }
            }

            List<UnitRef> snapshot;
            lock (unitsLock) snapshot = new List<UnitRef>(units);
            var count = snapshot.Count;
            var batch = new List<UnitRef>();
            for (int i = 0; i < count && batch.Count < cfg.MaxUnitsPerCycle; i++)
                batch.Add(snapshot[(roundRobin + i) % count]);
            if (count > 0)
                roundRobin = (roundRobin + batch.Count) % count;

            // Every pair of every unit in this cycle, resolved by element id in one query.
            Dictionary<ulong, PairRow> rows;
            try { rows = await QueryPairRows(batch.Select(u => store.Get(u.ElementId))); }
            catch (Exception e)
            {
                logger.LogWarning(e, "LinkedTransferUnit: pair lookup failed; paired elements skipped this cycle");
                rows = null;
            }

            int active = 0;
            double moved = 0;
            foreach (var u in batch)
            {
                try
                {
                    var r = await ProcessUnit(u, dryRun: false, rows);
                    StampError(u.ElementId, r);
                    Reports[u.ElementId] = r;
                    moved += r.MovedLitres;
                    if (r.Sources > 0 && r.Destinations > 0) active++;
                }
                catch (Exception e)
                {
                    if (IsElementNotFound(e))
                        DropMissingUnit(u);
                    else
                    {
                        RecordError(u.ElementId, "cycle failed: " + ErrorText(e));
                        var r = new UnitReport { Status = "error: " + e.Message, At = DateTime.UtcNow };
                        StampError(u.ElementId, r);
                        Reports[u.ElementId] = r;
                        logger.LogWarning(e, "LinkedTransferUnit: unit {unit} failed", u.ElementId);
                    }
                }
            }

            if (moved > 0)
                logger.LogInformation("LinkedTransferUnit: cycle {cycle}: {units} unit(s) processed, {active} with a live route, {litres:F1} L moved",
                    cycle, batch.Count, active, moved);
            if (cycle == 1 || (DateTime.UtcNow - lastHeartbeat).TotalMinutes >= cfg.HeartbeatMinutes)
            {
                lastHeartbeat = DateTime.UtcNow;
                logger.LogInformation("LinkedTransferUnit: heartbeat cycle {cycle}: {count} unit(s) placed, {processed} processed this cycle",
                    cycle, count, batch.Count);
            }
        }

        /// NQ.ErrorCode.ElementNotFound: ProcessUnit's own GetElement(u.ElementId) throws this
        /// when the unit's construct or element was deleted since the last scan.
        private static bool IsElementNotFound(Exception e) =>
            e is NQutils.Exceptions.BusinessException be && be.error.code == NQ.ErrorCode.ElementNotFound;

        /// The unit's element is gone. Drops it from the active set right away instead of
        /// retrying it (and warning) every cycle until the next Rescan, up to RescanSeconds
        /// later. Its saved config, if any, is marked missing now (UnitConfigKeepHours starts
        /// counting from this moment, not from the next scan) and left in place for the
        /// existing re-resolve / prune logic — never removed here. Logs once: once dropped, the
        /// unit is out of `units` and this cannot run again for it until it is seen afresh.
        private void DropMissingUnit(UnitRef u)
        {
            bool wasActive;
            lock (unitsLock) wasActive = units.RemoveAll(x => x.ElementId == u.ElementId) > 0;
            placed.TryRemove(u.ElementId, out _);
            Reports.TryRemove(u.ElementId, out _);
            RunningCache.TryRemove(u.ElementId, out _);
            lastErrors.TryRemove(u.ElementId, out _);
            Faults.Disarm(u.ElementId);
            if (store.Get(u.ElementId) != null)
                store.Update(u.ElementId, s => UnitSettingsRules.MarkMissing(s, DateTime.UtcNow));
            if (wasActive)
                logger.LogInformation("LinkedTransferUnit: unit {unit} is gone (element not found); dropped from the active set until it is placed and scanned again", u.ElementId);
        }

        // ------------------------------------------------------------------ discovery

        private async Task Rescan()
        {
            lastScan = DateTime.UtcNow;
            var root = bank.GetDefinition(cfg.ItemName);
            if (root == null)
            {
                if (units.Count > 0 || cycle == 1)
                    logger.LogWarning("LinkedTransferUnit: item {item} is not in the item bank; no units can exist", cfg.ItemName);
                units = new();
                unitTypeIds = new();
                return;
            }
            var ids = new HashSet<ulong> { root.Id };
            foreach (var cid in root.GetChildrenIdsRecursive())
                ids.Add(cid);
            unitTypeIds = ids;

            var rows = await sql.Query<long, long, long, long>(
                "SELECT e.id, e.construct_id, COALESCE(e.local_id, 0), e.element_type_id FROM element e JOIN construct c ON c.id = e.construct_id " +
                "WHERE e.element_type_id = ANY(@1) AND c.deleted_at IS NULL", ids.ToArray());
            var found = rows.Select(r => new UnitRef
            {
                ElementId = (ulong)r.Item1, ConstructId = (ulong)r.Item2, LocalId = (ulong)r.Item3, TypeId = (ulong)r.Item4,
            }).ToList();
            int before;
            lock (unitsLock) before = units.Count;
            if (found.Count != before)
                logger.LogInformation("LinkedTransferUnit: {count} placed unit(s) found", found.Count);
            lock (unitsLock) units = found;
            // Atomic reference swap (never Clear()+repopulate): a reader never sees an empty
            // `placed` mid-rebuild, which would reopen the very bug RegisterUnit exists to fix.
            placed = new ConcurrentDictionary<ulong, UnitRef>(found.ToDictionary(f => f.ElementId, f => f));
            foreach (var gone in Reports.Keys.Except(placed.Keys).ToList())
                Reports.TryRemove(gone, out _);
            foreach (var gone in RunningCache.Keys.Except(placed.Keys).ToList())
                RunningCache.TryRemove(gone, out _);
            foreach (var k in fallbackTried.Keys.Where(k => !placed.ContainsKey(k.unit)).ToList())
                fallbackTried.TryRemove(k, out _);
            foreach (var gone in lastErrors.Keys.Except(placed.Keys).ToList())
                lastErrors.TryRemove(gone, out _);
            locks.Prune();

            try { await RekeyUnits(); }
            catch (Exception e) { logger.LogWarning(e, "LinkedTransferUnit: unit config fallback lookup failed"); }

            try
            {
                foreach (var id in store.Prune(placed.Keys, DateTime.UtcNow, TimeSpan.FromHours(cfg.UnitConfigKeepHours)))
                    logger.LogInformation("LinkedTransferUnit: unit {unit} has been gone for {hours} h; dropped its saved config", id, cfg.UnitConfigKeepHours);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "LinkedTransferUnit: could not prune the unit store {path}", store.Path);
            }
        }

        /// A saved config whose unit id is gone moves to a placed unit with no config when the
        /// unit's fallback key matches it unambiguously (see UnitRekey).
        private async Task RekeyUnits()
        {
            var saved = store.Snapshot();
            var orphans = saved.Where(kv => !placed.ContainsKey(kv.Key) && kv.Value.ConstructId != 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            if (orphans.Count == 0) return;
            var fresh = units.Where(u => !saved.ContainsKey(u.ElementId)).ToList();
            if (fresh.Count == 0) return;

            var origins = await OriginsOf(fresh.Select(u => u.ConstructId).Distinct().ToArray());
            var orphanConstructs = orphans.Values.Select(s => s.ConstructId).ToHashSet();
            var candidates = new List<PlacedUnit>();
            foreach (var u in fresh)
            {
                origins.TryGetValue(u.ConstructId, out var origin);
                if (!orphanConstructs.Contains(u.ConstructId) && !orphanConstructs.Contains(origin)) continue;
                string name = "";
                try { name = NameOf(await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId)) ?? ""; }
                catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: unit {unit} name unreadable", u.ElementId); continue; }
                candidates.Add(new PlacedUnit
                {
                    ElementId = u.ElementId, ConstructId = u.ConstructId, LocalId = u.LocalId, TypeId = u.TypeId,
                    OriginConstructId = origin, Name = name,
                });
            }
            if (candidates.Count == 0) return;

            foreach (var (oldId, newId) in UnitRekey.Match(orphans, candidates))
            {
                if (!store.Rekey(oldId, newId)) continue;
                var s = store.Get(newId);
                logger.LogInformation("LinkedTransferUnit: unit {old} is now element {new}; its saved config ({src} source pair(s), {dst} destination pair(s)) moved with it",
                    oldId, newId, s?.SourcePairs.Count ?? 0, s?.DestinationPairs.Count ?? 0);
                if (s != null && s.Running && placed.TryGetValue(newId, out var u))
                {
                    try { await SetRunning(u, true); }
                    catch (Exception e) { logger.LogWarning(e, "LinkedTransferUnit: unit {unit} could not be restarted after the move", newId); }
                }
            }
        }

        /// originConstructId of each construct (0 when none).
        private async Task<Dictionary<ulong, ulong>> OriginsOf(ulong[] constructIds)
        {
            var result = new Dictionary<ulong, ulong>();
            if (constructIds.Length == 0) return result;
            var rows = await sql.Query<long, string>(
                "SELECT id, COALESCE(json_properties->'serverProperties'->>'originConstructId', '0') FROM construct WHERE id = ANY(@1)", constructIds);
            foreach (var r in rows)
                result[(ulong)r.Item1] = ulong.TryParse(r.Item2, out var o) ? o : 0;
            return result;
        }

        // ------------------------------------------------------------------ pairs

        /// One `element` row for a paired id, plus its construct's name.
        public sealed class PairRow
        {
            /// Null when the id was looked up and has no row.
            public ElementRow? Row;
            public string ConstructName = "";
        }

        /// Resolves every paired element id of the given units in ONE query. Every id asked
        /// for gets an entry; a gone id has a null Row.
        private async Task<Dictionary<ulong, PairRow>> QueryPairRows(IEnumerable<UnitSettings?> settings)
        {
            var ids = settings.Where(s => s != null)
                .SelectMany(s => s!.SourcePairs.Concat(s.DestinationPairs))
                .Select(p => p.ElementId).Distinct().ToArray();
            var result = ids.ToDictionary(id => id, id => new PairRow());
            if (ids.Length == 0) return result;
            var rows = await sql.Query<long, long, long, long, int, string>(
                "SELECT e.id, COALESCE(e.construct_id, 0), COALESCE(e.local_id, 0), e.element_type_id, " +
                "CASE WHEN e.construct_id IS NOT NULL AND c.deleted_at IS NULL THEN 1 ELSE 0 END, COALESCE(c.name, '') " +
                "FROM element e LEFT JOIN construct c ON c.id = e.construct_id WHERE e.id = ANY(@1)", ids);
            foreach (var r in rows)
                result[(ulong)r.Item1] = new PairRow
                {
                    Row = new ElementRow((ulong)r.Item1, (ulong)r.Item2, (ulong)r.Item3, (ulong)r.Item4, r.Item5 == 1),
                    ConstructName = r.Item6 ?? "",
                };
            return result;
        }

        /// Fallback candidates for a gone pair: (local_id, type) on the stored construct, or on a
        /// live construct whose originConstructId is the stored construct.
        private async Task<List<(Candidate c, string constructName)>> FallbackCandidates(PairedElement p)
        {
            var rows = await sql.Query<long, long, long, long, string>(
                "SELECT e.id, e.construct_id, e.local_id, e.element_type_id, COALESCE(c.name, '') FROM element e JOIN construct c ON c.id = e.construct_id " +
                "WHERE c.deleted_at IS NULL AND e.local_id = @1 AND e.element_type_id = @2 " +
                "AND (e.construct_id = @3 OR c.json_properties->'serverProperties'->>'originConstructId' = @4) LIMIT 16",
                (long)p.LocalId, (long)p.ElementTypeId, (long)p.ConstructId, p.ConstructId.ToString());
            var result = new List<(Candidate, string)>();
            foreach (var r in rows)
            {
                var c = new Candidate { ElementId = (ulong)r.Item1, ConstructId = (ulong)r.Item2, LocalId = (ulong)r.Item3, TypeId = (ulong)r.Item4 };
                try { c.Name = NameOf(await orleans.GetConstructElementsGrain(c.ConstructId).GetElement(c.ElementId)) ?? ""; }
                catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: candidate {id} unreadable", c.ElementId); continue; }
                result.Add((c, r.Item5 ?? ""));
            }
            return result;
        }

        /// A live, resolved pair: its element and where it is.
        private sealed class LivePair
        {
            public PairRole Role;
            public PairView View;
            public ElementInfo Element;
        }

        /// Resolves the unit's pairs (by id, else by fallback key), writes back any moved or
        /// re-resolved element, and returns the views plus the live elements.
        private async Task<(List<PairView> views, List<LivePair> live)> ResolvePairs(UnitRef u, UnitSettings? s, Dictionary<ulong, PairRow>? rows)
        {
            var views = new List<PairView>();
            var live = new List<LivePair>();
            if (s == null || !s.HasPairs) return (views, live);
            // The cycle's batch query, unless a pair was added since it ran (or there was none).
            if (rows == null || s.SourcePairs.Concat(s.DestinationPairs).Any(p => !rows.ContainsKey(p.ElementId)))
                rows = await QueryPairRows(new[] { s });

            foreach (var role in new[] { PairRole.Source, PairRole.Destination })
            {
                foreach (var stored in s.Pairs(role))
                {
                    var view = new PairView
                    {
                        Role = role, ElementId = stored.ElementId, ConstructId = stored.ConstructId, LocalId = stored.LocalId,
                        Name = stored.Name, TypeName = TypeName(stored.ElementTypeId),
                    };
                    views.Add(view);
                    rows.TryGetValue(stored.ElementId, out var pr);
                    var outcome = PairResolver.ById(stored, pr?.Row);
                    ulong elementId = stored.ElementId, constructId = stored.ConstructId;
                    string constructName = pr?.ConstructName ?? "";

                    if (outcome == ByIdOutcome.Dormant)
                    {
                        view.Status = PairStatus.Dormant;
                        view.Note = "in an inventory, or its construct is not live";
                        continue;
                    }
                    if (outcome == ByIdOutcome.Gone)
                    {
                        var key = (u.ElementId, role, stored.ElementId);
                        if (fallbackTried.TryGetValue(key, out var last) && (DateTime.UtcNow - last.at).TotalSeconds < cfg.PairRetrySeconds)
                        {
                            view.Status = last.status;
                            view.Note = NoteFor(last.status);
                            continue;
                        }
                        var cands = await FallbackCandidates(stored);
                        var (fo, match) = PairResolver.Fallback(stored, cands.Select(c => c.c));
                        if (fo != FallbackOutcome.Accepted || match == null)
                        {
                            var st = fo == FallbackOutcome.Ambiguous ? PairStatus.RepairNeeded : PairStatus.Missing;
                            if (!fallbackTried.TryGetValue(key, out var prev) || prev.status != st)
                                logger.LogInformation("LinkedTransferUnit: unit {unit} {role} pair {id} ({name}) is gone: {what}",
                                    u.ElementId, PairRules.RoleName(role), stored.ElementId, stored.Name,
                                    st == PairStatus.RepairNeeded ? $"{cands.Count} candidate(s), none unambiguous; re-pair needed" : "no match by construct, local id and type");
                            fallbackTried[key] = (DateTime.UtcNow, st);
                            view.Status = st;
                            view.Note = NoteFor(st);
                            continue;
                        }
                        fallbackTried.TryRemove(key, out _);
                        elementId = match.ElementId;
                        constructId = match.ConstructId;
                        constructName = cands.First(c => c.c.ElementId == match.ElementId).constructName;
                        var replaced = Replace(u.ElementId, role, stored.ElementId, match.ElementId, match.ConstructId, match.LocalId, match.Name);
                        logger.LogInformation("LinkedTransferUnit: unit {unit} {role} pair {old} re-resolved to element {new} on construct {cid} (L{local}){dup}",
                            u.ElementId, PairRules.RoleName(role), stored.ElementId, match.ElementId, match.ConstructId, match.LocalId,
                            replaced ? "" : "; already paired, old entry dropped");
                        view.ElementId = elementId;
                        view.ConstructId = constructId;
                        view.LocalId = match.LocalId;
                    }
                    else if (outcome == ByIdOutcome.Moved)
                    {
                        var r = pr!.Row!.Value;
                        constructId = r.ConstructId;
                        Replace(u.ElementId, role, stored.ElementId, stored.ElementId, r.ConstructId, r.LocalId, null);
                        logger.LogInformation("LinkedTransferUnit: unit {unit} {role} pair {id} moved to construct {cid} (L{local})",
                            u.ElementId, PairRules.RoleName(role), stored.ElementId, r.ConstructId, r.LocalId);
                        view.ConstructId = r.ConstructId;
                        view.LocalId = r.LocalId;
                    }

                    ElementInfo ei;
                    try { ei = await orleans.GetConstructElementsGrain(constructId).GetElement(elementId); }
                    catch (Exception e)
                    {
                        logger.LogDebug(e, "LinkedTransferUnit: paired element {id} unreadable", elementId);
                        view.Status = PairStatus.Dormant;
                        view.Note = "not readable right now";
                        continue;
                    }
                    view.Status = PairStatus.Ok;
                    view.ConstructName = constructName;
                    var name = NameOf(ei) ?? "";
                    view.Name = name;
                    if (name != stored.Name && outcome == ByIdOutcome.Live)
                        store.Update(u.ElementId, x =>
                        {
                            var p = x.Pairs(role).FirstOrDefault(q => q.ElementId == elementId);
                            if (p == null || p.Name == name) return false;
                            p.Name = name;
                            return true;
                        });
                    live.Add(new LivePair { Role = role, View = view, Element = ei });
                }
            }
            return (views, live);
        }

        private static string NoteFor(PairStatus s) => s switch
        {
            PairStatus.Missing => "element gone; nothing matches it yet",
            PairStatus.RepairNeeded => "element gone; pair it again",
            _ => "",
        };

        /// Rewrites one stored pair to a new element / place. When the new element is already
        /// paired in that role the old entry is dropped instead. False in that case.
        private bool Replace(ulong unitId, PairRole role, ulong oldId, ulong newId, ulong constructId, ulong localId, string? name)
        {
            bool dup = false;
            store.Update(unitId, s =>
            {
                var list = s.Pairs(role);
                var p = list.FirstOrDefault(q => q.ElementId == oldId);
                if (p == null) return false;
                if (newId != oldId && list.Any(q => q.ElementId == newId))
                {
                    list.Remove(p);
                    dup = true;
                    return true;
                }
                p.ElementId = newId;
                p.ConstructId = constructId;
                p.LocalId = localId;
                if (name != null) p.Name = name;
                return true;
            });
            return !dup;
        }

        public string TypeName(ulong typeId)
        {
            var d = bank.GetDefinition(typeId);
            if (d == null) return typeId.ToString();
            try
            {
                var dn = (d.BaseObject as NQutils.Def.GameplayObject)?.displayName;
                if (!string.IsNullOrEmpty(dn)) return dn;
            }
            catch { }
            return d.Name;
        }

        // ------------------------------------------------------------------ one unit

        public Task<UnitReport> ProcessUnit(UnitRef u, bool dryRun = false) => ProcessUnit(u, dryRun, null);

        private async Task<UnitReport> ProcessUnit(UnitRef u, bool dryRun, Dictionary<ulong, PairRow>? rows)
        {
            var report = new UnitReport { At = DateTime.UtcNow };
            StampError(u.ElementId, report);
            var unit = await orleans.GetConstructElementsGrain(u.ConstructId).GetElement(u.ElementId);
            report.Running = IsRunning(unit);
            RunningCache[u.ElementId] = report.Running;
            var saved = store.Get(u.ElementId);
            if (saved != null)
                saved = store.Update(u.ElementId, s => Remember(s, u, unit));

            var (route, origin) = UnitSettingsRules.Resolve(saved, NameOf(unit), cfg.AllowNameRoutes, cfg.RouteSeparator);
            report.Origin = origin;
            report.Route = route?.ToString() ?? "";
            var (views, livePairs) = await ResolvePairs(u, saved, rows);
            report.Pairs = views;
            var filter = saved?.Filter;
            if (filter != null && filter.ModeValue != FilterMode.Off)
                report.Filter = $"{ItemFilter.ModeName(filter.ModeValue)} of {filter.Items.Count}";
            if (route == null && livePairs.Count == 0 && views.Count == 0)
            {
                report.Status = "idle: nothing to route; pair containers or set prefixes in Linked Transfer Unit > Configure";
                return report;
            }
            var what = Describe(route, views) + (report.Filter.Length > 0 ? $", filter: {report.Filter}" : "");
            var state = cfg.RequireRunning ? (report.Running ? "running" : "stopped") : "running (RequireRunning off)";
            if (cfg.RequireRunning && !report.Running && !dryRun)
            {
                report.Status = $"stopped: {what}; press Start to run it";
                return report;
            }

            // The loop runs one unit at a time, so any journal entry of this unit is left over
            // from an interrupted move. The unit waits until it is resolved (at startup, after
            // JournalStaleSeconds, or by an admin), so the check compares against still-valid totals.
            var leftover = dryRun ? 0 : journal.Snapshot().Count(m => m.UnitId == u.ElementId);
            if (leftover > 0)
            {
                report.Status = $"paused: {leftover} interrupted move(s) are being checked; it resumes once they are resolved";
                return report;
            }

            var actor = await ResolveActor(u.ConstructId);
            if (actor == null)
            {
                report.Status = "idle: the unit's construct has no owner";
                return report;
            }
            report.Actor = actor.ToString();

            var unitWorld = (await scenegraph.ResolveWorldLocation(Local(u.ConstructId, unit.position))).position;
            var endpoints = route != null && (route.SourcePrefix.Length > 0 || route.DestPrefix.Length > 0)
                ? await FindEndpoints(unitWorld, u.ConstructId)
                : new List<Endpoint>();
            var pairedSrc = await PairedEndpoints(livePairs, PairRole.Source, unitWorld);
            var pairedDst = await PairedEndpoints(livePairs, PairRole.Destination, unitWorld);

            // Union of prefix matches and paired elements. Sources nearest first; destinations in
            // the output order: paired ones as saved, then prefix matches by name, then id.
            var srcCands = Union(route == null ? null : endpoints.Where(e => Routing.Matches(e.Name, route.SourcePrefix, cfg.CaseInsensitive)), pairedSrc);
            var dstCands = Union(route == null ? null : endpoints.Where(e => Routing.Matches(e.Name, route.DestPrefix, cfg.CaseInsensitive)), pairedDst);
            var dstPairs = store.Get(u.ElementId)?.DestinationPairs ?? new List<PairedElement>();
            foreach (var e in dstCands)
                e.PairIndex = dstPairs.FindIndex(p => p.ElementId == e.ElementId);
            dstCands = OutputRules.Order(dstCands, e => e.PairIndex, e => e.Name, e => e.ElementId);
            var mode = saved?.OutputModeValue ?? OutputMode.InOrder;
            report.Output = mode;
            var (sources, deniedSrc) = await Filter(srcCands, actor, Right.ContainerRetrieve);
            var (dests, deniedDst) = await Filter(dstCands, actor, Right.ContainerPut);
            report.DeniedByRights = deniedSrc.Count + deniedDst.Count;
            foreach (var lp in livePairs)
                if ((lp.Role == PairRole.Source ? deniedSrc : deniedDst).Contains(lp.Element.elementId) && lp.View.Note.Length == 0)
                    lp.View.Note = $"{actor} lacks Container {(lp.Role == PairRole.Source ? "Retrieve" : "Put")}";
            // A pool that is both source and destination cannot feed itself.
            var destRoots = dests.Select(d => d.RootId).ToHashSet();
            sources = sources.Where(s => !destRoots.Contains(s.RootId)).ToList();
            report.Sources = sources.Count;
            report.Destinations = dests.Count;
            var cursor = mode == OutputMode.RoundRobin ? Cursor(u.ElementId, saved) : 0;
            report.DestinationOrder = (mode == OutputMode.RoundRobin ? OutputRules.Rotate(dests, cursor) : dests)
                .Select(d => d.Name.Length > 0 ? d.Name : $"element {d.ElementId}").ToList();
            if (sources.Count == 0 || dests.Count == 0)
            {
                report.Status = $"{state}, waiting: {what} found {sources.Count} source(s), {dests.Count} destination(s) within {cfg.MaxRangeMeters} m"
                    + (report.DeniedByRights > 0 ? $"; {report.DeniedByRights} container(s) refused: {actor} lacks the RDMS right" : "");
                return report;
            }
            if (dryRun)
            {
                report.Status = $"{state}: {what}, {sources.Count} source(s), {dests.Count} destination(s), acting as {actor}";
                return report;
            }

            // Every litre still goes through MoveBetween (locks, journal, one transaction per
            // slot); the output mode only decides which destination is offered how much.
            double budget = cfg.MaxLitresPerUnitPerCycle;
            var available = mode == OutputMode.RoundRobin ? await MovableLitres(sources, actor, filter) : 0;
            var (movedLitres, nextCursor) = await OutputRules.Distribute(mode, dests, cursor, budget, available, async (dst, cap) =>
            {
                double got = 0;
                foreach (var src in sources)
                {
                    if (cap - got <= 0) break;
                    got += await MoveBetween(u, src, dst, actor, cap - got, filter);
                }
                return got;
            });
            report.MovedLitres = movedLitres;
            if (mode == OutputMode.RoundRobin && nextCursor != cursor)
                AdvanceCursor(u.ElementId, nextCursor);
            report.Status = report.MovedLitres > 0
                ? $"running: {what}, moved {report.MovedLitres:F1} L last cycle"
                : $"running: {what}, nothing to move (sources empty, destinations full, or no accepted items)";
            return report;
        }

        private static string Describe(Route? route, List<PairView> pairs)
        {
            var parts = new List<string>();
            if (route != null) parts.Add($"route \"{route}\"");
            int s = pairs.Count(p => p.Role == PairRole.Source), d = pairs.Count(p => p.Role == PairRole.Destination);
            if (s + d > 0) parts.Add($"{s} paired source(s), {d} paired destination(s)");
            return string.Join(" + ", parts);
        }

        // ------------------------------------------------------------------ round robin

        /// The unit's live round-robin cursor (from the store after a restart).
        private long Cursor(ulong unitId, UnitSettings? saved) =>
            cursors.TryGetValue(unitId, out var c) ? c.cursor : saved?.OutputCursor ?? 0;

        private void AdvanceCursor(ulong unitId, long value)
        {
            var now = DateTime.UtcNow;
            cursors.TryGetValue(unitId, out var c);
            if (OutputRules.CursorDue(c.saved, now))
            {
                store.Update(unitId, s => { if (s.OutputCursor == value) return false; s.OutputCursor = value; return true; });
                cursors[unitId] = (value, now);
            }
            else cursors[unitId] = (value, c.saved);
        }

        /// Writes the unit's live cursor to the store now (on Start/Stop and mode changes).
        public void FlushCursor(ulong unitId)
        {
            if (!cursors.TryGetValue(unitId, out var c)) return;
            if (store.Get(unitId) is { } s && s.OutputCursor != c.cursor)
                store.Update(unitId, x => { x.OutputCursor = c.cursor; return true; });
            cursors[unitId] = (c.cursor, DateTime.UtcNow);
        }

        /// Litres in the sources that this unit may move (instanced items and the unit's filter
        /// applied; destination rules are not). Only sizes the round-robin shares: reading it
        /// outside the locks is fine, a stale value only changes how the cycle is split.
        private async Task<double> MovableLitres(List<Endpoint> sources, Actor actor, ItemFilterSpec? filter)
        {
            double total = 0;
            var requester = new PlayerId { id = actor.PlayerId };
            foreach (var src in sources)
            {
                try
                {
                    var info = await storage.Get(StorageRef.Container(src.RootId), requester);
                    foreach (var slot in info.content)
                    {
                        if (cfg.SkipInstancedItems && slot.content.id != 0) continue;
                        if (slot.quantity.quantity <= 0) continue;
                        if (!ItemFilter.Passes(filter, Lineage(slot.content.type))) continue;
                        total += bank.VolumeFor(slot.content.type, (ulong)slot.quantity.quantity);
                    }
                }
                catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: cannot read source {src} for round robin", src.RootId); }
            }
            return total;
        }

        private static List<Endpoint> Union(IEnumerable<Endpoint>? a, IEnumerable<Endpoint> b)
        {
            var byId = new Dictionary<ulong, Endpoint>();
            foreach (var e in (a ?? Enumerable.Empty<Endpoint>()).Concat(b))
                byId.TryAdd(e.ElementId, e);
            return byId.Values.OrderBy(e => e.DistanceMeters).ToList();
        }

        /// Endpoints for the live pairs of one role: still a container, in range, not broken.
        /// Pairs that fail get a note and are left out.
        private async Task<List<Endpoint>> PairedEndpoints(List<LivePair> live, PairRole role, Vec3 unitWorld)
        {
            var mine = live.Where(l => l.Role == role).ToList();
            var result = new List<Endpoint>();
            if (mine.Count == 0) return result;
            var world = await scenegraph.BatchResolveWorldLocations(mine.Select(l => Local(l.Element.constructId, l.Element.position)).ToArray());
            for (int i = 0; i < mine.Count; i++)
            {
                var lp = mine[i];
                var def = bank.GetDefinition(lp.Element.elementType);
                if (def?.BaseObject is not NQutils.Def.ContainerUnit)
                {
                    lp.View.Note = "no longer a container";
                    continue;
                }
                if (world[i] == null)
                {
                    lp.View.Note = "position unknown";
                    continue;
                }
                var d = Distance(unitWorld, world[i].position);
                if (d > cfg.MaxRangeMeters)
                {
                    lp.View.Note = $"out of range ({d:F0} m of {cfg.MaxRangeMeters:F0} m)";
                    continue;
                }
                var hp = Backend.ElementPropertiesHelper.GetPropertyOrDefault(lp.Element.properties, def.BaseObject, NQutils.Def.Element.d_hitpointsRatio);
                if (hp <= 0)
                {
                    lp.View.Note = "broken (0 hit points)";
                    continue;
                }
                var fuel = def.BaseObject as NQutils.Def.FuelContainer;
                result.Add(new Endpoint
                {
                    ElementId = lp.Element.elementId,
                    ConstructId = lp.Element.constructId,
                    Name = NameOf(lp.Element) ?? "",
                    IsFuelTank = fuel != null,
                    FuelType = fuel?.fuelType ?? "",
                    DistanceMeters = d,
                });
            }
            return result;
        }

        public static string? NameOf(ElementInfo ei)
        {
            if (ei.properties != null && ei.properties.TryGetValue("name", out var pv) && pv != null)
                return pv.stringValue;
            return null;
        }

        private static RelativeLocation Local(ulong constructId, Vec3 pos) =>
            new RelativeLocation { constructId = constructId, position = pos, rotation = Quat.Identity };

        /// Distance between two elements in metres, or null when either position is unknown.
        public async Task<double?> DistanceBetween(ElementInfo a, ElementInfo b)
        {
            var w = await scenegraph.BatchResolveWorldLocations(new[] { Local(a.constructId, a.position), Local(b.constructId, b.position) });
            if (w == null || w.Length < 2 || w[0] == null || w[1] == null) return null;
            return Distance(w[0].position, w[1].position);
        }

        private async Task<Actor?> ResolveActor(ulong constructId)
        {
            var ci = await orleans.GetConstructInfoGrain(constructId).Get();
            var owner = ci.mutableData.ownerId;
            if (owner.playerId != 0)
                return new Actor { Owner = owner, PlayerId = owner.playerId };
            if (owner.organizationId != 0)
            {
                var legate = await orleans.GetOrganizationGrain(owner.organizationId).EffectiveSuperLegate();
                if (legate == 0) return null;
                return new Actor { Owner = owner, PlayerId = legate };
            }
            return null;
        }

        private async Task<EntityId> OwnerOf(ulong constructId)
        {
            var ci = await orleans.GetConstructInfoGrain(constructId).Get();
            return ci.mutableData.ownerId;
        }

        /// Every container, hub and fuel tank on constructs near the unit, within range.
        private async Task<List<Endpoint>> FindEndpoints(Vec3 unitWorld, ulong unitConstruct)
        {
            var constructIds = new HashSet<ulong> { unitConstruct };
            var near = await scenegraph.GetConstructsInRange(unitWorld, cfg.MaxRangeMeters);
            foreach (var cp in near.List)
                constructIds.Add(cp.ConstructId);

            var candidates = new List<ElementInfo>();
            foreach (var cid in constructIds)
            {
                try { candidates.AddRange(await ContainersOn(cid)); }
                catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: cannot list containers on {cid}", cid); }
            }
            if (candidates.Count == 0)
                return new();

            var locs = candidates.Select(c => Local(c.constructId, c.position)).ToArray();
            var world = await scenegraph.BatchResolveWorldLocations(locs);
            var result = new List<Endpoint>();
            for (int i = 0; i < candidates.Count; i++)
            {
                var w = world[i];
                if (w == null) continue;
                var d = Distance(unitWorld, w.position);
                if (d > cfg.MaxRangeMeters) continue;
                var c = candidates[i];
                var def = bank.GetDefinition(c.elementType);
                // A destroyed (0 hit points) container is skipped, as it is for a paired one.
                if (def != null && Backend.ElementPropertiesHelper.GetPropertyOrDefault(c.properties, def.BaseObject, NQutils.Def.Element.d_hitpointsRatio) <= 0)
                    continue;
                var fuel = def?.BaseObject as NQutils.Def.FuelContainer;
                result.Add(new Endpoint
                {
                    ElementId = c.elementId,
                    ConstructId = c.constructId,
                    Name = NameOf(c) ?? "",
                    IsFuelTank = fuel != null,
                    FuelType = fuel?.fuelType ?? "",
                    DistanceMeters = d,
                });
            }
            return result;
        }

        private async Task<List<ElementInfo>> ContainersOn(ulong constructId)
        {
            if (containerCache.TryGetValue(constructId, out var hit) &&
                (DateTime.UtcNow - hit.at).TotalSeconds < cfg.ContainerCacheSeconds)
                return hit.containers;
            var grain = orleans.GetConstructElementsGrain(constructId);
            var ids = await grain.GetElementsOfType<NQutils.Def.ContainerUnit>();
            var list = new List<ElementInfo>(ids.Count);
            foreach (var id in ids)
                list.Add(await grain.GetElement(id));
            containerCache[constructId] = (DateTime.UtcNow, list);
            return list;
        }

        private static double Distance(Vec3 a, Vec3 b)
        {
            var dx = a.x - b.x; var dy = a.y - b.y; var dz = a.z - b.z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// Hub resolution, dedupe by pool, and the rights check (every cycle, never cached).
        /// Returns the usable endpoints and the element ids refused by RDMS.
        private async Task<(List<Endpoint>, HashSet<ulong> denied)> Filter(IEnumerable<Endpoint> candidates, Actor actor, Right needed)
        {
            var byRoot = new Dictionary<ulong, Endpoint>();
            var kept = new List<Endpoint>();     // candidate order kept (it is the output order)
            var denied = new HashSet<ulong>();
            foreach (var e in candidates)
            {
                var root = (ulong)(await orleans.GetContainerGrain(e.ElementId).GetRootContainerId());
                if (root == 0) root = e.ElementId;
                if (byRoot.ContainsKey(root)) continue;
                if (!await Allowed(actor, e.ConstructId, root, needed)) { denied.Add(e.ElementId); continue; }
                e.RootId = root;
                if (root != e.ElementId)
                {
                    // Linked to a hub: the pool is the hub's, and a hub is never a fuel tank.
                    e.IsFuelTank = false;
                    e.FuelType = "";
                }
                byRoot[root] = e;
                kept.Add(e);
            }
            return (kept, denied);
        }

        private async Task<bool> Allowed(Actor actor, ulong constructId, ulong elementId, Right needed)
        {
            if (!cfg.AllowOtherOwnersWithRights)
            {
                var owner = await OwnerOf(constructId);
                if (owner.playerId != actor.Owner.playerId || owner.organizationId != actor.Owner.organizationId)
                    return false;
            }
            var asset = new AssetId { type = AssetType.Element, construct = constructId, element = elementId };
            var rights = await orleans.GetRDMSRightGrain(actor.PlayerId)
                .GetRightsForPlayerOnAsset(actor.PlayerId, asset, false);
            return rights.rights.Contains(needed);
        }

        // ------------------------------------------------------------------ moving

        /// Moves what fits from one source pool to one destination pool. Each slot is ONE
        /// storage transaction holding both the debit and the credit: the server commits both in
        /// one SQL transaction or neither, and an uncommitted transaction is rolled back when it
        /// is disposed (ItemStorageTransactionGrain.EndTransaction). The mod adds per-storage
        /// locks, a quantity that fits the measured room, an in-flight journal entry around the
        /// commit, a re-check when the commit itself throws, and a per-unit last error.
        private async Task<double> MoveBetween(UnitRef u, Endpoint src, Endpoint dst, Actor actor, double budget, ItemFilterSpec? filter)
        {
            using var held = await locks.Acquire(src.RootId, dst.RootId);
            var requester = new PlayerId { id = actor.PlayerId };
            // Read inside the locks, so no other unit's move lands between the read and the move.
            var dstInfo = await storage.Get(StorageRef.Container(dst.RootId), requester);
            // A hub may report no capacity of its own; then the storage enforces room
            // (AllowOverload=false) and a refused move is simply skipped.
            var free = dstInfo.maxVolume <= 0 && !dst.IsFuelTank
                ? double.MaxValue
                : TransferMath.FreeVolume(dstInfo.volume, dstInfo.maxVolume, dst.IsFuelTank, cfg.FuelFillPercent);
            if (free <= 0) return 0;
            var srcInfo = await storage.Get(StorageRef.Container(src.RootId), requester);
            var srcTotals = Totals(srcInfo);
            var dstTotals = Totals(dstInfo);

            double moved = 0;
            foreach (var slot in srcInfo.content.OrderBy(s => s.position))
            {
                if (budget - moved <= 0 || free <= 0) break;
                var item = slot.content;
                if (cfg.SkipInstancedItems && item.id != 0) continue;
                if (!ItemFilter.Passes(filter, Lineage(item.type))) continue;
                if (!Accepts(dst, dstInfo.itemFilterId, item.type)) continue;

                var slotQty = slot.quantity.quantity;
                if (slotQty <= 0) continue;
                var slotLitres = bank.VolumeFor(item.type, (ulong)slotQty);
                var litres = TransferMath.LitresToMove(slotLitres, free, budget - moved);
                var qty = TransferMath.QuantityFor(slotQty, slotLitres, litres);
                if (qty <= 0) continue;
                var actualLitres = bank.VolumeFor(item.type, (ulong)qty);
                if (actualLitres > free + 1e-6) continue;   // never ask for more room than measured

                var outcome = await MoveOne(u, src, dst, actor, requester, item, qty,
                    srcTotals.GetValueOrDefault(item.type), dstTotals.GetValueOrDefault(item.type));
                if (outcome == MoveOutcome.Unknown) break;          // storages changed under us: re-read next cycle
                if (outcome != MoveOutcome.Applied) continue;
                srcTotals[item.type] = srcTotals.GetValueOrDefault(item.type) - qty;
                dstTotals[item.type] = dstTotals.GetValueOrDefault(item.type) + qty;
                moved += actualLitres;
                free -= actualLitres;
                if (cfg.LogEveryMove)
                    logger.LogInformation("LinkedTransferUnit: unit {unit} moved {name} x{qty} ({litres:F1} L) {from} -> {to} as {actor}",
                        u.ElementId, bank.GetDefinition(item.type)?.Name ?? item.type.ToString(), qty, actualLitres,
                        src.RootId, dst.RootId, actor.ToString());
            }
            return moved;
        }

        /// One slot's move as one storage transaction, journalled around the commit.
        private async Task<MoveOutcome> MoveOne(UnitRef u, Endpoint src, Endpoint dst, Actor actor, PlayerId requester,
            ItemInfo item, long qty, long srcBefore, long dstBefore)
        {
            var entry = journal.Begin(u.ElementId, src.RootId, dst.RootId, item.type, qty, srcBefore, dstBefore, DateTime.UtcNow);
            var keepEntry = false;
            var stage = "debit";
            var opts = new OperationOptions { Requester = requester, AllowPartial = false, AllowOverload = false };
            try
            {
                await using (var tr = await storage.MakeTransaction(Tag.MaintenanceOp("LinkedTransferUnit",
                    new { unit = u.ElementId, from = src.RootId, to = dst.RootId, journal = entry.Id })))
                {
                    await storage.GiveOrTakeItems(tr, StorageRef.Container(src.RootId),
                        new List<ItemAndQuantity> { new ItemAndQuantity { item = item, quantity = -qty } }, opts);
                    if (Faults.Take(u.ElementId, FaultKind.ThrowAfterDebit) is { } f1) throw new InjectedFaultException(f1.kind);
                    stage = "credit";
                    await storage.GiveOrTakeItems(tr, StorageRef.Container(dst.RootId),
                        new List<ItemAndQuantity> { new ItemAndQuantity { item = item, quantity = qty } }, opts);
                    if (Faults.Take(u.ElementId, FaultKind.ThrowBeforeCommit) is { } f2) throw new InjectedFaultException(f2.kind);
                    if (Faults.Take(u.ElementId, FaultKind.HoldBeforeCommit) is { } f3)
                    {
                        logger.LogWarning("LinkedTransferUnit: [fault injection] unit {unit} holding move {id} open for {ms} ms before its commit", u.ElementId, entry.Id, f3.holdMs);
                        await Task.Delay(f3.holdMs);
                    }
                    if (Faults.Take(u.ElementId, FaultKind.CrashBeforeCommit) is { } f4)
                    {
                        keepEntry = true;   // what a crash leaves: an entry, and a transaction that never committed
                        throw new InjectedFaultException(f4.kind);
                    }
                    stage = "commit";
                    await tr.Commit();
                }
                return MoveOutcome.Applied;
            }
            catch (Exception e)
            {
                // Leaving the using block disposed the transaction, which rolls it back unless
                // the commit went through. Before the commit nothing can have been written. A
                // commit that threw may still have written (the SQL step succeeded and a later
                // in-memory step failed), so that case is checked against the storages.
                var outcome = MoveOutcome.NotApplied;
                if (stage == "commit")
                    outcome = await Recheck(entry, actor);
                var name = bank.GetDefinition(item.type)?.Name ?? item.type.ToString();
                var what = $"{name} x{qty} {src.RootId} -> {dst.RootId}";
                if (e is InjectedFaultException)
                    logger.LogWarning("LinkedTransferUnit: [fault injection] unit {unit} move {id} of {what}: {fault} at {stage}; {outcome}",
                        u.ElementId, entry.Id, what, e.Message, stage, keepEntry ? "journal entry left for replay" : MoveRules.Name(outcome));
                switch (outcome)
                {
                    case MoveOutcome.Applied:
                        logger.LogWarning(e, "LinkedTransferUnit: unit {unit} move of {what}: the commit reported an error but the items DID move; counted as moved", u.ElementId, what);
                        return MoveOutcome.Applied;
                    case MoveOutcome.Unknown:
                        RecordError(u.ElementId, $"move of {what} at {stage}: {ErrorText(e)}; outcome unknown, totals changed meanwhile");
                        logger.LogError(e, "LinkedTransferUnit: unit {unit} move {id} of {what}: the commit failed and the totals match neither before nor after; nothing is re-sent. Check the storages.",
                            u.ElementId, entry.Id, what);
                        return MoveOutcome.Unknown;
                    default:
                        RecordError(u.ElementId, $"move of {what} refused at {stage}: {ErrorText(e)}");
                        logger.LogDebug(e, "LinkedTransferUnit: unit {unit} move of {what} refused at {stage}; rolled back", u.ElementId, what, stage);
                        return MoveOutcome.NotApplied;
                }
            }
            finally
            {
                if (!keepEntry) journal.End(entry.Id);
            }
        }

        /// The item type's totals in both storages now, against the journal entry.
        private async Task<MoveOutcome> Recheck(InFlightMove m, Actor? actor)
        {
            try
            {
                var who = new PlayerId { id = actor?.PlayerId ?? 0 };
                var from = Totals(await storage.Get(StorageRef.Container(m.FromStorage), who)).GetValueOrDefault(m.ItemType);
                var to = Totals(await storage.Get(StorageRef.Container(m.ToStorage), who)).GetValueOrDefault(m.ItemType);
                return MoveRules.Classify(m, from, to);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "LinkedTransferUnit: could not re-read storages {from} / {to} for move {id}", m.FromStorage, m.ToStorage, m.Id);
                return MoveOutcome.Unknown;
            }
        }

        /// Interrupted moves (journal entries from an earlier server instance, or stale ones):
        /// each is checked against the storages, logged loudly and dropped. A move is never
        /// re-sent: it was one transaction, so it happened in full or not at all.
        /// `all` also takes this instance's fresh entries (the admin replay action).
        public async Task<List<(InFlightMove move, MoveOutcome outcome)>> ResolveJournal(bool all)
        {
            var result = new List<(InFlightMove, MoveOutcome)>();
            foreach (var m in journal.Leftovers(DateTime.UtcNow, TimeSpan.FromSeconds(cfg.JournalStaleSeconds), all))
            {
                MoveOutcome outcome;
                using (await locks.Acquire(m.FromStorage, m.ToStorage))
                    outcome = await Recheck(m, null);
                var name = bank.GetDefinition(m.ItemType)?.Name ?? m.ItemType.ToString();
                var when = m.Instance != journal.Instance ? "before a server restart" : "in this server instance";
                if (outcome == MoveOutcome.Unknown)
                {
                    RecordError(m.UnitId, $"interrupted move of {name} x{m.Quantity} {m.FromStorage} -> {m.ToStorage}: outcome unknown");
                    logger.LogError("LinkedTransferUnit: INTERRUPTED MOVE {id} (unit {unit}, {name} x{qty}, {from} -> {to}, started {at:u}, {when}): outcome UNKNOWN, the totals changed since. It was one storage transaction, so it cannot have lost or duplicated items; nothing is re-sent.",
                        m.Id, m.UnitId, name, m.Quantity, m.FromStorage, m.ToStorage, m.StartedAt, when);
                }
                else
                    logger.LogWarning("LinkedTransferUnit: INTERRUPTED MOVE {id} (unit {unit}, {name} x{qty}, {from} -> {to}, started {at:u}, {when}): {outcome}; nothing is re-sent",
                        m.Id, m.UnitId, name, m.Quantity, m.FromStorage, m.ToStorage, m.StartedAt, when, MoveRules.Name(outcome));
                journal.End(m.Id);
                result.Add((m, outcome));
            }
            return result;
        }

        /// Plain (non-instanced) quantity per item type in a storage.
        private static Dictionary<ulong, long> Totals(StorageInfo info)
        {
            var d = new Dictionary<ulong, long>();
            foreach (var s in info.content)
                if (s.content.id == 0)
                    d[s.content.type] = d.GetValueOrDefault(s.content.type) + s.quantity.quantity;
            return d;
        }

        private void RecordError(ulong unit, string msg)
        {
            if (msg.Length > 300) msg = msg.Substring(0, 300) + "...";
            var prev = lastErrors.TryGetValue(unit, out var p) ? p.msg : null;
            lastErrors[unit] = (DateTime.UtcNow, msg);
            if (prev != msg)
                logger.LogInformation("LinkedTransferUnit: unit {unit} last error: {msg}", unit, msg);
        }

        private void StampError(ulong unit, UnitReport r)
        {
            if (lastErrors.TryGetValue(unit, out var e)) { r.LastError = e.msg; r.LastErrorAt = e.at; }
        }

        public (DateTime at, string msg)? LastError(ulong unit) =>
            lastErrors.TryGetValue(unit, out var e) ? e : null;

        public List<InFlightMove> InFlight() => journal.Snapshot();

        private static string ErrorText(Exception e)
        {
            while (e is AggregateException ae && ae.InnerExceptions.Count > 0) e = ae.InnerExceptions[0];
            if (e is NQutils.Exceptions.BusinessException be)
                return $"{be.error.code}: {be.Message}";
            return e.Message;
        }

        /// Destination filter: fuel tanks take only their fuel family; filtered
        /// containers take only their filter's subtree. The storage enforces both as well.
        private bool Accepts(Endpoint dst, ulong itemFilterId, ulong itemType)
        {
            if (dst.IsFuelTank && cfg.RequireMatchingFuelType)
            {
                if (!cfg.FuelTypeFamilies.TryGetValue(dst.FuelType, out var family))
                    family = dst.FuelType;
                var fam = bank.GetDefinition(family);
                if (fam == null || !IsSelfOrDescendant(itemType, fam.Id))
                    return false;
            }
            if (itemFilterId != 0 && !IsSelfOrDescendant(itemType, itemFilterId))
                return false;
            return true;
        }

        /// The type's id, then its parents' ids up to the item-bank root.
        private IEnumerable<ulong> Lineage(ulong type)
        {
            var def = bank.GetDefinition(type);
            for (int guard = 0; def != null && guard < 64; guard++)
            {
                yield return def.Id;
                def = def.Parent;
            }
        }

        /// The industry unit (any kind) nearest the player within `range` metres:
        /// (element id, distance), or null when there is none.
        public async Task<(ulong elementId, double meters)?> NearestIndustryUnit(ulong playerId, double range)
        {
            var (local, world) = await scenegraph.GetPlayerWorldPosition(new PlayerId { id = playerId });
            if (world == null) return null;
            // The construct the player stands on, plus constructs whose centre is near (a large
            // construct's centre can be far from the player, hence the margin).
            var ids = new HashSet<ulong>();
            if (local != null && local.constructId != 0) ids.Add(local.constructId);
            var near = await scenegraph.GetConstructsInRange(world.position, Math.Max(range, 1) + 200);
            foreach (var cp in near.List) ids.Add(cp.ConstructId);
            var elements = new List<ElementInfo>();
            foreach (var cid in ids)
            {
                try
                {
                    var grain = orleans.GetConstructElementsGrain(cid);
                    foreach (var id in await grain.GetElementsOfType<NQutils.Def.IndustryUnit>())
                        elements.Add(await grain.GetElement(id));
                }
                catch (Exception e) { logger.LogDebug(e, "LinkedTransferUnit: cannot list industry units on {cid}", cid); }
            }
            if (elements.Count == 0) return null;
            var w = await scenegraph.BatchResolveWorldLocations(elements.Select(e => Local(e.constructId, e.position)).ToArray());
            (ulong id, double d)? best = null;
            for (int i = 0; i < elements.Count; i++)
            {
                if (w[i] == null) continue;
                var d = Distance(world.position, w[i].position);
                if (d <= range && (best == null || d < best.Value.d)) best = (elements[i].elementId, d);
            }
            return best;
        }

        private bool IsSelfOrDescendant(ulong type, ulong ancestor)
        {
            var def = bank.GetDefinition(type);
            for (int guard = 0; def != null && guard < 64; guard++)
            {
                if (def.Id == ancestor) return true;
                def = def.Parent;
            }
            return false;
        }
    }
}
