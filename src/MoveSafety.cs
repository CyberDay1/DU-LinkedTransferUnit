using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LinkedTransferUnit
{
    // Item-safety pieces for the transfer loop. Pure: no server types, so they are unit-tested.
    // Long form: C:\Claude\docs\voxelmmo\du-ltu-item-safety.md

    /// One async lock per storage id. A move takes the locks of its source and destination
    /// pools, always in ascending id order, so two moves on the same storage never interleave
    /// inside the mod and two moves can never deadlock on each other.
    public sealed class ContainerLocks
    {
        private readonly ConcurrentDictionary<ulong, SemaphoreSlim> locks = new();

        public int Count => locks.Count;

        public async Task<IDisposable> Acquire(params ulong[] ids)
        {
            var ordered = ids.Distinct().OrderBy(i => i).ToArray();
            var taken = new List<SemaphoreSlim>(ordered.Length);
            try
            {
                foreach (var id in ordered)
                {
                    var s = locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
                    await s.WaitAsync();
                    taken.Add(s);
                }
            }
            catch
            {
                foreach (var s in taken) s.Release();
                throw;
            }
            return new Releaser(taken);
        }

        /// Drops idle locks (none held) so the table does not grow without bound.
        public void Prune()
        {
            foreach (var kv in locks)
                if (kv.Value.CurrentCount == 1 && kv.Value.Wait(0))
                {
                    locks.TryRemove(kv.Key, out _);
                    kv.Value.Release();
                }
        }

        private sealed class Releaser : IDisposable
        {
            private List<SemaphoreSlim>? held;
            public Releaser(List<SemaphoreSlim> held) { this.held = held; }
            public void Dispose()
            {
                var h = Interlocked.Exchange(ref held, null);
                if (h == null) return;
                for (int i = h.Count - 1; i >= 0; i--) h[i].Release();
            }
        }
    }

    /// One move between two storages, written down before its transaction commits and removed
    /// once the outcome is known. An entry still present after a crash is an interrupted move.
    public sealed class InFlightMove
    {
        public string Id { get; set; } = "";
        public ulong UnitId { get; set; }
        public ulong FromStorage { get; set; }
        public ulong ToStorage { get; set; }
        public ulong ItemType { get; set; }
        public long Quantity { get; set; }
        /// The item type's total in each storage just before the move.
        public long FromBefore { get; set; }
        public long ToBefore { get; set; }
        public DateTime StartedAt { get; set; }
        /// The mod instance that wrote it; a different one means the server restarted since.
        public string Instance { get; set; } = "";
    }

    public enum MoveOutcome { Applied, NotApplied, Unknown }

    public static class MoveRules
    {
        /// What became of an interrupted move, from the item type's totals now. The move is one
        /// storage transaction, so it is either applied in full or not at all; "Unknown" means
        /// something else touched the storages since, and the totals cannot tell which.
        public static MoveOutcome Classify(InFlightMove m, long fromNow, long toNow)
        {
            if (fromNow == m.FromBefore - m.Quantity && toNow == m.ToBefore + m.Quantity) return MoveOutcome.Applied;
            if (fromNow == m.FromBefore && toNow == m.ToBefore) return MoveOutcome.NotApplied;
            return MoveOutcome.Unknown;
        }

        public static string Name(MoveOutcome o) => o switch
        {
            MoveOutcome.Applied => "applied",
            MoveOutcome.NotApplied => "not applied (rolled back)",
            _ => "unknown (the storages changed since)",
        };
    }

    /// The in-flight journal: moves-inflight.json next to units.json. Written before every
    /// commit, cleared after it. Never replays a move: the move is atomic, so replaying it
    /// could only duplicate items. On startup (and each cycle, for entries from an earlier
    /// instance or older than the stale age) each leftover entry is checked against the
    /// storages, logged, and dropped.
    public sealed class MoveJournal
    {
        private readonly string path;
        private readonly object gate = new();
        private Dictionary<string, InFlightMove> map = new();
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public MoveJournal(string path) { this.path = path; }

        public string Path => path;
        /// Identifies this mod instance (one per server start).
        public string Instance { get; } = Guid.NewGuid().ToString("N").Substring(0, 12);

        public static string PathNextTo(string storePath) =>
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(storePath) ?? ".", "moves-inflight.json");

        public int Count { get { lock (gate) return map.Count; } }

        public void Load()
        {
            lock (gate)
            {
                if (!File.Exists(path)) { map = new(); return; }
                var list = JsonSerializer.Deserialize<List<InFlightMove>>(File.ReadAllText(path)) ?? new();
                map = list.Where(m => m != null && m.Id.Length > 0).ToDictionary(m => m.Id, m => m);
            }
        }

        public InFlightMove Begin(ulong unit, ulong from, ulong to, ulong type, long qty, long fromBefore, long toBefore, DateTime now)
        {
            var m = new InFlightMove
            {
                Id = Guid.NewGuid().ToString("N"), UnitId = unit, FromStorage = from, ToStorage = to, ItemType = type,
                Quantity = qty, FromBefore = fromBefore, ToBefore = toBefore, StartedAt = now, Instance = Instance,
            };
            lock (gate)
            {
                map[m.Id] = m;
                SaveLocked();
            }
            return m;
        }

        public bool End(string id)
        {
            lock (gate)
            {
                if (!map.Remove(id)) return false;
                SaveLocked();
                return true;
            }
        }

        /// Entries to resolve now: from another instance (a restart happened), or older than
        /// `staleAfter` (this instance lost track of them).
        public List<InFlightMove> Leftovers(DateTime now, TimeSpan staleAfter, bool all = false)
        {
            lock (gate)
                return map.Values.Where(m => all || m.Instance != Instance || now - m.StartedAt >= staleAfter).ToList();
        }

        public List<InFlightMove> Snapshot()
        {
            lock (gate) return map.Values.ToList();
        }

        private void SaveLocked()
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(map.Values.ToList(), Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// Debug-only fault injection (config FaultInjection, off by default; arming also needs an
    /// admin). A fault is armed for one unit and fires on that unit's next N moves.
    public enum FaultKind
    {
        /// Throw after the source debit is staged, before the destination credit.
        ThrowAfterDebit,
        /// Throw after both are staged, just before the commit.
        ThrowBeforeCommit,
        /// Wait HoldMs just before the commit (the harness deletes or races meanwhile).
        HoldBeforeCommit,
        /// Act as if the server died just before the commit: the transaction is abandoned and
        /// the journal entry is left behind for the next replay.
        CrashBeforeCommit,
    }

    public sealed class FaultPlan
    {
        private readonly ConcurrentDictionary<ulong, (FaultKind kind, int left, int holdMs)> armed = new();

        public static bool TryParseKind(string? s, out FaultKind kind)
        {
            kind = default;
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "throw-after-debit": kind = FaultKind.ThrowAfterDebit; return true;
                case "throw-before-commit": kind = FaultKind.ThrowBeforeCommit; return true;
                case "hold-before-commit": kind = FaultKind.HoldBeforeCommit; return true;
                case "crash-before-commit": kind = FaultKind.CrashBeforeCommit; return true;
                default: return false;
            }
        }

        public void Arm(ulong unit, FaultKind kind, int count, int holdMs) =>
            armed[unit] = (kind, Math.Clamp(count, 1, 1000), Math.Clamp(holdMs, 0, 120_000));

        public void Disarm(ulong unit) => armed.TryRemove(unit, out _);

        public int ArmedCount => armed.Count;

        /// The fault to fire on this move of `unit` at point `at`, consuming one shot. Null when none.
        public (FaultKind kind, int holdMs)? Take(ulong unit, FaultKind at)
        {
            while (armed.TryGetValue(unit, out var a))
            {
                if (a.kind != at) return null;
                var next = (a.kind, a.left - 1, a.holdMs);
                bool ok = next.Item2 <= 0 ? armed.TryRemove(new KeyValuePair<ulong, (FaultKind, int, int)>(unit, a)) : armed.TryUpdate(unit, next, a);
                if (ok) return (a.kind, a.holdMs);
            }
            return null;
        }
    }

    /// Thrown by an injected fault.
    public sealed class InjectedFaultException : Exception
    {
        public FaultKind Kind { get; }
        public InjectedFaultException(FaultKind kind) : base("injected fault: " + kind) { Kind = kind; }
    }
}
