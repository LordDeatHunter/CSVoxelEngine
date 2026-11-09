using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
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
        public readonly List<(int X, int Y, int Z, ChestProfile Profile, byte State)> Spills = [];
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
            (x, y, z) => rig!.LodEdits.Add((x, y, z)), (x, y, z) => rig!.Pops.Add((x, y, z)),
            (x, y, z, profile, state) => rig!.Spills.Add((x, y, z, profile, state)));
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

    [Fact]
    public void ItReplacesACellWithTheTargetBlockAndState()
    {
        var rig = Setup();
        var brick = Id("tower_bricks");
        var cobble = Id("cobblestone");
        rig.Chunk.SetBlockRaw(5, 1, 5, brick, 0);
        rig.Chunk.TakeSnapshot(null);
        Assert.False(rig.Chunk.ModifiedSinceSave);

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryReplace(5, 1, 5, brick, cobble, 2));

        Assert.True(rig.Chunks.TryGetCell(5, 1, 5, out var id, out var state));
        Assert.Equal(cobble, id);
        Assert.Equal(2, state);
        Assert.True(rig.Chunk.ModifiedSinceSave);
    }

    [Fact]
    public void ReplaceLeavesACellHoldingAnythingElse()
    {
        var rig = Setup();
        var stone = Id("stone");
        rig.Chunk.SetBlockRaw(5, 1, 5, stone, 0);

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Gone, rig.Writer.TryReplace(5, 1, 5, Id("tower_bricks"), Id("cobblestone"), 0));
        Assert.Equal(stone, rig.Chunks.GetBlockGlobal(5, 1, 5));
        Assert.Empty(rig.LodEdits);
    }

    [Fact]
    public void ClearsAndReplacesShareOneFrameBudget()
    {
        var rig = Setup();
        var brick = Id("tower_bricks");
        var cobble = Id("cobblestone");
        var cells = new List<(int X, int Y, int Z)>();
        for (var y = 1; cells.Count <= MobConfig.TowerMaxBlockWritesPerFrame; y++)
        for (var x = 0; x < Chunk.Size && cells.Count <= MobConfig.TowerMaxBlockWritesPerFrame; x++)
        {
            rig.Chunk.SetBlockRaw(x, y, 8, brick, 0);
            cells.Add((x, y, 8));
        }

        rig.Writer.BeginFrame();
        var written = 0;
        var last = TowerWriteResult.Written;
        for (var i = 0; i < cells.Count; i++)
        {
            var (x, y, z) = cells[i];
            last = i % 2 == 0 ? rig.Writer.TryClear(x, y, z, brick) : rig.Writer.TryReplace(x, y, z, brick, cobble, 0);
            if (last == TowerWriteResult.Written) written++;
        }
        Assert.Equal(MobConfig.TowerMaxBlockWritesPerFrame, written);
        Assert.Equal(TowerWriteResult.Capped, last);

        var (lx, ly, lz) = cells[^1];
        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryReplace(lx, ly, lz, brick, cobble, 0));
        Assert.Equal(cobble, rig.Chunks.GetBlockGlobal(lx, ly, lz));
    }

    [Fact]
    public void ReplacingAChestHandsItToTheSpillHook()
    {
        var rig = Setup();
        var chest = Id("tower_chest");
        var stone = Id("stone");
        rig.Chunk.SetBlockRaw(5, 1, 5, chest, 13);
        rig.Chunk.SetBlockRaw(6, 1, 5, stone, 0);

        rig.Writer.BeginFrame();
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryReplace(6, 1, 5, stone, BlockType.Air, 0));
        Assert.Empty(rig.Spills);
        Assert.Equal(TowerWriteResult.Written, rig.Writer.TryReplace(5, 1, 5, chest, BlockType.Air, 0));

        var spill = Assert.Single(rig.Spills);
        Assert.Equal((5, 1, 5), (spill.X, spill.Y, spill.Z));
        Assert.Same(BlockType.ById[chest]!.Chest, spill.Profile);
        Assert.Equal(13, spill.State);
    }
}
