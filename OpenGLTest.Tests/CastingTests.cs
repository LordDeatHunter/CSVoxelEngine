using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Casting: pouring a crucible of molten metal into a fired mold, letting it set and taking the
/// result out. Every number here comes out of the recipe, out of
/// <see cref="CastingManager.PourMlPerSecond"/> or out of the baked models — the rules under test
/// are relations (mass is conserved, the pour only pauses and never loses what it poured, the
/// cooling curve is monotone and the dissolve is complementary), never the particular millilitres
/// or seconds the JSON happens to ship.
/// </summary>
public sealed class CastingTests
{
    public CastingTests() => TestContent.EnsureLoaded();

    private static readonly string[] FiredMolds =
    [
        "fired_mold_pickaxe", "fired_mold_axe", "fired_mold_ingot",
    ];

    public static IEnumerable<object[]> AllFiredMolds =>
        FiredMolds.Select(name => new object[] { name });

    private static byte Copper => ContentRegistry.MoltenMaterialByName("copper")!.Id;

    private static Item Crucible => ContentRegistry.RequireItem("crucible");

    private static readonly Vector3 Cell = Vector3.Zero;

    private static readonly Vector3 Standing = new(0.5f, 1f, 0.5f);

    private const float Reach = 10f;

    /// <summary>One chunk holding a single mold block at the origin, so Update sees a live cell.</summary>
    private static ChunkManager MoldWorld(byte moldId)
    {
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        chunk.SetBlockRaw(0, 0, 0, moldId, 0);
        return manager;
    }

    private static void SetCell(ChunkManager manager, byte id) =>
        manager.Chunks[Vector3.Zero]!.SetBlockRaw(0, 0, 0, id, 0);

    private static (CastingManager Casting, ChunkManager Chunks, DropManager Drops, Inventory Inventory,
        ItemStack Stack, CastRecipe Recipe, byte MoldId) Setup(int moltenMl = -1)
    {
        var moldId = ContentRegistry.RequireBlock("fired_mold_pickaxe").Id;
        var recipe = ContentRegistry.CastRecipeFor(moldId, Copper)!;
        Assert.NotNull(recipe);

        var inventory = new Inventory();
        var stack = CrucibleInventory.Add(inventory, Crucible.Id,
            new CrucibleComponent(Copper, moltenMl < 0 ? recipe.AmountMl * 2 : moltenMl, 0));
        Assert.NotNull(stack);

        return (new CastingManager(), MoldWorld(moldId), new DropManager(), inventory, stack!, recipe, moldId);
    }

    /// <summary>
    /// Contract between <see cref="CastingRenderer"/> and the models
    /// <c>tools/generate-mold-models.ps1</c> bakes: the liquid height lerps between the pocket
    /// floor and the slab top, so those two levels have to be the ones the generator actually
    /// carves, and the pocket has to stand inside the rim the renderer scales the sheet to.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFiredMolds))]
    public void PocketConstantsMatchTheBakedFiredMolds(string name)
    {
        var quads = ContentRegistry.RequireBlock(name).BakedModel!.Quads
            .Where(q => q.Face == 4)
            .ToArray();
        Assert.NotEmpty(quads);

        var levels = quads
            .Select(q => MathF.Round(q.Positions.Max(p => p.Y), 4))
            .Distinct()
            .OrderBy(y => y)
            .ToArray();
        Assert.Equal(
            new[] { MathF.Round(CastingRenderer.PocketFloorY, 4), MathF.Round(CastingRenderer.PocketTopY, 4) },
            levels);

        foreach (var quad in quads.Where(q =>
                     MathF.Abs(q.Positions.Max(p => p.Y) - CastingRenderer.PocketFloorY) < 1e-4f))
        {
            Assert.All(quad.Positions, p =>
            {
                Assert.InRange(p.X, CastingRenderer.PocketInset, 1f - CastingRenderer.PocketInset);
                Assert.InRange(p.Z, CastingRenderer.PocketInset, 1f - CastingRenderer.PocketInset);
            });
        }

        // The sheet never z-fights the floor it rests on nor pokes out through the slab top
        Assert.True(CastingRenderer.LiquidHeight(0f) > CastingRenderer.PocketFloorY);
        Assert.True(CastingRenderer.LiquidHeight(1f) < CastingRenderer.PocketTopY);
    }

    [Fact]
    public void PourDrainsTheSelectedCrucibleAtTheDeclaredRateAndConservesMass()
    {
        var (casting, chunks, drops, inventory, stack, recipe, moldId) = Setup();
        var startMolten = stack.Get<CrucibleComponent>().MoltenMl;

        Assert.True(casting.TryStartPour(Cell, moldId, stack));

        // Half the mold's worth of pour time moves exactly that much metal
        var half = recipe.AmountMl / CastingManager.PourMlPerSecond / 2f;
        casting.Update(half, chunks, drops, inventory, stack.InstanceId, Standing, Reach);

        var state = casting.StateAt(0, 0, 0)!;
        Assert.NotNull(state);
        var poured = (int)(CastingManager.PourMlPerSecond * half);
        Assert.Equal(poured, state.FilledMl);
        Assert.Equal(startMolten - poured, stack.Get<CrucibleComponent>().MoltenMl);

        // Plenty of time to top it up: the mold takes exactly its amount and no more
        casting.Update(half + 1f, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        Assert.Equal(recipe.AmountMl, state.FilledMl);
        Assert.Equal(startMolten - recipe.AmountMl, stack.Get<CrucibleComponent>().MoltenMl);
        Assert.True(state.Cooling);
        Assert.False(state.Done);
        Assert.False(state.Pouring);
        // The frame that completes the fill does not also cool
        Assert.Equal(recipe.CoolSeconds, state.CoolLeft, precision: 4);

        casting.Update(recipe.CoolSeconds + 0.01f, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        Assert.True(state.Done);

        Assert.Equal(Enumerable.Repeat(recipe.ResultId, recipe.ResultCount), casting.Collect(Cell));
        Assert.Null(casting.StateAt(0, 0, 0));
        Assert.Empty(casting.Collect(Cell));
    }

    [Fact]
    public void PourPausesWhenDeselectedOutOfReachOrDryAndResumesOnClick()
    {
        var (casting, chunks, drops, inventory, stack, recipe, moldId) = Setup();
        var pourTime = recipe.AmountMl / CastingManager.PourMlPerSecond + 1f;

        // Another hotbar slot: the bound crucible is no longer the selected instance
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, 0, Standing, Reach);
        var state = casting.StateAt(0, 0, 0)!;
        Assert.Equal(0, state.FilledMl);
        Assert.False(state.Pouring);

        // Walking off: the mold cell is further away than the interaction reach
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId,
            new Vector3(0.5f, 0.5f + Reach + 1f, 0.5f), Reach);
        Assert.Equal(0, state.FilledMl);
        Assert.False(state.Pouring);

        // Running dry mid-pour keeps what went in and empties the vessel of its metal
        var halfCharge = recipe.AmountMl / 2;
        stack.Set(new CrucibleComponent(Copper, halfCharge, 0));
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        Assert.Equal(halfCharge, state.FilledMl);
        var emptied = stack.Get<CrucibleComponent>();
        Assert.Equal(0, emptied.MoltenMl);
        Assert.Equal(0, emptied.MaterialId);
        Assert.False(state.Cooling);
        Assert.False(state.Pouring);

        // A refilled vessel picks the same cast back up where it stopped
        stack.Set(new CrucibleComponent(Copper, recipe.AmountMl, 0));
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        Assert.Equal(recipe.AmountMl, state.FilledMl);
        Assert.True(state.Cooling);
    }

    [Fact]
    public void WrongMaterialRawMoldAndFinishedCastAreRefused()
    {
        var (casting, chunks, drops, inventory, stack, recipe, moldId) = Setup();
        var pourTime = recipe.AmountMl / CastingManager.PourMlPerSecond + 1f;

        // A vessel with no material in it has nothing to pour
        stack.Set(new CrucibleComponent(0, recipe.AmountMl, 0));
        Assert.False(casting.TryStartPour(Cell, moldId, stack));
        Assert.Null(casting.StateAt(0, 0, 0));

        // An unfired mold casts nothing
        stack.Set(new CrucibleComponent(Copper, recipe.AmountMl * 2, 0));
        var raw = ContentRegistry.RequireBlock("mold_pickaxe").Id;
        Assert.False(casting.TryStartPour(Cell, raw, stack));
        Assert.Null(casting.StateAt(0, 0, 0));

        // A half-filled mold refuses a metal it has no recipe for, and keeps what it holds
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(recipe.AmountMl / CastingManager.PourMlPerSecond / 2f,
            chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        var state = casting.StateAt(0, 0, 0)!;
        var partial = state.FilledMl;
        Assert.True(partial > 0);

        const byte foreign = 200;
        Assert.Null(ContentRegistry.MoltenMaterialById(foreign));
        stack.Set(new CrucibleComponent(foreign, recipe.AmountMl, 0));
        Assert.False(casting.TryStartPour(Cell, moldId, stack));
        Assert.Equal(partial, casting.StateAt(0, 0, 0)!.FilledMl);

        // A set cast is not a vessel for more metal until it has been taken out
        stack.Set(new CrucibleComponent(Copper, recipe.AmountMl * 2, 0));
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        casting.Update(recipe.CoolSeconds + 0.01f, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        Assert.True(state.Done);
        Assert.False(casting.TryStartPour(Cell, moldId, stack));

        Assert.NotEmpty(casting.Collect(Cell));
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
    }

    [Fact]
    public void CoolingProgressIsMonotoneAndEndsAtOne()
    {
        var (casting, chunks, drops, inventory, stack, recipe, moldId) = Setup();
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(recipe.AmountMl / CastingManager.PourMlPerSecond + 1f,
            chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        var state = casting.StateAt(0, 0, 0)!;
        Assert.True(state.Cooling);
        Assert.Equal(0f, state.Cool01);

        const int steps = 10;
        var step = recipe.CoolSeconds / steps;
        var samples = new List<float> { state.Cool01 };
        for (var i = 0; i < steps + 1; i++)
        {
            casting.Update(step, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
            samples.Add(state.Cool01);
        }

        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i] >= samples[i - 1]);
            Assert.True(samples[i] - samples[i - 1] <= 1f / steps + 1e-3f);
        }
        Assert.Equal(1f, samples[^1]);
        Assert.True(state.Done);

        // The dissolve is one smooth hand-over: the two alphas always sum to a full image
        foreach (var sample in samples)
            Assert.Equal(1f, CastingRenderer.LiquidAlpha(sample) + CastingRenderer.ResultAlpha(sample),
                precision: 5);
    }

    [Fact]
    public void RemovedMoldDropsAFinishedCastOnly()
    {
        var (casting, chunks, drops, inventory, stack, recipe, moldId) = Setup();
        var pourTime = recipe.AmountMl / CastingManager.PourMlPerSecond + 1f;

        // A set cast survives the mold being mined
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        casting.Update(recipe.CoolSeconds + 0.01f, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        casting.NotifyBlockRemoved(Cell, drops);
        Assert.Equal(recipe.ResultCount, drops.Drops.Count);
        Assert.All(drops.Drops, drop => Assert.Equal(recipe.ResultId, drop.BlockId));
        Assert.Null(casting.StateAt(0, 0, 0));

        // Metal that had not set yet goes with the mold
        var fresh = new DropManager();
        stack.Set(new CrucibleComponent(Copper, recipe.AmountMl * 2, 0));
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(recipe.AmountMl / CastingManager.PourMlPerSecond / 2f,
            chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        casting.NotifyBlockRemoved(Cell, fresh);
        Assert.Empty(fresh.Drops);
        Assert.Null(casting.StateAt(0, 0, 0));

        // A cell that stopped being the mold (a blast, a replacement) throws its cast out on the
        // next Update rather than holding it forever
        var blasted = new DropManager();
        Assert.True(casting.TryStartPour(Cell, moldId, stack));
        casting.Update(pourTime, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        casting.Update(recipe.CoolSeconds + 0.01f, chunks, drops, inventory, stack.InstanceId, Standing, Reach);
        SetCell(chunks, ContentRegistry.RequireBlock("stone").Id);
        casting.Update(0.1f, chunks, blasted, inventory, stack.InstanceId, Standing, Reach);
        Assert.Equal(recipe.ResultCount, blasted.Drops.Count);
        Assert.All(blasted.Drops, drop => Assert.Equal(recipe.ResultId, drop.BlockId));
        Assert.Null(casting.StateAt(0, 0, 0));
        Assert.False(casting.HasAny);
    }
}
