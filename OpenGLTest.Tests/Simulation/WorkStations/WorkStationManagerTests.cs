using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.WorkStations;
using Xunit;

namespace VoxelEngine.Tests.Simulation.WorkStations;

/// <summary>
/// The work station's rules, on the anvil and the fleshing beam: a station holds a stack of one input its recipes take,
/// which grows one at a time up to the input's largest recipe and comes back one at a time or whole. A recipe the stack
/// covers is picked first (which costs nothing; a station offering only one needs no pick), then worked: a hit needs a
/// recipe and the gap since the last one to have run down, and the hit that reaches the recipe's hits takes its input
/// off the stack and hands out its result and byproduct, or keeps the result waiting for the item that collects it. A
/// gone block hands the stack back, and a waiting result as the input it was worked from.
/// </summary>
public sealed class WorkStationManagerTests
{
    public WorkStationManagerTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Cell = new(4, 2, 4);
    private const float Gap = 2.5f;

    private static BlockType Anvil => ContentRegistry.RequireBlock("anvil");

    // A recipe that keeps its result waiting for a collect item (the fleshing beam's scraped hide)
    private static WorkRecipe Waiting => ContentRegistry.WorkRecipes.First(r => r.CollectWithId != 0);

    private static BlockType StationOf(WorkRecipe recipe) =>
        BlockType.Types.Values.First(b => b.WorkStation?.Recipes == recipe.Station);

    // The bloomery's output is what the iron forge recipes take
    private static ushort Metal => ContentRegistry.RequireBlock("bloomery").Bloomery!.BloomId;

    private const string Forge = "forge";

    private static WorkRecipe[] RecipesOf(ushort metal) =>
        ContentRegistry.WorkRecipes.Where(r => r.InputId == metal && r.Station == Forge).ToArray();

    private static WorkStationManager Ready() => Ready(Anvil);

    private static WorkStationManager Ready(BlockType station)
    {
        var stations = new WorkStationManager();
        stations.Ensure(Cell, station.Id);
        return stations;
    }

    // The recipe's own station with exactly its input stacked
    private static WorkStationManager StackedFor(WorkRecipe recipe)
    {
        var stations = Ready(StationOf(recipe));
        for (var i = 0; i < recipe.InputCount; i++)
            Assert.True(stations.TryStack(Cell, recipe.InputId));
        return stations;
    }

    private static WorkStationManager Stacked(int count)
    {
        var anvils = Ready();
        for (var i = 0; i < count; i++)
            Assert.True(anvils.TryStack(Cell, Metal));
        return anvils;
    }

    // Lands hits until one finishes, clearing the gap between them; returns the finishing hit and how many it took
    private static (WorkHit Hit, int Hits) HammerOut(WorkStationManager anvils)
    {
        var state = anvils.StateAt(4, 2, 4)!;
        for (var hits = 1; ; hits++)
        {
            var hit = anvils.TryHit(Cell, Gap, 0f);
            Assert.NotEqual(WorkHitKind.Refused, hit.Kind);
            if (hit.Kind == WorkHitKind.Finished) return (hit, hits);
            state.CooldownLeft = 0f;
        }
    }

    [Fact]
    public void AHitWithNoRecipePickedDoesNothingWhileTheStackOffersSeveral()
    {
        Assert.Equal(WorkHitKind.Refused, Ready().TryHit(Cell, Gap, 0f).Kind);
        // A stack alone is not enough either while it could make more than one thing: the hammer forges only a pick
        var count = Enumerable.Range(1, WorkStationManager.StackCap(Forge, Metal))
            .First(n => WorkStationManager.Offered(Forge, Metal, n).Count > 1);
        var anvils = Stacked(count);
        Assert.Equal(WorkHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
        Assert.Equal(0, anvils.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AStackOfferingJustOneRecipeIsWorkedWithoutAPick()
    {
        var recipe = ContentRegistry.WorkRecipes.First(r => r.Hits > 1
            && WorkStationManager.Offered(r.Station, r.InputId, WorkStationManager.StackCap(r.Station, r.InputId)).Count == 1);
        var stations = StackedFor(recipe);
        var state = stations.StateAt(4, 2, 4)!;
        Assert.Same(recipe, state.Working);

        Assert.Equal(WorkHitKind.Struck, stations.TryHit(Cell, Gap, 0f).Kind);
        // The first hit picks it, so its hits count from there like any pick's
        Assert.Equal((recipe.ResultId, 1), (state.SelectedResultId, state.Hits));
    }

    [Fact]
    public void AStationWithItsOwnStackCapHoldsSeveralInputsAndWorksThemOneAfterAnother()
    {
        var beam = BlockType.Types.Values.First(b => b.WorkStation is { Stack: > 0 });
        var recipe = ContentRegistry.WorkRecipes.First(r => r.Station == beam.WorkStation!.Recipes);
        var stations = Ready(beam);
        var stacked = 0;
        while (stations.TryStack(Cell, recipe.InputId))
            stacked++;
        Assert.Equal(Math.Max(beam.WorkStation!.Stack, WorkStationManager.StackCap(recipe.Station, recipe.InputId)), stacked);

        // Each work takes its own input and the next is ready to work at once, no pick between them
        HammerOut(stations);
        var state = stations.StateAt(4, 2, 4)!;
        Assert.Equal(stacked - recipe.InputCount, state.InputCount);
        Assert.Same(recipe, state.Working);
        state.CooldownLeft = 0f;
        HammerOut(stations);
        Assert.Equal(stacked - 2 * recipe.InputCount, state.InputCount);
    }

    [Fact]
    public void AWaitingResultStaysOnTheStationUntilItsCollectItemTakesItOneAtATime()
    {
        var recipe = Waiting;
        var stations = StackedFor(recipe);
        var (done, _) = HammerOut(stations);
        // Nothing drops: the result waits on the station
        Assert.Equal((recipe.ResultId, 0), (done.ResultId, done.ResultCount));
        var state = stations.StateAt(4, 2, 4)!;
        Assert.Equal((recipe.ResultId, recipe.ResultCount), (state.FinishedId, state.FinishedCount));
        Assert.False(state.IsEmpty);

        // An empty hand or any other item takes nothing; taking back takes only the stack, never the waiting result
        Assert.False(stations.TryCollect(Cell, 0, out _));
        Assert.False(stations.TryCollect(Cell, ContentRegistry.RequireItem("coal").Id, out _));
        Assert.False(stations.TryTake(Cell, all: true, out _, out _));

        for (var left = recipe.ResultCount; left > 0; left--)
        {
            Assert.True(stations.TryCollect(Cell, recipe.CollectWithId, out var result));
            Assert.Equal(recipe.ResultId, result);
            Assert.Equal(left - 1, state.FinishedCount);
        }
        Assert.False(stations.TryCollect(Cell, recipe.CollectWithId, out _));
        Assert.True(state.IsEmpty);
    }

    [Fact]
    public void AWaitingResultDropsBackAsItsInputWhenTheStationIsGone()
    {
        var recipe = Waiting;
        var stations = StackedFor(recipe);
        HammerOut(stations);

        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        stations.Update(0.1f, manager, drops);
        Assert.Equal(0, drops.Drops.Count(d => d.BlockId == recipe.ResultId));
        Assert.Equal(recipe.InputCount, drops.Drops.Count(d => d.BlockId == recipe.InputId));
    }

    [Fact]
    public void AWaitingResultSurvivesASaveAndTheWire()
    {
        var recipe = Waiting;
        var stations = StackedFor(recipe);
        HammerOut(stations);

        var saved = new WorkStationManager();
        Assert.Equal(0, saved.Restore(stations.Capture()));
        var restored = saved.StateAt(4, 2, 4)!;
        Assert.Equal((recipe.ResultId, recipe.ResultCount), (restored.FinishedId, restored.FinishedCount));

        using var writer = new PacketWriter(NetMessage.StationState);
        stations.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new WorkStationManager();
        mirror.ReadNet(reader);
        var mirrored = mirror.StateAt(4, 2, 4)!;
        Assert.Equal((recipe.ResultId, recipe.ResultCount), (mirrored.FinishedId, mirrored.FinishedCount));
    }

    [Fact]
    public void AStationTakesOnlyTheInputsOfItsOwnRecipeFolder()
    {
        // The anvil's metal never stacks on a station of another folder, and that station's input never on the anvil
        var other = Waiting;
        Assert.NotEqual(Forge, other.Station);
        Assert.False(Ready(StationOf(other)).TryStack(Cell, Metal));
        Assert.False(Ready().TryStack(Cell, other.InputId));
    }

    [Fact]
    public void AStackTakesOnlyAForgeMetal()
    {
        Assert.True(ContentRegistry.IsWorkInput(Forge, Metal));
        var anvils = Ready();
        Assert.False(anvils.TryStack(Cell, ContentRegistry.RequireItem("coal").Id));
        Assert.True(anvils.TryStack(Cell, Metal));
        // Another forge metal never joins the stack
        var other = ContentRegistry.WorkRecipes.First(r => r.InputId != Metal).InputId;
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
        Assert.Equal(RecipesOf(Metal).Max(r => r.InputCount), stacked);
        Assert.Equal(stacked, anvils.StateAt(4, 2, 4)!.InputCount);
        Assert.Equal(RecipesOf(Metal).Length, WorkStationManager.Offered(Forge, Metal, stacked).Count);
    }

    [Fact]
    public void TakingBackGivesOnePieceOrTheWholeStack()
    {
        var anvils = Stacked(3);

        Assert.True(anvils.TryTake(Cell, all: false, out var item, out var count));
        Assert.Equal((Metal, 1), (item, count));
        Assert.Equal(2, anvils.StateAt(4, 2, 4)!.InputCount);

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
        for (var count = 0; count <= recipes.Max(r => r.InputCount); count++)
        {
            var offered = WorkStationManager.Offered(Forge, Metal, count);
            Assert.All(offered, r => Assert.True(r.InputId == Metal && r.InputCount <= count));
            Assert.Equal(recipes.Count(r => r.InputCount <= count), offered.Count);
        }

        // An item no recipe takes is offered nothing, however many lie there
        Assert.Empty(WorkStationManager.Offered(Forge, ContentRegistry.RequireItem("coal").Id, 64));
    }

    [Fact]
    public void APickNeedsTheRecipeOfferedAndCostsNothing()
    {
        var recipes = RecipesOf(Metal);
        var cheapest = recipes.MinBy(r => r.InputCount)!;
        var costliest = recipes.MaxBy(r => r.InputCount)!;
        Assert.True(costliest.InputCount > cheapest.InputCount, "the iron recipes should differ in cost");

        var anvils = Stacked(cheapest.InputCount);
        Assert.False(anvils.TrySelect(Cell, costliest.ResultId));
        Assert.False(anvils.TrySelect(Cell, Metal));
        Assert.True(anvils.TrySelect(Cell, cheapest.ResultId));

        var state = anvils.StateAt(4, 2, 4)!;
        Assert.Same(cheapest, state.Selected);
        Assert.Equal(cheapest.InputCount, state.InputCount);
    }

    [Fact]
    public void HitsAreGatedByTheHammersGapAndTheRecipesHitsFinishIt()
    {
        // One with room for a spare unit on the stack, so the test sees exactly its own metal leave
        var recipe = RecipesOf(Metal).Where(r => r.InputCount < WorkStationManager.StackCap(Forge, Metal)).MaxBy(r => r.Hits)!;
        var anvils = Stacked(recipe.InputCount + 1);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        var state = anvils.StateAt(4, 2, 4)!;

        for (var hit = 1; hit < recipe.Hits; hit++)
        {
            Assert.Equal(WorkHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);
            // Right away again: refused, nothing counted
            Assert.Equal(WorkHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
            Assert.Equal(hit, state.Hits);
            // Nothing leaves the stack before the last hit
            Assert.Equal(recipe.InputCount + 1, state.InputCount);
            state.CooldownLeft = 0f;
        }

        var done = anvils.TryHit(Cell, Gap, 0f);
        Assert.Equal(WorkHitKind.Finished, done.Kind);
        Assert.Equal((recipe.ResultId, recipe.ResultCount), (done.ResultId, done.ResultCount));
        Assert.Equal((recipe.ByproductId, recipe.ByproductCount), (done.ByproductId, done.ByproductCount));
        // Exactly the recipe's metal is spent, and the pick is used up
        Assert.Equal(1, state.InputCount);
        Assert.Null(state.Selected);
        Assert.Equal(0, state.Hits);
    }

    [Fact]
    public void EveryWorkRecipeFinishesOnExactlyItsOwnHitCountOnItsOwnStation()
    {
        foreach (var recipe in ContentRegistry.WorkRecipes)
        {
            var stations = StackedFor(recipe);
            Assert.True(stations.TrySelect(Cell, recipe.ResultId));
            Assert.Equal(recipe.Hits, HammerOut(stations).Hits);
            var state = stations.StateAt(4, 2, 4)!;
            Assert.Equal(0, state.InputCount);
            Assert.Equal(recipe.CollectWithId == 0 ? 0 : recipe.ResultCount, state.FinishedCount);
        }
    }

    [Fact]
    public void RePickingTheSameRecipeKeepsItsHitsAndAnotherStartsOver()
    {
        var recipes = WorkStationManager.Offered(Forge, Metal, RecipesOf(Metal).Max(r => r.InputCount));
        var first = recipes.First(r => r.Hits > 1);
        var other = recipes.First(r => r.ResultId != first.ResultId);
        var anvils = Stacked(RecipesOf(Metal).Max(r => r.InputCount));
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
        var cheapest = RecipesOf(Metal).MinBy(r => r.InputCount)!;
        var anvils = Stacked(cheapest.InputCount);
        Assert.True(anvils.TrySelect(Cell, cheapest.ResultId));
        anvils.TryHit(Cell, Gap, 0f);

        Assert.True(anvils.TryTake(Cell, all: false, out _, out _));
        var state = anvils.StateAt(4, 2, 4)!;
        Assert.Null(state.Selected);
        Assert.Equal(0, state.Hits);
        state.CooldownLeft = 0f;
        Assert.Equal(WorkHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
    }

    [Fact]
    public void TheGapRunsDownWithTimeAndSlackLetsAHitThroughEarly()
    {
        var recipe = RecipesOf(Metal).First(r => r.Hits > 2);
        var anvils = Stacked(recipe.InputCount);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        Assert.Equal(WorkHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);

        // Half the gap passes, with the anvil block standing in its cell
        var (manager, _) = MeshTestWorld.WalledArea();
        var (lx, ly, lz) = VoxelCoords.LocalOf((int)Cell.X, (int)Cell.Y, (int)Cell.Z);
        manager.ChunkAt((int)Cell.X, (int)Cell.Y, (int)Cell.Z)!.SetBlockRaw(lx, ly, lz, Anvil.Id, 0);
        anvils.Update(Gap * 0.5f, manager, new DropManager());

        Assert.Equal(WorkHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
        // Within the slack of the end of the gap a late packet still counts
        Assert.Equal(WorkHitKind.Struck, anvils.TryHit(Cell, Gap, Gap * 0.5f + 0.01f).Kind);
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
        var anvils = Stacked(recipe.InputCount);
        Assert.True(anvils.TrySelect(Cell, recipe.ResultId));
        anvils.TryHit(Cell, Gap, 0f);

        var saved = new WorkStationManager();
        Assert.Equal(0, saved.Restore(anvils.Capture()));
        var restored = saved.StateAt(4, 2, 4)!;
        Assert.Equal((Metal, recipe.InputCount), (restored.InputId, restored.InputCount));
        Assert.Equal((recipe.ResultId, 1), (restored.SelectedResultId, restored.Hits));

        using var writer = new PacketWriter(NetMessage.StationState);
        anvils.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new WorkStationManager();
        mirror.ReadNet(reader);
        var mirrored = mirror.StateAt(4, 2, 4)!;
        Assert.Equal((Metal, recipe.InputCount), (mirrored.InputId, mirrored.InputCount));
        Assert.Equal((recipe.ResultId, 1), (mirrored.SelectedResultId, mirrored.Hits));

        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        anvils.Update(0.1f, manager, drops);
        Assert.Equal(recipe.InputCount, drops.Drops.Count(d => d.BlockId == Metal));
    }
}
