using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Rock salt, the leatherworker's salt: patches of <c>rock_salt</c> in the top few blocks of exposed stone. A column
/// hosts it when its surface is bare stone, which is the deep sea floor (an ocean column below the gravel band) and
/// every column whose soil is stripped (<see cref="ColumnData.DirtDepth"/> 0: a bare-rock mountain crest, a ravine
/// wall). A 2D noise picks the patches, and only stone cells from the surface down to
/// <see cref="WorldGenerationConfig.RockSaltDepth"/> are replaced, so a cave carved there stays open. A pure function of
/// position, the seed and the column like the other generators.
/// </summary>
public static class RockSaltGenerator
{
    private static FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort SaltBlock;

    private static readonly ushort RockBlock;

    static RockSaltGenerator()
    {
        Reseed();
        SaltBlock = ContentRegistry.RequireBlock("rock_salt").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>Builds the noise from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() =>
        PatchNoise = TerrainGenerator.MakeNoise(189, WorldGenerationConfig.RockSaltFrequency);

    /// <summary>Whether a column of this biome, terrain height and soil depth shows bare stone on top: the deep sea
    /// floor, or a column whose soil is stripped.</summary>
    public static bool IsSaltColumn(Biome biome, int height, int dirtDepth) =>
        dirtDepth == 0
        || (biome == Biome.Ocean
            && height < WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth);

    /// <summary>Whether the column at (x, z) lies in a salt patch.</summary>
    public static bool InPatch(int x, int z) => PatchNoise.GetNoise(x, z) > WorldGenerationConfig.RockSaltThreshold;

    /// <summary>Whether the stone cell at (x, y, z), in a column of this biome, height and soil depth, is rock salt:
    /// equal to what <see cref="Stamp"/> writes over stone.</summary>
    public static bool HasSalt(int x, int y, int z, Biome biome, int height, int dirtDepth)
    {
        var depth = height - y;
        return depth >= 0 && depth < WorldGenerationConfig.RockSaltDepth && IsSaltColumn(biome, height, dirtDepth)
               && InPatch(x, z);
    }

    /// <summary>The lowest y a salt patch can reach in this chunk column: a chunk wholly below it is skipped.</summary>
    public static int LowestY(ColumnData column) => column.MinHeight - WorldGenerationConfig.RockSaltDepth + 1;

    /// <summary>Replaces the stone of every salt column's top <see cref="WorldGenerationConfig.RockSaltDepth"/> cells
    /// with rock salt; nothing else is touched.</summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int lowerBoundY, int worldZ0)
    {
        for (var x = 0; x < Chunk.Size; x++)
        {
            for (var z = 0; z < Chunk.Size; z++)
            {
                var i = (x << Chunk.SizeShift) | z;
                var height = column.Heights[i];
                if (height - WorldGenerationConfig.RockSaltDepth + 1 >= lowerBoundY + Chunk.Size || height < lowerBoundY)
                    continue;
                if (!IsSaltColumn(column.Biomes[i], height, column.DirtDepth[i]) || !InPatch(worldX0 + x, worldZ0 + z))
                    continue;
                StampColumn(blocks, x, z, height - lowerBoundY);
            }
        }
    }

    // The stone of one column's top cells, from local y top down, turned to salt
    private static void StampColumn(ushort[] blocks, int x, int z, int top)
    {
        for (var depth = 0; depth < WorldGenerationConfig.RockSaltDepth; depth++)
        {
            var y = top - depth;
            if (y < 0) return;
            if (y >= Chunk.Size) continue;
            var index = Chunk.Idx(x, y, z);
            if (blocks[index] == RockBlock)
                blocks[index] = SaltBlock;
        }
    }
}
