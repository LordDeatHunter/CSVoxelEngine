using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The solid cloud patches (plans/LAYERS.md <c>L-11</c>): patchy slabs of the translucent, soft
/// <c>cloud_block</c> in the sky band <see cref="WorldGenerationConfig.CloudSlabBottomY"/>..
/// <see cref="WorldGenerationConfig.CloudSlabTopY"/>, above every peak
/// (<see cref="WorldGenerationConfig.ReliefCeilingHeight"/>) and the drawn cloud layer. A column holds
/// cloud where a coherent 2D noise is above <see cref="WorldGenerationConfig.CloudPatchThreshold"/>;
/// how far above sets its thickness, one cell at a patch's edge up to the whole band in its core,
/// roughened by a finer noise that also lifts the top, so a patch is a flat-bottomed, unevenly domed
/// slab. A pure function of (x, z) and the seed: <see cref="TryGetColumn"/> is the one rule the stamp
/// and the point query share, so <see cref="IsCloud"/> equals what <see cref="Stamp"/> writes into air
/// bit for bit across chunk seams. Cloud only ever replaces air.
/// </summary>
public static class CloudPatchGenerator
{
    private static FastNoiseLite PatchNoise;
    private static FastNoiseLite DetailNoise;

    /// <summary>The cloud block; resolved once in the static constructor.</summary>
    public static readonly byte CloudBlock;

    static CloudPatchGenerator()
    {
        Reseed();
        CloudBlock = ContentRegistry.RequireBlock("cloud_block").Id;
    }

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed()
    {
        PatchNoise = TerrainGenerator.MakeNoise(166, WorldGenerationConfig.CloudPatchFrequency, octaves: 2);
        DetailNoise = TerrainGenerator.MakeNoise(167, WorldGenerationConfig.CloudPatchDetailFrequency);
    }

    /// <summary>How many cells the band spans.</summary>
    public static int SlabHeight => WorldGenerationConfig.CloudSlabTopY - WorldGenerationConfig.CloudSlabBottomY + 1;

    /// <summary>
    /// The cloud cells of column (x, z), inclusive world y: false where the column holds none. The
    /// range always lies inside the band, at least one cell thick, thicker the deeper the column
    /// lies in its patch.
    /// </summary>
    public static bool TryGetColumn(int x, int z, out int bottom, out int top)
    {
        bottom = 0;
        top = -1;
        var value = PatchNoise.GetNoise(x, z);
        if (value <= WorldGenerationConfig.CloudPatchThreshold) return false;

        var strength = MathF.Min(1f, (value - WorldGenerationConfig.CloudPatchThreshold)
                                     / (WorldGenerationConfig.CloudPatchCoreValue - WorldGenerationConfig.CloudPatchThreshold));
        var detail = 0.5f * (DetailNoise.GetNoise(x, z) + 1f);
        var height = SlabHeight;
        var thickness = Math.Clamp(1 + (int)(strength * (height - 1) * (0.6f + 0.4f * detail) + 0.5f), 1, height);

        // The spare cells mostly go above the cloud: a flat-ish bottom under a lifted, uneven top
        var room = height - thickness;
        bottom = WorldGenerationConfig.CloudSlabBottomY + (int)(room * (0.2f + 0.3f * detail) + 0.5f);
        top = bottom + thickness - 1;
        return true;
    }

    /// <summary>Whether the cell belongs to a cloud patch; on air this is what <see cref="Stamp"/> writes.</summary>
    public static bool IsCloud(int x, int y, int z) =>
        TryGetColumn(x, z, out var bottom, out var top) && y >= bottom && y <= top;

    /// <summary>Writes the cloud into the air cells of the chunk whose minimum corner is
    /// (worldX0, worldY0, worldZ0).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0)
    {
        var yMin = Math.Max(0, WorldGenerationConfig.CloudSlabBottomY - worldY0);
        var yMax = Math.Min(Chunk.Size - 1, WorldGenerationConfig.CloudSlabTopY - worldY0);
        if (yMin > yMax) return;

        for (var x = 0; x < Chunk.Size; x++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            if (!TryGetColumn(worldX0 + x, worldZ0 + z, out var bottom, out var top)) continue;

            var lo = Math.Max(yMin, bottom - worldY0);
            var hi = Math.Min(yMax, top - worldY0);
            for (var y = lo; y <= hi; y++)
            {
                var i = Chunk.Idx(x, y, z);
                if (blocks[i] == BlockType.Air)
                    blocks[i] = CloudBlock;
            }
        }
    }

    /// <summary>Every world y <see cref="Stamp"/> can write: the band itself.</summary>
    internal static StampBand WriteBand() =>
        new(WorldGenerationConfig.CloudSlabBottomY, WorldGenerationConfig.CloudSlabTopY);
}
