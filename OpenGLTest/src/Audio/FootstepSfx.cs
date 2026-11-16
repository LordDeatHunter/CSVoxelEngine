namespace VoxelEngine.Audio;

/// <summary>
/// The footstep sound rules, GL-free: a body steps audibly when it walks on the ground (not flying,
/// not afloat, not creeping) fast enough to be walking, and a mob steps once per
/// <see cref="AudioConfig.MobStrideBlocks"/> of ground it covers (<see cref="Stride"/>). Which
/// sound it makes is the block underfoot's <see cref="BlockType.StepSfx"/>; <see cref="Footsteps"/>
/// plays them.
/// </summary>
public static class FootstepSfx
{
    /// <summary>Whether a footfall right now is heard: on the ground, out of the water, not flying or
    /// sneaking, and at least <see cref="AudioConfig.FootstepMinSpeed"/> sideways.</summary>
    public static bool Audible(bool grounded, bool flying, bool sneaking, bool inWater, float horizontalSpeed) =>
        grounded && !flying && !sneaking && !inWater && horizontalSpeed >= AudioConfig.FootstepMinSpeed;

    /// <summary>
    /// Adds <paramref name="walked"/> blocks to the distance since the last footfall
    /// (<paramref name="accumulated"/>); true, with the stride taken off, when it reaches a whole
    /// <see cref="AudioConfig.MobStrideBlocks"/>. Walking cannot bank more than one stride.
    /// </summary>
    public static bool Stride(ref float accumulated, float walked)
    {
        accumulated += walked;
        if (accumulated < AudioConfig.MobStrideBlocks) return false;
        accumulated = 0f;
        return true;
    }
}
