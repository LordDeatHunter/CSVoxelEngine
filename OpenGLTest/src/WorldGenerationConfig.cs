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
}
