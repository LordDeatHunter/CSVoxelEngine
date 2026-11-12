using System.Numerics;
using VoxelEngine.Entities;
using VoxelEngine.Simulation.Icebergs;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A headless sea for the iceberg tests: 5x5 chunk columns, two chunks deep (y -16..15), a stone
/// floor at <see cref="FloorY"/> under source water up to <see cref="WorldGenerationConfig.SeaLevel"/>,
/// and optionally a strip of ice on the top water layer from <see cref="IceMinX"/> east (the
/// pack-ice edge). Its <see cref="ChunkManager.WorldSource"/> is a <see cref="FlatWorldSource"/>
/// describing the same sea (an ocean column at sea level) far past the loaded chunks, so a spawn
/// ring never reaches a worldgen column; it is used only for its columns: the chunks are set by hand.
/// </summary>
internal sealed class IcebergTestWorld
{
    /// <summary>The top cell of the stone floor, deeper than the largest berg's draft.</summary>
    public const int FloorY = -13;

    /// <summary>The westmost world x of the ice strip.</summary>
    public const int IceMinX = 24;

    /// <summary>The lowest and highest chunk column on each horizontal axis.</summary>
    public const int ChunkMin = -2;
    public const int ChunkMax = 2;

    // How far the described (supplied) sea reaches, in chunk columns, past every spawn ring
    private const int SourceReach = 64;

    private IcebergTestWorld(ChunkManager chunks) => Chunks = chunks;

    public ChunkManager Chunks { get; }

    /// <summary>The water surface of the sea.</summary>
    public float Waterline => Chunks.GetFluidColumnTopGlobal(0, WorldGenerationConfig.SeaLevel, 0);

    /// <summary>The iceberg entity type.</summary>
    public static EntityType Type => EntityRegistry.Require("iceberg");

    /// <summary>The sea, with the ice strip when <paramref name="ice"/>, its columns in <paramref name="biome"/>.</summary>
    public static IcebergTestWorld Create(bool ice = true, Biome biome = Biome.Ocean)
    {
        var chunks = new ChunkManager(null!);
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var water = ContentRegistry.RequireBlock("water").Id;
        var iceId = ContentRegistry.RequireBlock("ice").Id;
        for (var cx = ChunkMin; cx <= ChunkMax; cx++)
        for (var cz = ChunkMin; cz <= ChunkMax; cz++)
        for (var cy = -1; cy <= 0; cy++)
        {
            var pos = new Vector3(cx, cy, cz);
            chunks.Chunks[pos] = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
        }

        var minX = ChunkMin * Chunk.Size;
        var maxX = (ChunkMax + 1) * Chunk.Size;
        for (var x = minX; x < maxX; x++)
        for (var z = minX; z < maxX; z++)
        {
            Set(chunks, x, FloorY, z, stone);
            for (var y = FloorY + 1; y <= WorldGenerationConfig.SeaLevel; y++)
                Set(chunks, x, y, z, ice && x >= IceMinX && y == WorldGenerationConfig.SeaLevel ? iceId : water);
        }

        chunks.WorldSource = new FlatWorldSource(-SourceReach, SourceReach, -SourceReach, SourceReach, FloorY, stone, biome,
            warmth: 0f, wetness: 0.5f, waterLevel: WorldGenerationConfig.SeaLevel);
        return new IcebergTestWorld(chunks);
    }

    private static void Set(ChunkManager chunks, int x, int y, int z, ushort id) =>
        chunks.ChunkAt(x, y, z)!.SetBlockRaw(x & Chunk.SizeMask, y & Chunk.SizeMask, z & Chunk.SizeMask, id, 0);

    /// <summary>A berg's waterline center over column (<paramref name="x"/>, <paramref name="z"/>).</summary>
    public Vector3 Center(float x, float z) => new(x, Waterline, z);

    /// <summary>A manager over the iceberg type with a fixed random seed.</summary>
    public static IcebergManager Manager() => new(Type, randomSeed: 1);
}
