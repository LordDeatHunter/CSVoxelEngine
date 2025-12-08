namespace silkgltriangle;

/// <summary>
/// Configuration for world generation and chunk management.
/// </summary>
public static class WorldGenerationConfig
{
    // View distance
    public const int ChunkRadius = 12;
    public const int ChunkRadiusSq = ChunkRadius * ChunkRadius;
    
    // Performance tuning
    public const int MaxChunksPerFrame = 64;
    public const int MaxDirtyChunksPerFrame = 128;
    public const int MaxConcurrentMeshGenerations = 32;
    
    // Chunk generation timing
    public const int MaxChunksQueuedPerCycle = 64;
    public const int GenerationDelayActiveMs = 50;
    public const int GenerationDelayIdleMs = 150;
    public const int YieldDelayMs = 5;
    public const int YieldEveryNChunks = 16;
    
    // Ring processing
    public const int CloseRingThreshold = 3;
    public const int CloseRingDelayMs = 20;
}

