namespace silkgltriangle;

/// <summary>
/// Configuration for world generation and chunk management.
/// </summary>
public static class WorldGenerationConfig
{
    // View distance
    public const int ChunkRadius = 16;
    public const int ChunkRadiusSq = ChunkRadius * ChunkRadius;
    
    // How many completed meshes to upload to GPU per frame
    public const int MaxMeshUploadsPerFrame = 32;
    
    // How many dirty chunks to process per frame (kick off mesh gen)
    public const int MaxDirtyChunksPerFrame = 64;
    
    // How many mesh generation tasks can run concurrently on the thread pool
    public const int MaxConcurrentMeshGenerations = 4;
    
    // How many dedicated threads for block data generation
    public const int MaxConcurrentBlockDataGenerations = 4;
    
    // Delay when no chunks need generating (ms)
    public const int GenerationDelayIdleMs = 200;

    // --- Far-terrain LOD ---

    // Total render distance in chunks; the 16..32 ring is covered by LOD regions only
    public const int LodChunkRadius = ChunkRadius * 2;

    // A LOD region is a column of RegionChunkSpan x RegionChunkSpan chunks, meshed at half block resolution
    public const int RegionChunkSpan = 4;
    public const int LodRegionRadius = LodChunkRadius / RegionChunkSpan;

    // How many completed LOD region meshes to upload to GPU per frame
    public const int MaxLodUploadsPerFrame = 8;
}
