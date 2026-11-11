using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.WorldGen.Sky;

/// <summary>
/// The thick cloud sea under the sky islands (plans/LAYERS.md <c>L-12</c>): the same soft
/// <c>cloud_block</c> as the cloud patches (<see cref="CloudPatchGenerator.CloudBlock"/>), in the band
/// <see cref="SkyIslandConfig.CloudSeaBottomY"/>..<see cref="SkyIslandConfig.CloudSeaTopY"/>, but nearly
/// continuous: a <see cref="CloudLayer"/> over a broad, low-threshold cover noise, so it reads as a white
/// sea with a few wide holes down to the world below, its top domed by a finer noise. Falling off an
/// island lands in it softly (the block's <c>softLanding</c> and <c>sink</c>). A pure function of (x, z)
/// and the seed; <see cref="IsCloud"/> equals what <see cref="Stamp"/> writes into air bit for bit across
/// chunk seams. It only ever replaces air.
/// </summary>
public static class CloudSeaGenerator
{
    private static CloudLayer _layer;

    static CloudSeaGenerator() => Reseed();

    /// <summary>Builds the noises from the current seed (static constructor and <see cref="WorldSeed.Apply"/>).</summary>
    internal static void Reseed() =>
        _layer = new CloudLayer(SkyIslandConfig.CloudSeaBottomY, SkyIslandConfig.CloudSeaTopY,
            TerrainGenerator.MakeNoise(172, SkyIslandConfig.CloudSeaFrequency, octaves: 2),
            TerrainGenerator.MakeNoise(173, SkyIslandConfig.CloudSeaDetailFrequency),
            SkyIslandConfig.CloudSeaThreshold, SkyIslandConfig.CloudSeaCoreValue);

    /// <summary>How many cells the band spans.</summary>
    public static int SeaHeight => _layer.Height;

    /// <summary>The cloud sea cells of column (x, z), inclusive world y: false where the column is a hole.</summary>
    public static bool TryGetColumn(int x, int z, out int bottom, out int top) =>
        _layer.TryGetColumn(x, z, out bottom, out top);

    /// <summary>Whether the cell belongs to the cloud sea; on air this is what <see cref="Stamp"/> writes.</summary>
    public static bool IsCloud(int x, int y, int z) => _layer.Contains(x, y, z);

    /// <summary>Writes the cloud sea into the air cells of the chunk whose minimum corner is
    /// (worldX0, worldY0, worldZ0).</summary>
    public static void Stamp(ushort[] blocks, int worldX0, int worldY0, int worldZ0) =>
        _layer.Stamp(blocks, CloudPatchGenerator.CloudBlock, worldX0, worldY0, worldZ0);

    /// <summary>Every world y <see cref="Stamp"/> can write: the band itself.</summary>
    internal static StampBand WriteBand() => new(SkyIslandConfig.CloudSeaBottomY, SkyIslandConfig.CloudSeaTopY);
}
