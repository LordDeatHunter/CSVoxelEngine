namespace VoxelEngine.Entities.Icebergs;

/// <summary>
/// Where in the cold a berg may spawn: open water just outside the pack-ice edge, i.e. a cold
/// weight from <see cref="IcebergConfig.SpawnColdMin"/> (a little below
/// <see cref="WorldGenerationConfig.PackIceColdStart"/>) up to
/// <see cref="WorldGenerationConfig.FreezeColdThreshold"/>, where the sheet closes, and never on
/// water the pack-ice rule froze (<c>SnowGenerator.IsFrozenWater</c>). GL-free and state-free.
/// </summary>
public static class IcebergSpawnRule
{
    /// <summary>
    /// Whether a column of cold weight <paramref name="cold"/> whose top water is
    /// <paramref name="frozen"/> or open lies in the spawn band.
    /// </summary>
    public static bool InBand(float cold, bool frozen) =>
        !frozen && cold >= IcebergConfig.SpawnColdMin && cold <= WorldGenerationConfig.FreezeColdThreshold;
}
