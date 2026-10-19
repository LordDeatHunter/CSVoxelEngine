namespace VoxelEngine.Config;

/// <summary>
/// World save tuning: the autosave cadence, the save writer's move retries and shutdown, the
/// teardown's worker joins and mesh drain, and the saved-entity restore.
/// </summary>
public static class SaveConfig
{
    // --- Saves ---
    // Seconds between autosaves of the loaded, edited chunks while a world with a save store runs.
    public const double AutosaveIntervalSeconds = 60.0;
    // Chunk snapshots one autosave call takes, so a large burst of edits spreads over frames.
    public const int MaxAutosaveSnapshotsPerCall = 256;
    // How long closing a world waits for the save writer thread to land its queued files.
    public const double SaveWriterShutdownTimeoutSeconds = 10.0;
    // Attempts to move a written temp file into place, since a reader or scanner may hold it briefly.
    public const int SaveMoveRetries = 3;
    // Pause between those move attempts.
    public const int SaveMoveRetryDelayMs = 50;
    // How long stopping the chunk-generation and LOD workers waits to join each thread.
    public const int WorkerStopJoinTimeoutMs = 5000;
    // How long closing a world waits for in-flight mesh builds (they read the worldgen caches).
    public const int MeshDrainTimeoutMs = 5000;
    // Saved mobs and drops whose chunk never loads are dropped after this many seconds of play.
    public const double EntityRestoreTimeoutSeconds = 30.0;
    // Nearest-first cap on the item drops one save writes.
    public const int MaxSavedDrops = 512;
}
