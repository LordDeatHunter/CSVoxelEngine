using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Stations;
using VoxelEngine.Networking;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// The anvil's rules: it takes only its own input, one piece at a time; a hit needs the gap since the last one to
/// have run down, the last hit finishes the piece into its result and slag, and a gone block hands the piece back.
/// It holds either that piece or a stack of one forge metal, which grows one at a time up to the metal's largest recipe,
/// comes back one at a time or whole, and is forged only into the recipes of that metal the stack covers.
/// </summary>
public sealed class AnvilManagerTests
{
    public AnvilManagerTests() => TestContent.EnsureLoaded();

    private static readonly Vector3 Cell = new(4, 2, 4);
    private const float Gap = 2.5f;

    private static BlockType Anvil => ContentRegistry.RequireBlock("anvil");
    private static AnvilProfile Profile => Anvil.Anvil!;

    private static AnvilManager Ready()
    {
        var anvils = new AnvilManager();
        anvils.Ensure(Cell, Anvil.Id);
        return anvils;
    }

    [Fact]
    public void ItTakesOnlyItsInputAndOnlyOnePiece()
    {
        var anvils = Ready();
        Assert.False(anvils.TryPlace(Cell, ContentRegistry.RequireItem("coal").Id));
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.False(anvils.TryPlace(Cell, Profile.InputId));
    }

    [Fact]
    public void AHitWithNothingOnTheAnvilDoesNothing()
    {
        Assert.Equal(AnvilHitKind.Refused, Ready().TryHit(Cell, Gap, 0f).Kind);
    }

    [Fact]
    public void HitsAreGatedByTheHammersGapAndTheLastOneFinishesThePiece()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        var state = anvils.StateAt(4, 2, 4)!;

        for (var hit = 1; hit < Profile.Hits; hit++)
        {
            Assert.Equal(AnvilHitKind.Struck, anvils.TryHit(Cell, Gap, 0f).Kind);
            // Right away again: refused, nothing counted
            Assert.Equal(AnvilHitKind.Refused, anvils.TryHit(Cell, Gap, 0f).Kind);
            Assert.Equal(hit, state.Hits);
            state.CooldownLeft = 0f;
        }

        var done = anvils.TryHit(Cell, Gap, 0f);
        Assert.Equal(AnvilHitKind.Finished, done.Kind);
        Assert.Equal(Profile.ResultId, done.ResultId);
        Assert.True(done.ResultCount > 0);
        Assert.Equal(Profile.SlagId, done.SlagId);
        Assert.False(state.HasPiece);
        Assert.Equal(0, state.Hits);
    }

    [Fact]
    public void TheGapRunsDownWithTimeAndSlackLetsAHitThroughEarly()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
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
    public void ThePieceCanBeTakenBackAndHitsAreLost()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        anvils.TryHit(Cell, Gap, 0f);

        Assert.True(anvils.TryTake(Cell, all: false, out var item, out var count));
        Assert.Equal(Profile.InputId, item);
        Assert.Equal(1, count);
        Assert.False(anvils.TryTake(Cell, all: false, out _, out _));
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.Equal(0, anvils.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AnAnvilWhoseBlockIsGoneDropsItsPiece()
    {
        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));

        // The cell holds air, not the anvil
        anvils.Update(0.1f, manager, drops);

        Assert.Null(anvils.StateAt(4, 2, 4));
        Assert.Single(drops.Drops);
        Assert.Equal(Profile.InputId, drops.Drops[0].BlockId);
    }

    [Fact]
    public void APieceAndItsHitsSurviveASaveAndTheWire()
    {
        var anvils = Ready();
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        anvils.TryHit(Cell, Gap, 0f);

        var saved = new AnvilManager();
        Assert.Equal(0, saved.Restore(anvils.Capture()));
        Assert.True(saved.StateAt(4, 2, 4)!.HasPiece);
        Assert.Equal(1, saved.StateAt(4, 2, 4)!.Hits);

        using var writer = new PacketWriter(NetMessage.StationState);
        anvils.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new AnvilManager();
        mirror.ReadNet(reader);
        Assert.True(mirror.StateAt(4, 2, 4)!.HasPiece);
        Assert.Equal(1, mirror.StateAt(4, 2, 4)!.Hits);
    }

    [Fact]
    public void AnEmptyAnvilIsNotSaved()
    {
        Assert.Empty(Ready().Capture());
    }

    // The metal the anvil's own work makes is what its forge recipes take (the iron chain)
    private static ushort Metal => Profile.ResultId;

    private static ForgeRecipe[] RecipesOf(ushort metal) =>
        ContentRegistry.ForgeRecipes.Where(r => r.MetalId == metal).ToArray();

    private static AnvilManager Stacked(int count)
    {
        var anvils = Ready();
        for (var i = 0; i < count; i++)
            Assert.True(anvils.TryStack(Cell, Metal));
        return anvils;
    }

    [Fact]
    public void AStackTakesOnlyAForgeMetalAndNeverLiesBesideABloom()
    {
        Assert.True(ContentRegistry.IsForgeMetal(Metal));
        var anvils = Ready();
        Assert.False(anvils.TryStack(Cell, ContentRegistry.RequireItem("coal").Id));
        Assert.False(anvils.TryStack(Cell, Profile.InputId));

        // A bloom on the anvil keeps the metal off, and a stack keeps the bloom off
        Assert.True(anvils.TryPlace(Cell, Profile.InputId));
        Assert.False(anvils.TryStack(Cell, Metal));
        Assert.True(anvils.TryTake(Cell, all: true, out _, out _));
        Assert.True(anvils.TryStack(Cell, Metal));
        Assert.False(anvils.TryPlace(Cell, Profile.InputId));
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

        // Another item never forges into this metal's recipes, however many lie there
        Assert.Empty(AnvilManager.Offered(Profile.InputId, 64));
    }

    [Fact]
    public void ForgingTakesTheRecipesMetalAndRefusesWhatTheStackDoesNotCover()
    {
        var recipes = RecipesOf(Metal);
        var cheapest = recipes.MinBy(r => r.MetalCount)!;
        var costliest = recipes.MaxBy(r => r.MetalCount)!;
        Assert.True(costliest.MetalCount > cheapest.MetalCount, "the iron recipes should differ in cost");

        var anvils = Stacked(cheapest.MetalCount);
        Assert.False(anvils.TryForge(Cell, costliest.ResultId, out _));
        Assert.Equal(cheapest.MetalCount, anvils.StateAt(4, 2, 4)!.MetalCount);
        Assert.False(anvils.TryForge(Cell, Metal, out _));

        Assert.True(anvils.TryForge(Cell, cheapest.ResultId, out var made));
        Assert.Same(cheapest, made);
        Assert.True(anvils.StateAt(4, 2, 4)!.IsEmpty);
    }

    [Fact]
    public void TheStationsForgeHandsTheResultToTheInventory()
    {
        var cheapest = RecipesOf(Metal).MinBy(r => r.MetalCount)!;
        var smithing = new SmithingStations();
        smithing.Anvils.Ensure(Cell, Anvil.Id);
        for (var i = 0; i < cheapest.MetalCount; i++)
            Assert.True(smithing.Anvils.TryStack(Cell, Metal));
        var inventory = Inventory.Empty();

        Assert.True(smithing.Forge(Cell, cheapest.ResultId, inventory));
        Assert.Equal(cheapest.ResultCount, inventory.CountFor(cheapest.ResultId));
        // The stack is spent, so a second pick is refused and gives nothing
        Assert.False(smithing.Forge(Cell, cheapest.ResultId, inventory));
        Assert.Equal(cheapest.ResultCount, inventory.CountFor(cheapest.ResultId));
    }

    [Fact]
    public void AStackSurvivesASaveAndTheWireAndDropsWhenItsBlockIsGone()
    {
        var anvils = Stacked(3);

        var saved = new AnvilManager();
        Assert.Equal(0, saved.Restore(anvils.Capture()));
        Assert.Equal((Metal, 3), (saved.StateAt(4, 2, 4)!.MetalId, saved.StateAt(4, 2, 4)!.MetalCount));
        Assert.False(saved.StateAt(4, 2, 4)!.HasPiece);

        using var writer = new PacketWriter(NetMessage.StationState);
        anvils.WriteNet(writer);
        using var reader = new PacketReader(writer.ToArray());
        Assert.Equal(NetMessage.StationState, reader.Message);
        var mirror = new AnvilManager();
        mirror.ReadNet(reader);
        Assert.Equal((Metal, 3), (mirror.StateAt(4, 2, 4)!.MetalId, mirror.StateAt(4, 2, 4)!.MetalCount));

        var (manager, _) = MeshTestWorld.WalledArea();
        var drops = new DropManager();
        anvils.Update(0.1f, manager, drops);
        Assert.Equal(3, drops.Drops.Count(d => d.BlockId == Metal));
    }
}
