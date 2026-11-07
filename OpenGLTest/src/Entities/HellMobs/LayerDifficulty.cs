using System;
using VoxelEngine.WorldGen.Hell;

namespace VoxelEngine.Entities.HellMobs;

/// <summary>
/// How much harder a realm mob is on a deeper layer: one multiplier per layer index,
/// <c>1 + index * </c><see cref="MobConfig.LayerDifficultyPerLayer"/>, capped at
/// <see cref="MobConfig.LayerDifficultyMaxMultiplier"/>, that scales its health, its damage and a
/// caster's fire rate, plus the pack a ring spawn rolls there. A pure function of the layer, so a
/// mob restored from a save or mirrored on a client at the same spot gets the same numbers. GL-free.
/// </summary>
public static class LayerDifficulty
{
    /// <summary>The hell layer a body centered at height <paramref name="y"/> stands in, 0 at and above
    /// hell's first layer (a mob spawned by hand above hell counts as the first layer).</summary>
    public static int LayerAt(float y) => Math.Max(0, HellGenerator.LayerIndex(MathUtils.FloorToInt(y)));

    /// <summary>The difficulty multiplier of <paramref name="layer"/>: 1 on the first layer, never
    /// falling with depth, never past <see cref="MobConfig.LayerDifficultyMaxMultiplier"/>.</summary>
    public static float Multiplier(int layer) =>
        MathF.Min(1f + Math.Max(0, layer) * MobConfig.LayerDifficultyPerLayer,
            MobConfig.LayerDifficultyMaxMultiplier);

    /// <summary><paramref name="baseAmount"/> (a health pool or a hit) scaled to <paramref name="layer"/>,
    /// rounded to the nearest whole point and never below the base.</summary>
    public static int Scale(int baseAmount, int layer) =>
        Math.Max(baseAmount, (int)MathF.Round(baseAmount * Multiplier(layer), MidpointRounding.AwayFromZero));

    /// <summary>A cooldown of <paramref name="seconds"/> on <paramref name="layer"/>: divided by the
    /// multiplier, so a deeper caster fires faster.</summary>
    public static float Cooldown(float seconds, int layer) => seconds / Multiplier(layer);

    /// <summary>How many mobs one ring spawn on <paramref name="layer"/> rolls: one, plus one more every
    /// <see cref="MobConfig.HellPackLayersPerExtra"/> layers, up to <see cref="MobConfig.HellPackMaxSize"/>.</summary>
    public static int PackSize(int layer) =>
        Math.Min(MobConfig.HellPackMaxSize, 1 + Math.Max(0, layer) / MobConfig.HellPackLayersPerExtra);
}
