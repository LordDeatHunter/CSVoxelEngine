using System;
using System.IO;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class FluidTests
{
    public FluidTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ADisabledManagerSchedulesNothing()
    {
        // A multiplayer client never ticks fluids, so its edits must not grow the schedule
        var disabled = new FluidManager { Enabled = false };
        disabled.ScheduleAround(3, 40, -7);
        disabled.ScheduleBox(new Vector3(0, 0, 0), new Vector3(4, 4, 4));
        Assert.Equal(0, disabled.PendingCells);

        var enabled = new FluidManager();
        enabled.ScheduleAround(3, 40, -7);
        Assert.True(enabled.PendingCells > 0);
    }

    [Fact]
    public void SourceIsFullHeightAndFallingIsToo()
    {
        Assert.True(FluidState.IsSource(FluidState.Source));
        Assert.Equal(8f / 9f, FluidState.SurfaceHeight(FluidState.Source), precision: 5);
        Assert.Equal(1f, FluidState.SurfaceHeight(FluidState.Falling), precision: 5);
        Assert.Equal(0, FluidState.Level(FluidState.Source));
        Assert.Equal(8, FluidState.Amount(FluidState.Falling));
        Assert.Equal(5, FluidState.Amount(FluidState.Flowing(3)));
    }

    // The whole chunk floored: with no hole in slope-search range every direction
    // ties and water spreads evenly, and unloaded neighbours read as walls.
    // A smaller floor has open edges the search correctly prefers (Minecraft getSpread).
    private static ChunkManager FullFloorBasin(out byte water)
    {
        TestContent.EnsureLoaded();
        var manager = new ChunkManager(null!);
        var chunk = new Chunk(null!, Vector3.Zero, generateBlockData: false);
        chunk.IsBlockDataReady = true;
        manager.Chunks[Vector3.Zero] = chunk;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        water = ContentRegistry.RequireBlock("water").Id;
        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
            chunk.SetBlockRaw(x, 0, z, stone, 0);
        return manager;
    }

    [Fact]
    public void SourceSpreadsEvenlyWithNoHoleNearby()
    {
        var manager = FullFloorBasin(out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(8, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(8, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(8, 1, 8, out var kept, out var keptState));
        Assert.Equal(water, kept);
        Assert.True(FluidState.IsSource(keptState));
        foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            Assert.True(manager.TryGetCell(8 + dx, 1, 8 + dz, out var spread, out _));
            Assert.Equal(water, spread);
        }
    }

    [Fact]
    public void SourceFallsDownward()
    {
        var manager = FullFloorBasin(out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(1, 5, 1, water, FluidState.Source);
        fluids.ScheduleAround(1, 5, 1);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(1, 4, 1, out var below, out var state));
        Assert.Equal(water, below);
        Assert.True(FluidState.IsFalling(state));
    }

    // A source sitting on the last column of a chunk, with its +X neighbour in a loaded
    // second chunk: the spread has to read across the border, which is the tick's chunk
    // cache's miss-then-hit path (every other fluid test lives in a single chunk).
    [Fact]
    public void SourceSpreadsAcrossAChunkBorder()
    {
        var manager = new ChunkManager(null!);
        var water = ContentRegistry.RequireBlock("water").Id;
        var stone = ContentRegistry.RequireBlock("stone").Id;
        for (var cx = 0; cx <= 1; cx++)
        {
            var chunk = new Chunk(null!, new Vector3(cx, 0, 0), generateBlockData: false);
            chunk.IsBlockDataReady = true;
            manager.Chunks[new Vector3(cx, 0, 0)] = chunk;
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
                chunk.SetBlockRaw(x, 0, z, stone, 0);
        }

        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(Chunk.SizeMask, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(Chunk.SizeMask, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(Chunk.Size, 1, 8, out var spread, out _));
        Assert.Equal(water, spread);
    }

    [Fact]
    public void TwoAdjacentSourcesBirthANewSource()
    {
        var manager = FullFloorBasin(out var water);
        var fluids = new FluidManager();
        var drops = new DropManager();
        manager.Chunks[Vector3.Zero].SetBlockRaw(2, 1, 8, water, FluidState.Source);
        manager.Chunks[Vector3.Zero].SetBlockRaw(4, 1, 8, water, FluidState.Source);
        fluids.ScheduleAround(2, 1, 8);
        fluids.ScheduleAround(4, 1, 8);

        fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(3, 1, 8, out var middle, out var middleState));
        Assert.Equal(water, middle);
        Assert.True(FluidState.IsSource(middleState));
    }

    [Fact]
    public void WakeLoadedChunksHonoursThePerFrameCap()
    {
        var manager = new ChunkManager(null!);
        const int extra = 3;
        // Chunk y=10 sits above sea level, so the wake skips the border scan and stays cheap
        for (var i = 0; i < WorldGenerationConfig.MaxWakeChunksPerFrame + extra; i++)
            manager.QueueChunk(new Vector3(i, 10, 0));

        new FluidManager().WakeLoadedChunks(manager);

        var remaining = 0;
        while (manager.TryDequeueLoaded(out _)) remaining++;
        Assert.Equal(extra, remaining);
    }

    // --- Border wake of restored (poured) water ---

    private static readonly TimeSpan StoreShutdownTimeout = TimeSpan.FromSeconds(10);

    // The restored chunk and its +X neighbour, both far above every terrain top and water level
    // of their columns: anything fluid up there was poured by a player
    private static readonly Vector3 RestoredPos = MeshTestWorld.HighAbove;
    private static readonly Vector3 GeneratedPos = MeshTestWorld.HighAbove + Vector3.UnitX;
    private const int PouredY = 4;
    private const int PouredZ = 4;

    /// <summary>
    /// A saved chunk holding one poured water source on its +X face, walled in by stone on every
    /// in-chunk side it could flow to (-X, -Z, +Z, below), so its only way out is across the
    /// border: the in-chunk frontier cannot wake it, only the border scan can.
    /// </summary>
    private static ChunkSnapshot EnclosedPouredSource()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var blocks = new byte[Chunk.BlockCount];
        const int x = Chunk.SizeMask;
        blocks[Chunk.Idx(x, PouredY, PouredZ)] = ContentRegistry.RequireBlock("water").Id;
        blocks[Chunk.Idx(x, PouredY - 1, PouredZ)] = stone;
        blocks[Chunk.Idx(x - 1, PouredY, PouredZ)] = stone;
        blocks[Chunk.Idx(x, PouredY, PouredZ - 1)] = stone;
        blocks[Chunk.Idx(x, PouredY, PouredZ + 1)] = stone;
        return new ChunkSnapshot(blocks, state: null, playerPlaced: null);
    }

    /// <summary>
    /// Restores <see cref="EnclosedPouredSource"/> beside a generated all-air chunk, loading the
    /// two in the given order and waking either after each load or once after both, then ticks.
    /// Returns what stands in the cell across the border from the source, and its state.
    /// </summary>
    private static (byte Id, byte State) PouredWaterAcrossBorder(bool restoredFirst, bool wakeAfterEachLoad)
    {
        var world = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var store = new ChunkSaveStore(world);
        try
        {
            store.Enqueue(ChunkKey.Pack(RestoredPos), EnclosedPouredSource());
            var manager = new ChunkManager(null!, saveStore: store);
            var fluids = new FluidManager();
            var drops = new DropManager();

            var order = restoredFirst
                ? new[] { RestoredPos, GeneratedPos }
                : new[] { GeneratedPos, RestoredPos };
            foreach (var pos in order)
            {
                manager.QueueChunk(pos);
                if (wakeAfterEachLoad) fluids.WakeLoadedChunks(manager);
            }

            var restored = manager.Chunks[RestoredPos];
            var generated = manager.Chunks[GeneratedPos];
            Assert.True(restored.IsRestored);
            Assert.False(generated.IsRestored);
            // Preconditions: the source really is poured water (above both its column's terrain
            // top and water level), and the enclosure leaves the border as its only wake path
            var column = ColumnCache.Get((int)RestoredPos.X, (int)RestoredPos.Z);
            Assert.True(restored.LowerBoundY > Math.Max(column.MaxHeight, column.MaxWaterLevel));
            var frontier = restored.FluidFrontier;
            Assert.True(frontier == null
                        || Array.IndexOf(frontier, (ushort)Chunk.Idx(Chunk.SizeMask, PouredY, PouredZ)) < 0);

            if (!wakeAfterEachLoad) fluids.WakeLoadedChunks(manager);
            for (var i = 0; i < 4; i++)
                fluids.Update(1f, manager, drops);

            Assert.True(manager.TryGetCell((int)generated.WorldMin.X, restored.LowerBoundY + PouredY, PouredZ,
                out var id, out var state));
            return (id, state);
        }
        finally
        {
            Assert.True(store.Shutdown(StoreShutdownTimeout), "the writer never finished");
            if (Directory.Exists(world)) Directory.Delete(world, recursive: true);
        }
    }

    private static void AssertFlowingWater((byte Id, byte State) cell)
    {
        Assert.Equal(ContentRegistry.RequireBlock("water").Id, cell.Id);
        Assert.False(FluidState.IsSource(cell.State));
    }

    [Fact]
    public void RestoredPouredWaterWakesAcrossABorderIntoOpenAir() =>
        AssertFlowingWater(PouredWaterAcrossBorder(restoredFirst: true, wakeAfterEachLoad: false));

    [Fact]
    public void RestoredPouredWaterWakesWhenTheGeneratedNeighbourLoadsLater()
    {
        // The restored chunk streams in last: its own border scan finds the loaded neighbour
        AssertFlowingWater(PouredWaterAcrossBorder(restoredFirst: false, wakeAfterEachLoad: true));
        // The generated chunk streams in last: the restored chunk's own wake saw no neighbour,
        // so only the generated side's scan (it borders a restored chunk) can wake the water
        AssertFlowingWater(PouredWaterAcrossBorder(restoredFirst: true, wakeAfterEachLoad: true));
    }

    /// <summary>
    /// A generated sea keeps the carved rule across a border: a source at its own column's water
    /// level does not wake into open air above the neighbour's terrain, while the same pair one
    /// cell higher (the source above both its terrain top and water level, i.e. poured) does, so
    /// the fluid side really is judged against the column that holds it.
    /// </summary>
    [Fact]
    public void GeneratedSeaStillDoesNotWakeIntoARavineAcrossABorder()
    {
        var found = false;
        int cx = 0, cz = 0, t = 0, level = 0;
        for (var radius = 0; radius <= 48 && !found; radius++)
        for (var ax = -radius; ax <= radius && !found; ax++)
        for (var az = -radius; az <= radius && !found; az++)
        {
            if (Math.Max(Math.Abs(ax), Math.Abs(az)) != radius) continue;
            var a = ColumnCache.Get(ax, az);
            if (a.MaxWaterLevel == TerrainGenerator.NoWater) continue;
            var b = ColumnCache.Get(ax + 1, az);
            for (var z = 1; z < Chunk.SizeMask && !found; z++)
            {
                var li = (Chunk.SizeMask << Chunk.SizeShift) | z;
                var y = a.WaterLevel[li];
                if (y == TerrainGenerator.NoWater || y <= a.Heights[li]) continue;
                // Open air above the neighbour's terrain at the water's height
                if (b.Heights[z] >= y) continue;
                // Room in one chunk for a floor under the source and the same pair one higher
                var ly = y & Chunk.SizeMask;
                if (ly < 1 || ly > Chunk.Size - 3) continue;
                (cx, cz, t, level, found) = (ax, az, z, y, true);
            }
        }
        Assert.True(found, "no wet border column beside a lower neighbour found near the origin");

        var water = ContentRegistry.RequireBlock("water").Id;
        Assert.NotEqual(water, SourceAcrossBorder(cx, cz, t, level).Id);
        Assert.Equal(water, SourceAcrossBorder(cx, cz, t, level + 1).Id);
    }

    /// <summary>
    /// Generates the chunk pair at (cx, cz) and (cx + 1, cz) around <paramref name="worldY"/>, sets
    /// a water source on the left chunk's +X face at that height, walled in on every in-chunk side,
    /// and opens the cell across the border into air, walled in on every other side so nothing but
    /// that source can fill it. Wakes, ticks, and returns what the opened cell holds.
    /// </summary>
    private static (byte Id, byte State) SourceAcrossBorder(int cx, int cz, int z, int worldY)
    {
        var manager = new ChunkManager(null!);
        var cy = worldY >> Chunk.SizeShift;
        var leftPos = new Vector3(cx, cy, cz);
        var rightPos = new Vector3(cx + 1, cy, cz);
        manager.QueueChunk(leftPos);
        manager.QueueChunk(rightPos);
        var left = manager.Chunks[leftPos];
        var right = manager.Chunks[rightPos];

        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var ly = worldY & Chunk.SizeMask;
        const int x = Chunk.SizeMask;
        left.SetBlockRaw(x, ly, z, water, FluidState.Source);
        left.SetBlockRaw(x, ly - 1, z, stone, 0);
        left.SetBlockRaw(x - 1, ly, z, stone, 0);
        left.SetBlockRaw(x, ly, z - 1, stone, 0);
        left.SetBlockRaw(x, ly, z + 1, stone, 0);
        left.SetBlockRaw(x, ly + 1, z, stone, 0);

        right.SetBlockRaw(0, ly, z, 0, 0);
        right.SetBlockRaw(1, ly, z, stone, 0);
        right.SetBlockRaw(0, ly, z - 1, stone, 0);
        right.SetBlockRaw(0, ly, z + 1, stone, 0);
        right.SetBlockRaw(0, ly + 1, z, stone, 0);

        var fluids = new FluidManager();
        var drops = new DropManager();
        fluids.WakeLoadedChunks(manager);
        for (var i = 0; i < 4; i++)
            fluids.Update(1f, manager, drops);

        Assert.True(manager.TryGetCell(cx * Chunk.Size + x, worldY, cz * Chunk.Size + z,
            out var kept, out var keptState));
        Assert.Equal(water, kept);
        Assert.True(FluidState.IsSource(keptState));

        Assert.True(manager.TryGetCell((cx + 1) * Chunk.Size, worldY, cz * Chunk.Size + z,
            out var id, out var state));
        return (id, state);
    }
}
