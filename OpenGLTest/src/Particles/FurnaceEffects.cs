using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Stations;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Smithing;

namespace VoxelEngine.Particles;

/// <summary>
/// What a lit kiln, alloy furnace or bloomery throws out of its top: a steady thread of smoke and a few embers
/// from the flue (<see cref="KilnManager.FlueCells"/>, <see cref="BloomeryManager.FlueCells"/>), and a burst of
/// sparks out of a bloomery's throat whenever its heat jumps, which is what a landing on its bellows (or lighting
/// it) does. The burst reads the heat alone, so it plays the same on a client's mirror, with nothing on the wire.
/// Cosmetic only; main thread.
/// </summary>
internal sealed class FurnaceEffects(FireParticleSystem fire, KilnManager kilns, BloomeryManager bloomeries)
{
    // Each bloomery's heat as of the last update, by anchor; swapped with the scratch map every update so a
    // bloomery that is gone drops out without a sweep
    private Dictionary<Vector3, float> _lastHeat = [];
    private Dictionary<Vector3, float> _heatScratch = [];
    private float _smokeDebt;
    private float _emberDebt;

    /// <summary>
    /// How many sparks a heat rise of <paramref name="rise"/> (0..1) throws: none under
    /// <see cref="ParticleConfig.BellowsBlastMinRise"/> (the mirror's rounding, never a pump), then more the
    /// harder the blow, up to <see cref="ParticleConfig.BellowsBlastMaxSparks"/>.
    /// </summary>
    public static int BlastSparks(float rise) =>
        rise < ParticleConfig.BellowsBlastMinRise
            ? 0
            : Math.Clamp((int)MathF.Ceiling(rise * ParticleConfig.BellowsBlastSparksPerHeat), 1,
                ParticleConfig.BellowsBlastMaxSparks);

    /// <summary>Smokes every lit flue within the fire particles' reach of <paramref name="eye"/> and blasts the
    /// bloomeries whose heat rose since the last call.</summary>
    public void Update(float deltaTime, Vector3 eye)
    {
        const float reach = ParticleConfig.FireParticleRadius * ParticleConfig.FireParticleRadius;

        _smokeDebt += ParticleConfig.FlueSmokePerSecond * deltaTime;
        _emberDebt += ParticleConfig.FlueEmberPerSecond * deltaTime;
        var smoke = (int)_smokeDebt;
        var embers = (int)_emberDebt;
        _smokeDebt -= smoke;
        _emberDebt -= embers;
        if (smoke > 0 || embers > 0)
        {
            Smoke(kilns.FlueCells, smoke, embers, eye, reach);
            Smoke(bloomeries.FlueCells, smoke, embers, eye, reach);
        }

        _heatScratch.Clear();
        foreach (var (anchor, facing, state) in bloomeries.View())
        {
            _heatScratch[anchor] = state.Heat;
            if (!state.Lit || !_lastHeat.TryGetValue(anchor, out var before)) continue;

            var sparks = BlastSparks(state.Heat - before);
            if (sparks == 0) continue;
            var throat = BloomeryLayout.ToWorld(anchor, facing, BloomeryLayout.ThroatLocal);
            if (Vector3.DistanceSquared(throat, eye) > reach) continue;
            fire.Puff(throat, sparks, ParticleConfig.FlueSpread, FireParticleKind.Ember);
            fire.Puff(throat, ParticleConfig.BellowsBlastSmoke, ParticleConfig.FlueSpread);
        }

        (_lastHeat, _heatScratch) = (_heatScratch, _lastHeat);
    }

    private void Smoke(IReadOnlyList<Vector3> flues, int smoke, int embers, Vector3 eye, float reach)
    {
        for (var i = 0; i < flues.Count; i++)
        {
            // The lists carry "origin minus the particle offset", like every lit cell
            var mouth = flues[i] + FireParticleSystem.SpawnOffset;
            if (Vector3.DistanceSquared(mouth, eye) > reach) continue;
            if (smoke > 0) fire.Puff(mouth, smoke, ParticleConfig.FlueSpread);
            if (embers > 0) fire.Puff(mouth, embers, ParticleConfig.FlueSpread, FireParticleKind.Ember);
        }
    }
}
