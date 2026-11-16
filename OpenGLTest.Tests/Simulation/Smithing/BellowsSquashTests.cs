using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.Simulation.Smithing;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Smithing;

/// <summary>
/// A bellows gives under a weight and swells back: the bag follows the feet standing in its soft layer, never rises
/// under them, grows back to a full block once they leave, and is out of the chunk mesh exactly while it is not a
/// full block.
/// </summary>
public sealed class BellowsSquashTests
{
    public BellowsSquashTests() => TestContent.EnsureLoaded();

    private static BlockType Bellows => ContentRegistry.RequireBlock("bellows");

    [Fact]
    public void TheBellowsIsASinkBlockThatHidesNoNeighbor()
    {
        // The body can settle into it, and a squashed bag must not leave a hole in the block beside it
        Assert.True(BlockType.SinkOf(Bellows.Id) > 0f);
        Assert.False(Bellows.OccludesNeighbors);
        // Leaving the chunk mesh per cell is something only the per-quad path can do
        Assert.False(BlockType.IsGreedy(Bellows.Id));
    }

    [Fact]
    public void OnlyFeetInTheSoftLayerPressAndNeverBelowTheSolidPart()
    {
        var sink = BlockType.SinkOf(Bellows.Id);
        const int cellY = 10;

        // Feet in the air over it and feet under its solid part press nothing
        Assert.Null(BellowsSquash.PressedTo(cellY + 1.5f, cellY, sink));
        Assert.Null(BellowsSquash.PressedTo(cellY + SinkLayer.SolidHeight(sink) - 0.3f, cellY, sink));

        // Feet standing in the layer press it to their own height, the deeper the lower
        var high = BellowsSquash.PressedTo(cellY + 0.9f, cellY, sink);
        var low = BellowsSquash.PressedTo(cellY + 0.6f, cellY, sink);
        Assert.NotNull(high);
        Assert.NotNull(low);
        Assert.True(low < high);
        Assert.InRange(low!.Value, SinkLayer.SolidHeight(sink), 1f);

        // A plain block has no soft layer to stand in
        Assert.Null(BellowsSquash.PressedTo(cellY + 1f, cellY, 0f));
    }

    [Fact]
    public void TheBagNeverRisesUnderAWeightAndSwellsBackToAFullBlockWithoutIt()
    {
        // Under a weight it follows the feet down and stays there, however long
        Assert.Equal(0.7f, BellowsSquash.Step(1f, 0.7f, 0.1f), precision: 5);
        Assert.Equal(0.6f, BellowsSquash.Step(0.6f, 0.9f, 10f), precision: 5);

        // Without one it only ever grows, and stops at the full block
        var height = 0.5f;
        for (var i = 0; i < 200 && height < 1f; i++)
        {
            var next = BellowsSquash.Step(height, null, 0.05f);
            Assert.True(next > height);
            Assert.True(next <= 1f);
            height = next;
        }
        Assert.Equal(1f, height, precision: 5);
        Assert.Equal(1f, BellowsSquash.Step(1f, null, 1f), precision: 5);
    }

    /// <summary>A bellows in a world: a body standing on its full top (a mob's feet, or a player who just landed)
    /// leaves it in the chunk mesh; feet sunk into it take it out of the mesh at their height; once they are gone it
    /// grows back and returns to the mesh.</summary>
    [Fact]
    public void ASunkBodySquashesTheBagAndItReturnsToTheChunkMeshOnceFull()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        center.SetBlockRaw(8, 8, 8, Bellows.Id, 0);
        var cell = origin + new Vector3(8, 8, 8);
        var key = ((int)cell.X, (int)cell.Y, (int)cell.Z);
        var squash = new BellowsSquash();
        var whole = center.GenerateMeshData(manager)!.Vertices.Count;
        Assert.True(whole > 0);

        // Standing on the full top presses nothing
        squash.Update(0.05f, manager, [cell + new Vector3(0.5f, 1f, 0.5f)], PlayerPhysics.PlayerRadius);
        Assert.Empty(squash.Active);
        Assert.False(manager.SwingingCells.Contains(key.Item1, key.Item2, key.Item3));

        // Feet sunk a third of a block into it
        var sunk = new List<Vector3> { cell + new Vector3(0.5f, 0.67f, 0.5f) };
        squash.Update(0.05f, manager, sunk, PlayerPhysics.PlayerRadius);
        var (activeCell, id, height) = Assert.Single(squash.Active);
        Assert.Equal(cell, activeCell);
        Assert.Equal(Bellows.Id, id);
        Assert.Equal(0.67f, height, precision: 4);
        Assert.True(manager.SwingingCells.Contains(key.Item1, key.Item2, key.Item3));
        Assert.Empty(center.GenerateMeshData(manager)!.Vertices);

        // Nobody on it: it grows every step until one more step would make it a full block
        const float step = 0.05f;
        var previous = height;
        while (BellowsSquash.Step(previous, null, step) < 1f)
        {
            squash.Update(step, manager, [], PlayerPhysics.PlayerRadius);
            Assert.True(squash.Active[0].Height > previous);
            previous = squash.Active[0].Height;
        }

        // ...and that step hands the cell back to the chunk mesh. A headless chunk cannot take the non-empty mesh
        // the hand-back rebuilds, so the chunk is out of the manager's reach for that one step.
        var chunkPos = center.Position;
        Assert.True(manager.Chunks.TryRemove(chunkPos, out _));
        squash.Update(step, manager, [], PlayerPhysics.PlayerRadius);
        manager.Chunks[chunkPos] = center;
        Assert.Empty(squash.Active);
        Assert.False(manager.SwingingCells.Contains(key.Item1, key.Item2, key.Item3));
        Assert.Equal(whole, center.GenerateMeshData(manager)!.Vertices.Count);
    }

    [Fact]
    public void ABellowsMinedWhileSquashedIsForgotten()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var origin = center.Position * Chunk.Size;
        center.SetBlockRaw(8, 8, 8, Bellows.Id, 0);
        var cell = origin + new Vector3(8, 8, 8);
        var squash = new BellowsSquash();
        squash.Update(0.05f, manager, [cell + new Vector3(0.5f, 0.6f, 0.5f)], PlayerPhysics.PlayerRadius);
        Assert.Single(squash.Active);

        center.SetBlockRaw(8, 8, 8, BlockType.Air, 0);
        squash.Update(0.05f, manager, [], PlayerPhysics.PlayerRadius);
        Assert.Empty(squash.Active);
        Assert.False(manager.SwingingCells.Contains((int)cell.X, (int)cell.Y, (int)cell.Z));
    }
}
