namespace silkgltriangle;

/// <summary>
/// GL-free twin of the selection outline's breathing in selection-fragment.glsl: the outline's
/// opacity eases between <see cref="WorldGenerationConfig.SelectionPulseMinOpacity"/> and full on
/// a sine of the lighting clock, and holds steady at full while the targeted block is being mined,
/// so the crack overlay is never read through a flickering frame. The renderer uploads
/// <see cref="Depth"/> as <c>uPulseDepth</c> and the period as <c>uPulsePeriod</c>; the shader
/// evaluates <see cref="Opacity"/> per fragment from <c>uTime</c>.
/// </summary>
public static class SelectionPulse
{
    /// <summary>
    /// How far the breath dips below full opacity (<c>uPulseDepth</c>): <c>1 - SelectionPulseMinOpacity</c>
    /// while the player only looks at the block, 0 while mining it, which holds the outline steady at full.
    /// </summary>
    public static float Depth(bool mining) =>
        mining ? 0f : 1f - MathUtils.Clamp01(WorldGenerationConfig.SelectionPulseMinOpacity);

    /// <summary>
    /// The outline's opacity multiplier at <paramref name="timeSeconds"/> for a dip of
    /// <paramref name="depth"/>: 1 at the top of each breath and <c>1 - depth</c> at its bottom,
    /// one breath every <see cref="WorldGenerationConfig.SelectionPulsePeriodSeconds"/>. A depth of
    /// 0 is a steady 1. The twin of the shader's <c>1 - uPulseDepth * (0.5 + 0.5 * sin(...))</c>.
    /// </summary>
    public static float Opacity(float timeSeconds, float depth) =>
        1f - depth * (0.5f + 0.5f * MathUtils.Oscillate(timeSeconds, WorldGenerationConfig.SelectionPulsePeriodSeconds));
}
