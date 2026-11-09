using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Effects.Sky;
using VoxelEngine.Entities;
using VoxelEngine.Entities.Goblins;

namespace VoxelEngine.Simulation.Camps;

/// <summary>
/// Goblin camps, met by chance while exploring at night: once the night is dark enough
/// (<see cref="MobConfig.GoblinCampNightFactor"/>), every <see cref="MobConfig.GoblinCampCheckSeconds"/> a roll of
/// <see cref="MobConfig.GoblinCampChancePerCheck"/> pitches one camp per night, far from a player
/// (<see cref="MobConfig.GoblinCampMinDistanceBlocks"/>..<see cref="MobConfig.GoblinCampMaxDistanceBlocks"/>, on the dry
/// surface the ring spawns take, <see cref="MobSpawner.TryFindSurfaceSpawn(ChunkManager, Vector3, float, float, int, out int, out int, out int)"/>):
/// a lit campfire (<c>pitchFire</c>, the world's own placement and ignition) and
/// <see cref="MobConfig.GoblinCampMinGoblins"/>..<see cref="MobConfig.GoblinCampMaxGoblins"/> goblins round it, each
/// carrying a torch and wandering about the fire (<see cref="Mob.WanderAnchor"/>) until a player comes close. A camp is
/// a forced spawn past the natural cap, never past the hard cap; the fire and the goblins then live by their own rules
/// (the fire burns down, a goblin despawns past <see cref="MobConfig.GoblinDespawnRadiusBlocks"/>; the anchor is not
/// saved). Authoritative worlds only, main thread.
/// </summary>
public sealed class GoblinCamps(GoblinManager goblins, Func<int, int, int, bool> pitchFire, Func<float>? roll = null)
{
    private readonly Func<float> _roll = roll ?? Random.Shared.NextSingle;
    private float _checkClock;
    // Whether this night has had its camp; the day clears it
    private bool _campedTonight;

    /// <summary>
    /// Once a frame: by day the night's camp is forgotten; at night, off the check timer and before tonight's camp,
    /// one roll may pitch a camp far from a random target (<paramref name="night"/> is <see cref="DayCycle.NightFactor"/>).
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, IReadOnlyList<IMobTarget> targets, float night)
    {
        if (night < MobConfig.GoblinCampNightFactor)
        {
            _campedTonight = false;
            _checkClock = 0f;
            return;
        }
        if (_campedTonight || targets.Count == 0) return;
        _checkClock += deltaTime;
        if (_checkClock < MobConfig.GoblinCampCheckSeconds) return;
        _checkClock = 0f;
        if (_roll() >= MobConfig.GoblinCampChancePerCheck) return;

        var target = targets[Math.Min(targets.Count - 1, (int)(_roll() * targets.Count))];
        for (var attempt = 0; attempt < MobConfig.GoblinCampSiteTries; attempt++)
        {
            // A site with bare air over its ground, so the fire never lands in a grass tuft's cell
            if (!MobSpawner.TryFindSurfaceSpawn(chunks, target.Eye, MobConfig.GoblinCampMinDistanceBlocks,
                    MobConfig.GoblinCampMaxDistanceBlocks, 1, out var x, out var groundY, out var z)
                || !chunks.TryGetCell(x, groundY + 1, z, out var above, out _) || above != BlockType.Air)
                continue;
            _campedTonight = Pitch(chunks, x, groundY, z) >= 0;
            return;
        }
    }

    /// <summary>
    /// Pitches a camp on ground cell (<paramref name="x"/>, <paramref name="groundY"/>, <paramref name="z"/>): the lit
    /// fire on top of it, then its goblins on dry standing spots round it. Returns how many goblins rose, or -1 (and no
    /// goblins) when the fire could not be pitched there. Also the scenario's <c>goblincamp</c> step.
    /// </summary>
    public int Pitch(ChunkManager chunks, int x, int groundY, int z)
    {
        if (!pitchFire(x, groundY, z)) return -1;

        var fire = new Vector3(x + 0.5f, groundY + 1f, z + 0.5f);
        var count = MobConfig.GoblinCampMinGoblins
                    + Math.Min(MobConfig.GoblinCampMaxGoblins - MobConfig.GoblinCampMinGoblins,
                        (int)(_roll() * (MobConfig.GoblinCampMaxGoblins - MobConfig.GoblinCampMinGoblins + 1)));
        var risen = 0;
        for (var i = 0; i < count; i++)
        {
            if (!MobSpawner.TryFindSurfaceSpawn(chunks, fire, MobConfig.GoblinCampRingMinBlocks,
                    MobConfig.GoblinCampRingMaxBlocks, MobConfig.GoblinCampSpotTries, out var gx, out var gy, out var gz))
                continue;
            // Never the fire's own cell
            if (gx == x && gz == z) continue;
            if (goblins.Spawn(GoblinManager.BodyCenter(gx, gy, gz), torches: 1) is not { } goblin) break;
            goblin.WanderAnchor = fire;
            risen++;
        }
        return risen;
    }
}
