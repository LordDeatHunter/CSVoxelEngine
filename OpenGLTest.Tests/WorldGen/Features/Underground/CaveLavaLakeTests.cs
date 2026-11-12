using System;
using System.Linq;
using System.Threading.Tasks;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features.Underground;
using Xunit;
using P = VoxelEngine.WorldGen.Features.Underground.CaveLavaLakes.CellPosition;

namespace VoxelEngine.Tests.WorldGen.Features.Underground;

[Collection("Worldgen seed")]
public sealed class CaveLavaLakeTests
{
    public CaveLavaLakeTests() => TestContent.EnsureLoaded();

    [Fact]
    public void DepthRaisesOccurrenceAndTheRangeOfAdmissibleSizes()
    {
        var previous = CaveLavaLakes.Chance(WorldGenerationConfig.LavaMaxY);
        foreach (var y in Enumerable.Range(WorldGenerationConfig.BarrierTopY + 2,
                     WorldGenerationConfig.LavaMaxY - WorldGenerationConfig.BarrierTopY - 1).Reverse())
        {
            Assert.True(CaveLavaLakes.Chance(y) >= previous);
            previous = CaveLavaLakes.Chance(y);
        }
        for (var r = 1; r <= 10; r++)
            Assert.True(CaveLavaLakes.AreaBudget(WorldGenerationConfig.BarrierTopY + 32, r / 10f)
                > CaveLavaLakes.AreaBudget(WorldGenerationConfig.LavaMaxY, r / 10f));
        Assert.Empty(CaveLavaLakes.Plan(-1, WorldGenerationConfig.BarrierTopY / 64 - 2, -1));
    }

    // An asymmetric connected cave depression straddling negative chunk seams. The rock
    // surface varies in height, and a rock pillar projects into the liquid footprint.
    private static ushort Terrain(int x, int y, int z)
    {
        var open = x >= -48 && x < -27 && z >= -48 && z < -36
            || x >= -44 && x < -36 && z >= -48 && z < -23;
        var floor = -105 + ((x + z) & 1);
        if (x >= -40 && x < -37 && z >= -43 && z < -40) floor = -99;
        return open && y > floor && y < -88 ? BlockType.Air : TerrainGenerator.RockBlock;
    }

    private static P[] Flood(Func<int, int, int, ushort> terrain, int budget = 2000) =>
        CaveLavaLakes.Flood(new P(-46, -103, -46), -101, -1, -2, -1, budget, terrain);

    [Fact]
    public void FlatLiquidFollowsAllNaturalWallsAndBottomsWithoutConstructingSupport()
    {
        var cells = Flood(Terrain).ToHashSet();
        Assert.NotEmpty(cells);
        Assert.True(cells.Select(p => p.X >> Chunk.SizeShift).Distinct().Count() > 1);
        Assert.True(cells.Select(p => p.Z >> Chunk.SizeShift).Distinct().Count() > 1);
        foreach (var p in cells)
        {
            Assert.Equal(BlockType.Air, Terrain(p.X, p.Y, p.Z));
            foreach (var (dx, dy, dz) in new[] { (-1, 0, 0), (1, 0, 0), (0, -1, 0), (0, 0, -1), (0, 0, 1) })
            {
                var q = new P(p.X + dx, p.Y + dy, p.Z + dz);
                Assert.True(cells.Contains(q) || BlockType.IsSolid(Terrain(q.X, q.Y, q.Z)), "spill at natural wall");
            }
            if (Terrain(p.X, p.Y + 1, p.Z) == BlockType.Air && !cells.Contains(p with { Y = p.Y + 1 }))
                Assert.Equal(-101, p.Y);
        }
        for (var x = -50; x < -20; x++)
        for (var z = -50; z < -20; z++)
            Assert.Equal(Terrain(x, -101, z) == BlockType.Air, cells.Contains(new P(x, -101, z)));
    }

    [Fact]
    public void SpillsUnknownBottomsFluidsAndBudgetExhaustionRejectRatherThanTruncateOrRepair()
    {
        Assert.Empty(Flood((_, _, _) => BlockType.Air));
        Assert.Empty(Flood((x, y, z) => y < -104 ? BlockType.Air : Terrain(x, y, z)));
        Assert.Empty(Flood((x, y, z) => x == -48 && z == -46 && y == -103
            ? LavaGenerator.LavaBlock : Terrain(x, y, z)));
        Assert.Empty(Flood(Terrain, 20));
        // A passage leaving the site's domain rejects the flood instead of closing its edge.
        Assert.Empty(Flood((x, y, z) => y == -103 && z == -46 && x <= -46
            ? BlockType.Air : Terrain(x, y, z)));
        var unknown = ContentRegistry.RequireBlock("torch").Id;
        Assert.Empty(Flood((x, y, z) => x == -48 && z == -46 && y == -103
            ? unknown : Terrain(x, y, z)));
    }

    [Fact]
    public void NegativeCrossChunkPlansAgreeWithPointQueriesAndParallelGeneration()
    {
        var pool = CaveLavaLakes.Plan(-4, -6, 1);
        Assert.NotEmpty(pool);
        var positions = pool.Select(p => VoxelCoords.ChunkPosOf(p.X, p.Y, p.Z)).Distinct().ToArray();
        Assert.True(positions.Length > 1, "the real pool must exercise a chunk seam");
        var serial = positions.Select(p => new Chunk(null!, p, true).Storage.ToDense()).ToArray();
        Parallel.For(0, positions.Length, n =>
            Assert.Equal(serial[n], new Chunk(null!, positions[n], true).Storage.ToDense()));
        foreach (var pos in positions.Reverse())
        {
            var x0 = (int)pos.X * Chunk.Size;
            var y0 = (int)pos.Y * Chunk.Size;
            var z0 = (int)pos.Z * Chunk.Size;
            var blocks = CaveTerrainProbe.Generate((int)pos.X, (int)pos.Y, (int)pos.Z);
            var original = (ushort[])blocks.Clone();
            var states = Enumerable.Repeat((byte)0xFF, Chunk.BlockCount).ToArray();
            CaveLavaLakes.Stamp(blocks, states, x0, y0, z0);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = Chunk.Idx(x, y, z);
                var filled = CaveLavaLakes.Contains(x0 + x, y0 + y, z0 + z);
                Assert.Equal(filled, blocks[i] != original[i]);
                if (!filled) continue;
                Assert.Equal(BlockType.Air, original[i]);
                Assert.Equal(LavaGenerator.LavaBlock, blocks[i]);
                Assert.True(FluidState.IsSource(states[i]));
            }
        }
    }

    [Fact]
    public void IncreasingTheBudgetAdmitsLargerTerrainRatherThanStretchingAnOutline()
    {
        var size = Flood(Terrain).Count(p => p.Y == -101);
        var shallow = CaveLavaLakes.AreaBudget(WorldGenerationConfig.LavaMaxY, 1);
        var deep = CaveLavaLakes.AreaBudget(WorldGenerationConfig.BarrierTopY + 32, 1);
        Assert.True(size > shallow && size < deep);
        Assert.Empty(Flood(Terrain, shallow));
        Assert.NotEmpty(Flood(Terrain, deep));
    }
}
