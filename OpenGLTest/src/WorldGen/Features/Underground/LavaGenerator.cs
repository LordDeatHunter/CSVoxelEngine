using VoxelEngine.Chunks;
using VoxelEngine.Content;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Lava pockets deep underground: cave floors at or below <see cref="WorldGenerationConfig.LavaMaxY"/>
/// take one or two lava sources at the spaced 3D candidates from CaveFluidSources.
/// A pure function of world position like the other generators, so neighbors agree without
/// shared state. Runs on a generation worker thread after the carvers and <c>FillWater</c>:
/// flooded cells are water, not air, so sea caves never pond lava, and the height check keeps
/// ravine floors and entrances (which reach deep but open to the sky) dry.
/// </summary>
public static class LavaGenerator
{
    private static FastNoiseLite PatchNoise;

    /// <summary>Block this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort LavaBlock;

    static LavaGenerator()
    {
        Reseed();
        LavaBlock = ContentRegistry.RequireBlock("lava").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => PatchNoise = TerrainGenerator.MakeNoise(71, WorldGenerationConfig.LavaPatchFrequency);

    /// <summary>The patch mask's noise at column (x, z): only a column above
    /// <see cref="WorldGenerationConfig.LavaPatchThreshold"/> ponds lava (the vulcanite in the barrier band reads it too).</summary>
    internal static float PatchValue(int x, int z) => PatchNoise.GetNoise(x, z);

    /// <summary>
    /// Whether cell (x, y, z) is a sparse lava-source candidate: below the lava
    /// ceiling, above the barrier and buried under the terrain. Stamp also requires a dry floor.
    /// </summary>
    public static bool HasLava(int x, int y, int z, int surfaceHeight)
    {
        if (y > WorldGenerationConfig.LavaMaxY) return false;
        if (y > surfaceHeight - WorldGenerationConfig.LavaBurial) return false;
        if (y <= WorldGenerationConfig.BarrierTopY) return false;
        return CaveFluidSources.Contains(x, y, z);
    }

    /// <summary>
    /// Stamps lava onto cave floors: air cells over a solid, non-fluid floor. The bottom
    /// layer is skipped (its floor lives in the chunk below, which this chunk cannot see),
    /// so ponded lava never floats.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        if (worldY0 > WorldGenerationConfig.LavaMaxY) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var surfaceHeight = column.Heights[(x << Chunk.SizeShift) | z];

            for (var ly = 1; ly < Chunk.Size; ly++)
            {
                var y = worldY0 + ly;
                if (y > WorldGenerationConfig.LavaMaxY) break;
                if (y > surfaceHeight - WorldGenerationConfig.LavaBurial) continue;

                var i = Chunk.Idx(x, ly, z);
                if (blocks[i] != BlockType.Air) continue;
                var floor = blocks[Chunk.Idx(x, ly - 1, z)];
                if (!BlockType.IsSolid(floor) || !BlockType.Occludes(floor)) continue;
                if (!HasLava(worldX0 + x, y, worldZ0 + z, surfaceHeight)) continue;

                blocks[i] = LavaBlock;
            }
        }
    }

}
