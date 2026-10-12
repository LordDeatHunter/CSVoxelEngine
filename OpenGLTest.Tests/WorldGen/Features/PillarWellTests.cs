using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Ruined pillars and desert wells: the pillar's stack honors its height and gap bits with the
/// bottom cell always standing, and the well writes a sandstone wall around a sunken pool.
/// </summary>
public sealed class PillarWellTests
{
    public PillarWellTests() => TestContent.EnsureLoaded();

    [Fact]
    public void APillarsStackHonoursItsHeightAndGaps()
    {
        var brick = RuinedPillarGenerator.BrickBlock;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        // Ground at y=40 (ly=8 in a chunk based at y=32): height 4, the second and third levels missing
        RuinedPillarGenerator.StampPillar(blocks, 32, 3, 4, 40, 4, gaps: 0b011);

        Assert.Equal(brick, blocks[Chunk.Idx(3, 9, 4)]);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(3, 10, 4)]);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(3, 11, 4)]);
        Assert.Equal(brick, blocks[Chunk.Idx(3, 12, 4)]);

        // The bottom cell always stands, whatever the gaps
        var solid = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        RuinedPillarGenerator.StampPillar(solid, 32, 3, 4, 40, 2, gaps: 0b1111);
        Assert.Equal(brick, solid[Chunk.Idx(3, 9, 4)]);
        Assert.Equal(BlockType.Air, solid[Chunk.Idx(3, 10, 4)]);
    }

    [Fact]
    public void AWellRingsWaterWithSandstone()
    {
        var sandstone = DesertWellGenerator.SandstoneBlock;
        var water = DesertWellGenerator.WaterBlock;
        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        DesertWellGenerator.StampWell(blocks, 32, 8, 8, 40);

        // The 5x5 wall one above the ground, the inner ring a floor at the ground's level
        Assert.Equal(sandstone, blocks[Chunk.Idx(6, 9, 6)]);
        Assert.Equal(sandstone, blocks[Chunk.Idx(8, 9, 10)]);
        Assert.Equal(sandstone, blocks[Chunk.Idx(7, 8, 7)]);
        Assert.Equal(water, blocks[Chunk.Idx(8, 8, 8)]);
        Assert.Equal(BlockType.Air, blocks[Chunk.Idx(8, 9, 8)]);
    }

    [Fact]
    public void BothHashesAreDeterministicAndInsideTheirCells()
    {
        var pillars = 0;
        var wells = 0;
        for (var cellX = -20; cellX <= 20; cellX++)
        for (var cellZ = -20; cellZ <= 20; cellZ++)
        {
            if (RuinedPillarGenerator.TryGetPillar(cellX, cellZ, out var px, out var pz, out var height, out _))
            {
                pillars++;
                Assert.InRange(height, WorldGenerationConfig.PillarMinHeight, WorldGenerationConfig.PillarMaxHeight);
                Assert.InRange(px, cellX << WorldGenerationConfig.PillarCellShift,
                    ((cellX + 1) << WorldGenerationConfig.PillarCellShift) - 1);
                Assert.InRange(pz, cellZ << WorldGenerationConfig.PillarCellShift,
                    ((cellZ + 1) << WorldGenerationConfig.PillarCellShift) - 1);
                Assert.True(RuinedPillarGenerator.TryGetPillar(cellX, cellZ, out var px2, out var pz2, out var h2, out _));
                Assert.Equal((px, pz, height), (px2, pz2, h2));
            }

            if (DesertWellGenerator.TryGetWell(cellX, cellZ, out var wx, out var wz))
            {
                wells++;
                Assert.InRange(wx, cellX << WorldGenerationConfig.WellCellShift,
                    ((cellX + 1) << WorldGenerationConfig.WellCellShift) - 1);
                Assert.InRange(wz, cellZ << WorldGenerationConfig.WellCellShift,
                    ((cellZ + 1) << WorldGenerationConfig.WellCellShift) - 1);
            }
        }

        Assert.True(pillars > 0, "no pillar candidate in the sampled cells");
        Assert.True(wells > 0, "no well candidate in the sampled cells");
    }
}
