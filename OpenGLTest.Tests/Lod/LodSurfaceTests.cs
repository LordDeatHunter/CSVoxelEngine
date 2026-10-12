using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>
/// The LOD reflects the current terrain: <see cref="LodSurface"/> overrides record an edited
/// column's actual top (raised by a placement, swapped by a replacement, rescanned after the top
/// is removed), and <see cref="LodRegion.BuildMeshData"/> feeds them in before erosion so a raised
/// structure shows at the finer levels while every level still nests under the next finer one.
/// </summary>
/// <remarks>
/// Overrides and caps are process-global (reset only by WorldSeed.Apply, which the parallel suite
/// never calls), so every fact here works in its own far level-1 region row: rz 779..783, 789 and 790,
/// clear of LodColumnCapTests (rz 777) and every other test's regions.
/// </remarks>
[Collection("LOD column caps")]
public sealed class LodSurfaceTests
{
    public LodSurfaceTests() => TestContent.EnsureLoaded();

    private const int ChunksPerLevel1Region = 8;   // 128 / 16
    private const int CentreChunk = 4;

    private static int HeightAt(int x, int z) =>
        ColumnCache.GetHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];

    /// <summary>
    /// The first level-1 region in row <paramref name="rz"/>, walking east from 777, that is frozen
    /// at its centre chunk column and grows no canopy anywhere near it: frozen land carries none,
    /// and a canopy bump would stand over the ground the edits move (LodColumnCapTests' recipe).
    /// </summary>
    internal static int FindQuietFrozenRx(int rz)
    {
        var spec = LodRegion.Levels[1];
        for (var rx = 777; rx < 777 + 400; rx++)
        {
            var x0 = rx * spec.RegionBlocks + CentreChunk * Chunk.Size;
            var z0 = rz * spec.RegionBlocks + CentreChunk * Chunk.Size;
            if (TerrainGenerator.BiomeAt(x0, z0) != Biome.Frozen) continue;

            var quiet = true;
            for (var x = x0 - 32; x < x0 + 48 && quiet; x++)
            for (var z = z0 - 32; z < z0 + 48; z++)
                if (TreeGenerator.TryGetCanopy(x, z, out _, out _))
                {
                    quiet = false;
                    break;
                }

            if (quiet) return rx;
        }

        Assert.Fail($"no quiet frozen level-1 region in row {rz}");
        return 0;
    }

    /// <summary>
    /// The Y of the opaque +Y quad whose XZ box strictly contains the world point, i.e. the drawn
    /// LOD surface height there.
    /// </summary>
    internal static float TopFaceYAt(LodMeshData data, float worldX, float worldZ)
    {
        var size = LodRegion.Levels[data.Level].RegionBlocks;
        var lx = worldX - data.X * size;
        var lz = worldZ - data.Z * size;
        for (var q = 0; q + 3 < data.Vertices.Count; q += 4)
        {
            var v0 = data.Vertices[q];
            if ((v0.Normal & 7) != 4 || v0.PackedColor >> 24 != 255) continue;

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (var c = 0; c < 4; c++)
            {
                var p = data.Vertices[q + c].Position;
                minX = MathF.Min(minX, p.X);
                maxX = MathF.Max(maxX, p.X);
                minZ = MathF.Min(minZ, p.Z);
                maxZ = MathF.Max(maxZ, p.Z);
            }

            if (lx > minX && lx < maxX && lz > minZ && lz < maxZ)
                return v0.Position.Y;
        }

        Assert.Fail($"no top face over ({worldX}, {worldZ}) in level {data.Level} region ({data.X}, {data.Z})");
        return 0f;
    }

    private static (ChunkManager Manager, Chunk Chunk) EmptyReadyChunk(Vector3 pos, ChunkManager? manager = null)
    {
        manager ??= new ChunkManager(null!);
        var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
        manager.Chunks[pos] = chunk;
        return (manager, chunk);
    }

    private sealed record Plateau(int Rx, int Rz, int Cx, int Cz, int Y, bool[] Recorded, ChunkManager Manager);

    /// <summary>
    /// Stone over the whole 16x16 centre chunk column of a quiet frozen level-1 region, eight
    /// blocks above its highest terrain column, each column recorded through
    /// <see cref="LodSurface.RecordEdit"/>. That covers the level-1 windows of two cells and six
    /// level-0 windows completely, and no 22-wide level-2 window.
    /// </summary>
    private static Plateau BuildPlateau(int rz, Action<int, int>? beforeRecord = null)
    {
        var rx = FindQuietFrozenRx(rz);
        var cx = rx * ChunksPerLevel1Region + CentreChunk;
        var cz = rz * ChunksPerLevel1Region + CentreChunk;
        var y = ColumnCache.GetHeights(cx, cz).Max() + 8;

        var (manager, chunk) = EmptyReadyChunk(new Vector3(cx, y >> Chunk.SizeShift, cz));
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var lz = 0; lz < Chunk.Size; lz++)
            chunk.SetBlockRaw(lx, y & Chunk.SizeMask, lz, TerrainGenerator.RockBlock, 0);

        beforeRecord?.Invoke(rx, rz);

        var recorded = new bool[Chunk.Size * Chunk.Size];
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var lz = 0; lz < Chunk.Size; lz++)
            recorded[lx * Chunk.Size + lz] =
                LodSurface.RecordEdit(manager, (cx << Chunk.SizeShift) + lx, y, (cz << Chunk.SizeShift) + lz);

        return new Plateau(rx, rz, cx, cz, y, recorded, manager);
    }

    private static LodMeshData[] BuildAllLevels(int rx, int rz, bool separableMin = true) =>
    [
        LodRegion.BuildMeshData(0, 2 * rx + 1, 2 * rz + 1, null, separableMin),
        LodRegion.BuildMeshData(1, rx, rz, null, separableMin),
        LodRegion.BuildMeshData(2, rx >> 1, rz >> 1, null, separableMin),
    ];

    [Fact]
    public void RaisedChunkColumnRaisesFinerLevelsAndStaysNested()
    {
        LodMeshData[]? before = null;
        var plateau = BuildPlateau(779, (rx, rz) => before = BuildAllLevels(rx, rz));
        Assert.All(plateau.Recorded, Assert.True);

        var after = BuildAllLevels(plateau.Rx, plateau.Rz);

        // The chunk column's centre, half a block off the cell grid so exactly one quad contains it.
        var px = (plateau.Cx << Chunk.SizeShift) + 8.5f;
        var pz = (plateau.Cz << Chunk.SizeShift) + 8.5f;
        var b = before!.Select(d => TopFaceYAt(d, px, pz)).ToArray();
        var a = after.Select(d => TopFaceYAt(d, px, pz)).ToArray();

        // The fully covered windows rise at levels 0 and 1...
        Assert.True(a[0] > b[0], $"level 0 stayed at {b[0]}");
        Assert.True(a[1] > b[1], $"level 1 stayed at {b[1]}");
        // ...level 2's window reaches past the plateau, so erosion can only keep or lift it...
        Assert.True(a[2] >= b[2]);
        // ...and the shells still nest: coarser never above finer, nothing above the real surface.
        Assert.True(a[2] <= a[1] && a[1] <= a[0], $"levels out of order: {a[0]}, {a[1]}, {a[2]}");
        Assert.True(a[0] <= plateau.Y + 1, $"level 0 at {a[0]} stands above the plateau top {plateau.Y + 1}");

        // The raised structure is covered by the chunk-Y range the burial test demands.
        Assert.True(after[1].ColumnCyMax[CentreChunk * ChunksPerLevel1Region + CentreChunk]
                    >= plateau.Y >> Chunk.SizeShift);
    }

    [Fact]
    public void SeparableMinMatchesTheNestedWindowUnderOverrides()
    {
        // Its own row, so it never depends on another fact having built the plateau first. A cap
        // under one plateau column also exercises the override-then-cap order in both paths.
        LodMeshData? baseline = null;
        var plateau = BuildPlateau(790, (rx, rz) => baseline = LodRegion.BuildMeshData(1, rx, rz));
        var capX = (plateau.Cx << Chunk.SizeShift) + 3;
        var capZ = (plateau.Cz << Chunk.SizeShift) + 3;
        Assert.True(LodRegion.LowerColumnBelow(capX, HeightAt(capX, capZ) - 30, capZ));

        var separable = BuildAllLevels(plateau.Rx, plateau.Rz);
        var nested = BuildAllLevels(plateau.Rx, plateau.Rz, separableMin: false);
        for (var level = 0; level < separable.Length; level++)
            AssertSameMesh(nested[level], separable[level]);

        // Positive control: the edits moved the output, so the equality compares edited builds.
        Assert.False(baseline!.Vertices.SequenceEqual(separable[1].Vertices));
    }

    [Fact]
    public void ALoneBlockNeverRepaintsOrDeletesWater()
    {
        // (a) One block on one column. The column is chosen so it is never the unique minimum of a
        // level-1 window: raising it then moves no eroded height, and the tile rule must keep its
        // stone off the cell too, because the raised column is not the drawn surface.
        const int Rz = 780;
        var spec = LodRegion.Levels[1];
        var rx = FindQuietFrozenRx(Rz);
        var cx = rx * ChunksPerLevel1Region + CentreChunk;
        var cz = Rz * ChunksPerLevel1Region + CentreChunk;
        var window = spec.CellSize + 2 * spec.SampleMargin;
        var (x, z) = FindColumnNeverUniqueWindowMin(cx, cz, window);
        var y = HeightAt(x, z) + 1;

        var baseline = LodRegion.BuildMeshData(1, rx, Rz);
        var (manager, chunk) = EmptyReadyChunk(new Vector3(cx, y >> Chunk.SizeShift, cz));
        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, TerrainGenerator.RockBlock, 0);
        Assert.True(LodSurface.RecordEdit(manager, x, y, z));

        var edited = LodRegion.BuildMeshData(1, rx, Rz);
        Assert.True(edited.Vertices.SequenceEqual(baseline.Vertices), "a lone block changed the shell");
        Assert.True(edited.ColumnCyMax[CentreChunk * ChunksPerLevel1Region + CentreChunk] >= y >> Chunk.SizeShift);

        // (b) A whole chunk column raised above the water in a sea region: the shell moves, but the
        // sheet is decided from the generated heights and must survive untouched.
        const int SeaRz = 781;
        var (seaRx, seaBaseline) = FindSeaRegion(SeaRz);
        var scx = seaRx * ChunksPerLevel1Region + CentreChunk;
        var scz = SeaRz * ChunksPerLevel1Region + CentreChunk;
        var column = ColumnCache.Get(scx, scz);
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var lz = 0; lz < Chunk.Size; lz++)
        {
            var i = (lx << Chunk.SizeShift) | lz;
            var surface = Math.Max(column.Heights[i], column.WaterLevel[i]);
            LodSurface.Set((scx << Chunk.SizeShift) + lx, (scz << Chunk.SizeShift) + lz, surface + 8,
                TerrainGenerator.RockBlock);
        }

        var seaEdited = LodRegion.BuildMeshData(1, seaRx, SeaRz);
        Assert.True(seaEdited.WaterVertices.SequenceEqual(seaBaseline.WaterVertices), "an edit deleted LOD water");
        Assert.True(seaEdited.WaterIndices.SequenceEqual(seaBaseline.WaterIndices));
        Assert.False(seaEdited.Vertices.SequenceEqual(seaBaseline.Vertices), "the overrides never reached the shell");
    }

    [Fact]
    public void PlacementAboveRaisesReplacementSwapsAndRemovalRescans()
    {
        var cx = 777 * ChunksPerLevel1Region + CentreChunk;
        var cz = 782 * ChunksPerLevel1Region + CentreChunk;
        var manager = new ChunkManager(null!);
        var heights = ColumnCache.GetHeights(cx, cz);
        var cyMin = (heights.Min() - 2) >> Chunk.SizeShift;
        var cyMax = (heights.Max() + 8) >> Chunk.SizeShift;
        for (var cy = cyMin; cy <= cyMax; cy++)
            manager.QueueChunk(new Vector3(cx, cy, cz));

        // A column whose generated top really is surface-solid, so the rescan in (3) lands inside
        // the loaded stack and (5) has a cell below the top to dig.
        var x = 0;
        var z = 0;
        var h = 0;
        var found = false;
        for (var i = 0; i < heights.Length && !found; i++)
        {
            x = (cx << Chunk.SizeShift) + (i >> Chunk.SizeShift);
            z = (cz << Chunk.SizeShift) + (i & Chunk.SizeMask);
            h = heights[i];
            found = manager.TryGetCell(x, h, z, out var topId, out _) && LodSurface.IsSurfaceSolid(topId);
        }
        Assert.True(found, "no column with a surface-solid generated top in the chunk column");

        var stone = TerrainGenerator.RockBlock;
        var sand = TerrainGenerator.SandBlock;

        // (1) A floating block above the top raises the override to it.
        Set(manager, x, h + 3, z, stone);
        Assert.True(LodSurface.RecordEdit(manager, x, h + 3, z));
        AssertOverride(x, z, h + 3, stone);

        // (2) Replacing that top swaps the block and keeps the height.
        Set(manager, x, h + 3, z, sand);
        Assert.True(LodSurface.RecordEdit(manager, x, h + 3, z));
        AssertOverride(x, z, h + 3, sand);

        // Recording the same replacement again neither reports a change nor bumps the version.
        var version = LodSurface.EditVersion;
        Assert.False(LodSurface.RecordEdit(manager, x, h + 3, z));
        Assert.Equal(version, LodSurface.EditVersion);

        // (3) Removing it rescans down to the real surface, found here by an independent walk.
        Set(manager, x, h + 3, z, 0);
        Assert.True(LodSurface.RecordEdit(manager, x, h + 3, z));
        var oracleY = h + 2;
        byte oracleId;
        while (!(manager.TryGetCell(x, oracleY, z, out oracleId, out _)
                 && BlockType.IsSolid(oracleId) && BlockType.Occludes(oracleId)))
            oracleY--;
        AssertOverride(x, z, oracleY, oracleId);

        // The same removal recorded twice changes nothing the second time.
        version = LodSurface.EditVersion;
        Assert.False(LodSurface.RecordEdit(manager, x, h + 3, z));
        Assert.Equal(version, LodSurface.EditVersion);
        AssertOverride(x, z, oracleY, oracleId);

        // (4) A non-solid block above the top is not a surface.
        var flower = ContentRegistry.RequireBlock("flower").Id;
        Set(manager, x, oracleY + 2, z, flower);
        Assert.False(LodSurface.RecordEdit(manager, x, oracleY + 2, z));
        AssertOverride(x, z, oracleY, oracleId);

        // (5) A dig strictly below the top goes to the removal cap, not the override: the cap it
        // wrote stands (an equal cap is refused), and the override does not move.
        Set(manager, x, oracleY - 1, z, 0);
        Assert.True(LodSurface.RecordEdit(manager, x, oracleY - 1, z));
        Assert.False(LodRegion.LowerColumnBelow(x, oracleY - 1, z));
        AssertOverride(x, z, oracleY, oracleId);
    }

    [Fact]
    public void SetOfAnIdenticalOverrideIsANoOp()
    {
        var x = (777 * ChunksPerLevel1Region + CentreChunk) * Chunk.Size + 3;
        var z = (789 * ChunksPerLevel1Region + CentreChunk) * Chunk.Size + 9;
        var stone = TerrainGenerator.RockBlock;
        var sand = TerrainGenerator.SandBlock;

        var v0 = LodSurface.EditVersion;
        Assert.True(LodSurface.Set(x, z, 70, stone));
        Assert.NotEqual(v0, LodSurface.EditVersion);

        // The identical override again: no change reported, no version bump.
        var v1 = LodSurface.EditVersion;
        Assert.False(LodSurface.Set(x, z, 70, stone));
        Assert.Equal(v1, LodSurface.EditVersion);

        // A different block or a different top is a change.
        Assert.True(LodSurface.Set(x, z, 70, sand));
        Assert.True(LodSurface.Set(x, z, 71, sand));
        AssertOverride(x, z, 71, sand);
    }

    [Fact]
    public void RescanStopsAtAnUnloadedChunkConservatively()
    {
        var cx = 777 * ChunksPerLevel1Region + CentreChunk;
        var cz = 783 * ChunksPerLevel1Region + CentreChunk;
        var cy = (ColumnCache.GetHeights(cx, cz).Max() >> Chunk.SizeShift) + 2;
        var (manager, chunk) = EmptyReadyChunk(new Vector3(cx, cy, cz));
        var x = (cx << Chunk.SizeShift) + 5;
        var z = (cz << Chunk.SizeShift) + 7;
        var y = chunk.LowerBoundY;

        chunk.SetBlockRaw(5, 0, 7, TerrainGenerator.RockBlock, 0);
        Assert.True(LodSurface.RecordEdit(manager, x, y, z));
        AssertOverride(x, z, y, TerrainGenerator.RockBlock);

        // Nothing is loaded below: the cell under the removed top is assumed solid, never air, and
        // keeps the generated tile rule.
        chunk.SetBlockRaw(5, 0, 7, 0, 0);
        Assert.True(LodSurface.RecordEdit(manager, x, y, z));
        AssertOverride(x, z, y - 1, 0);
    }

    private static void Set(ChunkManager manager, int x, int y, int z, byte id)
    {
        var chunk = manager.ChunkAt(x, y, z);
        Assert.NotNull(chunk);
        chunk.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, 0);
    }

    private static void AssertOverride(int x, int z, int top, byte block)
    {
        Assert.True(LodSurface.TryGet(x, z, out var actualTop, out var actualBlock), "no override recorded");
        Assert.Equal(top, actualTop);
        Assert.Equal(block, actualBlock);
    }

    /// <summary>
    /// A column of chunk column (cx, cz) that is never the unique minimum of any
    /// <paramref name="window"/>-wide square containing it: every such square holds another column
    /// at or below its height, so raising it cannot move an eroded minimum.
    /// </summary>
    private static (int X, int Z) FindColumnNeverUniqueWindowMin(int cx, int cz, int window)
    {
        for (var lx = 0; lx < Chunk.Size; lx++)
        for (var lz = 0; lz < Chunk.Size; lz++)
        {
            var x = (cx << Chunk.SizeShift) + lx;
            var z = (cz << Chunk.SizeShift) + lz;
            var h = HeightAt(x, z);
            var ok = true;
            for (var ox = x - window + 1; ox <= x && ok; ox++)
            for (var oz = z - window + 1; oz <= z && ok; oz++)
            {
                var shared = false;
                for (var wx = ox; wx < ox + window && !shared; wx++)
                for (var wz = oz; wz < oz + window; wz++)
                    if ((wx != x || wz != z) && HeightAt(wx, wz) <= h)
                    {
                        shared = true;
                        break;
                    }
                ok = shared;
            }

            if (ok) return (x, z);
        }

        Assert.Fail($"every column of chunk column ({cx}, {cz}) is some window's unique minimum");
        return default;
    }

    /// <summary>
    /// The first level-1 region in row <paramref name="rz"/> (walking east from 777) whose centre
    /// chunk column is mostly wet and whose build draws a sea sheet, with its baseline build.
    /// </summary>
    private static (int Rx, LodMeshData Baseline) FindSeaRegion(int rz)
    {
        for (var rx = 777; rx < 777 + 400; rx++)
        {
            var column = ColumnCache.Get(rx * ChunksPerLevel1Region + CentreChunk, rz * ChunksPerLevel1Region + CentreChunk);
            var wet = 0;
            for (var i = 0; i < column.Heights.Length; i++)
                if (column.WaterLevel[i] > column.Heights[i]) wet++;
            if (wet * 2 < column.Heights.Length) continue;

            var baseline = LodRegion.BuildMeshData(1, rx, rz);
            if (baseline.WaterVertices.Count > 0) return (rx, baseline);
        }

        Assert.Fail($"no sea level-1 region in row {rz}");
        return default;
    }

    private static void AssertSameMesh(LodMeshData expected, LodMeshData actual)
    {
        Assert.True(expected.Vertices.SequenceEqual(actual.Vertices));
        Assert.True(expected.Indices.SequenceEqual(actual.Indices));
        Assert.True(expected.WaterVertices.SequenceEqual(actual.WaterVertices));
        Assert.True(expected.WaterIndices.SequenceEqual(actual.WaterIndices));
        Assert.True(expected.ColumnCyMin.SequenceEqual(actual.ColumnCyMin));
        Assert.True(expected.ColumnCyMax.SequenceEqual(actual.ColumnCyMax));
        Assert.Equal(expected.YMin, actual.YMin);
        Assert.Equal(expected.YMax, actual.YMax);
    }
}
