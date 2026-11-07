using System.Linq;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using Xunit;

namespace VoxelEngine.Tests.Content;

/// <summary>
/// The metalworks content pipeline: molten materials, the melt recipes that fill a crucible and
/// the cast recipes that empty it into a fired mold. Every assertion is a relation between the
/// compiled tables — mass conservation, round-tripping ids, the (mold, material) key — rather
/// than a content literal, so retuning how much metal an ore yields never breaks a test.
/// </summary>
public sealed class MetalworksRecipeTests
{
    public MetalworksRecipeTests() => TestContent.EnsureLoaded();

    private static byte Item(string name) => ContentRegistry.RequireItem(name).Id;

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    [Fact]
    public void EveryPureCopperFormMeltsIntoOneMaterialAndBranchDoesNot()
    {
        var copper = ContentRegistry.MoltenMaterialByName("copper");
        Assert.NotNull(copper);

        string[] pureCopper =
        [
            "malachite_ore", "crushed_copper", "copper_ingot", "copper_pickaxe_head", "copper_axe_head",
            "copper_sword_head",
        ];

        foreach (var name in pureCopper)
        {
            var recipe = ContentRegistry.MeltRecipeForInput(Item(name));
            Assert.NotNull(recipe);
            // The crucible takes any form of a metal as long as they all yield the same melt.
            Assert.NotEqual((byte)0, recipe.MaterialId);
            Assert.Equal(copper.Id, recipe.MaterialId);
            Assert.Same(copper, ContentRegistry.MoltenMaterialById(recipe.MaterialId));
            Assert.True(recipe.AmountMl > 0);
        }

        // A stick is not metal: nothing melts that has no recipe, which is the whole gate.
        Assert.Null(ContentRegistry.MeltRecipeForInput(Item("branch")));
    }

    [Fact]
    public void EveryPureBronzeFormMeltsToBronzeAndEveryBronzeMoldCastsIt()
    {
        var bronze = ContentRegistry.MoltenMaterialByName("bronze");
        Assert.NotNull(bronze);

        foreach (var name in new[] { "bronze_ingot", "bronze_pickaxe_head", "bronze_axe_head", "bronze_sword_head" })
        {
            var recipe = ContentRegistry.MeltRecipeForInput(Item(name));
            Assert.NotNull(recipe);
            Assert.Equal(bronze.Id, recipe.MaterialId);
        }

        // Bronze is cast in the same fired molds as the other metals; the material picks the result.
        foreach (var mold in new[] { "fired_mold_ingot", "fired_mold_pickaxe", "fired_mold_axe", "fired_mold_sword" })
        {
            var cast = ContentRegistry.CastRecipeFor(Block(mold), bronze.Id);
            Assert.NotNull(cast);
            Assert.Equal(bronze.Id, ContentRegistry.MeltRecipeForInput(cast.ResultId)!.MaterialId);
        }
    }

    [Fact]
    public void IronCrushesInTheQuernButNeverMelts()
    {
        var crush = ContentRegistry.CrushRecipeForInput(Item("hematite_ore"));
        Assert.NotNull(crush);
        Assert.Equal(Item("crushed_iron"), crush.ResultId);

        // Without a melt recipe KilnManager never accepts the input: iron is smelted solid-state in the bloomery,
        // never melted in a crucible.
        foreach (var id in new[]
                 {
                     Item("hematite_ore"), Block("hematite_ore"), Item("crushed_iron"),
                     Item("iron_sword_head"), Item("iron_sword"),
                 })
            Assert.Null(ContentRegistry.MeltRecipeForInput(id));

        Assert.Null(ContentRegistry.MoltenMaterialByName("iron"));
    }

    [Fact]
    public void ACastMeltsBackIntoExactlyWhatItTook()
    {
        Assert.NotEmpty(ContentRegistry.CastRecipesList);

        foreach (var cast in ContentRegistry.CastRecipesList)
        {
            var melt = ContentRegistry.MeltRecipeForInput(cast.ResultId);
            Assert.NotNull(melt);
            Assert.Equal(cast.MaterialId, melt.MaterialId);
            // Mass conservation: re-melting the whole yield returns the poured volume, so a
            // miscast is never a loss of metal.
            Assert.Equal(cast.AmountMl * cast.ResultCount, melt.AmountMl);
        }
    }

    [Fact]
    public void CastRecipesAreKeyedByMoldAndMaterial()
    {
        var copper = ContentRegistry.MoltenMaterialByName("copper")!.Id;

        var pickaxe = ContentRegistry.CastRecipeFor(Block("fired_mold_pickaxe"), copper);
        Assert.NotNull(pickaxe);
        Assert.Equal(Item("copper_pickaxe_head"), pickaxe.ResultId);
        Assert.True(pickaxe.CoolSeconds > 0f);
        Assert.True(pickaxe.ResultCount >= 1);

        var ingot = ContentRegistry.CastRecipeFor(Block("fired_mold_ingot"), copper);
        Assert.NotNull(ingot);
        Assert.Equal(Item("copper_ingot"), ingot.ResultId);

        var axe = ContentRegistry.CastRecipeFor(Block("fired_mold_axe"), copper);
        Assert.NotNull(axe);
        Assert.Equal(Item("copper_axe_head"), axe.ResultId);

        // Wet clay holds no metal, and 0 is "no material", so neither key casts anything.
        Assert.Null(ContentRegistry.CastRecipeFor(Block("mold_pickaxe"), copper));
        Assert.Null(ContentRegistry.CastRecipeFor(Block("fired_mold_pickaxe"), 0));

        foreach (var fired in new[] { "fired_mold_pickaxe", "fired_mold_axe", "fired_mold_ingot" })
            Assert.True(ContentRegistry.IsCastMold(Block(fired)));
        foreach (var raw in new[] { "mold_pickaxe", "mold_axe", "mold_ingot", "stone" })
            Assert.False(ContentRegistry.IsCastMold(Block(raw)));
        Assert.False(ContentRegistry.IsCastMold(BlockType.Air));
    }

    [Fact]
    public void MoltenMaterialResolvesItsTileAndRoundTripsById()
    {
        var copper = ContentRegistry.MoltenMaterialByName("copper");
        Assert.NotNull(copper);
        Assert.NotEqual((byte)0, copper.Id);
        Assert.Same(copper, ContentRegistry.MoltenMaterialById(copper.Id));

        // 0 is "no material", which is what an empty crucible carries.
        Assert.Null(ContentRegistry.MoltenMaterialById(0));
        Assert.Null(ContentRegistry.MoltenMaterialByName("nothing"));

        // The pool is one tinted atlas tile, so the tile has to be a real slice and the tint opaque.
        Assert.Equal(TextureAtlas.TileOf(copper.TextureName), copper.Tile);
        Assert.Equal((byte)255, copper.Color.A);
        Assert.NotEmpty(copper.DisplayName);

        // Ids are 1-based positions in the published list, which is what MoltenMaterialById indexes.
        var materials = ContentRegistry.MoltenMaterials;
        Assert.NotEmpty(materials);
        Assert.Equal(
            Enumerable.Range(1, materials.Count).Select(i => (byte)i).ToArray(),
            materials.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void SandAndGlassMeltIntoMoltenGlassAndTheBasinCastsItBackIntoABlock()
    {
        var glass = ContentRegistry.MoltenMaterialByName("glass");
        Assert.NotNull(glass);

        foreach (var input in new[] { Block("sand"), Block("glass") })
        {
            var melt = ContentRegistry.MeltRecipeForInput(input);
            Assert.NotNull(melt);
            Assert.Equal(glass.Id, melt.MaterialId);
        }

        // The basin is the mold that turns molten material into a block
        var cast = ContentRegistry.CastRecipeFor(Block("casting_basin"), glass.Id);
        Assert.NotNull(cast);
        Assert.False(ItemRegistry.IsItem(cast.ResultId));
        Assert.True(ContentRegistry.IsCastMold(Block("casting_basin")));

        // Raw clay is not a mold, and copper has no basin recipe
        Assert.False(ContentRegistry.IsCastMold(Block("casting_basin_raw")));
        Assert.Null(ContentRegistry.CastRecipeFor(Block("casting_basin"), ContentRegistry.MoltenMaterialByName("copper")!.Id));
    }
}
