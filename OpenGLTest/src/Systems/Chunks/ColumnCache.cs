using System;
using System.Numerics;
using static silkgltriangle.Chunk;

namespace silkgltriangle;

/// <summary>
/// The process-global, seed-dependent column-data cache and the accessors over it, extracted
/// from <see cref="Chunk"/>. Columns are a pure function of (world seed, chunk position), so
/// this is shared by chunk generation and the far LOD.
/// </summary>
public static class ColumnCache
{
    // The cap test used to be `ColumnCache.Count >= limit`, and ConcurrentDictionary.Count
    // acquires EVERY lock in the table; it was evaluated on every miss, on the path the four
    // chunk-gen threads and the LOD builder share. GenerationalCache counts its own inserts
    // with Interlocked instead. See GenerationalCache for the four-generation eviction rule.
    private static readonly GenerationalCache<(int X, int Z), ColumnData> Cache =
        new(WorldGenerationConfig.ColumnCacheCapacity, WorldGenerationConfig.ColumnCacheGenerations);

    // Static, so a miss on any of the four chunk-gen threads does not allocate a closure per call
    private static readonly Func<(int X, int Z), ColumnData> Compute =
        static key => TerrainGenerator.Compute(key.X, key.Z);

    /// <summary>
    /// Drops every cached column. Only <see cref="WorldSeed.Apply"/> calls it: the columns are a
    /// function of the seed, so a swap makes all of them wrong.
    /// </summary>
    internal static void Reset() => Cache.Clear();

    /// <summary>
    /// Returns the 2D generation inputs for a chunk column. Nothing generated is ever solid
    /// above Heights[x, z]; the block at that height is solid unless a cave or entrance carved
    /// it. Cached, deterministic, and safe to call from any thread.
    /// </summary>
    public static ColumnData Get(int chunkX, int chunkZ) =>
        Cache.GetOrAdd((chunkX, chunkZ), Compute);

    /// <summary>
    /// The terrain heightmap for a chunk column, indexed by (x &lt;&lt; SizeShift) | z. Ravines
    /// are already cut in; caves are not (they are 3D). See <see cref="Get"/>.
    /// </summary>
    public static int[] GetHeights(int chunkX, int chunkZ) => Get(chunkX, chunkZ).Heights;

    /// <summary>Per-column biomes for a chunk column, indexed like
    /// <see cref="GetHeights"/>. The far LOD reads this to pick biome surface tiles.</summary>
    public static Biome[] GetBiomes(int chunkX, int chunkZ) => Get(chunkX, chunkZ).Biomes;

    // Below any real height, so none of a missing streamed column's cells seed sunlight; a chunk
    // whose column has not arrived stays dark until it does and the chunk re-meshes.
    internal static readonly int[] NoStreamedHeights = CreateNoStreamedHeights();

    private static int[] CreateNoStreamedHeights()
    {
        var heights = new int[Size * Size];
        Array.Fill(heights, int.MinValue);
        return heights;
    }

    /// <summary>
    /// Fills one column of a chunk's block array from the chunk floor up to the terrain height:
    /// grass on top, dirt for dirtDepth blocks under it, stone below that. dirtDepth == 0 marks a
    /// bare stone column, deep ravine walls, where the top block is stone too. A wet column tops
    /// out in dirt instead. Shoreline columns build from sand, capped with gravel where
    /// <see cref="TerrainGenerator.IsGravelPatch"/> holds. <paramref name="terrainHeight"/>
    /// (ravines cut in) decides how deep to fill; <paramref name="bed"/> (ravines not cut) decides
    /// whether the column is water.
    /// </summary>
    internal static void GenerateColumn(byte[] blocks, int chunkX, int chunkZ, int x, int z,
        int terrainHeight, int bed, int dirtDepth, int lowerBoundY, Biome biome)
    {
        var worldX = chunkX * Size + x;
        var worldZ = chunkZ * Size + z;

        var topBlock = TerrainGenerator.IsWaterColumn(worldX, worldZ, bed)
            ? TerrainGenerator.SoilBlock
            : TerrainGenerator.SurfaceBlock;
        var soilBlock = TerrainGenerator.SoilBlock;
        var soilDepth = dirtDepth;

        switch (biome)
        {
            case Biome.Desert:
                // Dune sand over sandstone. A ravine that stripped the soil (dirtDepth 0)
                // still reads as bare rock, the same rule the forest column uses.
                topBlock = TerrainGenerator.SandBlock;
                soilBlock = TerrainGenerator.SandstoneBlock;
                soilDepth = dirtDepth == 0 ? 0 : WorldGenerationConfig.SandstoneDepth;
                break;

            case Biome.Ocean:
                // Depth below the waterline picks the floor: shelf sand, slope gravel, deep
                // basin stone. Each material fills its own band, so there is no soil gradient.
                (topBlock, soilBlock) =
                    terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanSandDepth
                        ? (TerrainGenerator.SandBlock, TerrainGenerator.SandBlock)
                        : terrainHeight >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.OceanGravelDepth
                            ? (TerrainGenerator.GravelBlock, TerrainGenerator.GravelBlock)
                            : (TerrainGenerator.RockBlock, TerrainGenerator.RockBlock);
                soilDepth = 2;
                break;

            case Biome.Frozen:
                // Barren rock rather than grass: the snow pass lays a drift on top, so the plains
                // read as rock-and-snow instead of a white field over soil.
                topBlock = TerrainGenerator.RockBlock;
                soilBlock = TerrainGenerator.RockBlock;
                soilDepth = 2;
                break;

            default:
                // Sand and gravel ring actual water only: the height band plus a lake or river rim.
                // Without the weight test a dry ravine floor in the band would wear sand in the
                // middle of the forest, which is what made random patches before.
                if (dirtDepth != 0 && TerrainGenerator.IsBeachHeight(terrainHeight)
                    && TerrainGenerator.SurfaceWaterWeight(worldX, worldZ) > 0f)
                {
                    topBlock = TerrainGenerator.IsGravelPatch(worldX, worldZ)
                        ? TerrainGenerator.GravelBlock
                        : TerrainGenerator.SandBlock;
                    soilBlock = TerrainGenerator.SandBlock;
                }
                break;
        }

        for (var y = 0; y < Size; y++)
        {
            var depth = terrainHeight - (lowerBoundY + y);
            if (depth < 0) break;

            blocks[Idx(x, y, z)] = soilDepth == 0 ? TerrainGenerator.RockBlock
                : depth == 0 ? topBlock
                : depth <= soilDepth ? soilBlock
                : TerrainGenerator.RockBlock;
        }
    }
}
