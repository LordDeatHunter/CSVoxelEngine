using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Sky;
using VoxelEngine.WorldGen.Structures;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Sky;

/// <summary>
/// The sky islands and the cloud sea (plans/LAYERS.md L-12): blocks only inside their bands, islands
/// inside zones with wide open sky between the zones, big zones rarer than small ones, islands that
/// never touch, a domed top over a tapering underside, generated chunks that equal the point query
/// across chunk seams, two neighboring stamps that equal one union stamp, and a layout that follows
/// the world seed.
/// </summary>
[Collection("Worldgen seed")]
public sealed class SkyIslandTests
{
    private const int Volume = Chunk.Size * Chunk.Size * Chunk.Size;

    public SkyIslandTests() => TestContent.EnsureLoaded();

    /// <summary>The first islands a ring scan of island cells finds around the origin.</summary>
    private static List<SkyIsland> IslandsNearOrigin(int count)
    {
        var found = new List<SkyIsland>();
        for (var ring = 0; ring < 200 && found.Count < count; ring++)
        for (var gx = -ring; gx <= ring; gx++)
        for (var gz = -ring; gz <= ring; gz++)
        {
            if (Math.Max(Math.Abs(gx), Math.Abs(gz)) != ring) continue;
            if (SkyIslands.TryGetIsland(gx, gz, out var island)) found.Add(island);
        }

        Assert.NotEmpty(found);
        return found;
    }

    private static SkyIsland BiggestNearOrigin()
    {
        var best = default(SkyIsland);
        foreach (var island in IslandsNearOrigin(12))
            if (island.Radius > best.Radius) best = island;
        return best;
    }

    private static Chunk Generate(int cx, int cy, int cz)
    {
        var chunk = new Chunk(null!, new Vector3(cx, cy, cz), generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk);
        return chunk;
    }

    /// <summary>What the sky holds at a cell far above every column's ground: a sky tree's cell, island (with
    /// its flora), cloud sea, cloud patch or air, from the tree shapes and the three point queries.</summary>
    private static byte ExpectedSky(int x, int y, int z)
    {
        var tree = SkyTreeCell(x, y, z);
        var island = SkyIslandGenerator.BlockAt(x, y, z);
        // A tree grows over the flora, never into the island
        if (tree != BlockType.Air && !BlockType.IsSolid(island)) return tree;
        if (island != BlockType.Air) return island;
        if (CloudSeaGenerator.IsCloud(x, y, z) || CloudPatchGenerator.IsCloud(x, y, z)) return CloudPatchGenerator.CloudBlock;
        return BlockType.Air;
    }

    /// <summary>The block of the sky tree rooted in the cell's tree cell at (x, y, z), or air.</summary>
    private static byte SkyTreeCell(int x, int y, int z)
    {
        var tree = SkyTrees.TreeOf(SkyTrees.CellOf(x), SkyTrees.CellOf(z));
        if (tree == null || y < tree.MinY || y > tree.MaxY) return BlockType.Air;
        foreach (var cell in tree.Cells)
            if (cell.X == x && cell.Y == y && cell.Z == z)
                return cell.Block;
        return BlockType.Air;
    }

    [Fact]
    public void EveryIslandAndCloudSeaColumnLiesInsideItsBand()
    {
        var islandColumns = 0;
        var seaColumns = 0;
        var holes = 0;
        foreach (var island in IslandsNearOrigin(8))
        {
            var r = (int)MathF.Ceiling(island.Radius);
            for (var x = (int)island.CenterX - r; x <= (int)island.CenterX + r; x++)
            for (var z = (int)island.CenterZ - r; z <= (int)island.CenterZ + r; z++)
            {
                if (!SkyIslandGenerator.TryGetColumn(x, z, out var bottom, out var top)) continue;
                islandColumns++;
                Assert.True(bottom < top, $"column ({x}, {z}) has its top {top} at or under its bottom {bottom}");
                Assert.InRange(bottom, WorldGenerationConfig.SkyIslandBottomY, WorldGenerationConfig.SkyIslandTopY);
                Assert.InRange(top, WorldGenerationConfig.SkyIslandBottomY, WorldGenerationConfig.SkyIslandTopY);
            }
        }

        for (var x = -512; x < 512; x += 4)
        for (var z = -512; z < 512; z += 4)
        {
            if (!CloudSeaGenerator.TryGetColumn(x, z, out var bottom, out var top))
            {
                holes++;
                continue;
            }

            seaColumns++;
            Assert.InRange(bottom, SkyIslandConfig.CloudSeaBottomY, top);
            Assert.InRange(top, bottom, SkyIslandConfig.CloudSeaTopY);
        }

        // A sea, not patches: most columns hold it, yet it has holes down to the world below
        Assert.True(islandColumns > 0, "no island column found");
        Assert.True(seaColumns > holes && holes > 0, $"{seaColumns} sea columns and {holes} holes");
    }

    [Fact]
    public void NothingIsGeneratedInTheSkyOutsideTheBands()
    {
        var island = IslandsNearOrigin(1)[0];
        var cx = (int)MathF.Floor(island.CenterX) >> Chunk.SizeShift;
        var cz = (int)MathF.Floor(island.CenterZ) >> Chunk.SizeShift;
        var gaps = new[]
        {
            (WorldGenerationConfig.CloudSlabTopY + 1, SkyIslandConfig.CloudSeaBottomY - 1),
            (SkyIslandConfig.CloudSeaTopY + 1, WorldGenerationConfig.SkyIslandBottomY - 1),
            (WorldGenerationConfig.SkyIslandTopY + 1, WorldGenerationConfig.SkyIslandTopY + 3 * Chunk.Size),
        };

        foreach (var (low, high) in gaps)
        for (var cy = (low + Chunk.Size - 1) >> Chunk.SizeShift; (cy + 1) * Chunk.Size - 1 <= high; cy++)
        {
            var chunk = Generate(cx, cy, cz);
            Assert.Equal(0, chunk.SolidCount);
        }
    }

    [Fact]
    public void TheSkyHoldsIslandZonesWithWideOpenGapsBetweenThem()
    {
        var islandColumns = 0;
        var openColumns = 0;
        for (var x = -2048; x < 2048; x += 16)
        for (var z = -2048; z < 2048; z += 16)
        {
            if (SkyIslandGenerator.TryGetColumn(x, z, out _, out _))
            {
                islandColumns++;
                continue;
            }

            if (!SkyIslands.ZoneAt(x, z).InZone) openColumns++;
        }

        Assert.True(islandColumns > 0 && openColumns > islandColumns,
            $"{islandColumns} island columns, {openColumns} columns of open sky outside every zone");

        // Every island stands in a zone, and two surviving zones keep the gap between their outlines
        foreach (var island in IslandsNearOrigin(20))
            Assert.True(SkyIslands.ZoneAt(island.CenterX, island.CenterZ).InZone);

        var zones = new List<(float X, float Z, float R)>();
        for (var gx = -8; gx <= 8; gx++)
        for (var gz = -8; gz <= 8; gz++)
            if (SkyIslands.TryGetZone(gx, gz, out var x, out var z, out var r, out _))
                zones.Add((x, z, r));
        Assert.True(zones.Count > 1);
        for (var i = 0; i < zones.Count; i++)
        for (var j = i + 1; j < zones.Count; j++)
        {
            var d = Vector2.Distance(new Vector2(zones[i].X, zones[i].Z), new Vector2(zones[j].X, zones[j].Z));
            Assert.True(d >= zones[i].R + zones[j].R + SkyIslandConfig.ZoneGapBlocks, $"zones {i} and {j} crowd");
        }
    }

    [Fact]
    public void BiggerZonesAreRarerThanSmallerOnes()
    {
        var byTier = new int[SkyZone.BigTier + 1];
        for (var gx = -40; gx <= 40; gx++)
        for (var gz = -40; gz <= 40; gz++)
            if (SkyIslands.TryGetZone(gx, gz, out _, out _, out _, out var tier))
                byTier[tier]++;

        Assert.True(byTier[0] > byTier[1] && byTier[1] > byTier[2] && byTier[2] > 0,
            $"zones by tier: {string.Join(", ", byTier)}");
    }

    [Fact]
    public void ZoneAtIsAPureQueryWhoseCenterIsItsCore()
    {
        for (var gx = -6; gx <= 6; gx++)
        for (var gz = -6; gz <= 6; gz++)
        {
            if (!SkyIslands.TryGetZone(gx, gz, out var x, out var z, out var r, out var tier)) continue;

            var zone = SkyIslands.ZoneAt(x, z);
            Assert.Equal(zone, SkyIslands.ZoneAt(x, z));
            Assert.Equal(1f, zone.Weight);
            Assert.Equal(tier, zone.Tier);
            Assert.Equal(r, zone.Radius);

            // Just past the nominal radius is open sky: the gap keeps every other zone far away
            if (SkyLandmasses.TryFindNear(x + r + 1f, z, out _)) continue; // a landmass is a zone too
            Assert.False(SkyIslands.ZoneAt(x + r + 1f, z).InZone);
        }
    }

    [Fact]
    public void IslandsNeverTouch()
    {
        var islands = IslandsNearOrigin(40);
        for (var i = 0; i < islands.Count; i++)
        for (var j = i + 1; j < islands.Count; j++)
        {
            var d = Vector2.Distance(new Vector2(islands[i].CenterX, islands[i].CenterZ),
                new Vector2(islands[j].CenterX, islands[j].CenterZ));
            Assert.True(d >= islands[i].Radius + islands[j].Radius + SkyIslandConfig.IslandGapBlocks,
                $"islands {islands[i]} and {islands[j]} crowd");
        }
    }

    [Fact]
    public void AnIslandDomesOverAnUndersideThatTapersFromItsCoreToItsRim()
    {
        var island = BiggestNearOrigin();
        float coreThickness = 0, rimThickness = 0, coreTop = 0, rimTop = 0;
        int core = 0, rim = 0;
        for (var a = 0; a < 32; a++)
        {
            var angle = a * MathF.Tau / 32;
            foreach (var (share, isCore) in new[] { (0.1f, true), (0.6f, false) })
            {
                var x = (int)MathF.Round(island.CenterX + MathF.Cos(angle) * island.Radius * share);
                var z = (int)MathF.Round(island.CenterZ + MathF.Sin(angle) * island.Radius * share);
                if (!SkyIslandGenerator.TryGetColumn(island, x, z, out var bottom, out var top)) continue;

                Assert.True(top >= island.BaseY && bottom < island.BaseY, $"({x}, {z}) {bottom}..{top} about {island.BaseY}");
                if (isCore)
                {
                    core++;
                    coreThickness += top - bottom;
                    coreTop += top;
                }
                else
                {
                    rim++;
                    rimThickness += top - bottom;
                    rimTop += top;
                }
            }
        }

        Assert.True(core > 0 && rim > 0);
        Assert.True(coreThickness / core > rimThickness / rim, "the underside does not taper");
        Assert.True(coreTop / core >= rimTop / rim, "the top does not dome");
    }

    [Fact]
    public void GeneratedSkyChunksEqualThePointQueryAcrossSeams()
    {
        var island = BiggestNearOrigin();
        var cx = (int)MathF.Floor(island.CenterX) >> Chunk.SizeShift;
        var cz = (int)MathF.Floor(island.CenterZ) >> Chunk.SizeShift;
        var islandBlocks = 0;
        var seaBlocks = 0;
        var layers = new List<int> { SkyIslandConfig.CloudSeaBottomY >> Chunk.SizeShift };
        for (var cy = (island.BaseY - (int)island.Depth - 14) >> Chunk.SizeShift;
             cy <= (island.BaseY + SkyIslandConfig.IslandFloraHeadroom + 8) >> Chunk.SizeShift; cy++)
            layers.Add(cy);

        for (var dcx = -1; dcx <= 0; dcx++)
        for (var dcz = -1; dcz <= 0; dcz++)
        foreach (var cy in layers)
        {
            var chunk = Generate(cx + dcx, cy, cz + dcz);
            Assert.True(chunk.IsBlockDataReady);
            var solid = 0;
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var wx = (cx + dcx) * Chunk.Size + x;
                var wy = cy * Chunk.Size + y;
                var wz = (cz + dcz) * Chunk.Size + z;
                var block = chunk.Blocks[Chunk.Idx(x, y, z)];
                // A ruin on the island owns its footprint cells; the point query knows nothing of it
                if (!RuinPlacement.SkyRuinOwns(wx, wy, wz)) Assert.Equal(ExpectedSky(wx, wy, wz), block);
                if (block == BlockType.Air) continue;
                solid++;
                if (block == CloudPatchGenerator.CloudBlock) seaBlocks++;
                else islandBlocks++;
            }

            // A chunk holding island or sea counts it, so it meshes like any other non-empty chunk
            Assert.Equal(solid, chunk.SolidCount);
        }

        Assert.True(islandBlocks > 0 && seaBlocks > 0, $"{islandBlocks} island and {seaBlocks} sea cells");
    }

    [Fact]
    public void TwoNeighboringChunkStampsEqualOneUnionStamp()
    {
        var island = BiggestNearOrigin();
        var x0 = ((int)MathF.Floor(island.CenterX) >> Chunk.SizeShift) * Chunk.Size;
        var z0 = ((int)MathF.Floor(island.CenterZ) >> Chunk.SizeShift) * Chunk.Size;
        var y0 = (island.BaseY >> Chunk.SizeShift) * Chunk.Size;

        // The union: every island reaching the 32-wide box, each column asked once over that list
        Span<SkyIsland> union = stackalloc SkyIsland[16];
        var count = SkyIslands.CollectIslands(x0 - Chunk.Size, z0, x0 + Chunk.Size - 1, z0 + Chunk.Size - 1, union);
        Assert.True(count > 0);

        var written = 0;
        for (var half = -1; half <= 0; half++)
        {
            var blocks = new byte[Volume];
            var chunkX0 = x0 + half * Chunk.Size;
            SkyIslandGenerator.Stamp(blocks, chunkX0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                int bottom = 0, top = -1;
                SkyIslandPalette? palette = null;
                foreach (var candidate in union[..count])
                {
                    if (!SkyIslandGenerator.TryGetColumn(candidate, chunkX0 + x, z0 + z, out bottom, out top)) continue;
                    palette = SkyBiomes.Palette(SkyBiomes.OfIsland(candidate));
                    break;
                }

                for (var y = 0; y < Chunk.Size; y++)
                {
                    var wy = y0 + y;
                    var expected = palette != null
                        ? SkyFlora.BlockAt(palette, chunkX0 + x, wy, z0 + z, bottom, top)
                        : BlockType.Air;
                    Assert.Equal(expected, blocks[Chunk.Idx(x, y, z)]);
                    if (expected != BlockType.Air) written++;
                }
            }
        }

        Assert.True(written > 0);
    }

    [Fact]
    public void TheStampWritesOnlyAir()
    {
        var island = BiggestNearOrigin();
        var x0 = ((int)MathF.Floor(island.CenterX) >> Chunk.SizeShift) * Chunk.Size;
        var z0 = ((int)MathF.Floor(island.CenterZ) >> Chunk.SizeShift) * Chunk.Size;
        var y0 = (island.BaseY >> Chunk.SizeShift) * Chunk.Size;
        var marker = ContentRegistry.RequireBlock("glass").Id;
        var blocks = new byte[Volume];
        Array.Fill(blocks, marker);
        SkyIslandGenerator.Stamp(blocks, x0, y0, z0);
        CloudSeaGenerator.Stamp(blocks, x0, SkyIslandConfig.CloudSeaBottomY, z0);
        // A sky tree writes over air and flora only, never over anything solid
        byte[]? state = null;
        for (var cy = y0; cy <= y0 + 2 * Chunk.Size; cy += Chunk.Size)
            SkyTrees.Stamp(blocks, ref state, x0, cy, z0);
        Assert.All(blocks, b => Assert.Equal(marker, b));
        Assert.Null(state);
    }

    [Fact]
    public void TheSkyFollowsTheWorldSeed()
    {
        try
        {
            WorldSeed.Apply(WorldSeed.Default);
            var first = SkyCells();
            WorldSeed.Apply(WorldSeed.Default + 7);
            Assert.False(first.SetEquals(SkyCells()));
            WorldSeed.Apply(WorldSeed.Default);
            Assert.True(first.SetEquals(SkyCells()));
            Assert.NotEmpty(first);
        }
        finally { WorldSeed.Apply(WorldSeed.Default); }
    }

    /// <summary>The island centers and the cloud sea columns over a fixed patch of the sky.</summary>
    private static HashSet<(int, int, int)> SkyCells()
    {
        var set = new HashSet<(int, int, int)>();
        for (var gx = -20; gx <= 20; gx++)
        for (var gz = -20; gz <= 20; gz++)
            if (SkyIslands.TryGetIsland(gx, gz, out var island))
                set.Add(((int)island.CenterX, island.BaseY, (int)island.CenterZ));
        for (var x = 0; x < 256; x += 8)
        for (var z = 0; z < 256; z += 8)
            if (CloudSeaGenerator.TryGetColumn(x, z, out var bottom, out var top))
                set.Add((x, bottom - top, z));
        return set;
    }
}
