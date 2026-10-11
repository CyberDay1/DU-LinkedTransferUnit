using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LinkedTransferUnit;
using Xunit;

public class OutputModeParseTests
{
    [Theory]
    [InlineData("inorder", OutputMode.InOrder)]
    [InlineData("In order", OutputMode.InOrder)]
    [InlineData("in-order", OutputMode.InOrder)]
    [InlineData("roundrobin", OutputMode.RoundRobin)]
    [InlineData("Round robin", OutputMode.RoundRobin)]
    [InlineData("round_robin", OutputMode.RoundRobin)]
    public void ParsesNames(string text, OutputMode expected) => Assert.Equal(expected, OutputRules.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("random")]
    public void UnknownIsNullAndStoresAsInOrder(string text)
    {
        Assert.Null(OutputRules.Parse(text));
        Assert.Equal(OutputMode.InOrder, OutputRules.ParseStored(text));
    }

    [Fact]
    public void NamesRoundTrip()
    {
        foreach (var m in new[] { OutputMode.InOrder, OutputMode.RoundRobin })
            Assert.Equal(m, OutputRules.Parse(OutputRules.Name(m)));
    }

    [Fact]
    public void ModePayload()
    {
        Assert.True(OutputRules.TryParseModePayload("{\"mode\":\"roundrobin\"}", out var m, out _));
        Assert.Equal(OutputMode.RoundRobin, m);
        Assert.False(OutputRules.TryParseModePayload("{\"mode\":\"fastest\"}", out _, out var e1));
        Assert.NotEmpty(e1);
        Assert.False(OutputRules.TryParseModePayload("{\"mode\":1}", out _, out _));
        Assert.False(OutputRules.TryParseModePayload("[]", out _, out _));
        Assert.False(OutputRules.TryParseModePayload("", out _, out _));
        Assert.False(OutputRules.TryParseModePayload("{\"mode\":\"" + new string('x', 300) + "\"}", out _, out _));
    }

    [Fact]
    public void MovePayload()
    {
        Assert.True(OutputRules.TryParseMovePayload("{\"elementId\":\"18446744073709551615\",\"dir\":-1}", out var id, out var dir, out _));
        Assert.Equal(ulong.MaxValue, id);
        Assert.Equal(-1, dir);
        Assert.True(OutputRules.TryParseMovePayload("{\"elementId\":42,\"dir\":5}", out id, out dir, out _));
        Assert.Equal(42UL, id);
        Assert.Equal(1, dir);
        Assert.False(OutputRules.TryParseMovePayload("{\"elementId\":42,\"dir\":0}", out _, out _, out _));
        Assert.False(OutputRules.TryParseMovePayload("{\"dir\":1}", out _, out _, out _));
        Assert.False(OutputRules.TryParseMovePayload("nope", out _, out _, out _));
    }
}

public class OutputOrderTests
{
    sealed class D
    {
        public ulong Id; public string Name = ""; public int Pair = -1;
        public override string ToString() => Name;
    }

    static List<string> Names(IEnumerable<D> l) => l.Select(x => x.Name).ToList();

    [Fact]
    public void PairedInSavedOrderThenPrefixByNameThenId()
    {
        var items = new[]
        {
            new D { Id = 9, Name = "zeta-in" },
            new D { Id = 5, Name = "Alpha-in" },
            new D { Id = 3, Name = "paired-two", Pair = 1 },
            new D { Id = 7, Name = "beta-in" },
            new D { Id = 1, Name = "paired-one", Pair = 0 },
            new D { Id = 4, Name = "beta-in" },       // same name: element id decides
        };
        var o = OutputRules.Order(items, x => x.Pair, x => x.Name, x => x.Id);
        Assert.Equal(new[] { "paired-one", "paired-two", "Alpha-in", "beta-in", "beta-in", "zeta-in" }, Names(o));
        Assert.Equal(4UL, o[3].Id);
        Assert.Equal(7UL, o[4].Id);
    }

    [Fact]
    public void OrderIsStableWhateverTheInputOrder()
    {
        var items = Enumerable.Range(1, 8).Select(i => new D { Id = (ulong)i, Name = "c" + (i % 3), Pair = i == 6 ? 0 : -1 }).ToList();
        var a = OutputRules.Order(items, x => x.Pair, x => x.Name, x => x.Id).Select(x => x.Id).ToList();
        var rng = new Random(7);
        for (int k = 0; k < 20; k++)
        {
            var shuffled = items.OrderBy(_ => rng.Next()).ToList();
            Assert.Equal(a, OutputRules.Order(shuffled, x => x.Pair, x => x.Name, x => x.Id).Select(x => x.Id).ToList());
        }
        Assert.Equal(6UL, a[0]);
    }

    [Fact]
    public void DuplicateIdsKeepTheFirst()
    {
        var o = OutputRules.Order(new[] { new D { Id = 1, Name = "a" }, new D { Id = 1, Name = "b" } }, x => x.Pair, x => x.Name, x => x.Id);
        Assert.Single(o);
        Assert.Equal("a", o[0].Name);
    }

    [Theory]
    [InlineData(0, 3, 0)]
    [InlineData(4, 3, 1)]
    [InlineData(-1, 3, 2)]
    [InlineData(long.MaxValue, 5, 2)]
    [InlineData(7, 0, 0)]
    public void StartIndexWraps(long cursor, int count, int expected) => Assert.Equal(expected, OutputRules.StartIndex(cursor, count));

    [Fact]
    public void RotateStartsAtTheCursor()
    {
        Assert.Equal(new[] { "c", "a", "b" }, OutputRules.Rotate(new[] { "a", "b", "c" }, 2));
        Assert.Empty(OutputRules.Rotate(new string[0], 3));
    }

    [Fact]
    public void MovePairSwapsWithItsNeighbour()
    {
        var l = new List<PairedElement> { new() { ElementId = 1 }, new() { ElementId = 2 }, new() { ElementId = 3 } };
        Assert.True(OutputRules.MovePair(l, 3, -1));
        Assert.Equal(new ulong[] { 1, 3, 2 }, l.Select(p => p.ElementId));
        Assert.True(OutputRules.MovePair(l, 1, +1));
        Assert.Equal(new ulong[] { 3, 1, 2 }, l.Select(p => p.ElementId));
        Assert.False(OutputRules.MovePair(l, 3, -1));   // already first
        Assert.False(OutputRules.MovePair(l, 2, +1));   // already last
        Assert.False(OutputRules.MovePair(l, 99, +1));  // not paired
        Assert.Equal(new ulong[] { 3, 1, 2 }, l.Select(p => p.ElementId));
    }
}

/// A small world for Distribute: one source pool of items, destinations with room and an
/// accept rule. `Fill` behaves like the engine's MoveBetween loop: whole units only, never
/// more than the cap or the room, items the destination refuses stay put.
public sealed class FakeWorld
{
    public sealed class Item { public string Type = ""; public double UnitLitres = 1; public long Qty; }
    public sealed class Dest
    {
        public string Name = "";
        public double Room;
        public Func<string, bool> Accepts = _ => true;
        public Dictionary<string, long> Got = new();
        public double Litres;
        public override string ToString() => Name;
    }

    public readonly List<Item> Source = new();
    public double SourceLitres => Source.Sum(i => i.Qty * i.UnitLitres);

    public Task<double> Fill(Dest d, double cap)
    {
        double moved = 0;
        foreach (var it in Source)
        {
            if (!d.Accepts(it.Type) || it.Qty <= 0) continue;
            var room = Math.Min(cap - moved, d.Room - d.Litres);
            var n = Math.Min(it.Qty, (long)Math.Floor(room / it.UnitLitres + 1e-9));
            if (n <= 0) continue;
            it.Qty -= n;
            d.Got[it.Type] = d.Got.GetValueOrDefault(it.Type) + n;
            d.Litres += n * it.UnitLitres;
            moved += n * it.UnitLitres;
        }
        return Task.FromResult(moved);
    }

    /// Total quantity of a type across the source and every destination (conservation).
    public long Total(string type, IEnumerable<Dest> dests) =>
        Source.Where(i => i.Type == type).Sum(i => i.Qty) + dests.Sum(d => d.Got.GetValueOrDefault(type));
}

public class OutputDistributeTests
{
    static FakeWorld.Dest Dst(string n, double room, Func<string, bool> accepts = null) =>
        new() { Name = n, Room = room, Accepts = accepts ?? (_ => true) };

    static async Task<double> Run(FakeWorld w, OutputMode m, IReadOnlyList<FakeWorld.Dest> d, long cursor, double budget) =>
        (await RunC(w, m, d, cursor, budget)).moved;

    static Task<(double moved, long next)> RunC(FakeWorld w, OutputMode m, IReadOnlyList<FakeWorld.Dest> d, long cursor, double budget) =>
        OutputRules.Distribute(m, d, cursor, budget, m == OutputMode.RoundRobin ? w.SourceLitres : 0, w.Fill);

    [Fact]
    public async Task InOrderFillsTheFirstUntilFullThenTheNext()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 900 });
        var d = new[] { Dst("a", 400), Dst("b", 400), Dst("c", 400) };
        var moved = await Run(w, OutputMode.InOrder, d, 0, 1000);
        Assert.Equal(900, moved);
        Assert.Equal(new double[] { 400, 400, 100 }, d.Select(x => x.Litres));
        Assert.Equal(900, w.Total("ore", d));
    }

    [Fact]
    public async Task InOrderStopsAtTheBudget()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 5000 });
        var d = new[] { Dst("a", 600), Dst("b", 600) };
        Assert.Equal(1000, await Run(w, OutputMode.InOrder, d, 0, 1000));
        Assert.Equal(new double[] { 600, 400 }, d.Select(x => x.Litres));
    }

    [Fact]
    public async Task InOrderSkipsAFilteredDestination()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 300 });
        var d = new[] { Dst("fuel-only", 1000, t => t == "nitron"), Dst("b", 1000) };
        await Run(w, OutputMode.InOrder, d, 0, 1000);
        Assert.Equal(0, d[0].Litres);
        Assert.Equal(300, d[1].Litres);
    }

    [Fact]
    public async Task RoundRobinSplitsOneCycleEvenly()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 5000 });
        var d = new[] { Dst("a", 5000), Dst("b", 5000), Dst("c", 5000) };
        var moved = await Run(w, OutputMode.RoundRobin, d, 0, 900);
        Assert.Equal(900, moved, 6);
        Assert.All(d, x => Assert.Equal(300, x.Litres, 6));
    }

    [Fact]
    public async Task RoundRobinSplitsAScarceSourceEvenlyToo()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 300 });
        var d = new[] { Dst("a", 5000), Dst("b", 5000), Dst("c", 5000) };
        await Run(w, OutputMode.RoundRobin, d, 0, 1000);
        Assert.All(d, x => Assert.Equal(100, x.Litres, 6));
        Assert.Equal(300, w.Total("ore", d));
    }

    [Fact]
    public async Task RoundRobinSkipsFullAndFilteredDestinations()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 5000 });
        var d = new[] { Dst("full", 0), Dst("a", 5000), Dst("tank", 5000, t => t == "nitron"), Dst("b", 5000) };
        var moved = await Run(w, OutputMode.RoundRobin, d, 0, 1000);
        Assert.Equal(1000, moved, 6);
        Assert.Equal(0, d[0].Litres);
        Assert.Equal(0, d[2].Litres);
        Assert.Equal(500, d[1].Litres, 6);
        Assert.Equal(500, d[3].Litres, 6);
    }

    [Fact]
    public async Task RoundRobinGivesASmallDestinationsShareToTheOthers()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 5000 });
        var d = new[] { Dst("small", 100), Dst("a", 5000), Dst("b", 5000) };
        var moved = await Run(w, OutputMode.RoundRobin, d, 0, 1000);
        Assert.Equal(1000, moved, 6);
        Assert.Equal(100, d[0].Litres, 6);
        Assert.Equal(450, d[1].Litres, 6);
        Assert.Equal(450, d[2].Litres, 6);
    }

    [Fact]
    public async Task RoundRobinPlacesAnItemBiggerThanAShare()
    {
        // Three destinations share 30 L; a 25 L part is bigger than a 10 L share. It still moves,
        // to the destination the cycle starts at, and only whole parts move.
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "part", UnitLitres = 25, Qty = 4 });
        var d = new[] { Dst("a", 1000), Dst("b", 1000), Dst("c", 1000) };
        var moved = await Run(w, OutputMode.RoundRobin, d, 1, 30);
        Assert.Equal(25, moved);
        Assert.Equal(1, d[1].Got.GetValueOrDefault("part"));
        Assert.Equal(4, w.Total("part", d));
    }

    [Fact]
    public async Task RoundRobinRotatesFairlyOverManyCycles()
    {
        // One 10 L part fits a cycle; across 30 cycles with the cursor advancing, every
        // destination receives 10, including when one is reordered in between.
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "part", UnitLitres = 10, Qty = 30 });
        var d = new[] { Dst("a", 1000), Dst("b", 1000), Dst("c", 1000) };
        long cursor = 0;
        var firsts = new List<string>();
        for (int cycle = 0; cycle < 30; cycle++)
        {
            var before = d.Select(x => x.Litres).ToArray();
            (_, cursor) = await RunC(w, OutputMode.RoundRobin, d, cursor, 10);
            firsts.Add(d[Enumerable.Range(0, 3).First(i => d[i].Litres > before[i])].Name);
        }
        Assert.All(d, x => Assert.Equal(10, x.Got["part"]));
        Assert.Equal(new[] { "a", "b", "c", "a", "b", "c" }, firsts.Take(6));
        Assert.Equal(30, w.Total("part", d));
    }

    [Fact]
    public async Task RoundRobinFairnessHoldsWhenOneDestinationFills()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "part", UnitLitres = 10, Qty = 40 });
        var d = new[] { Dst("a", 1000), Dst("small", 30), Dst("c", 1000) };
        long cursor = 0;
        for (int cycle = 0; cycle < 40; cycle++)
            (_, cursor) = await RunC(w, OutputMode.RoundRobin, d, cursor, 10);
        Assert.Equal(30, d[1].Litres);                 // full at 3 parts, then skipped
        Assert.Equal(37, d[0].Got["part"] + d[2].Got["part"]);
        Assert.InRange(Math.Abs(d[0].Got["part"] - d[2].Got["part"]), 0, 1);
        Assert.Equal(40, w.Total("part", d));
    }

    [Fact]
    public async Task FuelTanksTakeOnlyFuelInBothModes()
    {
        foreach (var mode in new[] { OutputMode.InOrder, OutputMode.RoundRobin })
        {
            var w = new FakeWorld();
            w.Source.Add(new() { Type = "ore", Qty = 400 });
            w.Source.Add(new() { Type = "nitron", Qty = 200 });
            bool Fuel(string t) => t == "nitron";
            var d = new[] { Dst("tank-1", 150, Fuel), Dst("box", 1000), Dst("tank-2", 150, Fuel) };
            await Run(w, mode, d, 0, 1000);
            Assert.False(d[0].Got.ContainsKey("ore"));
            Assert.False(d[2].Got.ContainsKey("ore"));
            Assert.True(d[0].Litres <= 150 && d[2].Litres <= 150);
            Assert.Equal(400, w.Total("ore", d));
            Assert.Equal(200, w.Total("nitron", d));
            Assert.Equal(600, d.Sum(x => x.Litres));    // everything placed: room is 1300 L
        }
    }

    [Fact]
    public async Task CursorSkipsPastAFullStartDestination()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "part", UnitLitres = 10, Qty = 10 });
        var d = new[] { Dst("a", 1000), Dst("full", 0), Dst("c", 1000) };
        var r = await RunC(w, OutputMode.RoundRobin, d, 1, 10);   // starts at "full"
        Assert.Equal(10, d[2].Litres);                             // c served instead
        Assert.Equal(0, r.next);                                   // so a is next, not c again
        r = await RunC(w, OutputMode.InOrder, d, 5, 10);
        Assert.Equal(5, r.next);                                   // in order never moves it
        r = await RunC(w, OutputMode.RoundRobin, new[] { Dst("x", 0) }, 7, 10);
        Assert.Equal(7, r.next);                                   // nothing moved: unchanged
    }

    [Fact]
    public async Task NothingMovesWithNoBudgetOrNoDestination()
    {
        var w = new FakeWorld();
        w.Source.Add(new() { Type = "ore", Qty = 100 });
        Assert.Equal(0, await Run(w, OutputMode.RoundRobin, new FakeWorld.Dest[0], 0, 1000));
        Assert.Equal(0, await Run(w, OutputMode.RoundRobin, new[] { Dst("a", 100) }, 0, 0));
        Assert.Equal(0, await Run(w, OutputMode.InOrder, new[] { Dst("a", 0) }, 0, 1000));
        Assert.Equal(100, w.SourceLitres);
    }

    [Fact]
    public async Task NeverOverTheBudgetOrTheRoom()
    {
        var rng = new Random(3);
        for (int k = 0; k < 200; k++)
        {
            var w = new FakeWorld();
            w.Source.Add(new() { Type = "ore", Qty = rng.Next(0, 3000) });
            w.Source.Add(new() { Type = "part", UnitLitres = rng.Next(1, 40), Qty = rng.Next(0, 50) });
            var d = Enumerable.Range(0, rng.Next(1, 6)).Select(i => Dst("d" + i, rng.Next(0, 800), i == 2 ? t => t == "part" : null)).ToArray();
            var mode = k % 2 == 0 ? OutputMode.InOrder : OutputMode.RoundRobin;
            double budget = rng.Next(1, 1500);
            long ore0 = w.Total("ore", d), part0 = w.Total("part", d);
            var moved = await Run(w, mode, d, rng.Next(0, 100), budget);
            Assert.True(moved <= budget + 1e-6, $"{mode}: moved {moved} of {budget}");
            Assert.All(d, x => Assert.True(x.Litres <= x.Room + 1e-6));
            Assert.Equal(moved, d.Sum(x => x.Litres), 6);
            Assert.Equal(ore0, w.Total("ore", d));
            Assert.Equal(part0, w.Total("part", d));
        }
    }
}

public class OutputCursorStoreTests
{
    [Fact]
    public void ModeAndCursorSurviveARestart()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltu-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var a = new UnitStore(path);
            a.Update(11, s => { s.OutputMode = OutputRules.Name(OutputMode.RoundRobin); s.OutputCursor = 41; return true; });
            var b = new UnitStore(path);
            b.Load();
            Assert.Equal(OutputMode.RoundRobin, b.Get(11).OutputModeValue);
            Assert.Equal(41, b.Get(11).OutputCursor);
            // A copy carries both (Rekey moves a config to a new element id).
            Assert.True(b.Rekey(11, 12));
            Assert.Equal(41, b.Get(12).OutputCursor);
            Assert.Equal("roundrobin", b.Get(12).OutputMode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfigsSavedBeforeOutputModesLoadInOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltu-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"5\":{\"Source\":\"a\",\"Destination\":\"b\"},\"6\":{\"OutputMode\":null}}");
            var s = new UnitStore(path);
            s.Load();
            Assert.Equal(OutputMode.InOrder, s.Get(5).OutputModeValue);
            Assert.Equal(0, s.Get(5).OutputCursor);
            Assert.Equal("inorder", s.Get(6).OutputMode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CursorIsWrittenAtMostEveryInterval()
    {
        var t0 = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(OutputRules.CursorDue(null, t0));
        Assert.False(OutputRules.CursorDue(t0, t0 + OutputRules.CursorSaveInterval - TimeSpan.FromSeconds(1)));
        Assert.True(OutputRules.CursorDue(t0, t0 + OutputRules.CursorSaveInterval));
    }

    [Fact]
    public async Task RotationContinuesFromTheSavedCursor()
    {
        // Run 5 cycles, save the cursor, "restart" from the store, run 4 more: the starts are
        // exactly those of 9 uninterrupted cycles.
        async Task<List<int>> Starts(int cycles, long cursor0, Func<long, long> afterRun)
        {
            var w = new FakeWorld();
            w.Source.Add(new() { Type = "part", UnitLitres = 10, Qty = 100 });
            var d = Enumerable.Range(0, 4).Select(i => new FakeWorld.Dest { Name = "d" + i, Room = 1000 }).ToArray();
            var starts = new List<int>();
            long c = cursor0;
            for (int i = 0; i < cycles; i++)
            {
                var before = d.Select(x => x.Litres).ToArray();
                (_, c) = await OutputRules.Distribute(OutputMode.RoundRobin, d, c, 10, w.SourceLitres, w.Fill);
                starts.Add(Enumerable.Range(0, 4).First(k => d[k].Litres > before[k]));
            }
            afterRun(c);
            return starts;
        }
        var path = Path.Combine(Path.GetTempPath(), "ltu-out-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UnitStore(path);
            var first = await Starts(5, 0, c => { store.Update(1, s => { s.OutputCursor = c; return true; }); return c; });
            var reloaded = new UnitStore(path);
            reloaded.Load();
            var second = await Starts(4, reloaded.Get(1).OutputCursor, c => c);
            var straight = await Starts(9, 0, c => c);
            Assert.Equal(straight, first.Concat(second));
        }
        finally { File.Delete(path); }
    }
}
