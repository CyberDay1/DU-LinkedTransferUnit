using System;
using System.Collections.Generic;
using System.IO;
using LinkedTransferUnit;
using Xunit;

public class PanelPayloadTests
{
    [Fact]
    public void ParsesSourceAndDestination()
    {
        Assert.True(UnitSettingsRules.TryParsePayload("{\"source\":\"ore\",\"destination\":\"refinery in\"}", out var s, out var d, out _));
        Assert.Equal("ore", s);
        Assert.Equal("refinery in", d);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"source\":\"a\"}")]
    [InlineData("{\"source\":1,\"destination\":\"b\"}")]
    public void RejectsBadPayloads(string payload)
    {
        Assert.False(UnitSettingsRules.TryParsePayload(payload, out _, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Fact]
    public void RejectsOversizedPayload()
    {
        var big = "{\"source\":\"" + new string('a', UnitSettingsRules.MaxPayloadLength) + "\",\"destination\":\"b\"}";
        Assert.False(UnitSettingsRules.TryParsePayload(big, out _, out _, out _));
    }
}

public class PrefixValidationTests
{
    [Fact]
    public void TrimsAndAccepts()
    {
        Assert.True(UnitSettingsRules.Validate("  outbound-cargo ", " ship-fuel", 64, true, out var s, out var d, out _));
        Assert.Equal("outbound-cargo", s);
        Assert.Equal("ship-fuel", d);
    }

    [Theory]
    [InlineData("", "b")]
    [InlineData("   ", "b")]
    [InlineData("a", "")]
    [InlineData(null, "b")]
    [InlineData("", "")]
    public void EmptySidesAreAllowed(string src, string dst)
    {
        // A side left empty uses only its paired elements; both empty clears the prefixes.
        Assert.True(UnitSettingsRules.Validate(src, dst, 64, true, out var s, out var d, out _));
        Assert.Equal((src ?? "").Trim(), s);
        Assert.Equal((dst ?? "").Trim(), d);
    }

    [Fact]
    public void RejectsTooLong()
    {
        Assert.False(UnitSettingsRules.Validate(new string('a', 9), "b", 8, true, out _, out _, out var err));
        Assert.Contains("longer than 8", err);
        Assert.True(UnitSettingsRules.Validate(new string('a', 8), "b", 8, true, out _, out _, out _));
    }

    [Fact]
    public void RejectsControlCharacters() =>
        Assert.False(UnitSettingsRules.Validate("a\nb", "c", 64, true, out _, out _, out _));

    [Fact]
    public void SamePrefixFollowsCaseRule()
    {
        Assert.False(UnitSettingsRules.Validate("Fuel", "fuel", 64, true, out _, out _, out var err));
        Assert.Equal("Source and destination are the same prefix.", err);
        Assert.True(UnitSettingsRules.Validate("Fuel", "fuel", 64, false, out _, out _, out _));
    }
}

public class RouteChoiceTests
{
    private static UnitSettings Saved(string s, string d) => new() { Source = s, Destination = d };

    [Fact]
    public void SavedPrefixesWinOverTheName()
    {
        var (r, o) = UnitSettingsRules.Resolve(Saved("ore", "refinery"), "cargo > fuel", true, ">");
        Assert.Equal(RouteOrigin.Panel, o);
        Assert.Equal("ore", r.SourcePrefix);
        Assert.Equal("refinery", r.DestPrefix);
    }

    [Fact]
    public void FallsBackToTheNameWhenAllowed()
    {
        var (r, o) = UnitSettingsRules.Resolve(null, "cargo > fuel", true, ">");
        Assert.Equal(RouteOrigin.Name, o);
        Assert.Equal("fuel", r.DestPrefix);
    }

    [Fact]
    public void NameRoutesCanBeTurnedOff()
    {
        var (r, o) = UnitSettingsRules.Resolve(null, "cargo > fuel", false, ">");
        Assert.Null(r);
        Assert.Equal(RouteOrigin.None, o);
    }

    [Fact]
    public void OneSavedPrefixIsAPanelRoute()
    {
        var (r, o) = UnitSettingsRules.Resolve(Saved("ore", ""), "cargo > fuel", true, ">");
        Assert.Equal(RouteOrigin.Panel, o);
        Assert.Equal("ore", r.SourcePrefix);
        Assert.Equal("", r.DestPrefix);
    }

    [Fact]
    public void EmptySavedPrefixesFallBackToTheName()
    {
        var (_, o) = UnitSettingsRules.Resolve(Saved("", ""), "cargo > fuel", true, ">");
        Assert.Equal(RouteOrigin.Name, o);
    }

    [Fact]
    public void PairsTurnTheNameRouteOff()
    {
        var s = Saved("", "");
        s.DestinationPairs.Add(new PairedElement { ElementId = 5 });
        var (r, o) = UnitSettingsRules.Resolve(s, "cargo > fuel", true, ">");
        Assert.Null(r);
        Assert.Equal(RouteOrigin.None, o);
    }

    [Fact]
    public void NoRouteAtAll()
    {
        var (r, o) = UnitSettingsRules.Resolve(null, "Linked Transfer Unit", true, ">");
        Assert.Null(r);
        Assert.Equal(RouteOrigin.None, o);
    }
}

public class MarkMissingTests
{
    [Fact]
    public void SetsItOnce()
    {
        var s = new UnitSettings();
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UnitSettingsRules.MarkMissing(s, t0));
        Assert.Equal(t0, s.MissingSince);
    }

    [Fact]
    public void SecondSightingDoesNotRewriteTheTimestamp()
    {
        var s = new UnitSettings();
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UnitSettingsRules.MarkMissing(s, t0));
        Assert.False(UnitSettingsRules.MarkMissing(s, t0.AddMinutes(5)));
        Assert.Equal(t0, s.MissingSince);
    }
}

public class UnitStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "ltu-store-" + Guid.NewGuid().ToString("N"), "units.json");

    [Fact]
    public void MissingFileIsEmpty()
    {
        var s = new UnitStore(TempPath());
        s.Load();
        Assert.Equal(0, s.Count);
        Assert.Null(s.Get(1));
    }

    [Fact]
    public void SurvivesReload()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Set(18446744073709551000UL, new UnitSettings { Source = "a", Destination = "b", UpdatedBy = 10000 });
        var again = new UnitStore(path);
        again.Load();
        var got = again.Get(18446744073709551000UL);
        Assert.NotNull(got);
        Assert.Equal("a", got.Source);
        Assert.Equal("b", got.Destination);
        Assert.Equal(10000UL, got.UpdatedBy);
        Assert.False(File.Exists(path + ".tmp"));
        Directory.Delete(Path.GetDirectoryName(path), true);
    }

    [Fact]
    public void PruneWaitsForTheGracePeriod()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Set(1, new UnitSettings { Source = "a", Destination = "b" });
        s.Set(2, new UnitSettings { Source = "c", Destination = "d" });
        var t0 = new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
        var placed = new HashSet<ulong> { 1 };

        Assert.Empty(s.Prune(placed, t0, TimeSpan.FromMinutes(10)));             // first miss starts the clock
        Assert.Empty(s.Prune(placed, t0.AddMinutes(5), TimeSpan.FromMinutes(10)));
        Assert.Equal(new[] { 2UL }, s.Prune(placed, t0.AddMinutes(10), TimeSpan.FromMinutes(10)));
        Assert.NotNull(s.Get(1));
        Assert.Null(s.Get(2));
        Directory.Delete(Path.GetDirectoryName(path), true);
    }

    [Fact]
    public void ReappearingUnitResetsTheClock()
    {
        var path = TempPath();
        var s = new UnitStore(path);
        s.Set(7, new UnitSettings { Source = "a", Destination = "b" });
        var t0 = DateTime.UtcNow;
        s.Prune(new HashSet<ulong>(), t0, TimeSpan.FromMinutes(10));
        s.Prune(new HashSet<ulong> { 7 }, t0.AddMinutes(5), TimeSpan.FromMinutes(10));
        Assert.Empty(s.Prune(new HashSet<ulong>(), t0.AddMinutes(12), TimeSpan.FromMinutes(10)));
        Assert.NotNull(s.Get(7));
        Directory.Delete(Path.GetDirectoryName(path), true);
    }
}

public class NewConfigKeyTests
{
    [Fact]
    public void Defaults()
    {
        var c = new LtuConfig();
        Assert.True(c.AllowNameRoutes);
        Assert.True(c.RequireRunning);
        Assert.True(c.HookStockStartStop);
        Assert.Equal(64, c.MaxPrefixLength);
    }

    [Fact]
    public void MaxPrefixLengthIsClamped()
    {
        var c = new LtuConfig { MaxPrefixLength = 0 };
        c.Sanitize();
        Assert.Equal(1, c.MaxPrefixLength);
        c.MaxPrefixLength = 10000;
        c.Sanitize();
        Assert.Equal(256, c.MaxPrefixLength);
    }
}
