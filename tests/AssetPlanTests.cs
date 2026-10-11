using System.Linq;
using LinkedTransferUnit;
using Xunit;

public class AssetPlanTests
{
    static readonly string[] Folder =
    {
        "/m/client/ltu_kit.nqdef", "/m/client/ltu_mesh.nqdef", "/m/client/ltu_kit_v1.node",
        "/m/client/ltu_kit_v1.mat", "/m/client/ltu_mesh_v1.mesh", "/m/client/README.md", "/m/client/notes.txt",
    };

    [Fact]
    public void Variant_picks_its_own_nqdef_and_sends_every_art_file()
    {
        var (nqdef, files, warnings) = AssetPlan.Plan(Folder, "kit");
        Assert.Equal("ltu_kit.nqdef", nqdef);
        Assert.Equal(new[] { "ltu_kit_v1.mat", "ltu_kit_v1.node" }, files.ToArray());
        Assert.Empty(warnings);
    }

    [Fact]
    public void Other_variants_files_stay_home_shared_files_always_go()
    {
        var folder = Folder.Append("/m/client/ltu_shared_icon.png").Append("/m/client/ltu_meshy.dds");
        var (_, files, _) = AssetPlan.Plan(folder, "mesh");
        Assert.Equal(new[] { "ltu_mesh_v1.mesh", "ltu_meshy.dds", "ltu_shared_icon.png" }, files.ToArray());
    }

    [Fact]
    public void Variant_is_case_and_space_insensitive()
    {
        Assert.Equal("ltu_mesh.nqdef", AssetPlan.Plan(Folder, " Mesh ").nqdef);
    }

    [Fact]
    public void Unknown_variant_sends_no_definition_and_warns()
    {
        var (nqdef, _, warnings) = AssetPlan.Plan(Folder, "gold");
        Assert.Null(nqdef);
        Assert.Contains(warnings, w => w.Contains("ltu_gold.nqdef"));
    }

    [Fact]
    public void Upper_case_file_names_are_flagged()
    {
        var (_, files, warnings) = AssetPlan.Plan(new[] { "ltu_kit.nqdef", "LTU_Body.mesh" }, "kit");
        Assert.Contains("LTU_Body.mesh", files);
        Assert.Contains(warnings, w => w.Contains("LTU_Body.mesh"));
    }

    [Fact]
    public void Client_art_on_clears_the_alias_off_restores_it()
    {
        var c = new LtuConfig();
        Assert.True(c.ClientAssets);
        Assert.Equal("", c.EffectiveAssetAlias);
        c.ClientAssets = false;
        Assert.Equal("TransferUnit", c.EffectiveAssetAlias);
    }
}
