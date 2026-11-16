using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// The anvil's rules: it holds a stack of one forge metal, which grows one at a time up to the metal's largest recipe and
/// comes back one at a time or whole. A recipe the stack covers is picked first (which costs nothing), then hammered: a
/// hit needs a pick and the gap since the last one to have run down, and the hit that reaches the recipe's hits takes
/// its metal off the stack and hands out its result and byproduct. A gone block hands the stack back.
/// </summary>
public sealed class AnvilManagerTests
{
    public AnvilManagerTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Cell = new(4, 2, 4);
    private const float Gap = 2.5f;

    private static BlockType Anvil => ContentRegistry.RequireBlock("anvil");

    // The bloomery's output is what the iron forge recipes take
    private static ushort Metal => ContentRegistry.RequireBlock("bloomery").Bloomery!.BloomId;

    private static ForgeRecipe[] RecipesOf(ushort metal) =>
        ContentRegistry.ForgeRecipes.Where(r => r.MetalId == metal).ToArray();

    private static AnvilManager Ready()
    {
        var anvils = new AnvilManager();
        anvils.Ensure(Cell, Anvil.Id);
        return anvils;
    }

    private static AnvilManager Stacked(int count)
    {
        var anvils = Ready();
        for (var i = 0; i < count; i++)
            Assert.True(anvils.TryStack(Cell, Metal));
        return anvils;
    }

    // Lands hits until one finishes, clearing the gap between them; returns the finishing hit and how many it took
    private static (AnvilHit Hit, int Hits) HammerOut(AnvilManager anvils)
    {
        var state = anvils.StateAt(4, 2, 4)!;
        for (var hits = 1; ; hits++)
        {
            var hit = anvils.TryHit(Cell, Gap, 0f);
            Assert.NotEqual(AnvilHitKind.Refused, hit.Kind);
            if (hit.Kind == AnvilHitKind.Finished) return (hit, hits);
            state.CooldownLeft = 0f;
        }
    }

    [Fact]
    public void AHitWithNoRecipePickedDoesNothing()
    {
        Assert.Equal(AnvilHitKind.Refused, Ready().TryHit(Cell, Gap, 0f).Kind);
        // A stack alone is not enough either: the hammer forges only a picked recipe
        var anvils = Stacked(RecipesOf(Metal).Min(r => r.MetalCount));
        Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
        Assert.Equal(0, anvils.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AStackTakesOnlyAForgeMetal()
    {
        Assert.True(ContentRegistry.IsForgeMetal(Metal));
        var anvils = Ready();
        Assert.False(anvils.TryStack(Cell, ContentRegistry.RequireItem("coal").Id));
        Assert.True(anvils.TryStack(Cell, Metal));
        // Another forge metal never joins the stack
        var other = ContentRegistry.ForgeRecipes.First(r => r.MetalId != Metal).MetalId;
        Assert.False(anvils.TryStack(Cell, other));
    }

    [Fact]
    public void AStackGrowsOneAtATimeUpToTheLargestRecipeOfItsMetal()
    {
        var anvils = Ready();
        var stacked = 0;
        while (anvils.TryStack(Cell, Metal))
            stacked++;

        // Never more than any recipe could use, and enough for the costliest one
        Assert.Equal(RecipesOf(Metal).Max(r => r.MetalCount), stacked);
        Assert.Equal(stacked, anvils.StateAt(4, 2, 4)!.MetalCount);
        Assert.Equal(RecipesOf(Metal).Length, AnvilManager.Offered(Metal, stacked).Count);
    }

    [Fact]
    public void TakingBackGivesOnePieceOrTheWholeStack()
    {
        var anvils = Stacked(3);

        Assert.True(anvils.TryTake(Cell, all: false, out var item, out var count));
        Assert.Equal((Metal, 1), (item, count));
        Assert.Equal(2, anvils.StateAt(4, 2, 4)!.MetalCount);

        Assert.True(anvils.TryTake(Cell, all: true, out item, out count));
        Assert.Equal((Metal, 2), (item, count));
        Assert.True(anvils.StateAt(4, 2, 4)!.IsEmpty);
        Assert.False(anvils.TryTake(Cell, all: true, out _, out _));
    }

    [Fact]
    public void ARecipeIsOfferedOnlyForTheMetalOnTheAnvilAndOnlyWhenTheStackCoversIt()
    {
        var recipes = RecipesOf(Metal);
        Assert.NotEmpty(recipes);
        for (var count = 0; count <= recipes.Max(r => r.MetalCount); count++)
        {
            var offered = AnvilManager.Offered(Metal, count);
            Assert.All(offered, r => Assert.True(r.MetalId == Metal && r.MetalCount <= count));
            Assert.Equal(recipes.Count(r => r.MetalCount <= count), offered.Count);
        }

        // An item no recipe takes is offered nothing, however many lie there
        Assert.Empty(AnvilManager.Offered(ContentRegistry.RequireItem("coal").Id, 64));
    }

    [Fact]
    public void APickNeedsTheRecipeOfferedAndCostsNothing()
    {
        var recipes = RecipesOf(Metal);
        var cheapest = recipes.MinBy(r => r.MetalCount)!;
        var costliest = recipes.MaxBy(r => r.MetalCount)!;
        Assert.True(costliest.MetalCount > cheapest.MetalCount, "the iron recipes should differ in cost");

        var anvils = Stacked(cheapest.MetalCount);
        Assert.False(anvils.TrySelect(Cell, costliest.ResultId));
        Assert.False(anvils.TrySelect(Cell, Metal));
        Assert.True(anvils.TrySelect(Cell, cheapest.ResultId));

        var state = anvils.StateAt(4, 2, 4)!;
        Assert.Same(cheapest, state.Selected);
        Assert.Equal(cheapest.MetalCount, state.MetalCount);
    }

    [Fact]
    public void HitsAreGatedByTheHammersGapAndTheRecipesHitsFinishIt()
    {
        // One with room for a spare unit on the stack, so the test sees exactly its own metal leave
        var recipe = RecipesOf(Metal).Where(r => r.MetalCount < AnvilManager.StackCap(Metal)).MaxBy(r => r.Hits)!;
        var anvils = Stacked(recipe.MetalCount + 1);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        var state = anvils.StateAt(4, 2, 4)!;

        for (var hit = 1; hit < recipe.Hits; hit++)
        {
            Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);
            // Right away again: refused, nothing counted
            Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
            Assert.Equal(hit, state.Hits);
            // Nothing leaves the stack before the last hit
            Assert.Equal(recipe.MetalCount + 1, state.MetalCount);
            state.CooldownLeft = 0f;
        }

        var done = anvils.TryHit(Cell, Gap, 0f);
        Assert.Equal(AnvilHitKind.Finished, done.Kind);
        Assert.Equal((recipe.ResultId, recipe.ResultCount), (done.ResultId, done.ResultCount));
        Assert.Equal((recipe.ByproductId, recipe.ByproductCount), (done.ByproductId, done.ByproductCount));
        // Exactly the recipe's metal is spent, and the pick is used up
        Assert.Equal(1, state.MetalCount);
        Assert.Null(state.Selected);
        Assert.Equal(0, state.Hits);
    }

    [Fact]
    public void EveryForgeRecipeFinishesOnExactlyItsOwnHitCount()
    {
        foreach (var recipe in ContentRegistry.ForgeRecipes)
        {
            var anvils = Ready();
            for (var i = 0; i < recipe.MetalCount; i++)
                Assert.True(anvils.TryStack(Cell, recipe.MetalId));
            Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
            Assert.Equal(recipe.Hits, HammerOut(anvils).Hits);
            Assert.True(anvils.StateAt(4, 2, 4)!.IsEmpty);
        }
    }

    [Fact]
    public void RePickingTheSameRecipeKeepsItsHitsAndAnotherStartsOver()
    {
        var recipes = AnvilManager.Offered(Metal, RecipesOf(Metal).Max(r => r.MetalCount));
        var first = recipes.First(r => r.Hits > 1);
        var other = recipes.First(r => r.ResultId != first.ResultId);
        var anvils = Stacked(RecipesOf(Metal).Max(r => r.MetalCount));
        Assert.True(anvils.TrySelect(Cell, first.ResultId));
        anvils.TryHit(Cell, Gap, 0f);

        Assert.True(anvils.TrySelect(Cell, first.ResultId));
        Assert.Equal(1, anvils.StateAt(4, 2, 4)!.Hits);
        Assert.True(anvils.TrySelect(Cell, other.ResultId));
        Assert.Equal(0, anvils.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void TakingTheStackBelowThePicksCostForgetsThePick()
    {
        var cheapest = RecipesOf(Metal).MinBy(r => r.MetalCount)!;
        var anvils = Stacked(cheapest.MetalCount);
        Assert.True(anvils.TrySelect(Cell, cheapest.ResultId));
        anvils.TryHit(Cell, Gap, 0f);

        Assert.True(anvils.TryTake(Cell, all: false, out _, out _));
        var state = anvils.StateAt(4, 2, 4)!;
        Assert.Null(state.Selected);
        Assert.Equal(0, state.Hits);
        state.CooldownLeft = 0f;
        Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
    }

    [Fact]
    public void TheGapRunsDownWithTimeAndSlackLetsAHitThroughEarly()
    {
        var recipe = RecipesOf(Metal).First(r => r.Hits > 2);
        var anvils = Stacked(recipe.MetalCount);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);

        // Half the gap passes, with the anvil block standing in its cell
        var (manager, _) = MeshTestWorld.WalledArea();
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)Cell.X, (int)Cell.Y, (int)Cell.Z);
        manager.ChunkAt((int)Cell.X, (int)Cell.Y, (int)Cell.Z)!.SetBlockRaw(lx, ly, lz, Anvil.Id, 0);
        anvils.Update(Gap * 0.5f, manager, new DropManager());

        Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
        // Within the slack of the end of the gap a late packet still counts
        Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, Gap * 0.5f + 0.01f).Kind);
    }

    [Fact]
    public void AnEmptyAnvilIsNotSaved()
    {
        Assert.Empty(Ready().Capture());
    }

    [Fact]
    public void AStackAndItsPickSurviveASaveAndTheWireAndTheStackDropsWhenItsBlockIsGone()
    {
        var recipe = RecipesOf(Metal).First(r => r.Hits > 1);
        var anvils = Stacked(recipe.MetalCount);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        anvils.TryHit(Cell, Gap, 0f);

        var saved = new AnvilManager();
        Assert.Equal(0, saved.Restore(anvils.Capture()));
        var restored = saved.StateAt(4, 2, 4)!;
        Assert.Equal((Metal, recipe.MetalCount), (restored.MetalId, restored.MetalCount));
        Assert.Equal((recipe.ResultId, 1), (restored.SelectedResultId, restored.Hits));

        using var writer = new PacketWriter(NetMessage.StationState);
        anvils.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new AnvilManager();
        mirror.ReadNet(reader);
        var mirrored = mirror.StateAt(4, 2, 4)!;
        Assert.Equal((Metal, recipe.MetalCount), (mirrored.MetalId, mirrored.MetalCount));
        Assert.Equal((recipe.ResultId, 1), (mirrored.SelectedResultId, mirrored.Hits));

        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        anvils.Update(0.1f, manager, drops);
        Assert.Equal(recipe.MetalCount, drops.Drops.Count(d => d.BlockId == Metal));
    }
}
