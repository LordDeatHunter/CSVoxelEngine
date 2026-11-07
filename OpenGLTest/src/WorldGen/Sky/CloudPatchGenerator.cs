using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The solid cloud patches (plans/LAYERS.md <c>L-11</c>): patchy slabs of the translucent, soft
/// <c>cloud_block</c> in the sky band <see cref="WorldGenerationConfig.CloudSlabBottomY"/>..
/// <see cref="WorldGenerationConfig.CloudSlabTopY"/>, above every peak
/// (<see cref="WorldGenerationConfig.ReliefCeilingHeight"/>) and the drawn cloud layer. A
/// <see cref="CloudLayer"/> over a coherent 2D noise above <see cref="WorldGenerationConfig.CloudPatchThreshold"/>:
/// one cell at a patch's edge up to the whole band in its core. A pure function of (x, z) and the seed:
/// <see cref="TryGetColumn"/> is the one rule the stamp and the point query share, so
/// <see cref="IsCloud"/> equals what <see cref="Stamp"/> writes into air bit for bit across chunk seams.
/// Cloud only ever replaces air.
/// </summary>
public static class CloudPatchGenerator
{
    private static CloudLayer _layer;

    /// <summary>The cloud block; resolved once in the static constructor.</summary>
    public static readonly byte CloudBlock;

    static CloudPatchGenerator()
    {
        Reseed();
        CloudBlock = ContentRegistry.RequireBlock("cloud_block").Id;
    }

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() =>
        _layer = new CloudLayer(WorldGenerationConfig.CloudSlabBottomY, WorldGenerationConfig.CloudSlabTopY,
            TerrainGenerator.MakeNoise(166, WorldGenerationConfig.CloudPatchFrequency, octaves: 2),
            TerrainGenerator.MakeNoise(167, WorldGenerationConfig.CloudPatchDetailFrequency),
            WorldGenerationConfig.CloudPatchThreshold, WorldGenerationConfig.CloudPatchCoreValue);

    /// <summary>How many cells the band spans.</summary>
    public static int SlabHeight => _layer.Height;

    /// <summary>The cloud cells of column (x, z), inclusive world y: false where the column holds none
    /// (<see cref="CloudLayer.TryGetColumn"/>).</summary>
    public static bool TryGetColumn(int x, int z, out int bottom, out int top) =>
        _layer.TryGetColumn(x, z, out bottom, out top);

    /// <summary>Whether the cell belongs to a cloud patch; on air this is what <see cref="Stamp"/> writes.</summary>
    public static bool IsCloud(int x, int y, int z) => _layer.Contains(x, y, z);

    /// <summary>Writes the cloud into the air cells of the chunk whose minimum corner is
    /// (worldX0, worldY0, worldZ0).</summary>
    public static void Stamp(byte[] blocks, int worldX0, int worldY0, int worldZ0) =>
        _layer.Stamp(blocks, CloudBlock, worldX0, worldY0, worldZ0);

    /// <summary>Every world y <see cref="Stamp"/> can write: the band itself.</summary>
    internal static StampBand WriteBand() =>
        new(WorldGenerationConfig.CloudSlabBottomY, WorldGenerationConfig.CloudSlabTopY);
}
