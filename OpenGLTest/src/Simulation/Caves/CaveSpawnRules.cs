using System;
using VoxelEngine.Entities.CaveMobs;
using VoxelEngine.WorldGen.Caves;

namespace VoxelEngine.Simulation.Caves;

/// <summary>
/// Which mob kinds a cave biome holds (plans/CAVE_BIOMES.md CB-4), GL-free: the masks the cave spawn searches filter
/// by (<see cref="CaveSpawnSearch.AllowedBiomes"/>) and the goblins' per-biome weight. Slimes keep out of the magma
/// deep; goblins favor the crystal and fungal caves and never take the magma deep either; each sub-biome holds its own
/// cave mob kind (<see cref="CaveMobKindFor"/>).
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
