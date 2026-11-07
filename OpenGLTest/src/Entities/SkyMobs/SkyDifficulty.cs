using System;
using VoxelEngine.Entities.HellMobs;
using VoxelEngine.WorldGen.Sky;

namespace VoxelEngine.Entities.SkyMobs;

/// <summary>
/// How much harder a sky mob is in a bigger island zone: the zone's tier (<see cref="SkyZone.Tier"/>, 0 small,
/// 1 medium, 2 big; bigger is rarer) gives one multiplier, <c>1 + tier * </c><see cref="MobConfig.SkyDifficultyPerTier"/>,
/// that scales a mob's health and damage, the pack one ring spawn rolls there, and which kinds may spawn: the
/// bruiser only in the big zones. A pure function of the zone, so a mob restored from a save or mirrored on a
/// client at the same spot gets the same numbers. GL-free.
/// </summary>
public static class SkyDifficulty
{
    /// <summary>The tier of the island zone over column (<paramref name="x"/>, <paramref name="z"/>), 0 outside
    /// every zone (a mob spawned by hand away from the islands counts as the smallest).</summary>
    public static int TierAt(float x, float z)
    {
        var zone = SkyIslands.ZoneAt(x, z);
        return zone.InZone ? Clamp(zone.Tier) : 0;
    }

    /// <summary>The difficulty multiplier of <paramref name="tier"/>: 1 in a small zone, rising with the tier.</summary>
    public static float Multiplier(int tier) => 1f + Clamp(tier) * MobConfig.SkyDifficultyPerTier;

    /// <summary><paramref name="baseAmount"/> (a health pool or a hit) scaled to <paramref name="tier"/>, never
    /// below the base.</summary>
    public static int Scale(int baseAmount, int tier) => LayerDifficulty.ScaleBy(baseAmount, Multiplier(tier));

    /// <summary>How many mobs one ring spawn in a zone of <paramref name="tier"/> rolls at most: one, plus
    /// <see cref="MobConfig.SkyPackExtraPerTier"/> per tier, up to <see cref="MobConfig.SkyPackMaxSize"/>.</summary>
    public static int PackSize(int tier) =>
        Math.Min(MobConfig.SkyPackMaxSize, 1 + Clamp(tier) * MobConfig.SkyPackExtraPerTier);

    /// <summary>Whether a zone of <paramref name="tier"/> may spawn the bruiser: only the big, rare ones.</summary>
    public static bool AllowsBruiser(int tier) => tier >= SkyZone.BigTier;

    /// <summary>The kind one ring spawn in a zone of <paramref name="tier"/> takes for a uniform
    /// <paramref name="roll"/> in 0..1: a <see cref="Cloudgolem"/> with <see cref="MobConfig.SkyBruiserShare"/>
    /// where <see cref="AllowsBruiser"/>, else a <see cref="Galeraptor"/>.</summary>
    public static string KindFor(int tier, float roll) =>
        AllowsBruiser(tier) && roll < MobConfig.SkyBruiserShare ? Cloudgolem.EntityName : Galeraptor.EntityName;

    private static int Clamp(int tier) => Math.Clamp(tier, 0, SkyZone.BigTier);
}
