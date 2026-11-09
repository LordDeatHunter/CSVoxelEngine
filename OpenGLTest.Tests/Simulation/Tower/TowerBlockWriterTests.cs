using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The battle tower's block writer (<see cref="TowerBlockWriter"/>): it clears the unbreakable cells mining refuses,
/// only when they still hold the expected block, leaves an unloaded cell for later, stays inside its frame budget
/// and runs the edit hooks (gravity, fluids, leaf decay, the far LOD, the save mark).
/// </summary>
[Collection("LOD column caps")]
public sealed class TowerBlockWriterTests
{
    public TowerBlockWriterTests() => TestContent.EnsureLoaded();

    private static byte Id(string name) => ContentRegistry.RequireBlock(name).Id;

    private sealed class Rig
    {
        public required ChunkManager Chunks;
        public required Chunk Chunk;
        public required FluidManager Fluids;
        public required LeafDecayManager LeafDecay;
        public required FallingBlockManager Falling;
        public required TowerBlockWriter Writer;
        public readonly List<(int X, int Y, int Z)> LodEdits = [];
        public readonly List<(int X, int Y, int Z)> Pops = [];
    }

    // One loaded chunk at the origin with a stone floor at y = 0, and a writer over it
    private static Rig Setup()
    {
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[Vector3.Zero] = chunk;
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);

        var fluids = new FluidManager();
        var leafDecay = new LeafDecayManager();
        var falling = new FallingBlockManager();
        var lod = new LodManager(null!);
        Rig? rig = null;
        var writer = new TowerBlockWriter(chunks, fluids, leafDecay, falling, lod,
            (x, y, z) => rig!.LodEdits.Add((x, y, z)), (x, y, z) => rig!.Pops.Add((x, y, z)));
        rig = new Rig
        {
            Chunks = chunks, Chunk = chunk, Fluids = fluids, LeafDecay = leafDecay, Falling = falling, Writer = writer,
        };
        return rig;
    }

    [Fact]
    public void ItClearsAnUnbreakableCellThatMiningRefuses()
    {
        var rig = Setup();
        var door = Id("tower_door");
        rig.Chunk.SetBlockRaw(5, 1, 5, door, 0);
        Assert.False(rig.Chunk.RemoveBlockAt(new Vector3(5, 1, 5), rig.Chunks));
        Assert.Equal(door, rig.Chunks.GetBlockGlobal(5, 1, 5));

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryClear(5, 1, 5, door));

        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(5, 1, 5));
        Assert.True(rig.Fluids.PendingCells > 0);
        Assert.True(rig.LeafDecay.PendingCells > 0);
        Assert.Contains((5, 1, 5), rig.LodEdits);
        Assert.Contains((5, 1, 5), rig.Pops);
    }

    [Fact]
    public void ItOnlyClearsTheExpectedBlock()
    {
        var rig = Setup();
        var door = Id("tower_door");
        var stone = Id("stone");
        rig.Chunk.SetBlockRaw(5, 1, 5, stone, 0);

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Gone, rig.Writer.TryClear(5, 1, 5, door));
        Assert.Equal(TowerWriteResult.Gone, rig.Writer.TryClear(6, 1, 5, door));

        Assert.Equal(stone, rig.Chunks.GetBlockGlobal(5, 1, 5));
        Assert.Empty(rig.LodEdits);
    }

    [Fact]
    public void AnUnloadedCellIsLeftForLater()
    {
        var rig = Setup();
        var door = Id("tower_door");

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Unloaded, rig.Writer.TryClear(Chunk.Size + 4, 1, 5, door));
        Assert.Equal(TowerWriteResult.Unloaded, rig.Writer.TryClear(-3, 1, 5, door));
        Assert.Empty(rig.LodEdits);
    }

    [Fact]
    public void WritesStopAtTheFrameCapAndResumeNextFrame()
    {
        var rig = Setup();
        var door = Id("tower_door");
        var cells = new List<(int X, int Y, int Z)>();
        for (var y = 1; cells.Count <= MobConfig.TowerMaxBlockWritesPerFrame; y++)
        for (var x = 0; x < Chunk.Size && cells.Count <= MobConfig.TowerMaxBlockWritesPerFrame; x++)
        {
            rig.Chunk.SetBlockRaw(x, y, 8, door, 0);
            cells.Add((x, y, 8));
        }

        rig.Writer.BeginFrame();
        var written = 0;
        TowerWriteResult last = TowerWriteResult.Written;
        foreach (var (x, y, z) in cells)
        {
            last = rig.Writer.TryClear(x, y, z, door);
            if (last == TowerWriteResult.Written)
                written++;
        }
        Assert.Equal(MobConfig.TowerMaxBlockWritesPerFrame, written);
        Assert.Equal(TowerWriteResult.Capped, last);
        var (lx, ly, lz) = cells[^1];
        Assert.Equal(door, rig.Chunks.GetBlockGlobal(lx, ly, lz));

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryClear(lx, ly, lz, door));
        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(lx, ly, lz));
    }

    [Fact]
    public void SandOnAClearedCellFallsAndTheChunkIsSaved()
    {
        var rig = Setup();
        var door = Id("tower_door");
        rig.Chunk.SetBlockRaw(5, 1, 5, door, 0);
        rig.Chunk.SetBlockRaw(5, 2, 5, Id("sand"), 0);
        rig.Chunk.TakeSnapshot(null);
        Assert.False(rig.Chunk.ModifiedSinceSave);
        Assert.Empty(rig.Falling.Falling);

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryClear(5, 1, 5, door));

        Assert.NotEmpty(rig.Falling.Falling);
        Assert.True(rig.Chunk.ModifiedSinceSave);
    }
}
