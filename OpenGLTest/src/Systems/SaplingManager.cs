using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// The saplings planted in the world (an acorn, or any <see cref="ItemUseAction.PlantTree"/> use),
/// GL-free and main thread only: each is one cell with a species and a clock, and every
/// <see cref="WorldGenerationConfig.SaplingPulseSeconds"/> (jittered per pulse) the manager grows
/// it one <see cref="TreeGrower.GrowFurther"/> pulse through the same reactive path a player's
/// ash uses (<see cref="World.TryGrowTree"/>), until the species' own maturity step
/// (<see cref="TreeGrower.MaturityStepsAt"/>) — so a sapling becomes a tree over a few minutes.
/// A cell in an unloaded chunk waits (nothing is judged from air); a cell whose wood is gone
/// (mined, or grown over) is forgotten. In memory only, like the station block entities: a reload
/// leaves a half-grown sapling standing until it is grown again by hand.
/// </summary>
public sealed class SaplingManager
{
    /// <summary>One scheduled sapling: the species and its root cell.</summary>
    private struct Sapling
    {
        public string Species;
        public int X, Y, Z;
        public float Age;
        public float NextSeconds;
        public int Pulses;
    }

    private readonly List<Sapling> _saplings = [];

    /// <summary>Saplings still growing (for tests and diagnostics).</summary>
    public int Count => _saplings.Count;

    /// <summary>
    /// Schedules a freshly planted sapling's root cell; a cell already scheduled keeps its clock
    /// (a second acorn on the same sapling changes nothing).
    /// </summary>
    public void Schedule(string species, int x, int y, int z, Random? random = null)
    {
        if (string.IsNullOrEmpty(species)) return;
        foreach (var sapling in _saplings)
        {
            if (sapling.X == x && sapling.Y == y && sapling.Z == z) return;
        }
        _saplings.Add(new Sapling
        {
            Species = species, X = x, Y = y, Z = z,
            NextSeconds = PulseSeconds(random ?? Random.Shared),
        });
    }

    /// <summary>Whether a cell already has a scheduled sapling.</summary>
    public bool Contains(int x, int y, int z)
    {
        foreach (var sapling in _saplings)
        {
            if (sapling.X == x && sapling.Y == y && sapling.Z == z) return true;
        }
        return false;
    }

    /// <summary>
    /// Ages every sapling by <paramref name="deltaTime"/> and pulses the ones whose clock is up:
    /// one growth pulse through <paramref name="grow"/> (the world's reactive grow), and the
    /// sapling is forgotten once it has taken its species' maturity step, or once its wood is
    /// gone. An unloaded read or a refused grow (a covered sapling) leaves the sapling waiting
    /// with its clock reset, never dropping it.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, Func<string, int, int, int, int, TreeGrowthResult> grow,
        Random? random = null)
    {
        if (deltaTime <= 0f || chunks == null || grow == null || _saplings.Count == 0) return;
        var rng = random ?? Random.Shared;

        for (var i = _saplings.Count - 1; i >= 0; i--)
        {
            var sapling = _saplings[i];
            sapling.Age += deltaTime;
            if (sapling.Age < sapling.NextSeconds)
            {
                _saplings[i] = sapling;
                continue;
            }
            sapling.Age = 0f;
            sapling.NextSeconds = PulseSeconds(rng);

            var species = TreeSpeciesRegistry.TryGet(sapling.Species);
            if (species == null || !chunks.TryGetCell(sapling.X, sapling.Y, sapling.Z, out var id, out _))
            {
                // An unknown species or an unloaded cell: keep the sapling and wait
                _saplings[i] = sapling;
                continue;
            }
            if (id != species.WoodId)
            {
                // Its wood is gone (mined, or grown into something else): forget it
                _saplings.RemoveAt(i);
                continue;
            }

            var maturity = TreeGrower.MaturityStepsAt(species, sapling.X, sapling.Y, sapling.Z,
                WorldGenerationConfig.Seed);
            if (sapling.Pulses >= maturity)
            {
                _saplings.RemoveAt(i);
                continue;
            }

            if (grow(sapling.Species, sapling.X, sapling.Y, sapling.Z, 1).Status == TreeGrowthStatus.NotGrowable)
            {
                _saplings[i] = sapling;
                continue;
            }

            sapling.Pulses++;
            if (sapling.Pulses >= maturity)
            {
                _saplings.RemoveAt(i);
                continue;
            }
            _saplings[i] = sapling;
        }
    }

    // The gap before the next pulse: the configured seconds, jittered up to half either way so a
    // row of saplings planted together does not pulse in lockstep.
    private static float PulseSeconds(Random rng) =>
        WorldGenerationConfig.SaplingPulseSeconds
        * (1f - WorldGenerationConfig.SaplingPulseJitter * 0.5f + WorldGenerationConfig.SaplingPulseJitter * rng.NextSingle());
}
