using System.IO;
using LinkedTransferUnit;
using Xunit;

public class RoutingTests
{
    [Theory]
    [InlineData("outbound-cargo > ship-fuel", "outbound-cargo", "ship-fuel")]
    [InlineData("  a>b  ", "a", "b")]
    [InlineData("ore hub > refinery in", "ore hub", "refinery in")]
    public void ParsesRoutes(string name, string src, string dst)
    {
        var r = Routing.Parse(name, ">");
        Assert.NotNull(r);
        Assert.Equal(src, r.SourcePrefix);
        Assert.Equal(dst, r.DestPrefix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Transfer Unit")]
    [InlineData("a > b > c")]
    [InlineData(" > b")]
    [InlineData("a > ")]
    public void RejectsNonRoutes(string name) => Assert.Null(Routing.Parse(name, ">"));

    [Fact]
    public void CustomSeparator() => Assert.Equal("b", Routing.Parse("a -> b", "->").DestPrefix);

    [Theory]
    [InlineData("outbound-cargo", true)]
    [InlineData("outbound-cargo-2", true)]
    [InlineData("Outbound-Cargo B", true)]
    [InlineData("  outbound-cargo", true)]
    [InlineData("old-outbound-cargo", false)]
    [InlineData("outbound", false)]
    [InlineData("", false)]
    public void PrefixMatchCaseInsensitive(string element, bool expected) =>
        Assert.Equal(expected, Routing.Matches(element, "outbound-cargo", caseInsensitive: true));

    [Fact]
    public void PrefixMatchCaseSensitive()
    {
        Assert.False(Routing.Matches("Outbound-Cargo", "outbound-cargo", caseInsensitive: false));
        Assert.True(Routing.Matches("outbound-cargo-9", "outbound-cargo", caseInsensitive: false));
    }
}

public class TransferMathTests
{
    [Fact]
    public void ContainerFillsToCapacity() => Assert.Equal(400, TransferMath.FreeVolume(600, 1000, false, 50));

    [Fact]
    public void FuelTankFillsToPercent() => Assert.Equal(200, TransferMath.FreeVolume(600, 1000, true, 80));

    [Fact]
    public void FullOrOverfilledHasNoRoom()
    {
        Assert.Equal(0, TransferMath.FreeVolume(1000, 1000, true, 100));
        Assert.Equal(0, TransferMath.FreeVolume(1200, 1000, false, 100));
        Assert.Equal(0, TransferMath.FreeVolume(0, 0, false, 100));
    }

    [Theory]
    [InlineData(50, 400, 1000, 50)]
    [InlineData(5000, 400, 1000, 400)]
    [InlineData(5000, 4000, 1000, 1000)]
    [InlineData(5000, 4000, 0, 0)]
    public void LitresIsTheSmallestLimit(double slot, double free, double budget, double expected) =>
        Assert.Equal(expected, TransferMath.LitresToMove(slot, free, budget));
}

public class ConfigTests
{
    [Fact]
    public void MissingFileGivesDefaults()
    {
        var (cfg, _) = LtuConfig.Load(Path.Combine(Path.GetTempPath(), "no-such-ltu-config.json"));
        Assert.Equal(1000, cfg.MaxRangeMeters);
        Assert.True(cfg.CaseInsensitive);
        Assert.Equal("AtmoFuel", cfg.FuelTypeFamilies["atmospheric"]);
    }

    [Fact]
    public void ShippedSampleParsesAndClamps()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "{ // comment\n \"maxRangeMeters\": 250, \"cycleSeconds\": 0, \"fuelFillPercent\": 150, }");
        var (cfg, _) = LtuConfig.Load(path);
        Assert.Equal(250, cfg.MaxRangeMeters);
        Assert.Equal(1, cfg.CycleSeconds);
        Assert.Equal(100, cfg.FuelFillPercent);
        File.Delete(path);
    }

    [Fact]
    public void RepoSampleConfigLoads()
    {
        var sample = Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "ModLinkedTransferUnit.json");
        var (cfg, source) = LtuConfig.Load(sample);
        Assert.Contains("ModLinkedTransferUnit.json", source);
        Assert.Equal("LinkedTransferUnit", cfg.ItemName);
    }
}
