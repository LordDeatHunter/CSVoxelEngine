using System.Collections.Generic;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>Fine-column oracle for the sparse disturbed-canopy rectangle lookup.</summary>
[Collection("Worldgen seed")]
public sealed class CanopyDisturbedRectangleTests
{
    private static void AssertMatchesPointOracle(int minX, int minZ, int maxX, int maxZ)
    {
        var expected = false;
        for (var x = minX; x < maxX && !expected; x++)
        for (var z = minZ; z < maxZ; z++)
            if (TreeCanopy.IsDisturbed(x, z)) { expected = true; break; }
        Assert.Equal(expected, TreeCanopy.AnyDisturbed(minX, minZ, maxX, maxZ));
    }

    [Fact]
    public void RectangleDisturbanceMatchesFineColumnTruthAcrossChunkAndWordSeams()
    {
        var previousDisturbed = TreeCanopy.ExportCanopyDisturbed();
        var previousAdded = TreeCanopy.ExportCanopyAdded();
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
            AssertMatchesPointOracle(-32, -32, 32, 32);
            AssertMatchesPointOracle(0, 0, 0, 1);
            AssertMatchesPointOracle(1, 1, -1, 2);
            AssertMatchesPointOracle(1, 1, 2, -1);
            for (var group = 0; group < 4; group++)
            {
                TreeCanopy.ResetCanopyDisturbed();
                TreeCanopy.ImportCanopyDisturbed(markers[(group * 4)..(group * 4 + 4)]);
                for (var n = 0; n < 112; n++)
                {
                    var minX = -40 + (n * 17 + group * 9) % 65;
                    var minZ = -40 + (n * 29 + group * 11) % 97;
                    var maxX = minX + n % 65;
                    var maxZ = minZ + (n * 7) % 65;
                    AssertMatchesPointOracle(minX, minZ, maxX, maxZ);
                }
                foreach (var (x, z) in markers[(group * 4)..(group * 4 + 4)])
                {
                    AssertMatchesPointOracle(x, z, x + 1, z + 1);
                    AssertMatchesPointOracle(x, z, x, z + 1);
                    AssertMatchesPointOracle(x, z, x + 1, z);
                }
            }

            TreeCanopy.ResetCanopyDisturbed();
            var dense = new List<(int X, int Z)>();
            for (var x = -16; x < 0; x++)
            for (var z = -16; z < 0; z++)
                dense.Add((x, z));
            TreeCanopy.ImportCanopyDisturbed(dense);
            for (var n = 0; n < 112; n++)
            {
                var minX = -32 + n % 33;
                var minZ = -32 + (n * 7) % 33;
                AssertMatchesPointOracle(minX, minZ, minX + (n * 11) % 65,
                    minZ + (n * 13) % 65);
            }
            // Every word, a partial Z range, the exclusive far edge, and both chunk seams.
            AssertMatchesPointOracle(-16, -16, 0, 0);
            AssertMatchesPointOracle(-16, -8, 0, -7);
            AssertMatchesPointOracle(-16, 0, 0, 1);
            AssertMatchesPointOracle(0, -16, 1, 0);
            AssertMatchesPointOracle(-16, -16, -15, -15);
            AssertMatchesPointOracle(-1, -1, 0, 0);

            // Import unions with existing marks; reset removes both old and imported marks.
            TreeCanopy.ImportCanopyDisturbed([(0, 0), (16, 16)]);
            AssertMatchesPointOracle(0, 0, 1, 1);
            AssertMatchesPointOracle(-1, 0, 0, 1);
            AssertMatchesPointOracle(0, -1, 1, 0);
            AssertMatchesPointOracle(1, 0, 2, 1);
            AssertMatchesPointOracle(0, 1, 1, 2);
            AssertMatchesPointOracle(-1, -1, 1, 1);
            AssertMatchesPointOracle(16, 16, 17, 17);
            TreeCanopy.ResetCanopyDisturbed();
            AssertMatchesPointOracle(-16, -16, 1, 1);
            TreeCanopy.ImportCanopyDisturbed([(0, 0)]);
            AssertMatchesPointOracle(0, 0, 1, 1);

            TreeCanopy.ResetCanopyDisturbed();
            TreeCanopy.ImportCanopyDisturbed([(int.MinValue, int.MinValue),
                (int.MaxValue - 1, int.MaxValue - 1)]);
            AssertMatchesPointOracle(int.MinValue, int.MinValue, int.MinValue + 1, int.MinValue + 1);
            AssertMatchesPointOracle(int.MinValue + 1, int.MinValue, int.MinValue + 2, int.MinValue + 1);
            AssertMatchesPointOracle(int.MaxValue - 1, int.MaxValue - 1, int.MaxValue, int.MaxValue);
            AssertMatchesPointOracle(int.MaxValue - 2, int.MaxValue - 2, int.MaxValue - 1, int.MaxValue - 1);
        }
        finally
        {
            TreeCanopy.ResetCanopyDisturbed();
            TreeCanopy.ImportCanopyDisturbed(previousDisturbed);
            TreeCanopy.ImportCanopyAdded(previousAdded);
        }
    }
}
