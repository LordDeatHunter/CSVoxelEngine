using System;
using VoxelEngine.Config;

namespace VoxelEngine.Simulation.Smithing;

/// <summary>Where a bloomery's heat sits relative to the smelting band.</summary>
public enum HeatZone
{
    /// <summary>Under <see cref="BloomeryConfig.BandMin"/>: the ore does not smelt, progress stalls.</summary>
    Cold,

    /// <summary>Inside the band: the ore smelts cleanly.</summary>
    Band,

    /// <summary>Over <see cref="BloomeryConfig.BandMax"/>: the ore still smelts, but it overheats and burns, and the
    /// fuel burns faster.</summary>
    Hot,
}

/// <summary>
/// The bloomery's heat model as pure functions, so the manager, the tests and the look-at readout agree: the fire
/// loses heat every second, each bellows pump (a jump beside the bloomery) adds some, and what the heat is relative
/// to the band decides whether the ore smelts, stalls or burns. GL-free.
/// </summary>
public static class BloomeryRules
{
    /// <summary>The zone a heat value (0..1) falls in.</summary>
    public static HeatZone ZoneOf(float heat) =>
        heat < BloomeryConfig.BandMin ? HeatZone.Cold
        : heat > BloomeryConfig.BandMax ? HeatZone.Hot
        : HeatZone.Band;

    /// <summary>The heat after <paramref name="dt"/> seconds without a pump, never under 0.</summary>
    public static float Decay(float heat, float dt) => MathF.Max(0f, heat - BloomeryConfig.HeatDecayPerSecond * dt);

    /// <summary>The heat after one bellows pump, never over 1.</summary>
    public static float Pump(float heat) => MathF.Min(1f, heat + BloomeryConfig.PumpHeat);

    /// <summary>How fast the fuel burns at this heat, relative to a calm fire: an overheated one eats it faster.</summary>
    public static float FuelBurnFactor(float heat) =>
        ZoneOf(heat) == HeatZone.Hot ? BloomeryConfig.OverheatFuelFactor : 1f;

    /// <summary>Whether the ore smelts at this heat: inside the band or above it (an overheated unit still
    /// progresses, it just burns).</summary>
    public static bool Smelts(float heat) => ZoneOf(heat) != HeatZone.Cold;

    /// <summary>Whether a unit that overheated for <paramref name="overheatSeconds"/> burns to slag instead of
    /// becoming a bloom.</summary>
    public static bool Burns(float overheatSeconds) => overheatSeconds >= BloomeryConfig.BurnSeconds;
}
