using System;
using System.Linq;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The edit paths outside World that move a column's far-LOD surface — a falling block landing,
/// a TNT crater, lava hardening — record it through <see cref="LodSurface.RecordEdit"/>, so the
/// LOD keeps reflecting the current terrain whichever system made the change.
/// </summary>
/// <remarks>
/// Overrides and caps are process-global (reset only by WorldSeed.Apply, which the parallel suite
/// never calls), so every fact works in its own far level-1 region row: rz 784, 785, 791 and 792,
/// clear of LodColumnCapTests (777), LodSurfaceTests (779..783, 789, 790) and LodEditStateTests
/// (786, 787).
/// </remarks>
[Collection("LOD column caps")]
public sealed class LodEditHookTests
{
    public LodEditHookTests() => TestContent.EnsureLoaded();

    private const int ChunksPerLevel1Region = 8;   // 128 / 16
    private const int CentreChunk = 4;

    private static int LocalIndex(int x, int z) =>
        ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)[LocalIndex(x, z)];

    /// <summary>Generates the vertical chunk stack of chunk column (cx, cz) spanning world y
    /// <paramref name="yMin"/>..<paramref name="yMax"/> through the real factory.</summary>
    private static void QueueStack(ChunkManager manager, int cx, int cz, int yMin, int yMax)
    {
        for (var cy = yMin >> Chunk.SizeShift; cy <= yMax >> Chunk.SizeShift; cy++)
            manager.QueueChunk(new Vector3(cx, cy, cz));
    }

    private static void Set(ChunkManager manager, int x, int y, int z, byte id, byte state = 0)
    {
        var chunk = manager.ChunkAt(x, y, z);
        Assert.NotNull(chunk);
        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, state);
    }

    private static byte BlockAt(ChunkManager manager, int x, int y, int z)
    {
        Assert.True(manager.TryGetCell(x, y, z, out var id, out _), $"({x}, {y}, {z}) is not loaded");
        return id;
    }

    [Fact]
    public void SettledSandRaisesTheLodSurface()
    {
        var (x, z, h) = FindDryColumn(784);
        var chunks = new ChunkManager(null!);
        QueueStack(chunks, x >> Chunk.SizeShift, z >> Chunk.SizeShift, h - 2, h + 8);
        var lod = new LodManager(null!);
        var fluids = new FluidManager();
        var drops = new DropManager();
        var falling = new FallingBlockManager();
        var stone = TerrainGenerator.RockBlock;
        var sand = TerrainGenerator.SandBlock;

        // A 3x3 stone slab one above the surface, so the landing sand has solid ground all round
        // its support and never slides off, and a clear shaft above its centre.
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
        {
            Set(chunks, x + dx, h + 1, z + dz, stone);
            LodSurface.RecordEdit(chunks, x + dx, h + 1, z + dz);
        }
        for (var y = h + 2; y <= h + 6; y++)
            Set(chunks, x, y, z, 0);

        // Sand hanging in the air above the slab is the column's surface until it falls.
        Set(chunks, x, h + 4, z, sand);
        Assert.True(LodSurface.RecordEdit(chunks, x, h + 4, z));
        Assert.True(LodSurface.TryGet(x, z, out var hangingTop, out _));
        Assert.Equal(h + 4, hangingTop);

        Assert.True(falling.TryTopple(chunks, lod, fluids, x, h + 4, z));
        for (var i = 0; i < 600 && falling.Falling.Count > 0; i++)
            falling.Update(1f / 60f, chunks, drops, fluids, lod);
        Assert.Empty(falling.Falling);

        // The sand came to rest on the slab, and the LOD surface followed it down to that cell.
        Assert.Equal(sand, BlockAt(chunks, x, h + 2, z));
        Assert.True(LodSurface.TryGet(x, z, out var top, out var block));
        Assert.Equal(h + 2, top);
        Assert.Equal(sand, block);
    }

    [Fact]
    public void BlastRecordsTheCraterFloor()
    {
        var chunks = new ChunkManager(null!);
        var (x, z, h) = FindDryColumn(785);
        var cx = x >> Chunk.SizeShift;
        var cz = z >> Chunk.SizeShift;
        QueueStack(chunks, cx, cz, h - 24, h + 8);
        Assert.True(LodSurface.IsSurfaceSolid(BlockAt(chunks, x, h, z)), "the column's generated top is no surface");

        var lod = new LodManager(null!);
        var fluids = new FluidManager();
        var drops = new DropManager();
        var falling = new FallingBlockManager();
        var tntBlock = ContentRegistry.RequireBlock("tnt").Id;
        var tnt = new TntManager();

        // A surface blast: the crater's floor becomes the column's LOD surface.
        tnt.Spawn(new Vector3(x, h + 1, z), tntBlock, 0f);
        tnt.Update(1e-4f, chunks, lod, drops, fluids, falling);

        // Walked from the old surface down, so a tree trunk left standing above the crater is
        // never mistaken for its floor.
        var floorY = TopSurfaceSolid(chunks, x, h, z, h - 24);
        Assert.True(floorY < h, "the blast never dug below the surface");
        Assert.True(LodSurface.TryGet(x, z, out var top, out var block), "the crater recorded no surface");
        Assert.Equal(floorY, top);
        Assert.Equal(BlockAt(chunks, x, floorY, z), block);

        // A buried blast leaves the surface standing: the override does not move, but the hole
        // caps the column below it, so an equal cap is refused.
        var bx = x + 2 * Chunk.Size;
        var bh = HeightAt(bx, z);
        QueueStack(chunks, bx >> Chunk.SizeShift, cz, bh - 24, bh + 8);
        var centreY = bh - 12;
        // A cell well inside the blast sphere, directly under its centre
        var dugY = centreY - 3;
        var dugBefore = BlockAt(chunks, bx, dugY, z);
        Assert.True(dugBefore != 0 && !BlockType.IsFluid(dugBefore), "nothing for the buried blast to clear");
        tnt.Spawn(new Vector3(bx, centreY, z), tntBlock, 0f);
        tnt.Update(1e-4f, chunks, lod, drops, fluids, falling);

        Assert.Equal(BlockType.Air, BlockAt(chunks, bx, dugY, z));
        Assert.True(LodSurface.IsSurfaceSolid(BlockAt(chunks, bx, bh, z)), "the buried blast reached the surface");
        Assert.False(LodSurface.TryGet(bx, z, out _, out _), "a buried blast moved the surface override");
        Assert.False(LodRegion.LowerColumnBelow(bx, dugY, z), "the hole did not cap the column");
    }

    [Fact]
    public void HardenedLavaAboveTheSurfaceRaisesTheLod()
    {
        var cx = 777 * ChunksPerLevel1Region + CentreChunk;
        var cz = 791 * ChunksPerLevel1Region + CentreChunk;

        // With the LOD attached, the crust is recorded as the column's new surface.
        var (x, y, z, cobble, withLod) = HardenLavaAbove(cx, cz, attachLod: true);
        Assert.Equal(cobble, BlockAt(withLod, x, y, z));
        Assert.True(LodSurface.TryGet(x, z, out var top, out var block));
        Assert.Equal(y, top);
        Assert.Equal(cobble, block);

        // The same hardening with no LOD attached records nothing.
        var (x2, y2, z2, _, withoutLod) = HardenLavaAbove(cx + 2, cz, attachLod: false);
        Assert.Equal(cobble, BlockAt(withoutLod, x2, y2, z2));
        Assert.False(LodSurface.TryGet(x2, z2, out _, out _));
    }

    [Fact]
    public void BlockWritesUnderTheGeneratedWaterLevelLeaveTheLodAlone()
    {
        const int Rz = 792;
        var cz = Rz * ChunksPerLevel1Region + CentreChunk;
        var cx = 0;
        var waterLevel = 0;
        var found = false;
        for (var rx = 777; rx < 777 + 400 && !found; rx++)
        {
            cx = rx * ChunksPerLevel1Region + CentreChunk;
            var column = ColumnCache.Get(cx, cz);
            waterLevel = column.WaterLevel[LocalIndex(8, 8)];
            // Any generated water will do (sea level is 0, so the chunk under it may sit at
            // negative y), as long as the crust cell of (A) stands above the generated terrain:
            // there an ungated record would raise the override, so (A) proves the gate.
            found = waterLevel != TerrainGenerator.NoWater
                    && (((waterLevel >> Chunk.SizeShift) - 1) << Chunk.SizeShift) + 1 > column.Heights[LocalIndex(8, 8)];
        }
        Assert.True(found, $"no chunk column in row {Rz} whose centre column stands under generated water");

        // (A) The whole chunk sits at or below the generated level: the lava still hardens, but
        // the crust is on the bed, so the far LOD keeps its generated depth band.
        var (x, y, z, cobble, under) = HardenLavaAt(cx, cz, (waterLevel >> Chunk.SizeShift) - 1, attachLod: true);
        Assert.True(y <= waterLevel);
        Assert.True(y > HeightAt(x, z), "the crust is not above the generated terrain");
        Assert.Equal(cobble, BlockAt(under, x, y, z));
        Assert.False(LodSurface.TryGet(x, z, out _, out _), "a crust under the water moved the LOD");

        // (B) The same column above both the terrain and the water line records the crust.
        var heights = ColumnCache.GetHeights(cx, cz);
        var aboveCy = (Math.Max(heights.Max(), waterLevel) >> Chunk.SizeShift) + 3;
        var (x2, y2, z2, _, above) = HardenLavaAt(cx, cz, aboveCy, attachLod: true);
        Assert.True(y2 > waterLevel);
        Assert.Equal(cobble, BlockAt(above, x2, y2, z2));
        Assert.True(LodSurface.TryGet(x2, z2, out var top, out var block), "a crust above the water was not recorded");
        Assert.Equal(y2, top);
        Assert.Equal(cobble, block);
    }

    /// <summary>
    /// A lava source beside a water source on a stone floor in an empty chunk high above chunk
    /// column (cx, cz)'s terrain, ticked until the lava crusts over.
    /// </summary>
    private static (int X, int Y, int Z, byte Cobble, ChunkManager Chunks) HardenLavaAbove(
        int cx, int cz, bool attachLod)
    {
        var cy = (ColumnCache.GetHeights(cx, cz).Max() >> Chunk.SizeShift) + 3;
        var result = HardenLavaAt(cx, cz, cy, attachLod);
        Assert.True(result.Y > HeightAt(result.X, result.Z), "the chunk is not above the terrain");
        return result;
    }

    /// <summary>
    /// A lava source beside a water source on a stone floor in an empty chunk at chunk
    /// (cx, <paramref name="cy"/>, cz), ticked until the lava crusts over. The crust cell is at
    /// local (8, 1, 8); callers assert how it sits against the generated terrain and water.
    /// </summary>
    private static (int X, int Y, int Z, byte Cobble, ChunkManager Chunks) HardenLavaAt(
        int cx, int cz, int cy, bool attachLod)
    {
        var pos = new Vector3(cx, cy, cz);
        var chunks = new ChunkManager(null!);
        var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
        chunks.Chunks[pos] = chunk;

        var stone = TerrainGenerator.RockBlock;
        var lava = ContentRegistry.RequireBlock("lava").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var cobble = ContentRegistry.RequireBlock("cobblestone").Id;
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var lz = 0; lz < Chunk.Size; lz++)
            chunk.SetBlockRaw(lx, 0, lz, stone, 0);
        chunk.SetBlockRaw(8, 1, 8, lava, FluidState.Source);
        chunk.SetBlockRaw(9, 1, 8, water, FluidState.Source);

        var x = (cx << Chunk.SizeShift) + 8;
        var y = chunk.LowerBoundY + 1;
        var z = (cz << Chunk.SizeShift) + 8;

        var fluids = new FluidManager();
        var drops = new DropManager();
        var lod = attachLod ? new LodManager(null!) : null;
        fluids.ScheduleAround(x, y, z);
        fluids.ScheduleAround(x + 1, y, z);
        for (var i = 0; i < 8 && BlockAt(chunks, x, y, z) != cobble; i++)
            fluids.Update(WorldGenerationConfig.FluidTickSeconds, chunks, drops, lod);

        return (x, y, z, cobble, chunks);
    }

    /// <summary>
    /// A column near the centre of a level-1 region in row <paramref name="rz"/> (walking east
    /// from 777) whose whole blast neighbourhood is dry land, so no fluid is left standing in
    /// the crater, and whose neighbor two chunks east is dry too, for the buried blast.
    /// </summary>
    private static (int X, int Z, int Height) FindDryColumn(int rz)
    {
        for (var rx = 777; rx < 777 + 400; rx++)
        {
            var cx = rx * ChunksPerLevel1Region + CentreChunk;
            var cz = rz * ChunksPerLevel1Region + CentreChunk;
            if (!IsDryChunkColumn(cx, cz) || !IsDryChunkColumn(cx + 2, cz)) continue;
            var x = (cx << Chunk.SizeShift) + 8;
            var z = (cz << Chunk.SizeShift) + 8;
            return (x, z, HeightAt(x, z));
        }

        Assert.Fail($"no dry level-1 region in row {rz}");
        return default;
    }

    private static bool IsDryChunkColumn(int cx, int cz)
    {
        var column = ColumnCache.Get(cx, cz);
        for (var i = 0; i < column.Heights.Length; i++)
            if (column.WaterLevel[i] >= column.Heights[i])
                return false;
        return true;
    }

    /// <summary>The topmost full, solid, occluding cell of the column between
    /// <paramref name="yTop"/> and <paramref name="yBottom"/>: an oracle independent of LodSurface.</summary>
    private static int TopSurfaceSolid(ChunkManager chunks, int x, int yTop, int z, int yBottom)
    {
        for (var y = yTop; y >= yBottom; y--)
        {
            var id = BlockAt(chunks, x, y, z);
            if (BlockType.IsSolid(id) && BlockType.Occludes(id)) return y;
        }

        Assert.Fail($"no solid cell in column ({x}, {z}) down to {yBottom}");
        return 0;
    }
}
