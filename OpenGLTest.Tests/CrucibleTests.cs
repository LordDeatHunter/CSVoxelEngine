using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The crucible: a vessel whose molten contents ride on its own item instance, the kiln slot it
/// stands in and the melting that slot drives. Every number here comes out of the content or out
/// of <see cref="KilnManager"/>'s own constants — the rules under test are relations (mass is
/// conserved, the pool only ever rises, a charge is refused when it would not fit), never the
/// particular millilitres the JSON happens to ship.
/// </summary>
public sealed class CrucibleTests
{
    public CrucibleTests() => TestContent.EnsureLoaded();

    private static BlockType Kiln => ContentRegistry.RequireBlock("mud_brick_kiln");
    private static Item Crucible => ContentRegistry.RequireItem("crucible");
    private static Item Coal => ContentRegistry.RequireItem("coal");
    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;

    private static (ChunkManager Manager, KilnManager Kilns, DropManager Drops) ConvertedKiln(
        Vector3 anchor, int facing = 0)
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        Multiblock.Convert(manager, anchor, facing, Kiln, new HashSet<Vector3>());
        var kilns = new KilnManager();
        kilns.Ensure(anchor, facing, Kiln.Id);
        return (manager, kilns, new DropManager());
    }

    private static MeltRecipe MeltOf(string itemName) =>
        ContentRegistry.MeltRecipeForInput(ContentRegistry.RequireItem(itemName).Id)!;

    /// <summary>
    /// Contract between <see cref="KilnRenderer"/>'s bowl constants and the authored model at
    /// <c>assets/models/block/crucible.json</c>: the renderer lerps a molten surface between
    /// those levels and scales the quad to the bowl's inside, so the geometry has to actually
    /// stand where the constants say. Asserts containment and the two levels, never a quad set.
    /// </summary>
    [Fact]
    public void CrucibleIsAToolCategoryItemWithABakedBowl()
    {
        var crucible = Crucible;
        Assert.IsType<CrucibleItem>(crucible);
        Assert.True(crucible.CrucibleCapacityMl > 0);
        Assert.Equal(InventoryCategory.Tools, Inventory.CategoryOf(crucible.Id));

        var recipe = ContentRegistry.RecipeByName("crucible");
        Assert.NotNull(recipe);
        Assert.Equal(crucible.Id, recipe!.ResultId);

        var baked = crucible.BakedModel;
        Assert.NotNull(baked);
        var quads = baked!.Quads;
        Assert.NotEmpty(quads);

        var positions = quads.SelectMany(q => q.Positions).ToArray();
        Assert.All(positions, p =>
        {
            Assert.InRange(p.X, 0f, 1f);
            Assert.InRange(p.Y, 0f, 1f);
            Assert.InRange(p.Z, 0f, 1f);
        });

        // The rim is the top of the bowl, and the footprint is centred and OuterHalf wide
        Assert.Equal(KilnRenderer.CrucibleTopY, positions.Max(p => p.Y), precision: 4);
        Assert.Equal(0.5f - KilnRenderer.CrucibleOuterHalf, positions.Min(p => p.X), precision: 4);
        Assert.Equal(0.5f - KilnRenderer.CrucibleOuterHalf, positions.Min(p => p.Z), precision: 4);
        Assert.Equal(0.5f + KilnRenderer.CrucibleOuterHalf, positions.Max(p => p.X), precision: 4);
        Assert.Equal(0.5f + KilnRenderer.CrucibleOuterHalf, positions.Max(p => p.Z), precision: 4);

        // An entity mesh has no cell neighbours, so nothing may be culled away
        Assert.All(quads, q => Assert.Equal(-1, q.CullFace));

        // The floor the metal pools on covers at least the whole inside of the walls...
        var floors = quads
            .Where(q => q.Face == 4
                && q.Positions.All(p => MathF.Abs(p.Y - KilnRenderer.CrucibleFloorY) < 1e-4f))
            .ToArray();
        Assert.NotEmpty(floors);
        Assert.Contains(floors, q =>
            q.Positions.Min(p => p.X) <= KilnRenderer.CrucibleInnerMin + 1e-4f
            && q.Positions.Min(p => p.Z) <= KilnRenderer.CrucibleInnerMin + 1e-4f
            && q.Positions.Max(p => p.X) >= KilnRenderer.CrucibleInnerMax - 1e-4f
            && q.Positions.Max(p => p.Z) >= KilnRenderer.CrucibleInnerMax - 1e-4f);

        // ...and never spills past the bowl's own footprint
        Assert.All(floors, q => Assert.All(q.Positions, p =>
        {
            Assert.InRange(p.X, 0.5f - KilnRenderer.CrucibleOuterHalf - 1e-4f,
                0.5f + KilnRenderer.CrucibleOuterHalf + 1e-4f);
            Assert.InRange(p.Z, 0.5f - KilnRenderer.CrucibleOuterHalf - 1e-4f,
                0.5f + KilnRenderer.CrucibleOuterHalf + 1e-4f);
        }));
    }

    [Fact]
    public void AddingACrucibleToTheInventoryKeepsEachInstancesContents()
    {
        var inventory = new Inventory();
        var id = Crucible.Id;
        var ore = MeltOf("malachite_ore");

        var first = CrucibleInventory.Add(inventory, id, new CrucibleComponent(Copper, ore.AmountMl, ore.AmountMl));
        var second = CrucibleInventory.Add(inventory, id, new CrucibleComponent(Copper, 0, ore.AmountMl));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.InstanceId, second!.InstanceId);

        Assert.Equal(new CrucibleComponent(Copper, ore.AmountMl, ore.AmountMl),
            inventory.GetToolStack(first.InstanceId)!.Get<CrucibleComponent>());
        Assert.Equal(new CrucibleComponent(Copper, 0, ore.AmountMl),
            inventory.GetToolStack(second.InstanceId)!.Get<CrucibleComponent>());

        // A full tools pool leaves the vessel where it was
        var full = new Inventory();
        var firestarter = ContentRegistry.RequireItem("firestarter").Id;
        while (full.ToolEntries().Count < Inventory.ToolCapacity)
            Assert.True(full.Add(firestarter));
        var before = full.ToolEntries().Count;
        Assert.Null(CrucibleInventory.Add(full, id, new CrucibleComponent(Copper, ore.AmountMl, 0)));
        Assert.Equal(before, full.ToolEntries().Count);
    }

    [Fact]
    public void PlaceAndTakeRoundTripKeepsContentsAndGatesInsertsByRoomAndMaterial()
    {
        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, _) = ConvertedKiln(anchor);

        var capacity = Crucible.CrucibleCapacityMl;
        var ingot = MeltOf("copper_ingot");
        var ore = MeltOf("malachite_ore");
        var branch = ContentRegistry.RequireItem("branch").Id;

        // No crucible, nothing to charge
        Assert.False(kilns.TryInsertMelt(anchor, ingot.InputId));

        // One millilitre short of room for another ingot
        var start = new CrucibleComponent(Copper, capacity - ingot.AmountMl + 1, 0);
        Assert.True(kilns.TryPlaceCrucible(anchor, Crucible.Id, start));
        Assert.False(kilns.TryPlaceCrucible(anchor, Crucible.Id, default));

        Assert.False(kilns.TryInsertMelt(anchor, ingot.InputId));
        // The smaller ore charge fits exactly when it is small enough to
        Assert.Equal(ore.AmountMl <= ingot.AmountMl - 1, kilns.TryInsertMelt(anchor, ore.InputId));
        Assert.False(kilns.TryInsertMelt(anchor, branch));

        var expectedPending = kilns.StateAt(2, 2, 2)!.Contents.PendingMl;
        Assert.True(kilns.TryTakeCrucible(anchor, out var takenId, out var taken));
        Assert.Equal(Crucible.Id, takenId);
        Assert.Equal(Copper, taken.MaterialId);
        Assert.Equal(start.MoltenMl, taken.MoltenMl);
        Assert.Equal(expectedPending, taken.PendingMl);

        Assert.False(kilns.TryTakeCrucible(anchor, out _, out _));
        Assert.False(kilns.TryInsertMelt(anchor, branch));
    }

    [Fact]
    public void CanAcceptRefusesAForeignMaterialAndOverflow()
    {
        const int unit = 100;
        const int capacity = 4 * unit;
        var a = new MeltRecipe("a", 1, 1, unit);
        var b = new MeltRecipe("b", 2, 2, unit);

        Assert.True(KilnManager.CanAccept(default, a, capacity));
        // A vessel already holding material 1 refuses material 2
        Assert.False(KilnManager.CanAccept(new CrucibleComponent(1, unit, 0), b, capacity));
        // Pending counts toward the capacity just like molten does
        Assert.False(KilnManager.CanAccept(new CrucibleComponent(1, capacity - unit, unit), a, capacity));
        Assert.True(KilnManager.CanAccept(new CrucibleComponent(1, capacity - unit, 0), a, capacity));
        // An empty vessel takes anything, whatever stale material id it still carries
        Assert.True(KilnManager.CanAccept(new CrucibleComponent(1, 0, 0), b, capacity));
    }

    [Fact]
    public void MeltingNeedsFireAndPacesOneChunkPerMeltSeconds()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor);
        var ore = MeltOf("malachite_ore");

        Assert.True(kilns.TryPlaceCrucible(anchor, Crucible.Id, default));
        var state = kilns.StateAt(2, 2, 2)!;
        while (state.Contents.PendingMl < 2 * KilnManager.MeltChunkMl)
            Assert.True(kilns.TryInsertMelt(anchor, ore.InputId));
        var charged = state.Contents.TotalMl;

        // Cold: nothing melts however long it stands
        kilns.Update(10f * KilnManager.MeltSeconds, manager, drops);
        Assert.Equal(0, state.Contents.MoltenMl);
        Assert.Equal(charged, state.Contents.PendingMl);
        Assert.Equal(0f, KilnManager.MeltProgress01(state));

        Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.True(kilns.Ignite(anchor));

        kilns.Update(KilnManager.MeltSeconds * 0.5f, manager, drops);
        Assert.Equal(0, state.Contents.MoltenMl);
        Assert.InRange(KilnManager.MeltProgress01(state), 0.45f, 0.55f);
        Assert.Equal(charged, state.Contents.TotalMl);

        kilns.Update(KilnManager.MeltSeconds * 0.5f + 0.01f, manager, drops);
        Assert.Equal(KilnManager.MeltChunkMl, state.Contents.MoltenMl);
        Assert.Equal(charged, state.Contents.TotalMl);

        kilns.Update(KilnManager.MeltSeconds, manager, drops);
        Assert.Equal(2 * KilnManager.MeltChunkMl, state.Contents.MoltenMl);
        Assert.Equal(charged, state.Contents.TotalMl);
    }

    [Fact]
    public void DisplayedMoltenMlRisesContinuouslyAcrossAMeltTickAndNeverDropsAtBurnOut()
    {
        var anchor = new Vector3(2, 2, 2);
        var (manager, kilns, drops) = ConvertedKiln(anchor);
        var ore = MeltOf("malachite_ore");

        Assert.True(kilns.TryPlaceCrucible(anchor, Crucible.Id, default));
        var state = kilns.StateAt(2, 2, 2)!;
        while (state.Contents.PendingMl < 2 * KilnManager.MeltChunkMl)
            Assert.True(kilns.TryInsertMelt(anchor, ore.InputId));
        Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.True(kilns.Ignite(anchor));

        // Stepping finely across a whole tick: the drawn pool only ever rises, and never by
        // more than the step is worth - i.e. the 100 ml bookkeeping is invisible
        var dt = KilnManager.MeltSeconds / 20f;
        var previous = KilnManager.DisplayMoltenMl(state);
        var perStep = KilnManager.MeltChunkMl * dt / KilnManager.MeltSeconds + 1e-3f;
        for (var i = 0; i < 25; i++)
        {
            kilns.Update(dt, manager, drops);
            var now = KilnManager.DisplayMoltenMl(state);
            Assert.True(now >= previous, $"step {i}: {now} < {previous}");
            Assert.True(now - previous <= perStep, $"step {i}: jumped {now - previous}");
            previous = now;
        }
        Assert.True(previous >= KilnManager.MeltChunkMl);

        // Burn-out mid-chunk must not take the drawn pool back down with the flame
        var (manager2, kilns2, drops2) = ConvertedKiln(anchor);
        Assert.True(kilns2.TryPlaceCrucible(anchor, Crucible.Id, default));
        var state2 = kilns2.StateAt(2, 2, 2)!;
        while (state2.Contents.PendingMl < 2 * KilnManager.MeltChunkMl)
            Assert.True(kilns2.TryInsertMelt(anchor, ore.InputId));
        Assert.True(kilns2.TryAddFuel(anchor, Coal));
        Assert.True(kilns2.Ignite(anchor));

        // Run the fire down to a sliver so it goes out in the middle of a chunk
        kilns2.Update(KilnManager.MeltSeconds * 0.25f, manager2, drops2);
        Assert.True(state2.Lit);
        state2.BurnLeft = KilnManager.MeltSeconds * 0.25f;
        var before = KilnManager.DisplayMoltenMl(state2);

        kilns2.Update(KilnManager.MeltSeconds * 0.5f, manager2, drops2);
        Assert.False(state2.Lit);
        Assert.True(KilnManager.DisplayMoltenMl(state2) >= before);
        Assert.True(KilnManager.MeltProgress01(state2) > 0f);
    }

    [Fact]
    public void BreakingTheKilnDropsTheCrucibleAndFuelButNotTheMetal()
    {
        var anchor = new Vector3(2, 2, 2);
        var (_, kilns, drops) = ConvertedKiln(anchor);
        var ore = MeltOf("malachite_ore");

        Assert.True(kilns.TryPlaceCrucible(anchor, Crucible.Id,
            new CrucibleComponent(Copper, ore.AmountMl, ore.AmountMl)));
        Assert.True(kilns.TryAddFuel(anchor, Coal));
        Assert.True(kilns.TryAddFuel(anchor, Coal));

        var mined = anchor + new Vector3(1, 0, 1);
        kilns.NotifyBlockRemoved(anchor, mined, drops);

        Assert.Equal(1, drops.Drops.Count(d => d.BlockId == Crucible.Id));
        Assert.Equal(2, drops.Drops.Count(d => d.BlockId == Coal.Id));
        // The metal inside is lost: nothing that melts into copper falls out
        Assert.DoesNotContain(drops.Drops, d =>
            ContentRegistry.MeltRecipeForInput(d.BlockId) is { } melt && melt.MaterialId == Copper);
        Assert.Equal(3, drops.Drops.Count);
        Assert.Null(kilns.StateAt(2, 2, 2));
    }
}
