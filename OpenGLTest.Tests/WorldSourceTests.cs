using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The <see cref="IWorldSource"/> seam behind <see cref="ChunkManager.CreateChunk"/>: the default
/// <see cref="GeneratedWorldSource"/> fills a chunk exactly as worldgen always did, a source that
/// supplies columns of its own has its blocks and columns used there and the fallback everywhere
/// else, and a chunk the save store holds beats whatever the source would fill. The live world's
/// column readers (the LOD edit hooks, the fluid wake, the ground height and the spawn settle) judge
/// a supplied column by the source's own terrain, never by worldgen's.
/// </summary>
[Collection("Worldgen seed")]
public sealed class WorldSourceTests : IDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(10);

    // How far above worldgen's tallest column the test plateaus stand, so no generated column could
    // ever hold the same terrain.
    private const int PlateauLift = 24;

    private readonly string _world = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public WorldSourceTests()
    {
        TestContent.EnsureLoaded();
        WorldSeed.Apply(WorldSeed.Default);
    }

    public void Dispose()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
        catch (InvalidOperationException)
        {
        }

        if (Directory.Exists(_world))
            Directory.Delete(_world, recursive: true);
    }

    private static byte Block(string name) => ContentRegistry.RequireBlock(name).Id;

    // A plateau over chunk columns [min, max] x [min, max], PlateauLift above worldgen's tallest
    // column there.
    private static FlatWorldSource Plateau(int min, int max)
    {
        var tallest = int.MinValue;
        for (var cx = min; cx <= max; cx++)
        for (var cz = min; cz <= max; cz++)
            tallest = Math.Max(tallest, ColumnCache.Get(cx, cz).MaxHeight);

        return new FlatWorldSource(min, max, min, max, tallest + PlateauLift, Block("sandstone"), Biome.Desert,
            warmth: 0.95f, wetness: 0.05f);
    }

    private static void AssertSameCells(Chunk expected, Chunk actual)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            Assert.True(expected.GetLocalBlock(x, y, z) == actual.GetLocalBlock(x, y, z),
                $"block ({x}, {y}, {z}) of {expected.Position} differs");
            Assert.True(expected.GetLocalState(x, y, z) == actual.GetLocalState(x, y, z),
                $"state ({x}, {y}, {z}) of {expected.Position} differs");
        }
    }

    // Every cell at or below the plateau top is the plateau block and every cell above it air.
    private static void AssertPlateau(Chunk chunk, FlatWorldSource source, byte block)
    {
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var expected = chunk.LowerBoundY + y <= source.Top ? block : BlockType.Air;
            Assert.True(expected == chunk.GetLocalBlock(x, y, z),
                $"cell ({x}, {y}, {z}) of {chunk.Position} is not the plateau");
        }
    }

    private static void ShutdownOrFail(ChunkSaveStore store) =>
        Assert.True(store.Shutdown(ShutdownTimeout), "the writer never finished");

    [Fact]
    public void TheGeneratedSourceFillsAChunkExactlyAsWorldgenDoes()
    {
        var column = ColumnCache.Get(0, 0);
        var surface = column.MaxHeight >> Chunk.SizeShift;
        Vector3[] positions =
        [
            new(0, surface, 0),
            new(0, WorldGenerationConfig.SeaLevel >> Chunk.SizeShift, 0),
            new(0, surface - 2, 0),
        ];

        foreach (var pos in positions)
        {
            var generated = new Chunk(null!, pos, generateBlockData: true);
            var filled = new Chunk(null!, pos, generateBlockData: false);
            Assert.False(filled.IsBlockDataReady);
            GeneratedWorldSource.Instance.Fill(filled);

            AssertSameCells(generated, filled);
            Assert.Equal(generated.SurfaceMinY, filled.SurfaceMinY);
            Assert.True(filled.IsBlockDataReady);
            Assert.False(filled.IsRestored);
            Assert.Equal(generated.FluidFrontier, filled.FluidFrontier);
        }

        // The column is the cache's own instance, and the default source supplies nothing itself
        for (var cx = -2; cx <= 2; cx++)
        for (var cz = -2; cz <= 2; cz++)
        {
            Assert.Same(ColumnCache.Get(cx, cz), GeneratedWorldSource.Instance.GetColumn(cx, cz));
            Assert.False(GeneratedWorldSource.Instance.Supplies(cx, cz));
        }
    }

    [Fact]
    public void ACustomSourceSuppliesItsColumnsAndChunksAndTheFallbackTheRest()
    {
        var source = Plateau(1, 2);
        var sandstone = Block("sandstone");
        var manager = new ChunkManager(null!) { WorldSource = source };

        // Inside: the top chunk of the plateau and one below it come from the source
        var topCy = source.Top >> Chunk.SizeShift;
        foreach (var pos in new Vector3[] { new(1, topCy, 2), new(2, topCy - 1, 1) })
        {
            var chunk = manager.CreateChunk(pos, ChunkKey.Pack(pos));
            Assert.True(chunk.IsBlockDataReady);
            Assert.True(chunk.IsRestored);
            Assert.False(chunk.ModifiedSinceSave);
            Assert.Equal(source.Top, chunk.SurfaceMinY);
            AssertPlateau(chunk, source, sandstone);
        }

        var supplied = manager.WorldSource.GetColumn(1, 1);
        Assert.All(supplied.Heights, h => Assert.Equal(source.Top, h));

        // Outside: exactly worldgen, over the cache's own column
        var outside = new Vector3(0, ColumnCache.Get(0, 0).MaxHeight >> Chunk.SizeShift, 0);
        var fallback = manager.CreateChunk(outside, ChunkKey.Pack(outside));
        var generated = new Chunk(null!, outside, generateBlockData: true);
        AssertSameCells(generated, fallback);
        Assert.False(fallback.IsRestored);
        Assert.Equal(generated.SurfaceMinY, fallback.SurfaceMinY);
        Assert.Same(ColumnCache.Get(3, 0), manager.WorldSource.GetColumn(3, 0));
        Assert.Same(ColumnCache.Get(0, 0), manager.WorldSource.GetColumn(0, 0));
    }

    [Fact]
    public void TheSaveStoreWinsOverTheSource()
    {
        var source = Plateau(1, 1);
        var sandstone = Block("sandstone");
        var topCy = source.Top >> Chunk.SizeShift;
        var ly = source.Top & Chunk.SizeMask;
        var saved = new Vector3(1, topCy, 1);
        var unsaved = new Vector3(1, topCy - 1, 1);

        var store = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: store) { WorldSource = source };
            manager.QueueChunk(saved);
            var chunk = manager.Chunks[saved];
            Assert.Equal(sandstone, chunk.GetLocalBlock(3, ly, 5));
            chunk.SetBlockRaw(3, ly, 5, BlockType.Air, 0);
            Assert.True(manager.FlushModified(int.MaxValue) > 0);
        }
        finally
        {
            ShutdownOrFail(store);
        }

        var reopened = new ChunkSaveStore(_world);
        try
        {
            var manager = new ChunkManager(null!, saveStore: reopened) { WorldSource = source };

            // The saved chunk carries the edit, which the source would have filled as plateau
            var restored = manager.CreateChunk(saved, ChunkKey.Pack(saved));
            Assert.True(restored.IsRestored);
            Assert.Equal(BlockType.Air, restored.GetLocalBlock(3, ly, 5));
            Assert.Equal(sandstone, restored.GetLocalBlock(4, ly, 5));

            // A chunk of the same column the store never held is the source's
            var fromSource = manager.CreateChunk(unsaved, ChunkKey.Pack(unsaved));
            AssertPlateau(fromSource, source, sandstone);
        }
        finally
        {
            ShutdownOrFail(reopened);
        }
    }

    [Fact]
    public void ALodEditReadsTheSourcesSurface()
    {
        var source = Plateau(3, 3);
        var plateau = source.Top;
        const int lx = 5;
        const int lz = 9;
        var x = 3 * Chunk.Size + lx;
        var z = 3 * Chunk.Size + lz;

        // With no override, the top is the source's height: the import's in the supplied column,
        // worldgen's in the generated column beside it
        Assert.Equal(plateau, LodSurface.TopOrHeight(source, x, z));
        Assert.Equal(ColumnCache.GetHeights(2, 3)[VoxelCoords.ColumnIndex(lx, lz)],
            LodSurface.TopOrHeight(source, 2 * Chunk.Size + lx, z));

        // A dig three cells under the import's surface caps the column; judged against worldgen's
        // surface, far below it, the same dig would be a removal above the LOD terrain
        Assert.False(LodRegion.LowerColumnBelow(x, plateau - 3, z), "worldgen's surface reached the plateau");
        Assert.False(LodRegion.LowerColumnBelow(source, x, plateau + 5, z));
        Assert.True(LodRegion.LowerColumnBelow(source, x, plateau - 3, z));

        // The edit hook reads the manager's source: mining into the loaded plateau caps the column
        var manager = new ChunkManager(null!) { WorldSource = source };
        var dugX = x + 1;
        var dugY = plateau - 3;
        manager.QueueChunk(new Vector3(3, dugY >> Chunk.SizeShift, 3));
        var (dlx, dly, dlz) = VoxelCoords.LocalOf(dugX, dugY, z);
        manager.ChunkAt(dugX, dugY, z)!.SetBlockRaw(dlx, dly, dlz, BlockType.Air, 0);
        Assert.True(LodSurface.RecordEdit(manager, dugX, dugY, z), "a dig under the import's surface was not capped");
    }

    // The top of a flooded plateau over chunk columns [1, 2] x [1, 2], PlateauLift above worldgen's
    // tallest column there and in column (3, 1) beside it, chosen so the top cell and the water cell
    // over it share a chunk.
    private static int WetPlateauTop()
    {
        var tallest = int.MinValue;
        for (var cx = 1; cx <= 3; cx++)
        for (var cz = 1; cz <= 2; cz++)
            tallest = Math.Max(tallest, ColumnCache.Get(cx, cz).MaxHeight);

        var top = tallest + PlateauLift;
        return (top & Chunk.SizeMask) == Chunk.SizeMask ? top + 1 : top;
    }

    private static FlatWorldSource WetPlateau(int top, IReadOnlySet<(int X, int Y, int Z)>? carved = null) =>
        new(1, 2, 1, 2, top, Block("sandstone"), Biome.Forest, warmth: 0.5f, wetness: 0.5f,
            waterLevel: top + 4, carved: carved);

    [Fact]
    public void ImportedWaterOverItsOwnBedWakesNothingButACarvedCellUnderItWakes()
    {
        var top = WetPlateauTop();
        var cy = top >> Chunk.SizeShift;
        var import = new Vector3(2, cy, 1);
        var beside = new Vector3(3, cy, 1);

        // Judged against worldgen's columns, the import's water would stand above both the terrain
        // and the water level of its column, i.e. read as poured water, and wake into the open air
        // of the generated column beside it
        const int faceZ = 7;
        var worldgenWater = ColumnCache.Get(2, 1);
        var worldgenBeside = ColumnCache.Get(3, 1);
        var waterIndex = VoxelCoords.ColumnIndex(Chunk.SizeMask, faceZ);
        Assert.True(FluidManager.IsWakeTarget(BlockType.Air, top + 1,
            worldgenBeside.Heights[VoxelCoords.ColumnIndex(0, faceZ)],
            top + 1, worldgenWater.Heights[waterIndex], worldgenWater.WaterLevel[waterIndex]));

        // Static water over its own bed: neither the interior nor the face it shares with the
        // generated chunk beside it wakes, because the import's own column holds the water
        var still = new ChunkManager(null!) { WorldSource = WetPlateau(top) };
        still.QueueChunk(import);
        still.QueueChunk(beside);
        var open = 0;
        for (var z = 0; z < Chunk.Size; z++)
        {
            Assert.Equal(TerrainGenerator.SeaBlock, still.GetBlockGlobal(3 * Chunk.Size - 1, top + 1, Chunk.Size + z));
            if (still.GetBlockGlobal(3 * Chunk.Size, top + 1, Chunk.Size + z) == BlockType.Air) open++;
        }
        Assert.True(open > 0, "the generated chunk beside the import had no open air on the shared face");

        var stillFluids = new FluidManager();
        stillFluids.WakeLoadedChunks(still);
        Assert.Equal(0, stillFluids.PendingCells);

        // One cell carved out of the plateau top under the water: the water over it wakes, judged
        // against the import's heights (the hole is under its terrain top)
        var hole = (2 * Chunk.Size + 5, top, Chunk.Size + 7);
        var breached = new ChunkManager(null!) { WorldSource = WetPlateau(top, new HashSet<(int, int, int)> { hole }) };
        breached.QueueChunk(import);
        Assert.Equal(BlockType.Air, breached.GetBlockGlobal(hole.Item1, hole.Item2, hole.Item3));
        var breachedFluids = new FluidManager();
        breachedFluids.WakeLoadedChunks(breached);
        Assert.True(breachedFluids.PendingCells > 0, "the water over the carved cell never woke");
    }

    [Fact]
    public void TheGroundHeightAndSpawnFollowTheSource()
    {
        var source = Plateau(-2, 2);
        var sandstone = Block("sandstone");
        var world = new World(null!, null!, null!, null!, null!, null!, null, null, serverMode: true,
            worldSource: source);
        try
        {
            var manager = world.GetChunkManager();
            Assert.Same(source, manager.WorldSource);
            Assert.Same(source, manager.ColumnSource);

            var sx = (int)MathF.Floor(world.Camera.Position.X);
            var sz = (int)MathF.Floor(world.Camera.Position.Z);

            // Pumped until the spawn column is loaded from the plateau top up past the reach the
            // settle scans from, and then until the eye has stopped moving for a while
            bool ScanLoaded() =>
                manager.TryGetCell(sx, source.Top, sz, out _, out _)
                && manager.TryGetCell(sx, source.Top + TreeGenerator.ReachUp + 3, sz, out _, out _);

            var sw = Stopwatch.StartNew();
            var previousY = float.NaN;
            var stableFrames = 0;
            while (stableFrames < 10 && sw.Elapsed.TotalSeconds < 30)
            {
                world.Update(0.05, null);
                var y = world.Camera.Position.Y;
                stableFrames = ScanLoaded() && y == previousY ? stableFrames + 1 : 0;
                previousY = y;
                Thread.Sleep(1);
            }

            Assert.True(stableFrames >= 10, "the spawn never settled over the import");

            Assert.True(world.TryGroundHeight(0, 0, out var ground));
            Assert.Equal(source.Top, ground);

            // The eye stands on the import's surface, not on worldgen's far below it
            var eyeY = world.Camera.Position.Y;
            Assert.True(eyeY > source.Top);
            var feet = (int)MathF.Floor(eyeY - Camera.EyeHeight);
            Assert.Equal(sandstone, manager.GetBlockGlobal(sx, feet - 1, sz));
            Assert.Equal(BlockType.Air, manager.GetBlockGlobal(sx, feet, sz));
        }
        finally
        {
            world.SaveAndDispose();
        }
    }
}
