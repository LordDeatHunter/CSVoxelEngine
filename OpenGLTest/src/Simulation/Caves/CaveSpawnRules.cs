using System;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.WorldGen;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Simulation.Caves;

/// <summary>
/// Which mob kinds a cave biome holds (plans/CAVE_BIOMES.md CB-4), GL-free: the masks the cave spawn searches filter
/// by (<see cref="CaveSpawnSearch.AllowedBiomes"/>) and the goblins' per-biome weight. Slimes keep out of the magma
/// deep; goblins favor the crystal and fungal caves and never take the magma deep either; each sub-biome holds its own
/// cave mob kind (<see cref="CaveMobKindFor"/>), spiders crawl through all of them and scorpions through the desert's
/// (<see cref="KindFor"/>).
/// </summary>
public static class CaveSpawnRules
{
    /// <summary>The mask bit of cave biome <paramref name="biome"/>.</summary>
    public static int Bit(CaveBiome biome) => 1 << (int)biome;

    /// <summary>Every cave biome's bit: a search that takes any cave.</summary>
    public static readonly int AllBiomes = MaskOf(static _ => true);

    /// <summary>Whether <paramref name="mask"/> holds cave biome <paramref name="biome"/>.</summary>
    public static bool Allows(int mask, CaveBiome biome) => (mask & Bit(biome)) != 0;

    /// <summary>Whether a cave slime may spawn in cave biome <paramref name="biome"/>: everywhere but the magma
    /// deep, whose scorched rock is no place for a gel.</summary>
    public static bool SlimeAllowedIn(CaveBiome biome) => biome != CaveBiome.MagmaDeep;

    /// <summary>The cave biomes a cave slime may spawn in (<see cref="SlimeAllowedIn"/>).</summary>
    public static readonly int SlimeMask = MaskOf(SlimeAllowedIn);

    /// <summary>
    /// The chance (0..1) that a cave goblin spawn found in cave biome <paramref name="biome"/> is kept:
    /// <see cref="MobConfig.GoblinCaveFavoredWeight"/> in the crystal and fungal caves,
    /// <see cref="MobConfig.GoblinCaveOtherWeight"/> in plain and echo caves, and 0 in the magma deep.
    /// </summary>
    public static float GoblinWeight(CaveBiome biome) => biome switch
    {
        CaveBiome.Crystal or CaveBiome.Fungal => MobConfig.GoblinCaveFavoredWeight,
        CaveBiome.MagmaDeep => 0f,
        _ => MobConfig.GoblinCaveOtherWeight,
    };

    /// <summary>The cave biomes a cave goblin may spawn in at all (<see cref="GoblinWeight"/> above 0).</summary>
    public static readonly int GoblinMask = MaskOf(static biome => GoblinWeight(biome) > 0f);

    /// <summary>
    /// The cave mob kind (its entity name) cave biome <paramref name="biome"/> holds: the shardling in crystal caves,
    /// the sporeling in fungal caves, the magmaborn in the magma deep; plain and echo caves hold none (null).
    /// </summary>
    public static string? CaveMobKindFor(CaveBiome biome) => biome switch
    {
        CaveBiome.Crystal => Shardling.EntityName,
        CaveBiome.Fungal => Sporeling.EntityName,
        CaveBiome.MagmaDeep => Magmaborn.EntityName,
        _ => null,
    };

    /// <summary>
    /// The kind a cave spawn found in <paramref name="biome"/> becomes, by <paramref name="roll"/> (0..1). With
    /// <paramref name="scorpions"/> the desert's echo caves give them the first
    /// <see cref="ScorpionConfig.CaveSpawnChance"/> of the roll, and the rest of the roll is judged as in any other
    /// cave. Spiders live in every cave, so with <paramref name="spiders"/> they take
    /// <see cref="SpiderConfig.CaveSpawnChance"/> of the floors in a cave with no kind of its own (the rest stay
    /// empty) and <see cref="SpiderConfig.BiomeCaveSpawnChance"/> of the floors where a kind lives, which keeps the
    /// rest. Without either it is the biome's own kind (<see cref="CaveMobKindFor"/>). Null spawns nothing.
    /// </summary>
    public static string? KindFor(CaveBiome biome, float roll, bool spiders, bool scorpions = false)
    {
        if (scorpions && biome == CaveBiome.DesertEcho)
        {
            if (roll < ScorpionConfig.CaveSpawnChance) return Scorpion.EntityName;
            roll = (roll - ScorpionConfig.CaveSpawnChance) / (1f - ScorpionConfig.CaveSpawnChance);
        }

        var own = CaveMobKindFor(biome);
        if (!spiders) return own;
        if (own == null) return roll < SpiderConfig.CaveSpawnChance ? Spider.EntityName : null;
        return roll < SpiderConfig.BiomeCaveSpawnChance ? Spider.EntityName : own;
    }

    /// <summary>Whether a surface column of <paramref name="biome"/> may take a scorpion: the desert only.</summary>
    public static bool ScorpionSurfaceBiome(Biome biome) => biome == Biome.Desert;

    /// <summary>Whether a scorpion's surface spawn turn is taken, by <paramref name="roll"/> (0..1): always at
    /// night, and with <see cref="ScorpionConfig.DaySpawnChance"/> by day.</summary>
    public static bool ScorpionSurfaces(bool night, float roll) => night || roll < ScorpionConfig.DaySpawnChance;

    /// <summary>The cave biomes that hold a cave mob kind at all (<see cref="CaveMobKindFor"/> not null).</summary>
    public static readonly int CaveMobMask = MaskOf(static biome => CaveMobKindFor(biome) != null);

    private static int MaskOf(Func<CaveBiome, bool> holds)
    {
        var mask = 0;
        foreach (var biome in Enum.GetValues<CaveBiome>())
        {
            if (holds(biome)) mask |= Bit(biome);
        }
        return mask;
    }
}
