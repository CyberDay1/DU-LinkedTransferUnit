using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LinkedTransferUnit;
using Xunit;

public class MoveSafetyTests
{
    // ---- quantity that fits the measured room

    [Fact]
    public void QuantityRoundsDownForBulkyItems()
    {
        // 500 parts of 10 L = 5000 L; 1000 L of room -> 100 parts, not 1000 (the old GetQuantity answer).
        Assert.Equal(100, TransferMath.QuantityFor(500, 5000, 1000));
        // 999 L of room -> 99 parts (never rounds up past the room).
        Assert.Equal(99, TransferMath.QuantityFor(500, 5000, 999));
    }

    [Fact]
    public void QuantityForMaterialsUsesSubLitreUnits()
    {
        const long perLitre = 1L << 24;
        long slot = 200 * perLitre;                          // 200 L of ore
        var q = TransferMath.QuantityFor(slot, 200, 12.5);
        Assert.Equal((long)(12.5 * perLitre), q);
        Assert.True(q * 200.0 / slot <= 12.5);
    }

    [Theory]
    [InlineData(10, 100, 1000, 10)]   // whole slot fits
    [InlineData(10, 100, 100, 10)]
    [InlineData(10, 100, 0, 0)]
    [InlineData(0, 0, 50, 0)]
    [InlineData(10, 100, 9.99, 0)]    // less than one item of room: nothing, not one
    public void QuantityEdges(long slotQty, double slotLitres, double litres, long expected) =>
        Assert.Equal(expected, TransferMath.QuantityFor(slotQty, slotLitres, litres));

    [Fact]
    public void QuantityNeverExceedsTheRoomProperty()
    {
        var rng = new Random(7);
        for (int i = 0; i < 5000; i++)
        {
            long qty = rng.NextInt64(1, 1L << 40);
            double unit = rng.NextDouble() * 50 + 0.001;
            double slotLitres = qty * unit;
            double room = rng.NextDouble() * slotLitres * 1.2;
            var q = TransferMath.QuantityFor(qty, slotLitres, room);
            Assert.InRange(q, 0, qty);
            Assert.True(q * unit <= room + 1e-6 * Math.Max(1, room), $"q {q} unit {unit} room {room}");
        }
    }

    // ---- interrupted-move classification

    static InFlightMove M() => new() { Id = "x", Quantity = 30, FromBefore = 100, ToBefore = 5 };

    [Fact] public void ClassifyApplied() => Assert.Equal(MoveOutcome.Applied, MoveRules.Classify(M(), 70, 35));
    [Fact] public void ClassifyNotApplied() => Assert.Equal(MoveOutcome.NotApplied, MoveRules.Classify(M(), 100, 5));
    [Fact] public void ClassifyHalfIsUnknown() => Assert.Equal(MoveOutcome.Unknown, MoveRules.Classify(M(), 70, 5));
    [Fact] public void ClassifyOtherActivityIsUnknown() => Assert.Equal(MoveOutcome.Unknown, MoveRules.Classify(M(), 90, 5));

    // ---- journal

    static string TempFile() => Path.Combine(Path.GetTempPath(), "ltu-journal-" + Guid.NewGuid().ToString("N"), "moves-inflight.json");

    [Fact]
    public void JournalSurvivesARestartAndEndsClean()
    {
        var path = TempFile();
        var now = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var a = new MoveJournal(path);
        var kept = a.Begin(1, 10, 20, 99, 30, 100, 5, now);
        var done = a.Begin(1, 10, 20, 98, 7, 7, 0, now);
        Assert.True(a.End(done.Id));
        Assert.False(a.End(done.Id));
        // This instance's fresh entry is not a leftover yet.
        Assert.Empty(a.Leftovers(now.AddSeconds(5), TimeSpan.FromSeconds(120)));
        Assert.Single(a.Leftovers(now.AddSeconds(5), TimeSpan.FromSeconds(120), all: true));
        Assert.Single(a.Leftovers(now.AddSeconds(121), TimeSpan.FromSeconds(120)));

        // "Restart": a new instance reads the file and sees the entry at once.
        var b = new MoveJournal(path);
        b.Load();
        var left = b.Leftovers(now.AddSeconds(1), TimeSpan.FromSeconds(120));
        Assert.Single(left);
        Assert.Equal(kept.Id, left[0].Id);
        Assert.Equal(30, left[0].Quantity);
        Assert.Equal(100, left[0].FromBefore);
        Assert.True(b.End(kept.Id));

        var c = new MoveJournal(path);
        c.Load();
        Assert.Equal(0, c.Count);
    }

    [Fact]
    public void JournalPathSitsNextToTheStore() =>
        Assert.Equal("/OrleansGrains/Mods/LinkedTransferUnit/moves-inflight.json",
            MoveJournal.PathNextTo("/OrleansGrains/Mods/LinkedTransferUnit/units.json").Replace('\\', '/'));

    // ---- locks

    [Fact]
    public async Task LocksSerialiseSharedStorages()
    {
        var locks = new ContainerLocks();
        int inside = 0, maxInside = 0;
        async Task Work(ulong a, ulong b)
        {
            using (await locks.Acquire(a, b))
            {
                var n = System.Threading.Interlocked.Increment(ref inside);
                maxInside = Math.Max(maxInside, n);
                await Task.Delay(5);
                System.Threading.Interlocked.Decrement(ref inside);
            }
        }
        // Every pair shares storage 1, in both orders: never two at once, and no deadlock.
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? Work(1, (ulong)(i + 2)) : Work((ulong)(i + 2), 1)));
        Assert.Equal(1, maxInside);
        locks.Prune();
        Assert.Equal(0, locks.Count);
    }

    [Fact]
    public async Task DisjointLocksRunTogether()
    {
        var locks = new ContainerLocks();
        using var a = await locks.Acquire(1, 2);
        var b = locks.Acquire(3, 4);
        Assert.True(b.IsCompleted);
        (await b).Dispose();
        var c = locks.Acquire(2, 5);
        Assert.False(c.IsCompleted);
        a.Dispose();
        (await c).Dispose();
    }

    // ---- fault plan

    [Fact]
    public void FaultFiresOnlyAtItsPointAndOnlyNTimes()
    {
        var f = new FaultPlan();
        Assert.True(FaultPlan.TryParseKind("throw-after-debit", out var k));
        f.Arm(5, k, 2, 0);
        Assert.Null(f.Take(5, FaultKind.ThrowBeforeCommit));
        Assert.Null(f.Take(6, FaultKind.ThrowAfterDebit));
        Assert.NotNull(f.Take(5, FaultKind.ThrowAfterDebit));
        Assert.NotNull(f.Take(5, FaultKind.ThrowAfterDebit));
        Assert.Null(f.Take(5, FaultKind.ThrowAfterDebit));
        Assert.Equal(0, f.ArmedCount);
        Assert.False(FaultPlan.TryParseKind("explode", out _));
    }

    [Fact]
    public void FaultInjectionIsOffByDefault() => Assert.False(new LtuConfig().FaultInjection);
}
