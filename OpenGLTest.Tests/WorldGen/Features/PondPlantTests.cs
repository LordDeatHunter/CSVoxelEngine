using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Features;
using Xunit;

namespace VoxelEngine.Tests.WorldGen.Features;

/// <summary>
/// The pond plants' placement rules over hand-built columns: a reed stands in the shallowest
/// water ring at a shore (never a dry column or deeper water), and a lily pad floats on a lake's
/// surface but never on the sea's.
/// </summary>
public sealed class PondPlantTests
{
    public PondPlantTests() => TestContent.EnsureLoaded();

    private const int Ground = 10;

    private static ColumnData Column(int[] water) =>
        new(Fill(Ground), new byte[Chunk.Size * Chunk.Size], new float[Chunk.Size * Chunk.Size],
            new float[Chunk.Size * Chunk.Size], waterLevel: water);

    private static int[] Fill(int value)
    {
        var array = new int[Chunk.Size * Chunk.Size];
        for (var i = 0; i < array.Length; i++) array[i] = value;
        return array;
    }

    private static (int X, int Z) Cell(int i) => (i >> Chunk.SizeShift, i & Chunk.SizeMask);

    [Fact]
    public void ReedsStandInTheShallowEdgeOnly()
    {
        var water = Fill(Ground + 1);
        water[0] = TerrainGenerator.NoWater;
        water[1] = Ground + 2;
        var column = Column(water);

        // Clumps are sparse, so pick the first chunk whose columns hold members and stamp there
        var members = new List<int>();
        var chunkX = 0;
        var chunkZ = 0;
        for (var cx = -6; cx <= 6 && members.Count == 0; cx++)
        for (var cz = -6; cz <= 6 && members.Count == 0; cz++)
        {
            for (var i = 0; i < water.Length; i++)
            {
                var (x, z) = Cell(i);
                if (ReedGenerator.TryGetReed((cx << Chunk.SizeShift) + x, (cz << Chunk.SizeShift) + z))
                    members.Add(i);
            }
            if (members.Count > 0)
            {
                chunkX = cx;
                chunkZ = cz;
            }
        }
        Assert.NotEmpty(members);

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        ReedGenerator.Stamp(blocks, column, chunkX << Chunk.SizeShift, 0, chunkZ << Chunk.SizeShift);

        var memberSet = new HashSet<int>(members);
        var standing = 0;
        for (var i = 0; i < water.Length; i++)
        {
            var (x, z) = Cell(i);
            var isReed = blocks[Chunk.Idx(x, Ground + 1, z)] == ReedGenerator.ReedBlock;
            // A clump member only takes one in the shallow ring (one under the water level):
            // never on the dry column, never in the two-deep water
            Assert.Equal(memberSet.Contains(i) && water[i] == Ground + 1, isReed);
            if (isReed) standing++;
        }
        Assert.True(standing > 0, "no shallow column carried a reed");
    }

    [Fact]
    public void ALilyPadLandsOnALakeSurfaceButNotOnTheSea()
    {
        var water = Fill(Ground + 2);
        water[0] = WorldGenerationConfig.SeaLevel;
        var column = Column(water);

        var members = new List<int>();
        for (var i = 0; i < water.Length; i++)
        {
            var (x, z) = Cell(i);
            if (LilyPadGenerator.TryGetPad(x, z)) members.Add(i);
        }
        Assert.NotEmpty(members);

        var blocks = new byte[Chunk.Size * Chunk.Size * Chunk.Size];
        LilyPadGenerator.Stamp(blocks, column, 0, 0, 0);

        var memberSet = new HashSet<int>(members);
        var floating = 0;
        for (var i = 0; i < water.Length; i++)
        {
            var (x, z) = Cell(i);
            var isPad = blocks[Chunk.Idx(x, Ground + 2, z)] == LilyPadGenerator.PadBlock;
            // A pad column takes one on its lake surface, and the sea column never does
            Assert.Equal(memberSet.Contains(i) && water[i] != WorldGenerationConfig.SeaLevel, isPad);
            if (isPad) floating++;
        }
        Assert.True(floating > 0, "no lake column carried a pad");
    }
}
