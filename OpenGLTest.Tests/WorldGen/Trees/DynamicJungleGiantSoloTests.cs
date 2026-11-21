using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Trees;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Trees;

/// <summary>Terrain-backed solo growth at the first observed natural Dynamic giant root.</summary>
[Collection("Worldgen seed")]
public sealed class DynamicJungleGiantSoloTests
{
    [Fact]
    public void FrozenNaturalGiantSoloShapeMatchesAdmissionAcrossSameProfileReset()
    {
        const int x = 4004;
        const int z = 811;
        const int expectedBaseY = 32;

        try
        {
            TestContent.EnsureLoaded();
            var giant = TreeSpeciesRegistry.Require("jungle_giant");
            var ordinary = TreeSpeciesRegistry.Require("jungle_tree");
            var reach = TreeRasterizer.ReachOf(giant);
            Assert.True(2 * reach.Horizontal <= WorldGenerationConfig.TreeCellBlocks);

            TreeBatches.TreeShape? first = null;
            for (var observation = 0; observation < 2; observation++)
            {
                WorldSeed.Apply(1337, WorldGenProfile.Dynamic);
                Assert.True(TreeGenerator.TryGetTree(x, z, out var admitted, out var baseY));
                Assert.Same(giant, admitted);
                Assert.Equal(expectedBaseY, baseY);

                var growsBefore = TreeBatches.GrowsOnThisThread;
                var shape = TreeBatches.ShapeOf(x, z);
                Assert.Equal(growsBefore + 1, TreeBatches.GrowsOnThisThread);
                Assert.Same(giant, shape.Species);
                Assert.Equal((x, baseY, z), (shape.X, shape.BaseY, shape.Z));
                Assert.True(shape.WoodCount > 0);
                Assert.True(shape.WoodCount < shape.Cells.Length);
                for (var i = 0; i < shape.Cells.Length; i++)
                {
                    var cell = shape.Cells[i];
                    Assert.Equal(i < shape.WoodCount ? giant.WoodId : giant.LeavesId, cell.Block);
                    Assert.NotEqual(ordinary.WoodId, cell.Block);
                    Assert.NotEqual(ordinary.LeavesId, cell.Block);
                    Assert.InRange(cell.X, x - reach.Horizontal, x + reach.Horizontal);
                    Assert.InRange(cell.Y, baseY - reach.Down, baseY + reach.Up);
                    Assert.InRange(cell.Z, z - reach.Horizontal, z + reach.Horizontal);
                }

                if (first == null)
                {
                    first = shape;
                    continue;
                }

                Assert.NotSame(first, shape);
                Assert.Equal(first.WoodCount, shape.WoodCount);
                Assert.Equal(first.Cells, shape.Cells);
            }
        }
        finally
        {
            WorldSeed.Apply(WorldSeed.Default);
        }
    }
}
