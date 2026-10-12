using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Multi-state blocks (candle-style piles): flint and obsidian ground and grow pebble piles of
/// 1-3, branches ground and grow crossed-stick piles of 1-4, with the count riding the chunk
/// state byte. Worldgen pebble piles never write state (0 keeps the position-hash default);
/// player pebble piles pack the total with their obsidian share, and a bare branch pile reads
/// as one stick.
/// </summary>
public sealed class MultistateTests
{
    public MultistateTests() => TestContent.EnsureLoaded();

    private static byte Pebble => ContentRegistry.RequireBlock("pebble").Id;
    private static byte Flint => ContentRegistry.RequireItem("flint").Id;
    private static byte Obsidian => ContentRegistry.RequireItem("obsidian").Id;
    private static byte BranchPile => ContentRegistry.RequireBlock("branch_pile").Id;
    private static byte Branch => ContentRegistry.RequireItem("branch").Id;

    [Fact]
    public void ExplicitStateWinsOverTheWorldgenHash()
    {
        var pebble = BlockType.ById[Pebble]!;
        for (var x = 0; x < 8; x++)
        for (var z = 0; z < 8; z++)
        {
            Assert.Equal(1, PebbleGenerator.CountWithState(x, z, 1, pebble.StackMax));
            Assert.Equal(2, PebbleGenerator.CountWithState(x, z, 2, pebble.StackMax));
            Assert.Equal(3, PebbleGenerator.CountWithState(x, z, 3, pebble.StackMax));
            Assert.Equal(PebbleGenerator.CountAt(x, z),
                PebbleGenerator.CountWithState(x, z, 0, pebble.StackMax));
        }
    }

    [Fact]
    public void PebbleLootDropsTheExplicitPile()
    {
        var table = ContentRegistry.LootTableFor("pebble");
        Assert.NotNull(table);

        var into = new List<byte>();
        table.Roll(new LootContext(8, 0, 8, Count: 2), into);
        Assert.Equal([Flint, Flint], into);

        into.Clear();
        table.Roll(new LootContext(8, 0, 8), into);
        Assert.Equal(PebbleGenerator.CountAt(8, 8), into.Count);
        Assert.All(into, id => Assert.Equal(Flint, id));
    }

    [Fact]
    public void PileCountPassesOnlyExplicitStackStatesThrough()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.Equal(0, DropManager.PileCount(Pebble, 8, 8, 0));
        Assert.Equal(2, DropManager.PileCount(Pebble, 8, 8, 2));
        Assert.Equal(0, DropManager.PileCount(Pebble, 8, 8, 9));
        Assert.Equal(0, DropManager.PileCount(stone, 8, 8, 2));
    }

    [Fact]
    public void ChunkStateRoundtripsThroughRawWrites()
    {
        var (_, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, Pebble, 2);
        Assert.Equal(2, center.GetLocalState(8, 8, 8));
        center.SetBlockRaw(8, 8, 8, Pebble, 0);
        Assert.Equal(0, center.GetLocalState(8, 8, 8));
    }

    [Fact]
    public void PebbleMeshGrowsOneStonePerState()
    {
        // One stone is five emitted faces (the bottom sits on the ground): 20 verts, 30 indices.
        foreach (var state in new byte[] { 1, 2, 3 })
        {
            var (manager, center) = MeshTestWorld.WalledArea();
            center.SetBlockRaw(8, 8, 8, Pebble, state);

            var mesh = center.GenerateMeshData(manager, greedy: false);
            Assert.NotNull(mesh);
            Assert.Equal(20 * state, mesh.Vertices.Count);
            Assert.Equal(30 * state, mesh.Indices.Count);
        }
    }

    [Fact]
    public void WorldgenPebbleMeshMatchesItsHashCount()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, Pebble, 0);

        var expected = PebbleGenerator.CountAt(8, 8);
        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        Assert.Equal(20 * expected, mesh.Vertices.Count);
    }

    [Fact]
    public void BranchPileStateReadsBackDirectly()
    {
        var pile = BlockType.ById[BranchPile]!;
        for (byte state = 1; state <= pile.StackMax; state++)
            Assert.Equal(state, BranchPileGenerator.CountWithState(state, pile.StackMax));
        Assert.Equal(1, BranchPileGenerator.CountWithState(0, pile.StackMax));
    }

    [Fact]
    public void BranchPileLootDropsTheExplicitPile()
    {
        var table = ContentRegistry.LootTableFor("branch_pile");
        Assert.NotNull(table);

        var into = new List<byte>();
        table.Roll(new LootContext(8, 0, 8, Count: 3), into);
        Assert.Equal([Branch, Branch, Branch], into);

        into.Clear();
        table.Roll(new LootContext(8, 0, 8), into);
        Assert.Equal([Branch], into);
    }

    [Fact]
    public void PileCountPassesBranchPileStatesThrough()
    {
        Assert.Equal(0, DropManager.PileCount(BranchPile, 8, 8, 0));
        Assert.Equal(4, DropManager.PileCount(BranchPile, 8, 8, 4));
    }

    [Fact]
    public void BranchPileMeshGrowsOneStickPerState()
    {
        // One stick is five emitted faces (the bottom is skipped): 20 verts, 30 indices.
        foreach (var state in new byte[] { 1, 2, 3, 4 })
        {
            var (manager, center) = MeshTestWorld.WalledArea();
            center.SetBlockRaw(8, 8, 8, BranchPile, state);

            var mesh = center.GenerateMeshData(manager, greedy: false);
            Assert.NotNull(mesh);
            Assert.Equal(20 * state, mesh.Vertices.Count);
            Assert.Equal(30 * state, mesh.Indices.Count);
        }
    }

    [Fact]
    public void BareBranchPileMeshDefaultsToOneStick()
    {
        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, BranchPile, 0);

        var mesh = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mesh);
        Assert.Equal(20, mesh.Vertices.Count);
    }

    [Fact]
    public void PebbleStatesPackTotalWithObsidianShare()
    {
        var pile = BlockType.ById[Pebble]!;
        for (var total = 1; total <= pile.StackMax; total++)
        for (var obsidian = 0; obsidian <= total; obsidian++)
        {
            var state = PebbleGenerator.StateFor(total, obsidian);
            Assert.True(PebbleGenerator.TryDecode(state, pile.StackMax, out var decodedTotal, out var decodedObsidian));
            Assert.Equal(total, decodedTotal);
            Assert.Equal(obsidian, decodedObsidian);
            Assert.Equal(total, PebbleGenerator.CountWithState(8, 8, state, pile.StackMax));
            Assert.Equal(obsidian, PebbleGenerator.ObsidianCountWithState(8, 8, state, pile.StackMax));
            Assert.Equal(total, DropManager.PileCount(Pebble, 8, 8, state));
            Assert.Equal(obsidian, DropManager.PileSecondary(Pebble, 8, 8, state));
        }
    }

    [Fact]
    public void LegacyPebbleStatesReadAsPureFlint()
    {
        var pile = BlockType.ById[Pebble]!;
        for (byte state = 1; state <= pile.StackMax; state++)
            Assert.Equal(0, PebbleGenerator.ObsidianCountWithState(3, 5, state, pile.StackMax));
    }

    [Fact]
    public void PebbleLootDropsExactlyWhatThePileHeld()
    {
        var table = ContentRegistry.LootTableFor("pebble");
        Assert.NotNull(table);

        var pile = BlockType.ById[Pebble]!;
        for (var total = 1; total <= pile.StackMax; total++)
        for (var obsidian = 0; obsidian <= total; obsidian++)
        {
            var into = new List<byte>();
            table.Roll(new LootContext(8, 0, 8, total, obsidian), into);
            Assert.Equal(total, into.Count);
            Assert.Equal(obsidian, into.Count(id => id == Obsidian));
            Assert.Equal(total - obsidian, into.Count(id => id == Flint));
        }
    }

    [Fact]
    public void WorldgenPilesSplitFlintAndObsidianAcrossTheTotal()
    {
        var pile = BlockType.ById[Pebble]!;
        for (var x = 0; x < 16; x++)
        for (var z = 0; z < 16; z++)
        {
            var total = PebbleGenerator.CountWithState(x, z, 0, pile.StackMax);
            var obsidian = PebbleGenerator.ObsidianCountWithState(x, z, 0, pile.StackMax);
            Assert.Equal(total, PebbleGenerator.CountAt(x, z));
            Assert.InRange(obsidian, 0, total);

            var expected = 0;
            for (var i = 0; i < total; i++)
                if (PebbleGenerator.IsObsidianAt(x, z, i)) expected++;
            Assert.Equal(expected, obsidian);
        }
    }

    [Fact]
    public void NaturalObsidianStaysRareButPresent()
    {
        var pile = BlockType.ById[Pebble]!;
        var stones = 0;
        var obsidian = 0;
        for (var x = 0; x < 64; x++)
        for (var z = 0; z < 64; z++)
        {
            stones += PebbleGenerator.CountAt(x, z);
            obsidian += PebbleGenerator.ObsidianCountWithState(x, z, 0, pile.StackMax);
        }
        var fraction = (double)obsidian / stones;
        Assert.InRange(fraction, 0.001, 0.05);
    }

    [Fact]
    public void ObsidianShardsGroundPebblePilesLikeFlint()
    {
        var flint = ContentRegistry.RequireItem("flint");
        var shard = ContentRegistry.RequireItem("obsidian");
        Assert.Equal(ItemUseAction.PlaceStack, flint.UseAction);
        Assert.Equal(ItemUseAction.PlaceStack, shard.UseAction);
        Assert.Equal(Pebble, flint.UseBlockId);
        Assert.Equal(Pebble, shard.UseBlockId);
    }

    [Fact]
    public void MixedPileMeshKeepsStoneCountAndTintsObsidian()
    {
        var pebbleType = BlockType.ById[Pebble]!;
        var obsidianType = BlockType.ById[PebbleGenerator.ObsidianBlock]!;

        var (manager, center) = MeshTestWorld.WalledArea();
        center.SetBlockRaw(8, 8, 8, Pebble, PebbleGenerator.StateFor(3, 1));
        var mixed = center.GenerateMeshData(manager, greedy: false);
        Assert.NotNull(mixed);
        Assert.Equal(60, mixed.Vertices.Count);
        var layers = new HashSet<ushort>();
        foreach (var v in mixed.Vertices) layers.Add(v.Layer);
        Assert.Contains(pebbleType.Top, layers);
        Assert.Contains(obsidianType.Top, layers);
    }

    [Fact]
    public void PileSecondaryPassesOnlyPebbleStatesThrough()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        Assert.Equal(0, DropManager.PileSecondary(Pebble, 8, 8, 0));
        Assert.Equal(1, DropManager.PileSecondary(Pebble, 8, 8, PebbleGenerator.StateFor(2, 1)));
        Assert.Equal(0, DropManager.PileSecondary(Pebble, 8, 8, 9));
        Assert.Equal(0, DropManager.PileSecondary(stone, 8, 8, 2));
    }
}
