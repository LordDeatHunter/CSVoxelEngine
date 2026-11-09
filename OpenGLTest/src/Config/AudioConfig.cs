namespace VoxelEngine.Config;

/// <summary>
/// Audio tuning: the day and night ambience beds, their volume and crossfades, and the sound-effect
/// volume.
/// </summary>
public static class AudioConfig
{
    // --- Ambience ---
    // Looping day/night ambience, crossfaded by the lighting pass's dayness (0 at night,
    // 1 in full day). Dayness itself ramps over ~30 s at dawn/dusk, so the crossfade is
    // inherently slow; FadeRate only smooths the per-frame volume steps.
    public const string DayAmbiencePath = "assets/sfx/ambience/forest_ambience.mp3";
    public const string NightAmbiencePath = "assets/sfx/ambience/night_ambience.mp3";
    // Peak volume of each loop when fully faded in (0..1)
    public const float AmbienceVolume = 0.35f;
    // Exponential approach rate per second the day/night crossfade eases toward the current
    // dayness; the master volume is applied directly so a slider change is instant
    public const float AmbienceFadeRate = 0.8f;
    // Both loops rise from silence over this many seconds at startup
    public const float AmbienceStartupFadeSeconds = 8f;
    // Peak volume of one-shot gameplay sfx (block hits, pickups) mixed by SfxManager
    public const float SfxVolume = 0.7f;

    // --- Skeleton rattle ---
    // A skeleton within this many blocks of the camera can be heard rattling
    public const float SkeletonRattleRangeBlocks = 24f;
    // Seconds between rattles are drawn uniformly from this range; kept long so it stays an occasional accent
    public const float SkeletonRattleMinSeconds = 12f;
    public const float SkeletonRattleMaxSeconds = 35f;
}
