using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// What the sky dome needs each frame beyond its <see cref="SkyPalette"/>: the night sky's
/// stars, the eye the cloud cover in front of them is measured from, and the sun's lens glare.
/// Built once a frame by <c>RenderManager.SetLighting</c>; the water mirror redraws the dome from
/// the same frame with its own mirrored <see cref="Eye"/> and no glare.
/// </summary>
/// <param name="StarVisibility">0 by day .. 1 at night (<see cref="StarField.Visibility"/>).</param>
/// <param name="StarSpin">The sky's turn with the sun, in radians (<see cref="StarField.ToStarSpace"/>).</param>
/// <param name="StarTime">The twinkle clock in seconds (<see cref="StarField.Twinkle"/>), wrapped to keep float precision.</param>
/// <param name="Eye">Where the dome is seen from, for the cloud cover along each view ray.</param>
/// <param name="CloudDrift">The cloud layer's wrapped drift this frame (<see cref="CloudCopies.Drift"/>).</param>
/// <param name="GlareColor">The linear sun colour times <see cref="SunGlare.Intensity"/>; zero turns the glare off.</param>
/// <param name="MoonVisibility">How visible the moon is (<see cref="MoonPhase.Visibility"/>): the stars behind its disc are hidden by that much.</param>
/// <param name="Aurora">How strongly the aurora shows (<see cref="silkgltriangle.Aurora.Visibility"/>); zero draws none.</param>
/// <param name="AuroraDrift">The aurora's noise drift this frame (<see cref="silkgltriangle.Aurora.Drift"/>).</param>
public readonly record struct SkyDomeFrame(float StarVisibility, float StarSpin, float StarTime, Vector3 Eye,
    Vector2 CloudDrift, Vector3 GlareColor, float MoonVisibility = 0f, float Aurora = 0f, Vector4 AuroraDrift = default);
