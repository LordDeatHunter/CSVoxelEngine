using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Features;

/// <summary>
/// Fallen logs: a rare hollow log of the biome's tree species lying along X or Z on the floor,
/// decided per 2^<see cref="WorldFeatureConfig.FallenLogCellShift"/>-block cell from a pure hash
/// of (cell, seed), so every chunk the log reaches stamps the same cells (clipped at its border).
/// The block is the species' <c>worldgen.fallenLog</c>: a facing model block (never
/// <c>tree_wood</c>), so a log never welds, fells or feeds leaves, and its state byte holds the
/// facing that turns the tube along the log's axis. It is worldgen-only, drops and is picked as
/// the plain log, and the far LOD shows none. The start column's ground is read from
/// <see cref="ColumnCache"/> like the pebble scatter's, so a log near a chunk border is whole.
/// </summary>
public static class FallenLogGenerator
{
    private const ulong AcceptStream = 0x9E3779B97F4A7C15UL;
    private const ulong StartXStream = unchecked(AcceptStream * 2);
    private const ulong StartZStream = unchecked(AcceptStream * 3);
    private const ulong LengthStream = unchecked(AcceptStream * 4);
    private const ulong AxisStream = unchecked(AcceptStream * 5);

    /// <summary>
    /// The facing a log lying along Z is stamped with. The engine's facing order is north 0,
    /// south 1, west 2, east 3, and the hollow-log model is authored as a tube along Z facing
    /// north, so north keeps the tube along Z.
    /// </summary>
    public const byte FacingAlongZ = 0;

    /// <summary>The facing a log lying along X is stamped with: west turns the north-authored tube along X.</summary>
    public const byte FacingAlongX = 2;

    // The hollow log of each biome's worldgen tree species, indexed by (int)Biome; 0 = none
    private static readonly byte[] LogByBiome;
    private static readonly bool[] FallenLogById = new bool[256];

    static FallenLogGenerator()
    {
        var species = TreeGenerator.SpeciesByBiome;
        LogByBiome = new byte[species.Length];
        for (var b = 0; b < species.Length; b++)
        {
            var id = species[b]?.Worldgen?.FallenLogId ?? 0;
            LogByBiome[b] = id;
            if (id != 0) FallenLogById[id] = true;
        }
    }

    /// <summary>
    /// The hollow log of the biome's worldgen tree species, or 0 where the species declares none
    /// (the desert's cactus, the dead tree of the tundra and steppe) or no tree grows.
    /// </summary>
    public static byte HollowLogFor(Biome biome) => LogByBiome[(int)biome];

    /// <summary>Whether a block is one this generator stamps as some biome's fallen log.</summary>
    public static bool IsFallenLog(byte id) => FallenLogById[id];

    /// <summary>
    /// The log a cell holds: its start column (jittered inside the cell), its length and its axis
    /// (along X or along Z), or false for the many cells without one. A pure function of the cell
    /// and the seed.
    /// </summary>
    public static bool TryGetLog(int cellX, int cellZ, out int startX, out int startZ, out int length,
        out bool alongX)
    {
        startX = startZ = 0;
        length = 0;
        alongX = true;
        var h = SplitMix64.Hash(cellX, cellZ, WorldGenerationConfig.Seed ^ 0x10A5);
        if (Unit(SplitMix64.Mix(h + AcceptStream)) >= WorldFeatureConfig.FallenLogChance) return false;

        var size = 1 << WorldFeatureConfig.FallenLogCellShift;
        var span = (ulong)(size - WorldFeatureConfig.FallenLogMaxLength + 1);
        startX = (cellX << WorldFeatureConfig.FallenLogCellShift) + (int)(SplitMix64.Mix(h + StartXStream) % span);
        startZ = (cellZ << WorldFeatureConfig.FallenLogCellShift) + (int)(SplitMix64.Mix(h + StartZStream) % span);
        var lengths = WorldFeatureConfig.FallenLogMaxLength - WorldFeatureConfig.FallenLogMinLength + 1;
        length = WorldFeatureConfig.FallenLogMinLength + (int)(SplitMix64.Mix(h + LengthStream) % (ulong)lengths);
        alongX = (SplitMix64.Mix(h + AxisStream) & 1) == 0;
        return true;
    }

    /// <summary>
    /// The log a cell places: <see cref="TryGetLog"/>'s hashed log, kept only where its start
    /// column's biome has a hollow log (<see cref="HollowLogFor"/>), its stored (dithered) surface
    /// biome is that same biome, so a log never lies on another biome's dithered ground, and the
    /// column is dry. <paramref name="ground"/> is the start column's height and
    /// <paramref name="log"/> the block every cell takes: the biome is decided at the start column
    /// only, so every chunk the log reaches stamps the same block. The one rule the stamp and the
    /// tests share.
    /// </summary>
    public static bool TryGetPlacedLog(int cellX, int cellZ, out int startX, out int startZ, out int length,
        out bool alongX, out int ground, out byte log)
    {
        ground = 0;
        log = BlockType.Air;
        if (!TryGetLog(cellX, cellZ, out startX, out startZ, out length, out alongX)) return false;

        var biome = TerrainGenerator.BiomeAt(startX, startZ);
        log = HollowLogFor(biome);
        if (log == BlockType.Air) return false;

        var column = ColumnCache.Get(VoxelCoords.WorldToChunk(startX), VoxelCoords.WorldToChunk(startZ));
        var index = VoxelCoords.ColumnIndex(VoxelCoords.WorldToLocal(startX), VoxelCoords.WorldToLocal(startZ));
        ground = column.Heights[index];
        if (column.Biomes[index] != biome) return false;
        return !TerrainGenerator.IsWaterColumn(startX, startZ, ground);
    }

    /// <summary>
    /// Stamps every log that reaches this chunk: its cells at one height (the start column's
    /// ground plus one), into air only, clipped at the chunk's border, each with the facing that
    /// turns its tube along the log's axis. Runs on a generation worker thread after the trees,
    /// so a log never displaces one.
    /// </summary>
    public static void Stamp(byte[] blocks, ref byte[]? state, int worldX0, int worldY0, int worldZ0)
    {
        var reach = WorldFeatureConfig.FallenLogMaxLength;
        var shift = WorldFeatureConfig.FallenLogCellShift;
        var minCellX = (worldX0 - reach) >> shift;
        var maxCellX = (worldX0 + Chunk.Size - 1) >> shift;
        var minCellZ = (worldZ0 - reach) >> shift;
        var maxCellZ = (worldZ0 + Chunk.Size - 1) >> shift;
        for (var cellX = minCellX; cellX <= maxCellX; cellX++)
        for (var cellZ = minCellZ; cellZ <= maxCellZ; cellZ++)
        {
            if (!TryGetPlacedLog(cellX, cellZ, out var startX, out var startZ, out var length, out var alongX,
                    out var ground, out var log))
                continue;

            var ly = ground + 1 - worldY0;
            if ((uint)ly >= Chunk.Size) continue;
            var facing = alongX ? FacingAlongX : FacingAlongZ;
            for (var i = 0; i < length; i++)
            {
                var lx = (alongX ? startX + i : startX) - worldX0;
                var lz = (alongX ? startZ : startZ + i) - worldZ0;
                if ((uint)lx >= Chunk.Size || (uint)lz >= Chunk.Size) continue;
                var index = Chunk.Idx(lx, ly, lz);
                if (blocks[index] != BlockType.Air) continue;
                blocks[index] = log;
                // Assign, never OR: the block id decides the state byte's meaning (a facing here)
                if (facing != 0 || state != null)
                    (state ??= new byte[Chunk.Size * Chunk.Size * Chunk.Size])[index] = facing;
            }
        }
    }

    /// <summary>
    /// The world-y band <see cref="Stamp"/> can write, given the tallest ground height within
    /// <see cref="WorldFeatureConfig.FallenLogMaxLength"/> blocks of the chunk: every cell sits at
    /// <c>ly = ground + 1 - worldY0</c> for the start column's ground, and a log that writes here
    /// has a cell inside the chunk, so its start lies within its length (at most
    /// <see cref="WorldFeatureConfig.FallenLogMaxLength"/>) of it. Its site may sit in a neighbor
    /// chunk column, so only the upper bound is used.
    /// </summary>
    internal static StampBand WriteBand(int neighborhoodMaxHeight) => new(int.MinValue, neighborhoodMaxHeight + 1);

    private static float Unit(ulong h) => (h >> 40) * (1f / (1 << 24));
}
