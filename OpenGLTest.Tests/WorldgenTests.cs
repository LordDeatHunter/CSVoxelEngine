using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class WorldgenTests
{
    public WorldgenTests() => TestContent.EnsureLoaded();

    private static int SurfaceChunkY()
    {
        var heights = Chunk.GetColumnHeights(0, 0);
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
        var found = false;
        for (var x = 100; x < 300 && !found; x++)
        for (var z = 100; z < 300 && !found; z++)
        {
            if (!TreeGenerator.TryGetTrunk(x, z, out var baseY1, out var shape1))
                continue;
            Assert.True(TreeGenerator.TryGetTrunk(x, z, out var baseY2, out var shape2));
            Assert.Equal(baseY1, baseY2);
            // Record-struct equality compares the wood/blob lists by reference, so this
            // only passes when the second call hit the shape cache (item 25).
            Assert.Equal(shape1, shape2);
            found = true;
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

        for (var cx = -8; cx <= 8 && checkedColumns < 4; cx++)
        for (var cz = -8; cz <= 8 && checkedColumns < 4; cz++)
        {
            var heights = Chunk.GetColumnHeights(cx, cz);
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
        var heights = Chunk.GetColumnHeights(0, 0);

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
            var heights = Chunk.GetColumnHeights(cx, cz);
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
                    var nh = Chunk.GetColumnHeights(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift)
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
        // magnified cutout texels fall back to solid leaf colour instead of
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
        var leafTiles = new HashSet<ushort>(leaves.FaceTiles);
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
            var heights = Chunk.GetColumnHeights(cx, cz);
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
                    var nh = Chunk.GetColumnHeights(nx >> Chunk.SizeShift, nz >> Chunk.SizeShift)
                        [((nx & Chunk.SizeMask) << Chunk.SizeShift) | (nz & Chunk.SizeMask)];
                    if (!TerrainGenerator.IsBeachHeight(nh)) allBeach = false;
                }
                if (allBeach && TerrainGenerator.IsGravelPatch(x0 + 1, z0 + 1))
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
}
