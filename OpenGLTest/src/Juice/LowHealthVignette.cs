using VoxelEngine.Rendering.Overlays;

namespace VoxelEngine.Juice;

/// <summary>
/// The low-health warning: how strongly the screen edge pulses red, as a pure function of the
/// health fraction that feeds the hearts and the clock. GL-free; <see cref="UiPasses"/>
/// hands the result to <see cref="DamageVignetteRenderer"/> as a second term of the damage
/// vignette's one full-screen pass. Nothing at or above
/// <see cref="JuiceConfig.LowHealthThreshold"/>, while dead or in creative; below it a
/// slow <see cref="MathUtils.Heartbeat"/> whose envelope grows as health falls.
/// </summary>
public static class LowHealthVignette
{
    /// <summary>
    /// The pulse's strength right now, 0..1. <paramref name="health01"/> is health over the pool;
    /// <paramref name="time"/> is any steadily running clock in seconds.
    /// </summary>
    public static float Intensity(float health01, float time, bool dead, bool creative)
    {
        const float threshold = JuiceConfig.LowHealthThreshold;
        // The negated test also refuses a NaN fraction
        if (dead || creative || !(health01 < threshold))
            return 0f;

        var severity = MathUtils.Clamp01((threshold - health01) / threshold);
        var envelope = MathUtils.Lerp(JuiceConfig.LowHealthMinStrength,
            JuiceConfig.LowHealthMaxStrength, severity);
        var beat = MathUtils.Heartbeat(time, JuiceConfig.LowHealthPulseSeconds);
        return envelope * MathUtils.Lerp(JuiceConfig.LowHealthPulseFloor, 1f, beat);
    }
}
