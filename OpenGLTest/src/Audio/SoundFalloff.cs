using System.Numerics;

namespace VoxelEngine.Audio;

/// <summary>
/// How far a positional sound carries: full volume within <see cref="AudioConfig.SfxFullVolumeBlocks"/> of the
/// listener's eye, falling off quadratically to silence at <see cref="AudioConfig.SfxRangeBlocks"/>. The direction is
/// OpenAL's (<see cref="SpatialVoices"/>); this is the loudness every play gets and the earshot a server sends sounds
/// within. GL-free and stateless.
/// </summary>
public static class SoundFalloff
{
    /// <summary>The distance gain, 1 near the listener down to 0 at the range.</summary>
    public static float Gain(float distance)
    {
        if (distance <= AudioConfig.SfxFullVolumeBlocks) return 1f;
        if (distance >= AudioConfig.SfxRangeBlocks) return 0f;
        var t = (distance - AudioConfig.SfxFullVolumeBlocks) / (AudioConfig.SfxRangeBlocks - AudioConfig.SfxFullVolumeBlocks);
        var remaining = 1f - t;
        return remaining * remaining;
    }

    /// <summary>Whether a sound at <paramref name="source"/> is within earshot of <paramref name="listener"/>.</summary>
    public static bool InRange(Vector3 listener, Vector3 source) =>
        Vector3.DistanceSquared(listener, source) < AudioConfig.SfxRangeBlocks * AudioConfig.SfxRangeBlocks;
}
