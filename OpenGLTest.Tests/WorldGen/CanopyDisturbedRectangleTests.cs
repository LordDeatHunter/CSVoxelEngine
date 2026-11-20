using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Fine-column oracle for the sparse disturbed-canopy rectangle lookup.</summary>
[Collection("LOD column caps")]
public sealed class CanopyDisturbedRectangleTests
{
    [Fact]
    public void RectangleDisturbanceMatchesFineColumnTruthAcrossChunkAndWordSeams()
    {
        var markers = new (int X, int Z)[]
        {
            (-17, -17), (-16, -1), (-13, 0), (-12, 15),
            (-9, 16), (-8, 31), (-5, -16), (-4, 1),
            (-1, 63), (0, -32), (3, 47), (4, 48),
            (7, -2), (8, 2), (15, 3), (16, 16)
        };
        try
        {
            TreeCanopy.ResetCanopyDisturbed();
            Assert.False(TreeCanopy.AnyDisturbed(-32, -32, 32, 32));
            Assert.False(TreeCanopy.AnyDisturbed(0, 0, 0, 1));
            for (var group = 0; group < 4; group++)
            {
                TreeCanopy.ResetCanopyDisturbed();
                TreeCanopy.ImportCanopyDisturbed(markers[(group * 4)..(group * 4 + 4)]);
                for (var n = 0; n < 128; n++)
                {
                    var minX = -40 + (n * 17 + group * 9) % 65;
                    var minZ = -40 + (n * 29 + group * 11) % 97;
                    var maxX = minX + n % 65;
                    var maxZ = minZ + (n * 7) % 65;
                    var expected = false;
                    for (var x = minX; x < maxX && !expected; x++)
                    for (var z = minZ; z < maxZ; z++)
                        if (TreeCanopy.IsDisturbed(x, z)) { expected = true; break; }
                    Assert.Equal(expected, TreeCanopy.AnyDisturbed(minX, minZ, maxX, maxZ));
                }
                foreach (var (x, z) in markers[(group * 4)..(group * 4 + 4)])
                {
                    Assert.True(TreeCanopy.AnyDisturbed(x, z, x + 1, z + 1));
                    Assert.False(TreeCanopy.AnyDisturbed(x, z, x, z + 1));
                    Assert.False(TreeCanopy.AnyDisturbed(x, z, x + 1, z));
                }
            }
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
        }
    }
}
