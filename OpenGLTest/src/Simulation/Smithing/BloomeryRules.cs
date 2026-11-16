using System;
using VoxelEngine.Config;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>
/// The bloomery's heat model as pure functions, so the manager, the tests and the look-at readout agree: the fire
/// loses heat every second, each landing on its bellows adds some (more the farther the body fell), and whether the
/// heat reaches the band decides whether the ore smelts on or its progress runs back. There is no too hot: a fire at
/// full heat smelts like one just inside the band. GL-free.
/// </summary>
public static class BloomeryRules
{
    /// <summary>Whether the ore smelts at this heat (at or over <see cref="BloomeryConfig.BandMin"/>); under it the
    /// fire is too cold and the unit's progress runs back.</summary>
    public static bool Smelts(float heat) => heat >= BloomeryConfig.BandMin;

    /// <summary>The heat after <paramref name="dt"/> seconds without a landing, never under 0.</summary>
    public static float Decay(float heat, float dt) => MathF.Max(0f, heat - BloomeryConfig.HeatDecayPerSecond * dt);

    /// <summary>
    /// Seconds of smelt progress one second at <paramref name="heat"/> makes: 1 at or over the band, and under it a
    /// negative rate that grows linearly from 0 at the band's edge to <see cref="BloomeryConfig.ColdReverseRate"/>
    /// for a fire with no heat at all.
    /// </summary>
    public static float ProgressRate(float heat)
    {
        if (Smelts(heat)) return 1f;
        var deficit = (BloomeryConfig.BandMin - MathF.Max(0f, heat)) / BloomeryConfig.BandMin;
        return -BloomeryConfig.ColdReverseRate * deficit;
    }

    /// <summary>The heat one landing on the bellows after a fall of <paramref name="fallBlocks"/> adds: nothing under
    /// <see cref="BloomeryConfig.BellowsMinFallBlocks"/>, otherwise a base plus a share per block fallen, capped at
    /// <see cref="BloomeryConfig.BellowsMaxHeat"/>.</summary>
    public static float LandingHeat(float fallBlocks)
    {
        if (!(fallBlocks >= BloomeryConfig.BellowsMinFallBlocks)) return 0f;
        return MathF.Min(BloomeryConfig.BellowsMaxHeat,
            BloomeryConfig.BellowsBaseHeat + fallBlocks * BloomeryConfig.BellowsHeatPerBlock);
    }

    /// <summary>
    /// How bright a lit bloomery's hearth and throat glow at <paramref name="heat"/>, 0..1: from
    /// <see cref="BloomeryConfig.GlowColdAlpha"/> for a fire with no heat up to 1 at
    /// <see cref="BloomeryConfig.GlowFullHeat"/>, never dimmer for a hotter fire.
    /// </summary>
    public static float GlowAlpha(float heat) =>
        MathUtils.Lerp(BloomeryConfig.GlowColdAlpha, 1f, MathUtils.Clamp01(heat / BloomeryConfig.GlowFullHeat));

    /// <summary>The heat after a landing that adds <paramref name="added"/>, never over 1.</summary>
    public static float Pump(float heat, float added) => MathF.Min(1f, heat + added);
}
