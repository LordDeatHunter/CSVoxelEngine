using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Simulation;

namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>
/// Gravel and sand pockets: blobs of loose rock buried inside stone, so mining into one can bring
/// a small collapse down (both are <c>falls</c> blocks, which <see cref="FallingBlockManager"/>
/// topples). A low-frequency 3D noise, sampled on the world-aligned <see cref="OreNoiseLattice"/>
/// and trilinearly interpolated, decides the blobs and a per-cell hash picks gravel or sand, so the
/// pockets are a pure function of world position and neighbors agree without shared state.
/// Only stone within a depth band under the surface is replaced — soil, ore and rock caps are left
/// alone. Runs on a generation worker thread after the carvers, like <see cref="LavaGenerator"/>.
/// </summary>
public static class GravelPocketGenerator
{
    private const int MaterialSalt = 0x6B1E77;

    private static FastNoiseLite PocketNoise;

    /// <summary>Blocks this generator stamps; resolved once in the static constructor.</summary>
    public static readonly ushort GravelBlock;
    public static readonly ushort SandBlock;

    private static readonly ushort StoneBlock;

    static GravelPocketGenerator()
    {
        Reseed();
        GravelBlock = ContentRegistry.RequireBlock("gravel").Id;
        SandBlock = ContentRegistry.RequireBlock("sand").Id;
        StoneBlock = ContentRegistry.RequireBlock("stone").Id;
    }

    /// <summary>
    /// Builds the noise from the current <see cref="WorldGenerationConfig.Seed"/>: once from the
    /// static constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed() => PocketNoise = TerrainGenerator.MakeNoise(93, WorldFeatureConfig.GravelPocketFrequency, octaves: 2);

    /// <summary>
    /// Whether the stone cell (x, y, z) is inside a pocket, buried at least
    /// <see cref="WorldFeatureConfig.GravelPocketMinBurial"/> under the column's surface: the
    /// interpolated <see cref="OreNoiseLattice"/> field exceeds the threshold there. Equals what
    /// <see cref="Stamp"/> writes for a stone cell.
    /// </summary>
    public static bool HasPocket(int x, int y, int z, int surfaceHeight)
    {
        if (y > surfaceHeight - WorldFeatureConfig.GravelPocketMinBurial) return false;
        return OreNoiseLattice.Exceeds(PocketNoise, x, y, z, WorldFeatureConfig.GravelPocketThreshold);
    }

    /// <summary>
    /// Replaces stone inside a pocket with gravel or sand: a pure function of the cell, so the
    /// same world always has the same pockets wherever a chunk is generated. A chunk lying wholly
    /// above every column's burial band is skipped outright.
    /// </summary>
    public static void Stamp(ushort[] blocks, ColumnData column, int worldX0, int worldY0, int worldZ0)
    {
        // Every cell's y is at least worldY0, so none can be buried deep enough
        if (worldY0 > column.MaxHeight - WorldFeatureConfig.GravelPocketMinBurial) return;
        OreNoiseLattice.Stamp(blocks, PocketNoise, WorldFeatureConfig.GravelPocketThreshold,
            worldX0, worldY0, worldZ0, new PocketRule(column.Heights, worldY0));
    }

    /// <summary>
    /// A pocket replaces stone buried at least <see cref="WorldFeatureConfig.GravelPocketMinBurial"/>
    /// under its column's surface, with sand or gravel picked by a per-cell hash.
    /// </summary>
    private readonly struct PocketRule(int[] heights, int worldY0) : IOreLatticeRule
    {
        public bool IsHost(ushort block, int localX, int localY, int localZ) =>
            block == StoneBlock
            && worldY0 + localY <= heights[(localX << Chunk.SizeShift) | localZ] - WorldFeatureConfig.GravelPocketMinBurial;

        public ushort Replacement(int worldX, int worldY, int worldZ) =>
            UnitHash(worldX, worldY, worldZ) < WorldFeatureConfig.GravelPocketSandShare ? SandBlock : GravelBlock;

        public float ThresholdShift(int localX, int localY, int localZ) => 0f;

        public float MinThresholdShift => 0f;
    }

    private static float UnitHash(int x, int y, int z)
    {
        var h = SplitMix64.Hash(x ^ (y * 0x9E37), z, WorldGenerationConfig.Seed ^ MaterialSalt);
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
