using System;

namespace silkgltriangle;

/// <summary>
/// How hard a world event (a TNT blast, a felled tree hitting the ground) shakes a camera standing
/// some distance away, GL-free: the event's full <c>strength</c> inside its own
/// <c>nearRadius</c>, then falling off as <c>nearRadius / distance</c>, and eased on to exactly zero
/// at <c>range</c>, so a far event never jolts the view. Never grows with distance. The result is
/// trauma for <see cref="Camera.AddShake"/> (0..1, clamped there).
/// </summary>
public static class ShakeFalloff
{
    /// <summary>The trauma an event of <paramref name="strength"/> adds at <paramref name="distance"/> blocks.</summary>
    public static float Trauma(float strength, float nearRadius, float range, float distance)
    {
        if (!(strength > 0f) || !(range > 0f) || !(distance < range)) return 0f;
        var near = distance <= nearRadius ? 1f : nearRadius / distance;
        var fade = 1f - MathUtils.Clamp01(distance / range);
        return MathUtils.Clamp01(strength * near * fade);
    }
}
