using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.Saves;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Tower;
using Xunit;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// The staged collapse of a defeated tower (<see cref="TowerCollapse"/>, BT-8) over a hand-built box and rule: it waits out its
/// delay, works from the top down, never writes more in a frame than the writer allows, leaves unloaded cells waiting without
/// holding up the rest and sweeps them once they load, resumes from its saved state to the same end, stays finished, and never
/// fills a cell a body stands in.
/// </summary>
[Collection("LOD column caps")]
public sealed class TowerCollapseTests
{
    private const int MinX = 0, MaxX = 7, BottomY = 1, TopY = 12, MinZ = 0, MaxZ = 7;

    public TowerCollapseTests() => TestContent.EnsureLoaded();

    private static byte Id(string name) => ContentRegistry.RequireBlock(name).Id;

    private sealed class Rig
    {
        public required ChunkManager Chunks;
        public required TowerBlockWriter Writer;
    }

    // Loaded, stone-filled chunks at the given chunk x positions (y = z = 0), and a writer over them
    private static Rig Setup(params int[] loadedChunkXs)
    {
        var chunks = new ChunkManager(null!);
        foreach (var cx in loadedChunkXs) LoadStoneChunk(chunks, cx);
        var fluids = new FluidManager();
        var lod = new LodManager(null!);
        var writer = new TowerBlockWriter(chunks, fluids, new LeafDecayManager(), new FallingBlockManager(), lod,
            (_, _, _) => { }, null);
        return new Rig { Chunks = chunks, Writer = writer };
    }

    private static void LoadStoneChunk(ChunkManager chunks, int cx)
    {
        var chunk = new Chunk(null!, new Vector3(cx, 0, 0), generateBlockData: false);
        var stone = Id("stone");
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = BottomY; y <= TopY; y++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, y, z, stone, 0);
        chunk.IsBlockDataReady = true;
        chunks.Chunks[new Vector3(cx, 0, 0)] = chunk;
    }

    // Every box cell of stone becomes cobblestone
    private static int StoneToCobble(int x, int y, int z, out byte expected)
    {
        expected = Id("stone");
        return Id("cobblestone");
    }

    private static TowerCollapse Collapse(int minX = MinX, float delay = 0f, int topY = TopY, TowerCollapse.CellRule? rule = null) =>
        new(minX, MaxX, BottomY, topY, MinZ, MaxZ, rule ?? StoneToCobble, delay);

    private static bool Frame(Rig rig, TowerCollapse collapse, Func<int, int, int, bool>? occupied = null, float dt = 1f / 60f)
    {
        rig.Writer.BeginFrame();
        var budget = MobConfig.TowerCollapseScanCellsPerFrame;
        return collapse.Step(dt, rig.Writer, rig.Chunks, ref budget, occupied);
    }

    private static void RunToDone(Rig rig, TowerCollapse collapse, int maxFrames = 1000)
    {
        for (var f = 0; f < maxFrames && !collapse.Done; f++) Frame(rig, collapse);
        Assert.True(collapse.Done);
    }

    private static int CountBox(Rig rig, byte block, int minX = MinX)
    {
        var count = 0;
        for (var y = BottomY; y <= TopY; y++)
        for (var z = MinZ; z <= MaxZ; z++)
        for (var x = minX; x <= MaxX; x++)
            if (rig.Chunks.GetBlockGlobal(x, y, z) == block) count++;
        return count;
    }

    private static int BoxCells(int minX = MinX) => (MaxX - minX + 1) * (TopY - BottomY + 1) * (MaxZ - MinZ + 1);

    [Fact]
    public void ItCollapsesTopDown()
    {
        var rig = Setup(0);
        var collapse = Collapse();
        var stone = Id("stone");
        var cobble = Id("cobblestone");
        while (!collapse.Done)
        {
            Frame(rig, collapse);
            int lowestWritten = int.MaxValue, highestStone = int.MinValue;
            for (var y = BottomY; y <= TopY; y++)
            for (var z = MinZ; z <= MaxZ; z++)
            for (var x = MinX; x <= MaxX; x++)
            {
                var id = rig.Chunks.GetBlockGlobal(x, y, z);
                if (id == cobble) lowestWritten = Math.Min(lowestWritten, y);
                else if (id == stone) highestStone = Math.Max(highestStone, y);
            }
            Assert.True(lowestWritten >= highestStone, $"wrote y {lowestWritten} while stone stood at y {highestStone}");
        }
    }

    [Fact]
    public void NoFrameWritesMoreThanTheWriterAllows()
    {
        var rig = Setup(0);
        var collapse = Collapse();
        var cobble = Id("cobblestone");
        var before = 0;
        for (var f = 0; f < 1000 && !collapse.Done; f++)
        {
            Frame(rig, collapse);
            var now = CountBox(rig, cobble);
            Assert.True(now - before <= MobConfig.TowerMaxBlockWritesPerFrame);
            before = now;
        }
        Assert.True(collapse.Done);
        Assert.Equal(BoxCells(), CountBox(rig, cobble));
    }

    [Fact]
    public void NothingFallsBeforeTheDelay()
    {
        var rig = Setup(0);
        var collapse = Collapse(delay: 1f);
        for (var f = 0; f < 5; f++) Frame(rig, collapse, dt: 0.15f);
        Assert.Equal(0, CountBox(rig, Id("cobblestone")));
        RunToDone(rig, collapse);
        Assert.Equal(BoxCells(), CountBox(rig, Id("cobblestone")));
    }

    [Fact]
    public void UnloadedCellsWaitWithoutBlockingTheRest()
    {
        var rig = Setup(0);
        var collapse = Collapse(minX: -4);
        var cobble = Id("cobblestone");
        for (var f = 0; f < 1000 && !collapse.MainPassDone; f++) Frame(rig, collapse);
        Frame(rig, collapse);

        Assert.True(collapse.MainPassDone);
        Assert.Equal(BoxCells(), CountBox(rig, cobble));
        Assert.False(collapse.Done);
        Assert.Equal((-1, 0, 0), Assert.Single(collapse.Waiting));

        LoadStoneChunk(rig.Chunks, -1);
        RunToDone(rig, collapse);
        Assert.Equal(BoxCells(-4), CountBox(rig, cobble, -4));
        Assert.Empty(collapse.Waiting);
    }

    [Fact]
    public void ItResumesFromItsSavedState()
    {
        // An uninterrupted run, the neighbor loading after the main pass
        var straight = Setup(0);
        var whole = Collapse(minX: -4);
        for (var f = 0; f < 1000 && !whole.MainPassDone; f++) Frame(straight, whole);
        LoadStoneChunk(straight.Chunks, -1);
        RunToDone(straight, whole);

        // The same run interrupted after a few frames and resumed from stations.json
        var rig = Setup(0);
        var first = Collapse(minX: -4);
        for (var f = 0; f < 5; f++) Frame(rig, first);
        var data = first.Capture();
        var parsed = StationSaveCodec.TryParse(StationSaveCodec.ToBytes(new StationSaveData
        {
            Towers = [new StationSaveData.TowerData { Cell = [0, 0], Collapse = data }]
        }));
        var restored = parsed!.Towers![0].Collapse!;
        Assert.Equal(data.NextY, restored.NextY);
        Assert.Equal(data.Done, restored.Done);

        var second = Collapse(minX: -4);
        second.Apply(restored);
        Assert.Equal(first.NextY, second.NextY);
        Assert.Equal(first.Waiting.OrderBy(c => c), second.Waiting.OrderBy(c => c));
        for (var f = 0; f < 1000 && !second.MainPassDone; f++) Frame(rig, second);
        LoadStoneChunk(rig.Chunks, -1);
        RunToDone(rig, second);

        for (var y = BottomY; y <= TopY; y++)
        for (var z = MinZ; z <= MaxZ; z++)
        for (var x = -4; x <= MaxX; x++)
            Assert.Equal(straight.Chunks.GetBlockGlobal(x, y, z), rig.Chunks.GetBlockGlobal(x, y, z));
    }

    [Fact]
    public void AFinishedCollapseIsPermanent()
    {
        var rig = Setup(0);
        var collapse = Collapse();
        RunToDone(rig, collapse);

        var restored = Collapse();
        restored.Apply(collapse.Capture());
        Assert.True(restored.Done);

        LoadStoneChunk(rig.Chunks, 0);
        for (var f = 0; f < 10; f++)
        {
            Frame(rig, collapse);
            Frame(rig, restored);
        }
        Assert.Equal(0, CountBox(rig, Id("cobblestone")));
    }

    [Fact]
    public void ItNeverEntombsABody()
    {
        var rig = Setup(0);
        var stone = Id("stone");
        var cobble = Id("cobblestone");
        var body = (X: 3, Y: TopY + 1, Z: 3);
        var collapse = Collapse(topY: TopY + 1, rule: (int x, int y, int z, out byte expected) =>
        {
            if (y <= TopY)
            {
                expected = stone;
                return cobble;
            }
            expected = BlockType.Air;
            return (x, y, z) == body ? cobble : -1;
        });

        for (var f = 0; f < 1000 && !collapse.Done; f++) Frame(rig, collapse, (x, y, z) => (x, y, z) == body);

        Assert.True(collapse.Done);
        Assert.Equal(BlockType.Air, rig.Chunks.GetBlockGlobal(body.X, body.Y, body.Z));
        Assert.Equal(BoxCells(), CountBox(rig, cobble));
    }
}
