using System;
using System.Collections.Generic;
using System.Numerics;
using silkgltriangle.Content;
using Xunit;

namespace silkgltriangle.Tests;

// Shares a collection with TreeWorldgenTests so its shape-cache reset never lands between
// TrunkShapeIsCachedAcrossChunks' two adjacent ShapeOf calls - that assertion compares the shapes
// by reference. See WorldgenShapeCacheCollection.
[Collection("Worldgen shape cache")]
public sealed class WorldgenTests
{
    public WorldgenTests() => TestContent.EnsureLoaded();

    private static int SurfaceChunkY()
    {
        var heights = ColumnCache.GetHeights(0, 0);
        var max = 0;
        foreach (var h in heights)
            if (h > max) max = h;
        return max >> Chunk.SizeShift;
    }

    [Fact]
    public void SameChunkGeneratesIdenticalBlocks()
    {
        var pos = new Vector3(0, SurfaceChunkY(), 0);
        var a = new Chunk(null!, pos, generateBlockData: true);
        var b = new Chunk(null!, pos, generateBlockData: true);

        Assert.Equal(a.SurfaceMinY, b.SurfaceMinY);
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            Assert.Equal(a.GetLocalBlock(x, y, z), b.GetLocalBlock(x, y, z));
    }

    [Fact]
    public void TrunkShapeIsCachedAcrossChunks()
    {
        // Biomes put desert and ocean beside the origin, so walk chunk columns until one is
        // forest (where trees grow) instead of assuming a fixed square is wooded.
        var found = false;
        for (var cx = 0; cx <= 64 && !found; cx++)
        for (var cz = -64; cz <= 64 && !found; cz++)
        {
            if (!TerrainGenerator.IsForest(cx * Chunk.Size + Chunk.Size / 2, cz * Chunk.Size + Chunk.Size / 2))
                continue;

            for (var x = cx * Chunk.Size; x < cx * Chunk.Size + Chunk.Size && !found; x++)
            for (var z = cz * Chunk.Size; z < cz * Chunk.Size + Chunk.Size && !found; z++)
            {
                if (!TreeGenerator.TryGetTree(x, z, out _, out _))
                    continue;
                // The same instance twice: the second call hit the shape cache (item 25) instead
                // of growing the tree again.
                Assert.Same(TreeGenerator.ShapeOf(x, z), TreeGenerator.ShapeOf(x, z));
                found = true;
            }
        }

        Assert.True(found);
    }

    [Fact]
    public void BeachColumnsNeverCapWithGrass()
    {
        var sand = ContentRegistry.RequireBlock("sand").Id;
        var gravel = ContentRegistry.RequireBlock("gravel").Id;
        var rock = ContentRegistry.RequireBlock("stone").Id;
        var checkedColumns = 0;

        // Ring outward from the origin to the shore: a pond now sits at its own surface instead of
        // being dug down to the waterline, so the beach band only occurs where the sea meets land.
        for (var r = 0; r <= 32 && checkedColumns < 4; r++)
        for (var cx = -r; cx <= r && checkedColumns < 4; cx++)
        for (var cz = -r; cz <= r && checkedColumns < 4; cz++)
        {
            if (r > 0 && cx != -r && cx != r && cz != -r && cz != r) continue;
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var i = 0; i < heights.Length && checkedColumns < 4; i++)
            {
                var h = heights[i];
                if (!TerrainGenerator.IsBeachHeight(h)) continue;

                var lx = i >> Chunk.SizeShift;
                var lz = i & Chunk.SizeMask;
                var chunk = new Chunk(null!, new Vector3(cx, h >> Chunk.SizeShift, cz),
                    generateBlockData: true);
                var ly = h - (int)chunk.Position.Y * Chunk.Size;
                if (ly < 0 || ly >= Chunk.Size) continue;
                var id = chunk.GetLocalBlock(lx, ly, lz);

                // Carves and water may take the surface, trees never plant this low, and
                // bare ravine rock wins over the beach — anything else solid must be the beach
                if (id != BlockType.Air && !BlockType.IsFluid(id)
                    && !TreeGenerator.IsTreeBlock(id) && id != rock)
                    Assert.True(id == sand || id == gravel,
                        $"beach column ({cx * Chunk.Size + lx},{h},{cz * Chunk.Size + lz}) caps with {id}");
                checkedColumns++;
            }
        }

        Assert.True(checkedColumns > 0);
    }

    [Fact]
    public void OnlyTreeBlocksStandAboveTheHeightmap()
    {
        var cy = SurfaceChunkY();
        var pos = new Vector3(0, cy, 0);
        var chunk = new Chunk(null!, pos, generateBlockData: true);
        var heights = ColumnCache.GetHeights(0, 0);

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var h = heights[(x << Chunk.SizeShift) | z];
            for (var ly = 0; ly < Chunk.Size; ly++)
            {
                if (cy * Chunk.Size + ly <= h) continue;
                var id = chunk.GetLocalBlock(x, ly, z);
                Assert.True(!BlockType.IsSolid(id) || TreeGenerator.IsTreeBlock(id),
                    $"solid non-tree {id} above height {h} at ({x},{ly},{z})");
            }
        }
    }

    [Fact]
    public void LodWearsSandOnBeaches()
    {
        // A 3x3 all-beach fine neighbourhood, so whichever LOD cell covers its centre reads
        // beachy from its uneroded max (erosion would drag it below the band)
        var found = false;
        var fx = 0;
        var fz = 0;
        for (var cx = 0; cx < 24 && !found; cx++)
        for (var cz = 0; cz < 24 && !found; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var x = 0; x < Chunk.Size && !found; x++)
            for (var z = 0; z < Chunk.Size && !found; z++)
            {
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                var allBeach = true;
                for (var dx = -1; dx <= 1 && allBeach; dx++)
                for (var dz = -1; dz <= 1 && allBeach; dz++)
                {
                    var nx = wx + dx;
                    var nz = wz + dz;
                    var nh = ColumnCache.GetHeights(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift)
                        [((nx & Chunk.SizeMask) << Chunk.SizeShift) | (nz & Chunk.SizeMask)];
                    if (!TerrainGenerator.IsBeachHeight(nh)) allBeach = false;
                }
                if (allBeach)
                {
                    fx = wx;
                    fz = wz;
                    found = true;
                }
            }
        }
        Assert.True(found);

        var mesh = LodRegion.BuildMeshData(1, fx / 64, fz / 64);
        var tile = ContentRegistry.RequireBlock("sand").FaceTiles[4];
        Assert.Contains(mesh.Vertices, v =>
            v.Layer == tile && v.UV.X >= 0f && v.UV.X <= 1f && v.UV.Y >= 0f && v.UV.Y <= 1f);
    }

    [Fact]
    public void LodLeavesAreOpaque()
    {
        // A canopy column's LOD cell must wear leaves flagged opaque (alpha 253), so
        // magnified cutout texels fall back to solid leaf color instead of
        // discarding to sky.
        var found = false;
        var tx = 0;
        var tz = 0;
        for (var x = 8; x < 256 && !found; x++)
        for (var z = 8; z < 256 && !found; z++)
        {
            if (TreeGenerator.CanopyTop(x, z) > 0)
            {
                tx = x;
                tz = z;
                found = true;
            }
        }
        Assert.True(found);

        const int level = 0;
        var spec = LodRegion.Levels[level];
        var mesh = LodRegion.BuildMeshData(level, tx / spec.RegionBlocks, tz / spec.RegionBlocks);
        var leaves = BlockType.ById[TreeGenerator.LeavesBlock]!;
        var leafTiles = TestContent.WorldTiles(leaves);
        Assert.Contains(mesh.Vertices,
            v => (v.PackedColor >> 24) == 253u && leafTiles.Contains(v.Layer));
    }

    [Fact]
    public void LodWearsGravelOnPatchedBeaches()
    {
        // A level-1 cell (2x2 fine columns) whose footprint is all beach and whose centre
        // is a gravel patch: the built region must carry the gravel tile. This also proves
        // patches generate at all — a missing patch fails the search, not the assert.
        var found = false;
        var rx = 0;
        var rz = 0;
        for (var cx = 0; cx < 40 && !found; cx++)
        for (var cz = 0; cz < 40 && !found; cz++)
        {
            var heights = ColumnCache.GetHeights(cx, cz);
            for (var x = 0; x < Chunk.Size && !found; x++)
            for (var z = 0; z < Chunk.Size && !found; z++)
            {
                var wx = cx * Chunk.Size + x;
                var wz = cz * Chunk.Size + z;
                var lx = wx - (wx / 64) * 64;
                var lz = wz - (wz / 64) * 64;
                var x0 = wx - lx % 2;
                var z0 = wz - lz % 2;
                var allBeach = true;
                for (var dx = 0; dx < 2 && allBeach; dx++)
                for (var dz = 0; dz < 2 && allBeach; dz++)
                {
                    var nx = x0 + dx;
                    var nz = z0 + dz;
                    var nh = ColumnCache.GetHeights(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift)
                        [((nx & Chunk.SizeMask) << Chunk.SizeShift) | (nz & Chunk.SizeMask)];
                    if (!TerrainGenerator.IsBeachHeight(nh)) allBeach = false;
                }
                var beachBiome = TerrainGenerator.BiomeAt(x0 + 1, z0 + 1);
                if (allBeach && TerrainGenerator.IsGravelPatch(x0 + 1, z0 + 1)
                    // Only ocean and the two sand-shored land biomes wear gravel in the LOD;
                    // desert and frozen rock keep their own materials.
                    && beachBiome is Biome.Ocean or Biome.Forest or Biome.Taiga
                    // A wooded column's canopy is the LOD's top face, which hides the beach cap.
                    && TreeGenerator.CanopyTop(x0, z0) == 0 && TreeGenerator.CanopyTop(x0 + 1, z0) == 0
                    && TreeGenerator.CanopyTop(x0, z0 + 1) == 0 && TreeGenerator.CanopyTop(x0 + 1, z0 + 1) == 0)
                {
                    rx = wx / 64;
                    rz = wz / 64;
                    found = true;
                }
            }
        }
        Assert.True(found);

        var mesh = LodRegion.BuildMeshData(1, rx, rz);
        var tile = ContentRegistry.RequireBlock("gravel").FaceTiles[4];
        Assert.Contains(mesh.Vertices, v =>
            v.Layer == tile && v.UV.X >= 0f && v.UV.X <= 1f && v.UV.Y >= 0f && v.UV.Y <= 1f);
    }

    /// <summary>
    /// First ocean chunk column whose whole 16x16 footprint ends below sea level, so the chunk at
    /// chunk-Y 0 is kept alive by <c>needsWater</c> yet is entirely above its own terrain —
    /// exactly the case C10's carve guard skips (MaxHeight &lt; lowerBoundY == 0). It must be
    /// ocean: a forest basin below the waterline is now dry, so only ocean is guaranteed wet
    /// (see TerrainGenerator.IsWaterColumn).
    /// </summary>
    private static (int cx, int cz) FindSubmergedChunkColumn()
    {
        // Gate on the cheap biome test first: GetColumnData is a full column of noise.
        for (var cx = -160; cx <= 160; cx++)
        for (var cz = -160; cz <= 160; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Ocean)
                continue;
            if (ColumnCache.Get(cx, cz).MaxHeight < WorldGenerationConfig.SeaLevel)
                return (cx, cz);
        }

        Assert.Fail("no fully submerged ocean chunk column within 160 chunks");
        return (0, 0);
    }

    [Fact]
    public void CarversWriteNothingBelowTheColumnTop()
    {
        // The premise C10's guard rests on: over a chunk whose columns all end below its
        // floor, both carvers are pure no-ops. The probe starts all-solid so NoiseCaves'
        // `blocks[idx] == BlockType.Air` continue cannot mask a write.
        var (cx, cz) = FindSubmergedChunkColumn();
        var column = ColumnCache.Get(cx, cz);
        var probe = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        Array.Fill(probe, TerrainGenerator.RockBlock);

        NoiseCaves.Carve(probe, column, cx * Chunk.Size, 0, cz * Chunk.Size);
        WormCarver.Carve(probe, column, cx * Chunk.Size, 0, cz * Chunk.Size);

        Assert.DoesNotContain(BlockType.Air, probe);
    }

    [Fact]
    public void CarversDoWriteInsideTheTerrain()
    {
        // Positive control: the same all-solid probe well inside the terrain does come back
        // carved, so CarversWriteNothingBelowTheColumnTop cannot pass vacuously.
        var found = false;
        for (var cx = 0; cx < 24 && !found; cx++)
        for (var cz = 0; cz < 24 && !found; cz++)
        {
            var column = ColumnCache.Get(cx, cz);
            var cy = (column.MinHeight - 48) >> Chunk.SizeShift;
            var probe = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
            Array.Fill(probe, TerrainGenerator.RockBlock);

            NoiseCaves.Carve(probe, column, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);
            WormCarver.Carve(probe, column, cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size);

            found = Array.IndexOf(probe, BlockType.Air) >= 0;
        }

        Assert.True(found);
    }

    [Fact]
    public void SubmergedSeaChunkGeneratesIdenticalBlocks()
    {
        // Same shape as SameChunkGeneratesIdenticalBlocks, but on the chunk C10's guard
        // actually fires on: fully submerged, so the carvers are skipped and only the
        // needsWater body runs.
        var (cx, cz) = FindSubmergedChunkColumn();
        Assert.True(ColumnCache.Get(cx, cz).MaxHeight < WorldGenerationConfig.SeaLevel);

        var pos = new Vector3(cx, 0, cz);
        var a = new Chunk(null!, pos, generateBlockData: true);
        var b = new Chunk(null!, pos, generateBlockData: true);

        Assert.Equal(a.SurfaceMinY, b.SurfaceMinY);
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            Assert.Equal(a.GetLocalBlock(x, y, z), b.GetLocalBlock(x, y, z));

        var water = ContentRegistry.RequireBlock("water").Id;
        var flooded = false;
        for (var x = 0; x < Chunk.Size && !flooded; x++)
        for (var y = 0; y < Chunk.Size && !flooded; y++)
        for (var z = 0; z < Chunk.Size && !flooded; z++)
            flooded = a.GetLocalBlock(x, y, z) == water;
        Assert.True(flooded, $"submerged chunk ({cx},0,{cz}) holds no water");
    }

    [Fact]
    public void DesertColumnsAreNeverCold()
    {
        // The temperate band: desert lives on the warm ramp and snow on the cold ramp, and the two
        // ramps never overlap, so a desert column sits off the cold ramp entirely and never under
        // snow. Scans the dithered column biomes (what the mesher and LOD actually wear), not just
        // BiomeAt, since the border dither must never push sand back toward the cold side.
        const int radius = 2048;
        const int step = 8;
        var foundDesert = false;
        var foundCold = false;
        for (var z = -radius; z <= radius; z += step)
        for (var x = -radius; x <= radius; x += step)
        {
            var local = ((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask);
            var biome = ColumnCache.Get(x >> Chunk.SizeShift, z >> Chunk.SizeShift).Biomes[local];
            foundCold |= biome is Biome.Taiga or Biome.Frozen || SnowGenerator.DepthAt(x, z, 0) > 0;
            if (biome != Biome.Desert) continue;
            foundDesert = true;

            Assert.True(TerrainGenerator.ColdWeight(x, z) == 0f,
                $"desert at ({x},{z}) is on the cold ramp (cold {TerrainGenerator.ColdWeight(x, z)})");
            Assert.True(SnowGenerator.DepthAt(x, z, 0) == 0, $"desert at ({x},{z}) lies under snow");
        }

        Assert.True(foundDesert, "scan area holds no desert");
        Assert.True(foundCold, "scan area holds no taiga/frozen or snow");
    }
}
