using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LinkedTransferUnit;
using Xunit;

public class ResolveByIdTests
{
    private static PairedElement Stored() => new()
    {
        ElementId = 324479, ConstructId = 1000009, LocalId = 2, ElementTypeId = 77, Name = "ore-in",
    };

    [Fact]
    public void SameRowIsLive() =>
        Assert.Equal(ByIdOutcome.Live, PairResolver.ById(Stored(), new ElementRow(324479, 1000009, 2, 77, true)));

    [Fact]
    public void NoRowIsGone() => Assert.Equal(ByIdOutcome.Gone, PairResolver.ById(Stored(), null));

    [Fact]
    public void DamagedPickupAndRedeployIsMoved()
    {
        // Doc 4a: same id, new local id; and a construct change (redeployed elsewhere).
        Assert.Equal(ByIdOutcome.Moved, PairResolver.ById(Stored(), new ElementRow(324479, 1000009, 7, 77, true)));
        Assert.Equal(ByIdOutcome.Moved, PairResolver.ById(Stored(), new ElementRow(324479, 1000012, 2, 77, true)));
    }

    [Fact]
    public void InInventoryIsDormant()
    {
        Assert.Equal(ByIdOutcome.Dormant, PairResolver.ById(Stored(), new ElementRow(324479, 0, 0, 77, false)));
        // Soft-deleted construct: the row still points at it, but it is not live.
        Assert.Equal(ByIdOutcome.Dormant, PairResolver.ById(Stored(), new ElementRow(324479, 1000009, 2, 77, false)));
    }

    [Fact]
    public void TypeChangeIsGone() =>
        Assert.Equal(ByIdOutcome.Gone, PairResolver.ById(Stored(), new ElementRow(324479, 1000009, 2, 78, true)));
}

public class FallbackTests
{
    private static PairedElement Stored(string name = "ore-in") => new()
    {
        ElementId = 324479, ConstructId = 1000009, LocalId = 7, ElementTypeId = 77, Name = name,
    };

    private static Candidate C(ulong id, ulong construct, ulong local = 7, ulong type = 77, string name = "ore-in") =>
        new() { ElementId = id, ConstructId = construct, LocalId = local, TypeId = type, Name = name };

    [Fact]
    public void NothingMatches()
    {
        var (o, m) = PairResolver.Fallback(Stored(), new Candidate[0]);
        Assert.Equal(FallbackOutcome.NoMatch, o);
        Assert.Null(m);
    }

    [Fact]
    public void SameConstructSameSlotAndName()
    {
        // Repair Unit re-adding a missing element: new id, snapshot's local id and name.
        var (o, m) = PairResolver.Fallback(Stored(), new[] { C(324500, 1000009) });
        Assert.Equal(FallbackOutcome.Accepted, o);
        Assert.Equal(324500UL, m.ElementId);
    }

    [Fact]
    public void SameSlotDifferentNameNeedsRepair()
    {
        // local_ids are reused: a different container in the old slot must not be taken.
        var (o, _) = PairResolver.Fallback(Stored(), new[] { C(324500, 1000009, name: "trash") });
        Assert.Equal(FallbackOutcome.Ambiguous, o);
    }

    [Fact]
    public void DefaultStoredNameAcceptsAnyName()
    {
        var (o, _) = PairResolver.Fallback(Stored(""), new[] { C(324500, 1000009, name: "whatever") });
        Assert.Equal(FallbackOutcome.Accepted, o);
    }

    [Fact]
    public void PristinePickupLosesTheNameSoARenamedPairNeedsRepair()
    {
        // Doc 4b: the redeployed element comes back with the default name.
        var (o, _) = PairResolver.Fallback(Stored("ore-in"), new[] { C(324481, 1000009, name: "") });
        Assert.Equal(FallbackOutcome.Ambiguous, o);
    }

    [Fact]
    public void TypeAndLocalIdMustMatch()
    {
        var (o, _) = PairResolver.Fallback(Stored(), new[] { C(1, 1000009, local: 8), C(2, 1000009, type: 78) });
        Assert.Equal(FallbackOutcome.NoMatch, o);
    }

    [Fact]
    public void OneRebornConstructIsAccepted()
    {
        // Compaction / blueprint redeploy: new construct whose originConstructId is the old one.
        var (o, m) = PairResolver.Fallback(Stored(), new[] { C(324488, 1000010) });
        Assert.Equal(FallbackOutcome.Accepted, o);
        Assert.Equal(1000010UL, m.ConstructId);
    }

    [Fact]
    public void SeveralRebornConstructsAreNeverGuessed()
    {
        var (o, m) = PairResolver.Fallback(Stored(), new[] { C(324488, 1000010), C(324600, 1000011) });
        Assert.Equal(FallbackOutcome.Ambiguous, o);
        Assert.Null(m);
    }

    [Fact]
    public void RebornNameMustPass()
    {
        var (o, _) = PairResolver.Fallback(Stored(), new[] { C(324488, 1000010, name: "renamed") });
        Assert.Equal(FallbackOutcome.Ambiguous, o);
    }

    [Fact]
    public void SameConstructWinsOverReborn()
    {
        var (o, m) = PairResolver.Fallback(Stored(), new[] { C(324488, 1000010), C(324500, 1000009) });
        Assert.Equal(FallbackOutcome.Accepted, o);
        Assert.Equal(324500UL, m.ElementId);
    }

    [Fact]
    public void NameAloneNeverMatches()
    {
        // Same name elsewhere, wrong slot: not a candidate.
        var (o, _) = PairResolver.Fallback(Stored(), new[] { C(9, 1000009, local: 3), C(10, 1000010, local: 4) });
        Assert.Equal(FallbackOutcome.NoMatch, o);
    }

    [Theory]
    [InlineData("a", "a", true)]
    [InlineData(" a ", "a", true)]
    [InlineData("a", "A", false)]
    [InlineData("", "x", true)]
    [InlineData(null, "x", true)]
    [InlineData("a", null, false)]
    public void NameCheck(string stored, string cand, bool ok) => Assert.Equal(ok, PairResolver.NameOk(stored, cand));
}

public class PairRuleTests
{
    private static List<PairedElement> Pairs(int n) =>
        Enumerable.Range(1, n).Select(i => new PairedElement { ElementId = (ulong)i }).ToList();

    [Fact]
    public void AddsUnderTheCap() => Assert.Null(PairRules.CanAdd(Pairs(15), 99, PairRole.Source, 16));

    [Fact]
    public void RefusesAtTheCap()
    {
        var err = PairRules.CanAdd(Pairs(16), 99, PairRole.Destination, 16);
        Assert.Contains("16 paired destinations", err);
    }

    [Fact]
    public void RefusesADuplicateInTheSameRole()
    {
        var err = PairRules.CanAdd(Pairs(3), 2, PairRole.Source, 16);
        Assert.Contains("already paired as a source", err);
    }

    [Fact]
    public void CapZeroRefusesEverything() => Assert.NotNull(PairRules.CanAdd(Pairs(0), 1, PairRole.Source, 0));

    [Theory]
    [InlineData("{\"role\":\"source\",\"elementId\":324479}", PairRole.Source, 324479UL)]
    [InlineData("{\"role\":\"destination\",\"elementId\":\"18446744073709551000\"}", PairRole.Destination, 18446744073709551000UL)]
    [InlineData("{\"role\":\"DST\",\"elementId\":5}", PairRole.Destination, 5UL)]
    public void ParsesUnpairPayloads(string payload, PairRole role, ulong id)
    {
        Assert.True(PairRules.TryParseRolePayload(payload, true, out var r, out var e, out _));
        Assert.Equal(role, r);
        Assert.Equal(id, e);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"role\":\"sideways\",\"elementId\":5}")]
    [InlineData("{\"role\":\"source\"}")]
    [InlineData("{\"role\":\"source\",\"elementId\":0}")]
    [InlineData("{\"role\":\"source\",\"elementId\":-4}")]
    [InlineData("{\"role\":\"source\",\"elementId\":\"abc\"}")]
    [InlineData("[1]")]
    public void RejectsBadUnpairPayloads(string payload)
    {
        Assert.False(PairRules.TryParseRolePayload(payload, true, out _, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Fact]
    public void BeginPayloadNeedsOnlyTheRole()
    {
        Assert.True(PairRules.TryParseRolePayload("{\"role\":\"destination\"}", false, out var r, out _, out _));
        Assert.Equal(PairRole.Destination, r);
    }
}

public class PairValidationTests
{
    private static PairRules.PairCheck Good() => new()
    {
        Role = PairRole.Source, OnLiveConstruct = true, IsContainer = true, UnitEdit = true, TargetRight = true,
        DistanceMeters = 120, RangeMeters = 1000, ElementId = 99, Cap = 16, Existing = new List<PairedElement>(),
    };

    [Fact]
    public void AllGood() => Assert.Null(PairRules.Check(Good()));

    [Fact]
    public void NotTheUnit()
    {
        var c = Good(); c.IsUnitItself = true;
        Assert.Contains("not the unit", PairRules.Check(c));
    }

    [Fact]
    public void MustBeOnAConstruct()
    {
        var c = Good(); c.OnLiveConstruct = false;
        Assert.Contains("not on a construct", PairRules.Check(c));
    }

    [Fact]
    public void MustBeAContainer()
    {
        var c = Good(); c.IsContainer = false;
        Assert.Contains("containers, hubs and fuel tanks", PairRules.Check(c));
    }

    [Fact]
    public void NeedsElementEditOnTheUnit()
    {
        var c = Good(); c.UnitEdit = false;
        Assert.Contains("Element Edit", PairRules.Check(c));
    }

    [Fact]
    public void SourceNeedsRetrieveDestinationNeedsPut()
    {
        var c = Good(); c.TargetRight = false;
        Assert.Contains("Container Retrieve", PairRules.Check(c));
        c.Role = PairRole.Destination;
        Assert.Contains("Container Put", PairRules.Check(c));
    }

    [Fact]
    public void Range()
    {
        var c = Good(); c.DistanceMeters = 1000;
        Assert.Null(PairRules.Check(c));
        c.DistanceMeters = 1000.5;
        Assert.Contains("the range is 1000 m", PairRules.Check(c));
        c.DistanceMeters = null;
        Assert.Contains("Could not locate", PairRules.Check(c));
    }

    [Fact]
    public void CapAndDuplicateComeLast()
    {
        var c = Good();
        c.Existing = Enumerable.Range(1, 16).Select(i => new PairedElement { ElementId = (ulong)i }).ToList();
        Assert.Contains("maximum", PairRules.Check(c));
        c.Existing = new List<PairedElement> { new() { ElementId = 99 } };
        Assert.Contains("already paired", PairRules.Check(c));
    }
}

public class PairingSessionTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LastsTheWindow()
    {
        var s = new PairingSessions();
        s.Begin(10000, 5, 1000007, PairRole.Destination, T0, TimeSpan.FromSeconds(60));
        var got = s.Get(10000, T0.AddSeconds(59));
        Assert.NotNull(got);
        Assert.Equal(5UL, got.UnitId);
        Assert.Equal(PairRole.Destination, got.Role);
        Assert.Null(s.Get(10000, T0.AddSeconds(60)));
        Assert.Null(s.Get(10000, T0.AddSeconds(1)));   // expired sessions are dropped
    }

    [Fact]
    public void ExtendAfterAPair()
    {
        var s = new PairingSessions();
        s.Begin(1, 5, 7, PairRole.Source, T0, TimeSpan.FromSeconds(60));
        s.Extend(1, T0.AddSeconds(50), TimeSpan.FromSeconds(60));
        Assert.NotNull(s.Get(1, T0.AddSeconds(100)));
    }

    [Fact]
    public void OnePerPlayerAndPerPlayerOnly()
    {
        var s = new PairingSessions();
        s.Begin(1, 5, 7, PairRole.Source, T0, TimeSpan.FromSeconds(60));
        s.Begin(1, 6, 7, PairRole.Destination, T0, TimeSpan.FromSeconds(60));
        Assert.Equal(6UL, s.Get(1, T0).UnitId);
        Assert.Null(s.Get(2, T0));
        s.End(1);
        Assert.Null(s.Get(1, T0));
    }
}

public class UnitRekeyTests
{
    private static UnitSettings Saved(ulong construct, ulong local, string name = "") => new()
    {
        Source = "a", ConstructId = construct, LocalId = local, ElementTypeId = 500, UnitName = name,
    };

    private static PlacedUnit U(ulong id, ulong construct, ulong local, ulong origin = 0, string name = "") =>
        new() { ElementId = id, ConstructId = construct, LocalId = local, TypeId = 500, OriginConstructId = origin, Name = name };

    [Fact]
    public void PristineUnitPlacedBackInTheSameSlot()
    {
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings> { [100] = Saved(1000007, 12) }, new[] { U(200, 1000007, 12) });
        Assert.Equal(200UL, m[100]);
    }

    [Fact]
    public void CompactedConstructRedeployed()
    {
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings> { [100] = Saved(1000007, 12, "fuel feed") },
            new[] { U(300, 1000020, 12, origin: 1000007, name: "fuel feed") });
        Assert.Equal(300UL, m[100]);
    }

    [Fact]
    public void TwoCopiesAreNotGuessed()
    {
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings> { [100] = Saved(1000007, 12) },
            new[] { U(300, 1000020, 12, origin: 1000007), U(301, 1000021, 12, origin: 1000007) });
        Assert.Empty(m);
    }

    [Fact]
    public void UnrelatedConstructIsIgnored()
    {
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings> { [100] = Saved(1000007, 12) }, new[] { U(300, 1000020, 12) });
        Assert.Empty(m);
    }

    [Fact]
    public void ConfigWithoutAKeyIsNeverMoved()
    {
        var old = new UnitSettings { Source = "a" };   // saved before the fallback key existed
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings> { [100] = old }, new[] { U(300, 0, 0) });
        Assert.Empty(m);
    }

    [Fact]
    public void TwoConfigsClaimingOneUnitGetNeither()
    {
        // Two old units from the same slot (picked up and replaced twice): the new unit is
        // given to neither, never to whichever came first.
        var m = UnitRekey.Match(new Dictionary<ulong, UnitSettings>
        {
            [100] = Saved(1000007, 12),
            [101] = Saved(1000007, 12),
        }, new[] { U(300, 1000007, 12) });
        Assert.Empty(m);
    }
}

public class PairStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ltu-store-" + Guid.NewGuid().ToString("N"), "units.json");

    [Fact]
    public void PairsSurviveReloadAndKeepPrefixes()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Update(42, x => { x.Source = "ore"; return true; });
        s.Update(42, x =>
        {
            x.DestinationPairs.Add(new PairedElement { ElementId = 7, ConstructId = 1000009, LocalId = 3, ElementTypeId = 77, Name = "fuel" });
            return true;
        });
        var again = new UnitStore(path);
        again.Load();
        var got = again.Get(42);
        Assert.Equal("ore", got.Source);
        var p = Assert.Single(got.DestinationPairs);
        Assert.Equal(1000009UL, p.ConstructId);
        Assert.Equal("fuel", p.Name);
        Directory.Delete(Path.GetDirectoryName(path), true);
    }

    [Fact]
    public void UpdateNeverMutatesThePublishedCopy()
    {
        var s = new UnitStore(TempPath());
        s.Update(1, x => { x.SourcePairs.Add(new PairedElement { ElementId = 9 }); return true; });
        var before = s.Get(1);
        s.Update(1, x => { x.SourcePairs.Clear(); return true; });
        Assert.Single(before.SourcePairs);
        Assert.Empty(s.Get(1).SourcePairs);
        Directory.Delete(Path.GetDirectoryName(s.Path), true);
    }

    [Fact]
    public void RefusedUpdateChangesNothing()
    {
        var s = new UnitStore(TempPath());
        Assert.Null(s.Update(1, x => false));
        Assert.Null(s.Get(1));
    }

    [Fact]
    public void RekeyMovesTheConfig()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Update(1, x => { x.Source = "a"; x.MissingSince = DateTime.UtcNow; return true; });
        Assert.True(s.Rekey(1, 2));
        Assert.Null(s.Get(1));
        Assert.Equal("a", s.Get(2).Source);
        Assert.Null(s.Get(2).MissingSince);
        s.Update(3, x => { x.Source = "b"; return true; });
        Assert.False(s.Rekey(2, 3));   // never overwrite an existing config
        Directory.Delete(Path.GetDirectoryName(path), true);
    }

    [Fact]
    public void OldFileWithoutPairsLoads()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"5\":{\"Source\":\"a\",\"Destination\":\"b\",\"UpdatedBy\":1,\"UpdatedAt\":\"2026-10-09T00:00:00Z\"}}");
        var s = new UnitStore(path);
        s.Load();
        var got = s.Get(5);
        Assert.Empty(got.SourcePairs);
        Assert.Empty(got.DestinationPairs);
        Assert.Equal(0UL, got.ConstructId);
        Directory.Delete(Path.GetDirectoryName(path), true);
    }

    [Fact]
    public void MissingSinceIsPersisted()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Update(1, x => { x.Source = "a"; return true; });
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        s.Prune(new HashSet<ulong>(), t0, TimeSpan.FromHours(1));
        var again = new UnitStore(path);
        again.Load();
        // After a restart the clock is not reset.
        Assert.Equal(new[] { 1UL }, again.Prune(new HashSet<ulong>(), t0.AddHours(1), TimeSpan.FromHours(1)));
        Directory.Delete(Path.GetDirectoryName(path), true);
    }
}

public class PairConfigTests
{
    [Fact]
    public void Defaults()
    {
        var c = new LtuConfig();
        Assert.Equal(16, c.MaxPairsPerRole);
        Assert.Equal(60, c.PairingWindowSeconds);
    }

    [Fact]
    public void Clamped()
    {
        var c = new LtuConfig { MaxPairsPerRole = -3, PairingWindowSeconds = 1, PairRetrySeconds = -1, UnitConfigKeepHours = 0 };
        c.Sanitize();
        Assert.Equal(0, c.MaxPairsPerRole);
        Assert.Equal(5, c.PairingWindowSeconds);
        Assert.Equal(0, c.PairRetrySeconds);
        Assert.Equal(0.1, c.UnitConfigKeepHours);
    }
}
