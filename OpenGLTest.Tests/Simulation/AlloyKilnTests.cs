using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Interaction;
using VoxelEngine.Items;
using VoxelEngine.Simulation;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation;

/// <summary>
/// The kiln's crucible rules over the shipped content: a plain kiln refuses a second metal, an
/// alloy kiln (a profile with <see cref="KilnProfile.CanAlloy"/>) takes an alloy's parts in any
/// order and melts every full set into the alloy, an unfinished set waits in the fire, and no
/// milliliter is ever gained or lost on the way.
/// </summary>
public sealed class AlloyKilnTests
{
    public AlloyKilnTests() => TestContent.EnsureLoaded();

    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static Item Crucible => ContentRegistry.RequireItem("crucible");
    private static Item Coal => ContentRegistry.RequireItem("coal");
    private static byte Material(string name) => ContentRegistry.MoltenMaterialByName(name)!.Id;
    private static AlloyRecipe Bronze => ContentRegistry.AlloyRecipes.Single(a => a.ResultId == Material("bronze"));

    private static MeltRecipe MeltOf(string itemName) =>
        ContentRegistry.MeltRecipeForInput(ContentRegistry.RequireItem(itemName).Id)!;

    private static (ChunkManager Manager, KilnManager Kilns, DropManager Drops, KilnManager.KilnState State)
        KilnWithCrucible(Vector3 anchor, bool canAlloy)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        Multiblock.Convert(manager, anchor, 0, Kiln, new HashSet<Vector3>());
        var kilns = new KilnManager();
        kilns.Ensure(anchor, 0, Kiln.Id);
        var state = kilns.StateAt((int)anchor.X, (int)anchor.Y, (int)anchor.Z)!;
        state.Profile = KilnProfile.Create(KilnProfile.Default.FuelCap, KilnProfile.Default.MeltSpeed, canAlloy);
        Assert.True(kilns.TryPlaceCrucible(anchor, Crucible.Id, default));
        return (manager, kilns, new DropManager(), state);
    }

    // Charges one melt input after another until the vessel holds `ml` of its metal
    private static void Charge(KilnManager kilns, Vector3 anchor, KilnManager.KilnState state, MeltRecipe input, int ml)
    {
        while (state.Contents.PendingOf(input.MaterialId) < ml)
            Assert.True(kilns.TryInsertMelt(anchor, input.InputId));
    }

    // Burns until nothing is left to melt, checking every step that no milliliter appears or vanishes
    private static void BurnDown(KilnManager kilns, Vector3 anchor, ChunkManager manager, DropManager drops,
        KilnManager.KilnState state)
    {
        if (!state.Lit)
        {
            Assert.True(kilns.TryAddFuel(anchor, Coal));
            Assert.True(kilns.Ignite(anchor));
        }
        var total = state.Contents.TotalMl;
        var dt = state.Profile.MeltSeconds / 4f;
        for (var seconds = 0f; KilnManager.MeltingMaterial(state) != 0 || seconds == 0f; seconds += dt)
        {
            Assert.True(seconds < Coal.FuelSeconds, "the charge never finished melting");
            kilns.Update(dt, manager, drops);
            Assert.Equal(total, state.Contents.TotalMl);
        }
    }

    [Fact]
    public void APlainKilnRefusesASecondMetalAndKeepsWhatItHas()
    {
        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _, state) = KilnWithCrucible(anchor, canAlloy: false);
        Assert.True(kilns.TryInsertMelt(anchor, MeltOf("copper_ingot").InputId));
        var before = state.Contents;

        Assert.False(kilns.TryInsertMelt(anchor, MeltOf("tin_ingot").InputId));
        Assert.Equal(before, state.Contents);
    }

    [Fact]
    public void OneCrucibleHoldsAFullSetOfEveryAlloy()
    {
        Assert.NotEmpty(ContentRegistry.AlloyRecipes);
        Assert.All(ContentRegistry.AlloyRecipes, alloy => Assert.True(alloy.SetMl <= Crucible.CrucibleCapacityMl,
            $"{alloy.Name} needs {alloy.SetMl} ml, more than a crucible holds"));
    }

    [Fact]
    public void AnAlloyKilnMakesTheSameBronzeWhicheverMetalGoesInFirst()
    {
        var copper = MeltOf("copper_ingot");
        var tin = MeltOf("tin_ingot");
        var copperMl = Bronze.Parts.Single(p => p.MaterialId == copper.MaterialId).Ml;
        var tinMl = Bronze.Parts.Single(p => p.MaterialId == tin.MaterialId).Ml;

        var results = new List<CrucibleComponent>();
        foreach (var copperFirst in new[] { true, false })
        {
            var anchor = new Vector3(2, 2, 2);
            var (manager, kilns, drops, state) = KilnWithCrucible(anchor, canAlloy: true);
            if (copperFirst)
            {
                Charge(kilns, anchor, state, copper, copperMl);
                Charge(kilns, anchor, state, tin, tinMl);
            }
            else
            {
                Charge(kilns, anchor, state, tin, tinMl);
                Charge(kilns, anchor, state, copper, copperMl);
            }
            var charged = state.Contents.TotalMl;
            BurnDown(kilns, anchor, manager, drops, state);

            // The whole set is molten bronze, exactly as much as went in
            Assert.Equal(charged, state.Contents.MoltenOf(Bronze.ResultId));
            Assert.Equal(0, state.Contents.PendingMl);
            results.Add(state.Contents with { PendingInputId = 0 });
        }
        Assert.Equal(results[0], results[1]);
    }

    [Fact]
    public void AnUnfinishedSetWaitsInTheFireUntilItIsFull()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops, state) = KilnWithCrucible(anchor, canAlloy: true);
        var copper = MeltOf("copper_ingot");
        var tin = MeltOf("crushed_tin");
        var tinMl = Bronze.Parts.Single(p => p.MaterialId == tin.MaterialId).Ml;
        Charge(kilns, anchor, state, copper, Bronze.Parts.Single(p => p.MaterialId == copper.MaterialId).Ml);
        Charge(kilns, anchor, state, tin, tinMl - tin.AmountMl);
        var waiting = state.Contents;

        Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.True(kilns.Ignite(anchor));
        kilns.Update(10f * state.Profile.MeltSeconds, manager, drops);
        Assert.Equal(waiting, state.Contents);
        Assert.Equal(0f, KilnManager.MeltProgress01(state));

        // The last unit completes the set, which then melts into the alloy
        Assert.True(kilns.TryInsertMelt(anchor, tin.InputId));
        var total = state.Contents.TotalMl;
        BurnDown(kilns, anchor, manager, drops, state);
        Assert.Equal(total, state.Contents.MoltenOf(Bronze.ResultId));
    }

    [Fact]
    public void AnAlloyKilnRefusesAMetalNoAlloyOfTheVesselUses()
    {
        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _, state) = KilnWithCrucible(anchor, canAlloy: true);
        Assert.True(kilns.TryInsertMelt(anchor, MeltOf("copper_ingot").InputId));
        var before = state.Contents;

        var glass = ContentRegistry.MeltRecipeForInput(ContentRegistry.RequireBlock("sand").Id)!;
        Assert.False(Bronze.Involves(glass.MaterialId));
        Assert.False(kilns.TryInsertMelt(anchor, glass.InputId));
        Assert.Equal(before, state.Contents);
    }
}
