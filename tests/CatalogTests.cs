using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LinkedTransferUnit;
using Xunit;

/// A small item bank shaped like the real one:
/// GameplayObject > BaseItem > { Consumable > Material > { Ore > Bauxite, Coal ; Pure > PureAluminium ; Fuel > Kergon },
///                               Part > { DeprecatedPart (hidden) > OldGear },
///                               DataItem > Blueprint,
///                               EmptyGroup (no children with items) > Nothing (hidden) }
static class Bank
{
    public const ulong Gameplay = 1, BaseItem = 2, Consumable = 3, Material = 4, Ore = 5, Bauxite = 6, Coal = 7,
        Pure = 8, PureAluminium = 9, Fuel = 10, Kergon = 11, Part = 12, DeprecatedPart = 13, OldGear = 14,
        DataItem = 15, Blueprint = 16, EmptyGroup = 17, Nothing = 18, Screw = 19,
        // Mirrors the live bank's "Tool" ("Tools") and "MissionPackageContentAphelia"
        // ("Aphelia Mission Packages") top-level groups, both excluded by the production default.
        Tool = 20, Wrench = 21, MissionPkg = 22, SomePackage = 23;

    public static List<CatalogNode> Nodes() => new()
    {
        N(Gameplay, "GameplayObject", "", 0),
        N(BaseItem, "BaseItem", "All items", Gameplay),
        N(Consumable, "Consumable", "Consumables", BaseItem),
        N(Material, "Material", "Materials", Consumable),
        N(Ore, "OreMaterial", "Ore", Material),
        N(Bauxite, "AluminiumOre", "Bauxite", Ore),
        N(Coal, "CarbonOre", "Coal", Ore),
        N(Pure, "PureMaterial", "Pure", Material),
        N(PureAluminium, "AluminiumPure", "Pure Aluminium", Pure),
        N(Fuel, "Fuel", "Fuels", Material),
        N(Kergon, "Kergon1", "", Fuel),                     // no display name
        N(Part, "Part", "Parts", BaseItem),
        N(Screw, "Screw", "Screw", Part, alias: "AluminiumScrew"),
        N(DeprecatedPart, "DeprecatedPart", "Deprecated parts", Part, hidden: true),
        N(OldGear, "OldGear", "Old gear", DeprecatedPart),
        N(DataItem, "DataItem", "Data Item", BaseItem),
        N(Blueprint, "Blueprint", "Blueprint", DataItem),
        N(EmptyGroup, "EmptyGroup", "Empty", BaseItem),
        N(Nothing, "Nothing", "Nothing", EmptyGroup, hidden: true),
        N(Tool, "Tool", "Tools", BaseItem),
        N(Wrench, "Wrench", "Wrench", Tool),
        N(MissionPkg, "MissionPackageContentAphelia", "Aphelia Mission Packages", BaseItem),
        N(SomePackage, "SomePackage", "Some Package", MissionPkg),
    };

    public static readonly Dictionary<string, string> Icons = new()
    {
        ["AluminiumOre"] = "ore/bauxite.png",
        ["CarbonOre"] = "ore/coal.png",
        ["Fuel"] = "fuel/generic.png",
        ["AluminiumScrew"] = "parts/screw.png",
    };

    static CatalogNode N(ulong id, string name, string label, ulong parent, bool hidden = false, string alias = "") =>
        new() { Id = id, Name = name, DisplayName = label, ParentId = parent, Hidden = hidden, AssetAlias = alias };

    // Mirrors LtuConfig's production default (CatalogExclude).
    public static Catalog Build() => Catalog.Build(Nodes(), "BaseItem", Icons, new[] { "DataItem", "Tool", "MissionPackageContentAphelia" });

    /// Self-and-ancestors, as TransferEngine.Lineage walks the bank.
    public static IEnumerable<ulong> Lineage(ulong id)
    {
        var parent = Nodes().ToDictionary(n => n.Id, n => n.ParentId);
        for (int guard = 0; id != 0 && guard < 64; guard++)
        {
            yield return id;
            id = parent.TryGetValue(id, out var p) ? p : 0;
        }
    }
}

public class CatalogBuildTests
{
    [Fact]
    public void KeepsVisibleItemsUnderTheRoot()
    {
        var c = Bank.Build();
        Assert.Equal(Bank.BaseItem, c.RootId);
        Assert.Equal(5, c.ItemCount);   // bauxite, coal, pure aluminium, kergon, screw
        foreach (var id in new[] { Bank.Bauxite, Bank.Coal, Bank.PureAluminium, Bank.Kergon, Bank.Screw })
            Assert.False(c.Get(id)!.IsGroup);
        Assert.True(c.Get(Bank.Ore)!.IsGroup);
        Assert.Null(c.Get(Bank.Gameplay));          // above the root
    }

    [Fact]
    public void DropsHiddenExcludedAndEmptySubtrees()
    {
        var c = Bank.Build();
        Assert.Null(c.Get(Bank.DeprecatedPart));    // hidden group
        Assert.Null(c.Get(Bank.OldGear));           // under a hidden group
        Assert.Null(c.Get(Bank.DataItem));          // excluded by name
        Assert.Null(c.Get(Bank.Blueprint));
        Assert.Null(c.Get(Bank.EmptyGroup));        // nothing visible under it
        Assert.Null(c.Get(Bank.Tool));              // excluded by name (production default)
        Assert.Null(c.Get(Bank.Wrench));
        Assert.Null(c.Get(Bank.MissionPkg));        // excluded by name (production default)
        Assert.Null(c.Get(Bank.SomePackage));
    }

    [Fact]
    public void ExcludingMultipleNamesDropsEachSubtreeButLeavesOthersVisible()
    {
        // Without the exclusion, Tool and MissionPackageContentAphelia show up like any
        // other top-level group under BaseItem.
        var unfiltered = Catalog.Build(Bank.Nodes(), "BaseItem", Bank.Icons, new[] { "DataItem" });
        Assert.NotNull(unfiltered.Get(Bank.Tool));
        Assert.NotNull(unfiltered.Get(Bank.Wrench));
        Assert.NotNull(unfiltered.Get(Bank.MissionPkg));
        Assert.NotNull(unfiltered.Get(Bank.SomePackage));
        Assert.Contains("Tools", unfiltered.Browse(Bank.BaseItem, 100).Groups.Select(g => g.Label));
        Assert.Contains("Aphelia Mission Packages", unfiltered.Browse(Bank.BaseItem, 100).Groups.Select(g => g.Label));

        // With both excluded (the production default), neither group nor its items appear
        // anywhere in the catalogue, while unrelated groups are unaffected.
        var c = Bank.Build();
        Assert.Null(c.Get(Bank.Tool));
        Assert.Null(c.Get(Bank.Wrench));
        Assert.Null(c.Get(Bank.MissionPkg));
        Assert.Null(c.Get(Bank.SomePackage));
        var rootGroups = c.Browse(Bank.BaseItem, 100).Groups.Select(g => g.Label).ToList();
        Assert.DoesNotContain("Tools", rootGroups);
        Assert.DoesNotContain("Aphelia Mission Packages", rootGroups);
        Assert.Equal(new[] { "Consumables", "Parts" }, rootGroups);
        Assert.Empty(c.Search("wrench", 10).Items);
        Assert.Empty(c.Search("mission", 10).Items.Concat(c.Search("mission", 10).Groups));
    }

    [Fact]
    public void CountsItemsPerGroup()
    {
        var c = Bank.Build();
        Assert.Equal(2, c.Get(Bank.Ore)!.ItemCount);
        Assert.Equal(4, c.Get(Bank.Material)!.ItemCount);
        Assert.Equal(1, c.Get(Bank.Part)!.ItemCount);
        Assert.Equal(5, c.Get(Bank.BaseItem)!.ItemCount);
    }

    [Fact]
    public void LabelFallsBackToTheName()
    {
        Assert.Equal("Kergon1", Bank.Build().Get(Bank.Kergon)!.Label);
        Assert.Equal("Bauxite", Bank.Build().Get(Bank.Bauxite)!.Label);
    }

    [Fact]
    public void IconsComeFromNameAliasAncestorOrFirstItem()
    {
        var c = Bank.Build();
        Assert.Equal("ore/bauxite.png", c.Get(Bank.Bauxite)!.Icon);           // own
        Assert.Equal("parts/screw.png", c.Get(Bank.Screw)!.Icon);             // asset alias
        Assert.Equal("fuel/generic.png", c.Get(Bank.Kergon)!.Icon);           // nearest ancestor
        Assert.Equal("", c.Get(Bank.PureAluminium)!.Icon);                    // none anywhere
        Assert.Equal("ore/bauxite.png", c.Get(Bank.Ore)!.Icon);               // group: first item by label
    }

    [Fact]
    public void MissingRootGivesAnEmptyCatalogue()
    {
        var c = Catalog.Build(Bank.Nodes(), "NoSuchRoot", Bank.Icons);
        Assert.Equal(0, c.ItemCount);
        Assert.Empty(c.Browse(0, 10).Items);
    }

    [Fact]
    public void RootAncestorLinkedBackIntoTheTreeIsVisitedOnce()
    {
        // As on the live server: the root's own ancestor shows up again under the root.
        var nodes = Bank.Nodes();
        nodes.First(n => n.Id == Bank.Gameplay).ParentId = Bank.Consumable;
        var c = Catalog.Build(nodes, "BaseItem", Bank.Icons, new[] { "DataItem", "Tool", "MissionPackageContentAphelia" });
        Assert.Equal(5, c.ItemCount);
        Assert.Null(c.Get(Bank.Gameplay));
        Assert.Equal(new[] { "Materials" }, c.Browse(Bank.Consumable, 10).Groups.Select(g => g.Label));
    }

    [Fact]
    public void ParentCycleDoesNotHang()
    {
        var nodes = Bank.Nodes();
        nodes.Add(new CatalogNode { Id = 50, Name = "Loop", ParentId = 50 });
        var c = Catalog.Build(nodes, "BaseItem", Bank.Icons);
        Assert.Null(c.Get(50));
    }
}

public class CatalogQueryTests
{
    [Fact]
    public void BrowseListsGroupsThenItemsByLabel()
    {
        var c = Bank.Build();
        var root = c.Browse(Bank.BaseItem, 100);
        Assert.Equal(new[] { "Consumables", "Parts" }, root.Groups.Select(g => g.Label));
        Assert.Empty(root.Items);
        var ore = c.Browse(Bank.Ore, 100);
        Assert.Equal(new[] { "Bauxite", "Coal" }, ore.Items.Select(i => i.Label));
        Assert.Equal(new[] { "All items", "Consumables", "Materials", "Ore" }, ore.Path.Select(p => p.Label));
    }

    [Fact]
    public void BrowseTruncatesLongGroups()
    {
        var page = Bank.Build().Browse(Bank.Ore, 1);
        Assert.Single(page.Items);
        Assert.Equal(2, page.Total);
        Assert.True(page.Truncated);
    }

    [Fact]
    public void BrowseOfAnItemOrUnknownIdShowsTheRoot()
    {
        var c = Bank.Build();
        Assert.Equal(Bank.BaseItem, c.Browse(Bank.Bauxite, 10).Group!.Id);
        Assert.Equal(Bank.BaseItem, c.Browse(999, 10).Group!.Id);
    }

    [Fact]
    public void PathLabelSkipsRootAndSelf()
    {
        Assert.Equal("Consumables / Materials / Ore", Bank.Build().PathLabel(Bank.Bauxite));
    }

    [Fact]
    public void SearchMatchesLabelOrNameIgnoringCase()
    {
        var c = Bank.Build();
        Assert.Equal(new[] { Bank.Bauxite }, c.Search("BAUX", 10).Items.Select(i => i.Id));
        Assert.Equal(new[] { Bank.Bauxite }, c.Search("aluminiumore", 10).Items.Select(i => i.Id));   // item-bank name
        Assert.Equal(new[] { Bank.PureAluminium }, c.Search("pure alu", 10).Items.Select(i => i.Id)); // every word
        Assert.Empty(c.Search("   ", 10).Items);
    }

    [Fact]
    public void SearchFindsGroupsAndRanksPrefixFirst()
    {
        var c = Bank.Build();
        var page = c.Search("pure", 10);
        Assert.Equal(new[] { Bank.Pure }, page.Groups.Select(g => g.Id));
        Assert.Equal(new[] { Bank.PureAluminium }, page.Items.Select(i => i.Id));
        var ore = c.Search("o", 10);  // "Ore" starts with o and ranks before "Coal", which only contains it
        Assert.Equal(Bank.Ore, ore.Groups.First().Id);
    }

    [Fact]
    public void SearchCapsResults()
    {
        var page = Bank.Build().Search("a", 2);
        Assert.Equal(2, page.Groups.Count + page.Items.Count);
        Assert.True(page.Truncated);
        Assert.True(page.Total > 2);
    }
}

public class ItemFilterTests
{
    static ItemFilterSpec F(string mode, params ulong[] ids) => new() { Mode = mode, Items = ids.ToList() };

    [Fact]
    public void OffAndNullPassEverything()
    {
        Assert.True(ItemFilter.Passes(null, Bank.Lineage(Bank.Coal)));
        Assert.True(ItemFilter.Passes(F("off", Bank.Coal), Bank.Lineage(Bank.Coal)));
    }

    [Fact]
    public void WhitelistPassesListedItemsAndGroupMembersOnly()
    {
        var f = F("whitelist", Bank.Bauxite, Bank.Pure);
        Assert.True(ItemFilter.Passes(f, Bank.Lineage(Bank.Bauxite)));
        Assert.True(ItemFilter.Passes(f, Bank.Lineage(Bank.PureAluminium)));   // through the group
        Assert.False(ItemFilter.Passes(f, Bank.Lineage(Bank.Coal)));
        Assert.False(ItemFilter.Passes(f, Bank.Lineage(Bank.Kergon)));
    }

    [Fact]
    public void BlacklistBlocksListedItemsAndGroupMembers()
    {
        var f = F("blacklist", Bank.Ore);
        Assert.False(ItemFilter.Passes(f, Bank.Lineage(Bank.Bauxite)));
        Assert.False(ItemFilter.Passes(f, Bank.Lineage(Bank.Coal)));
        Assert.True(ItemFilter.Passes(f, Bank.Lineage(Bank.Kergon)));
    }

    [Fact]
    public void EmptyListsAreStrictWhitelistsAndOpenBlacklists()
    {
        Assert.False(ItemFilter.Passes(F("whitelist"), Bank.Lineage(Bank.Coal)));
        Assert.True(ItemFilter.Passes(F("blacklist"), Bank.Lineage(Bank.Coal)));
    }

    [Fact]
    public void UnknownStoredModeCountsAsOff()
    {
        Assert.True(ItemFilter.Passes(F("greylist", Bank.Coal), Bank.Lineage(Bank.Bauxite)));
    }

    [Fact]
    public void ParsesStringAndNumberIds()
    {
        Assert.True(ItemFilter.TryParsePayload("{\"mode\":\"Whitelist\",\"items\":[\"6\",7]}", out var mode, out var ids, out _));
        Assert.Equal(FilterMode.Whitelist, mode);
        Assert.Equal(new ulong[] { 6, 7 }, ids);
        Assert.True(ItemFilter.TryParsePayload("{\"mode\":\"off\"}", out mode, out ids, out _));
        Assert.Equal(FilterMode.Off, mode);
        Assert.Empty(ids);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("[]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"mode\":\"greylist\",\"items\":[]}")]
    [InlineData("{\"mode\":\"whitelist\",\"items\":\"6\"}")]
    [InlineData("{\"mode\":\"whitelist\",\"items\":[\"six\"]}")]
    [InlineData("{\"mode\":\"whitelist\",\"items\":[-1]}")]
    [InlineData("{\"mode\":\"whitelist\",\"items\":[1.5]}")]
    public void RejectsBadPayloads(string payload)
    {
        Assert.False(ItemFilter.TryParsePayload(payload, out _, out _, out var err));
        Assert.NotEmpty(err);
    }

    [Fact]
    public void RejectsOversizedPayload()
    {
        var big = "{\"mode\":\"whitelist\",\"items\":[\"" + new string('1', ItemFilter.MaxPayloadLength) + "\"]}";
        Assert.False(ItemFilter.TryParsePayload(big, out _, out _, out _));
    }

    [Fact]
    public void ValidateDedupesAndChecksTheCatalogue()
    {
        var c = Bank.Build();
        Assert.True(ItemFilter.Validate(FilterMode.Blacklist, new ulong[] { Bank.Coal, Bank.Ore, Bank.Coal }, c.Contains, 10, out var spec, out _));
        Assert.Equal("blacklist", spec.Mode);
        Assert.Equal(new[] { Bank.Coal, Bank.Ore }, spec.Items);

        Assert.False(ItemFilter.Validate(FilterMode.Whitelist, new[] { Bank.OldGear }, c.Contains, 10, out _, out var err));   // hidden
        Assert.Contains(Bank.OldGear.ToString(), err);
        Assert.False(ItemFilter.Validate(FilterMode.Whitelist, new ulong[] { 999 }, c.Contains, 10, out _, out _));
    }

    [Fact]
    public void ValidateCapsTheListSize()
    {
        var c = Bank.Build();
        Assert.True(ItemFilter.Validate(FilterMode.Whitelist, new[] { Bank.Coal, Bank.Bauxite }, c.Contains, 2, out _, out _));
        Assert.False(ItemFilter.Validate(FilterMode.Whitelist, new[] { Bank.Coal, Bank.Bauxite, Bank.Kergon }, c.Contains, 2, out _, out var err));
        Assert.Contains("at most 2", err);
    }
}

public class FilterStoreTests
{
    [Fact]
    public void FilterSurvivesTheStoreAndCopiesAreIndependent()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltu-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new UnitStore(path);
            store.Update(42, s => { s.Filter = new ItemFilterSpec { Mode = "whitelist", Items = { 6, 8 } }; return true; });
            var copy = store.Get(42)!.Copy();
            copy.Filter.Items.Add(99);
            Assert.Equal(new ulong[] { 6, 8 }, store.Get(42)!.Filter.Items);

            var again = new UnitStore(path);
            again.Load();
            Assert.Equal("whitelist", again.Get(42)!.Filter.Mode);
            Assert.Equal(new ulong[] { 6, 8 }, again.Get(42)!.Filter.Items);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConfigsSavedBeforeFiltersLoadWithTheFilterOff()
    {
        var path = Path.Combine(Path.GetTempPath(), "ltu-filter-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"7\":{\"Source\":\"ore\",\"Destination\":\"\",\"Filter\":null}}");
            var store = new UnitStore(path);
            store.Load();
            Assert.Equal(FilterMode.Off, store.Get(7)!.Filter.ModeValue);
            Assert.Empty(store.Get(7)!.Filter.Items);
        }
        finally { File.Delete(path); }
    }
}
