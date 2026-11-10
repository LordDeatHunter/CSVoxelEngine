using System;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation;

/// <summary>
/// Where one species of wild animal lives: the surface biomes it spawns in, how likely it is among the
/// species sharing a column (a relative weight) and how many spawn together.
/// </summary>
public readonly record struct WildlifeSpawnRule(WildlifeKind Kind, float Weight, int GroupMin, int GroupMax, Biome[] Biomes)
{
    /// <summary>Whether the species spawns on a column of <paramref name="biome"/>.</summary>
    public bool LivesIn(Biome biome) => Array.IndexOf(Biomes, biome) >= 0;
}

/// <summary>
/// Which wild animal a surface spawn becomes (<see cref="Pick"/>): the species whose biomes hold the
/// column, weighted against each other. GL-free; the numbers live in <see cref="WildlifeConfig"/>.
/// </summary>
public static class WildlifeSpawnRules
{
    /// <summary>Every species' rule, in <see cref="WildlifeKind"/> order.</summary>
    public static readonly WildlifeSpawnRule[] All =
    [
        new(WildlifeKind.Deer, WildlifeConfig.DeerSpawnWeight, WildlifeConfig.DeerGroupMin, WildlifeConfig.DeerGroupMax,
            [Biome.Forest, Biome.Taiga, Biome.Steppe]),
        new(WildlifeKind.Boar, WildlifeConfig.BoarSpawnWeight, WildlifeConfig.BoarGroupMin, WildlifeConfig.BoarGroupMax,
            [Biome.Forest, Biome.Taiga, Biome.Savanna]),
        new(WildlifeKind.DireBoar, WildlifeConfig.DireBoarSpawnWeight, 1, 1, [Biome.Forest, Biome.Taiga]),
    ];

    /// <summary>
    /// The rule of the species a spawn on a column of <paramref name="biome"/> becomes for
    /// <paramref name="roll"/> (uniform in [0, 1)): one of the species living there that
    /// <paramref name="available"/> holds (its content is registered), each as likely as its weight among
    /// them. Null when none lives there.
    /// </summary>
    public static WildlifeSpawnRule? Pick(Biome biome, float roll, Func<WildlifeKind, bool> available)
    {
        var total = 0f;
        foreach (var rule in All)
            if (rule.LivesIn(biome) && available(rule.Kind)) total += rule.Weight;
        if (total <= 0f) return null;

        var at = roll * total;
        WildlifeSpawnRule? last = null;
        foreach (var rule in All)
        {
            if (!rule.LivesIn(biome) || !available(rule.Kind)) continue;
            last = rule;
            if (at < rule.Weight) return rule;
            at -= rule.Weight;
        }
        return last;
    }
}
