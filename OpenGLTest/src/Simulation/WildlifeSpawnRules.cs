using System;
using VoxelEngine.Entities.Wildlife;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Simulation;

/// <summary>
/// Where one species of wild animal lives: the surface biomes it spawns in, how likely it is among the
/// species sharing a column (a relative weight), and whether it keeps to the
/// waterside (<see cref="NearWater"/>, except in the <see cref="AnywhereBiomes"/> where its haunt is the whole biome,
/// such as the jungle's damp air).
/// </summary>
public readonly record struct WildlifeSpawnRule(WildlifeKind Kind, float Weight, Biome[] Biomes,
    bool NearWater = false, Biome[]? AnywhereBiomes = null)
{
    /// <summary>Whether the species spawns on a column of <paramref name="biome"/> that has surface water
    /// nearby or not (<paramref name="nearWater"/>).</summary>
    public bool LivesIn(Biome biome, bool nearWater) =>
        Array.IndexOf(Biomes, biome) >= 0
        && (nearWater || !NearWater || (AnywhereBiomes != null && Array.IndexOf(AnywhereBiomes, biome) >= 0));
}

/// <summary>
/// Where one species of sea animal lives: the water depth it needs over its column (<see cref="MinDepth"/>) and
/// whether it keeps to the sea (<see cref="OceanOnly"/>) or lives in any water (a lake, a river), and how likely it
/// is among the species sharing a column (a relative weight).
/// </summary>
public readonly record struct SeaSpawnRule(WildlifeKind Kind, float Weight, int MinDepth, bool OceanOnly = true)
{
    /// <summary>Whether the species lives in water <paramref name="depth"/> blocks deep on a column of
    /// <paramref name="biome"/>.</summary>
    public bool LivesIn(Biome biome, int depth) => depth >= MinDepth && (!OceanOnly || biome == Biome.Ocean);
}

/// <summary>
/// Which wild animal a surface spawn becomes (<see cref="Pick"/>): the species whose biomes hold the
/// column (the waterside ones only beside water), weighted against each other. GL-free; the numbers live in
/// <see cref="WildlifeConfig"/>.
/// </summary>
public static class WildlifeSpawnRules
{
    /// <summary>Every land species rule, in <see cref="WildlifeKind"/> order.</summary>
    public static readonly WildlifeSpawnRule[] All =
    [
        new(WildlifeKind.Deer, WildlifeConfig.DeerSpawnWeight,
            [Biome.Forest, Biome.Taiga, Biome.Steppe]),
        new(WildlifeKind.Boar, WildlifeConfig.BoarSpawnWeight,
            [Biome.Forest, Biome.Taiga, Biome.Savanna]),
        new(WildlifeKind.DireBoar, WildlifeConfig.DireBoarSpawnWeight, [Biome.Forest, Biome.Taiga]),
        new(WildlifeKind.Crow, WildlifeConfig.CrowSpawnWeight,
            [Biome.Forest, Biome.Desert, Biome.Taiga, Biome.Frozen, Biome.Steppe, Biome.Savanna]),
        new(WildlifeKind.Dragonfly, WildlifeConfig.DragonflySpawnWeight,
            [Biome.Forest, Biome.Taiga, Biome.Steppe, Biome.Savanna, Biome.Jungle], NearWater: true,
            AnywhereBiomes: [Biome.Jungle]),
        new(WildlifeKind.CorpseBloom, WildlifeConfig.BloomSpawnWeight, [Biome.Jungle]),
        new(WildlifeKind.Centipede, WildlifeConfig.CentipedeSpawnWeight, [Biome.Jungle]),
    ];

    /// <summary>Every sea species rule.</summary>
    public static readonly SeaSpawnRule[] Sea =
    [
        new(WildlifeKind.Cod, WildlifeConfig.CodSpawnWeight, WildlifeConfig.CodMinWaterDepthBlocks),
        new(WildlifeKind.Salmon, WildlifeConfig.SalmonSpawnWeight, WildlifeConfig.SalmonMinWaterDepthBlocks, OceanOnly: false),
        new(WildlifeKind.Tuna, WildlifeConfig.TunaSpawnWeight, WildlifeConfig.TunaMinWaterDepthBlocks),
        new(WildlifeKind.Shark, WildlifeConfig.SharkSpawnWeight, WildlifeConfig.SharkMinWaterDepthBlocks),
        new(WildlifeKind.Jellyfish, WildlifeConfig.JellyfishSpawnWeight, WildlifeConfig.JellyfishMinWaterDepthBlocks),
    ];

    /// <summary>
    /// The sea species a spawn in water <paramref name="depth"/> blocks deep on a column of <paramref name="biome"/>
    /// becomes for <paramref name="roll"/> (uniform in [0, 1)): one of those living there that
    /// <paramref name="available"/> holds, each as likely as its weight among them. Null when none lives there.
    /// </summary>
    public static SeaSpawnRule? PickSea(Biome biome, int depth, float roll, Func<WildlifeKind, bool> available)
    {
        var total = 0f;
        foreach (var rule in Sea)
            if (rule.LivesIn(biome, depth) && available(rule.Kind)) total += rule.Weight;
        if (total <= 0f) return null;

        var at = roll * total;
        SeaSpawnRule? last = null;
        foreach (var rule in Sea)
        {
            if (!rule.LivesIn(biome, depth) || !available(rule.Kind)) continue;
            last = rule;
            if (at < rule.Weight) return rule;
            at -= rule.Weight;
        }
        return last;
    }

    /// <summary>
    /// The rule of the species a spawn on a column of <paramref name="biome"/> becomes for
    /// <paramref name="roll"/> (uniform in [0, 1)), surface water being near it or not
    /// (<paramref name="nearWater"/>): one of the species living there that <paramref name="available"/> holds
    /// (its content is registered), each as likely as its weight among them. Null when none lives there.
    /// </summary>
    public static WildlifeSpawnRule? Pick(Biome biome, bool nearWater, float roll, Func<WildlifeKind, bool> available)
    {
        var total = 0f;
        foreach (var rule in All)
            if (rule.LivesIn(biome, nearWater) && available(rule.Kind)) total += rule.Weight;
        if (total <= 0f) return null;

        var at = roll * total;
        WildlifeSpawnRule? last = null;
        foreach (var rule in All)
        {
            if (!rule.LivesIn(biome, nearWater) || !available(rule.Kind)) continue;
            last = rule;
            if (at < rule.Weight) return rule;
            at -= rule.Weight;
        }
        return last;
    }
}
