using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The two global mob caps every kind spawns under, judged against the living mobs of every
/// registered <see cref="IMobPopulation"/> together. A natural spawn (a manager's ring spawner) is
/// refused once <see cref="WorldGenerationConfig.MobNaturalCap"/> mobs live; a forced spawn (a slime
/// splitting, a scenario, a save restore, a client mirroring its server) may pass that, but never
/// <see cref="WorldGenerationConfig.MobHardCap"/>, which is also what the healthbar buffers are sized
/// by (<see cref="WorldGenerationConfig.MobsMaxAlive"/>). <see cref="World"/> owns one and hands it to
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

    /// <summary>Whether a ring spawner may add a mob: fewer than the natural cap are alive.</summary>
    public bool AllowsNaturalSpawn => Living < WorldGenerationConfig.MobNaturalCap;

    /// <summary>How many forced spawns still fit under the hard cap (0 once it is full).</summary>
    public int ForcedRoom => Math.Max(0, WorldGenerationConfig.MobHardCap - Living);
}
