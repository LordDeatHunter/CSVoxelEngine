using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// The cave biome exclusive ores (plans/CAVE_BIOMES.md CB-2): amethyst in crystal caves, jade in fungal caves
/// and cinnabar in the magma-touched deep, each a vein field of its own 3D noise (seed offsets 183, 184, 185) on
/// the shared <see cref="OreNoiseLattice"/>, like the Y-band ores. A vein cell becomes ore only where it is plain
/// stone, its <see cref="CaveBiomeMap"/> cell is the ore's biome and it lies at or below the ore's highest y, so
/// <see cref="HasOre"/>, given the cell's <see cref="CaveBiomes.At"/> biome, equals what <see cref="Stamp"/>
/// writes bit for bit across every seam. The echoes and plain rock have none. Runs right after the Y-band ores
/// and before the palette pass, which rewrites only exact stone, so the ore survives inside the magma-touched
/// deep's scorched hellrock too. A pure function of position, the cell's cave biome and the seed; thread-safe:
/// the noises are read-only after their reseed.
/// </summary>
public static class CaveOres
{
    private static readonly int BiomeCount = Enum.GetValues<CaveBiome>().Length;

    // Indexed by CaveBiome; only the three sub-band biomes carry an ore
    private static readonly FastNoiseLite?[] Noises = new FastNoiseLite?[BiomeCount];
    private static readonly ushort[] Ores = new ushort[BiomeCount];
    private static readonly float[] Thresholds = new float[BiomeCount];
    private static readonly int[] MaxYs = new int[BiomeCount];

    private static readonly CaveBiome[] OreBiomes = [CaveBiome.Crystal, CaveBiome.Fungal, CaveBiome.MagmaDeep];

    /// <summary>The block every exclusive ore replaces: plain stone and nothing else.</summary>
    private static readonly ushort RockBlock;

    static CaveOres()
    {
        Reseed();
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
        Ores[(int)CaveBiome.Crystal] = ContentRegistry.RequireBlock("amethyst_ore").Id;
        Ores[(int)CaveBiome.Fungal] = ContentRegistry.RequireBlock("jade_ore").Id;
        Ores[(int)CaveBiome.MagmaDeep] = ContentRegistry.RequireBlock("cinnabar_ore").Id;
        Thresholds[(int)CaveBiome.Crystal] = WorldGenerationConfig.CaveAmethystThreshold;
        Thresholds[(int)CaveBiome.Fungal] = WorldGenerationConfig.CaveJadeThreshold;
        Thresholds[(int)CaveBiome.MagmaDeep] = WorldGenerationConfig.CaveCinnabarThreshold;
        MaxYs[(int)CaveBiome.Crystal] = WorldGenerationConfig.CaveAmethystMaxY;
        MaxYs[(int)CaveBiome.Fungal] = WorldGenerationConfig.CaveJadeMaxY;
        MaxYs[(int)CaveBiome.MagmaDeep] = WorldGenerationConfig.CaveCinnabarMaxY;
    }

    /// <summary>
    /// Builds the noises from the current <see cref="WorldGenerationConfig.Seed"/>: once from the static
    /// constructor, and again by <see cref="WorldSeed.Apply"/> while no worker thread runs.
    /// </summary>
    internal static void Reseed()
    {
        Noises[(int)CaveBiome.Crystal] = TerrainGenerator.MakeNoise(183, WorldGenerationConfig.CaveAmethystFrequency);
        Noises[(int)CaveBiome.Fungal] = TerrainGenerator.MakeNoise(184, WorldGenerationConfig.CaveJadeFrequency);
        Noises[(int)CaveBiome.MagmaDeep] = TerrainGenerator.MakeNoise(185, WorldGenerationConfig.CaveCinnabarFrequency);
    }

    /// <summary>The ore exclusive to <paramref name="biome"/>, or 0 when it has none (plain and the echoes).</summary>
    public static ushort OreOf(CaveBiome biome) => Ores[(int)biome];

    /// <summary>The highest world y the ore of <paramref name="biome"/> grows at (only meaningful when it has one).</summary>
    internal static int MaxYOf(CaveBiome biome) => MaxYs[(int)biome];

    /// <summary>
    /// Whether a stone cell (x, y, z) of cave biome <paramref name="biome"/> is that biome's exclusive ore: the
    /// biome has one, the cell lies above the barrier and at or below the ore's highest y, and the ore's
    /// <see cref="OreNoiseLattice"/> field exceeds its threshold there. Threshold test only; the caller decides
    /// what counts as a host (plain stone).
    /// </summary>
    public static bool HasOre(int x, int y, int z, CaveBiome biome)
    {
        var b = (int)biome;
        return Ores[b] != 0 && y <= MaxYs[b] && y > WorldGenerationConfig.BarrierTopY
            && OreNoiseLattice.Exceeds(Noises[b]!, x, y, z, Thresholds[b]);
    }

    /// <summary>
    /// The world-y band <see cref="Stamp"/> can write: above the barrier, up to the highest of the three ores'
    /// highest y (<see cref="StampBand"/>).
    /// </summary>
    internal static StampBand WriteBand()
    {
        var max = int.MinValue;
        foreach (var biome in OreBiomes) max = Math.Max(max, MaxYs[(int)biome]);
        return new StampBand(WorldGenerationConfig.BarrierTopY + 1, max);
    }

    /// <summary>
    /// Replaces every plain stone cell of the chunk at world origin (worldX0, worldY0, worldZ0) that lies inside
    /// its cave biome's exclusive vein field with that ore. <paramref name="caves"/> is the chunk's cave biome map;
    /// an ore whose biome the map lacks, or whose highest y lies under the chunk, is skipped unread. One scan of
    /// the chunk marks every ore's host lattice cells at once, and each ore then samples only its own.
    /// </summary>
    public static void Stamp(ushort[] blocks, CaveBiomeMap caves, int worldX0, int worldY0, int worldZ0)
    {
        var minLocalY = WorldGenerationConfig.BarrierTopY + 1 - worldY0;
        Span<int> maxLocalY = stackalloc int[BiomeCount];
        var anyOre = false;
        for (var b = 0; b < BiomeCount; b++)
        {
            // int.MinValue: no ore here (plain, an echo, absent from the map or wholly under the chunk)
            var live = Ores[b] != 0 && caves.Has((CaveBiome)b) && worldY0 <= MaxYs[b];
            maxLocalY[b] = live ? MaxYs[b] - worldY0 : int.MinValue;
            anyOre |= live;
        }

        if (!anyOre) return;

        // One host-cell mask per biome; each rule tests its cells again, so the mask only has to cover them
        Span<bool> hostCells = stackalloc bool[BiomeCount * OreNoiseLattice.HostCellCount];
        Span<bool> anyHost = stackalloc bool[BiomeCount];
        var biomes = caves.Cells;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = Math.Max(0, minLocalY); y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var idx = Chunk.Idx(x, y, z);
            var b = biomes[idx];
            if (y > maxLocalY[b] || blocks[idx] != RockBlock) continue;

            hostCells[b * OreNoiseLattice.HostCellCount + OreNoiseLattice.CellOf(x, y, z)] = true;
            anyHost[b] = true;
        }

        foreach (var biome in OreBiomes)
        {
            var b = (int)biome;
            if (!anyHost[b]) continue;

            OreNoiseLattice.StampHostCells(blocks, Noises[b]!, Thresholds[b], worldX0, worldY0, worldZ0,
                new BiomeVeinRule(biomes, (byte)biome, Ores[b], minLocalY, maxLocalY[b]),
                hostCells.Slice(b * OreNoiseLattice.HostCellCount, OreNoiseLattice.HostCellCount));
        }
    }

    /// <summary>
    /// An exclusive ore replaces plain stone of its own biome only, between the local y just above the barrier
    /// and its highest local y (the same band <see cref="HasOre"/> tests).
    /// </summary>
    private readonly struct BiomeVeinRule(byte[] biomes, byte biome, ushort ore, int minLocalY, int maxLocalY)
        : IOreLatticeRule
    {
        public bool IsHost(ushort block, int localX, int localY, int localZ) =>
            localY >= minLocalY && localY <= maxLocalY && block == RockBlock
            && biomes[Chunk.Idx(localX, localY, localZ)] == biome;

        public ushort Replacement(int worldX, int worldY, int worldZ) => ore;

        public float ThresholdShift(int localX, int localY, int localZ) => 0f;

        public float MinThresholdShift => 0f;
    }
}
