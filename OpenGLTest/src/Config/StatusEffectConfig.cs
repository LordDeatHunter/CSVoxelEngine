namespace VoxelEngine.Config;

/// <summary>
/// Status effect tuning (<see cref="VoxelEngine.Status.StatusEffectSet"/>): the shared limits every
/// effect from <c>assets/data/effects</c> runs under. The per-effect numbers live in the effect JSON.
/// </summary>
public static class StatusEffectConfig
{
    /// <summary>The largest duration scale one application may ask for: a caller's scale is clamped to
    /// 0..this before it multiplies the effect's own duration.</summary>
    public const float MaxDurationScale = 4f;

    /// <summary>How far, in blocks horizontally, around an affected body's feet its effect's block
    /// actions reach (<see cref="VoxelEngine.Status.StatusEffectBlockWriter"/>); vertically the scan is
    /// the cell under the feet, the feet's own cell and the one above.</summary>
    public const int BlockReachBlocks = 1;

    /// <summary>The most cells one body's effect pulse may convert, so a burning mob in a canopy
    /// thins it rather than clearing it at once.</summary>
    public const int MaxBlockWritesPerPulse = 2;

    /// <summary>The most cells every effect pulse together may convert in one frame, so a crowd of
    /// burning or frozen bodies never floods the remesh queue.</summary>
    public const int MaxBlockWritesPerFrame = 6;

    /// <summary>How far an affected mob's albedo is mixed toward its effect's tint color (0..1), the
    /// alpha of the world shader's <c>uStatusTint</c>; under the hit flash, so a hit still reads.</summary>
    public const float MobTintStrength = 0.45f;

    /// <summary>The most affected mobs, nearest to the eye first, that emit effect particles in one
    /// frame, so a crowd never floods the particle pool.</summary>
    public const int MaxParticleEmitters = 16;

    /// <summary>The mean particles per second each active effect with a particle kind sheds off an
    /// affected mob.</summary>
    public const float ParticlesPerSecond = 6f;
}
