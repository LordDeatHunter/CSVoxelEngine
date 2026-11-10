using System;
using System.Collections.Generic;

namespace VoxelEngine.Entities;

/// <summary>
/// The global mob caps every kind spawns under, judged against the living mobs of every
/// registered <see cref="IMobPopulation"/> together. A natural spawn (a manager's ring spawner) is
/// refused once <see cref="NaturalCap"/> hostile mobs live (<see cref="MobConfig.MobNaturalCap"/> for each of
/// the <see cref="Players"/> in play), and the wild animals (<see cref="IMobPopulation.IsWildlife"/>) fill a
/// budget of their own beside it (<see cref="WildlifeCap"/>); a forced spawn (a slime
/// splitting, a scenario, a save restore, a client mirroring its server) may pass that, but never
/// <see cref="MobConfig.MobHardCap"/>, which is also what the healthbar buffers are sized
/// by (<see cref="MobConfig.MobsMaxAlive"/>). <see cref="World"/> owns one and hands it to
/// every manager it builds; a manager built alone makes its own. Main thread only.
/// </summary>
public sealed class MobCaps
{
    private readonly List<IMobPopulation> _populations = [];

    /// <summary>Counts <paramref name="population"/> from now on (once, however often it registers).</summary>
    public void Register(IMobPopulation population)
    {
        ArgumentNullException.ThrowIfNull(population);
        if (!_populations.Contains(population))
            _populations.Add(population);
    }

    /// <summary>Living mobs of every registered kind together.</summary>
    public int Living
    {
        get
        {
            var living = 0;
            for (var i = 0; i < _populations.Count; i++)
                living += _populations[i].LivingCount;
            return living;
        }
    }

    /// <summary>How many players the mobs spawn around (the world sets it each frame); the natural cap grows
    /// with it, up to <see cref="MobConfig.MobNaturalCapMaxPlayers"/> of them.</summary>
    public int Players { get; set; } = 1;

    /// <summary>The most living mobs the ring spawners fill up to: <see cref="MobConfig.MobNaturalCap"/> for
    /// each player in play.</summary>
    public int NaturalCap =>
        MobConfig.MobNaturalCap * Math.Clamp(Players, 1, MobConfig.MobNaturalCapMaxPlayers);

    /// <summary>The most living wild animals their spawner fills up to: <see cref="WildlifeConfig.WildlifeCap"/>
    /// for each player in play, like <see cref="NaturalCap"/>.</summary>
    public int WildlifeCap =>
        WildlifeConfig.WildlifeCap * Math.Clamp(Players, 1, MobConfig.MobNaturalCapMaxPlayers);

    /// <summary>Whether a ring spawner may add a hostile mob: fewer than the natural cap are alive.</summary>
    public bool AllowsNaturalSpawn => LivingOf(wildlife: false) < NaturalCap;

    /// <summary>Whether the wildlife spawner may add an animal: fewer than the wildlife cap are alive.</summary>
    public bool AllowsWildlifeSpawn => LivingOf(wildlife: true) < WildlifeCap;

    /// <summary>How fast the hostile ring spawners' timers run: 1 at the natural cap, rising evenly to
    /// 1 + <see cref="MobConfig.MobSpawnRefillHaste"/> with nothing alive, so an emptied world refills
    /// quickly and a full one only tops up.</summary>
    public float SpawnHaste => Haste(LivingOf(wildlife: false), NaturalCap);

    /// <summary><see cref="SpawnHaste"/> for the wildlife spawner, against the wildlife cap.</summary>
    public float WildlifeSpawnHaste => Haste(LivingOf(wildlife: true), WildlifeCap);

    private static float Haste(int living, int cap) =>
        1f + MobConfig.MobSpawnRefillHaste * (1f - Math.Min(1f, living / (float)cap));

    // Living mobs of the wildlife populations, or of all the others
    private int LivingOf(bool wildlife)
    {
        var living = 0;
        for (var i = 0; i < _populations.Count; i++)
            if (_populations[i].IsWildlife == wildlife) living += _populations[i].LivingCount;
        return living;
    }

    /// <summary>How many forced spawns still fit under the hard cap (0 once it is full).</summary>
    public int ForcedRoom => Math.Max(0, MobConfig.MobHardCap - Living);
}
