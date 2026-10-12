using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Entities;

/// <summary>
/// One mob's interest in the target it noticed, GL-free (main thread: it reads the live chunk map).
/// A line-of-sight check (<see cref="BlockRaycaster.SegmentHitsBlock"/>) runs every
/// <see cref="WorldGenerationConfig.MobSightCheckSeconds"/>; once the sight has been blocked for
/// <see cref="WorldGenerationConfig.MobLoseInterestSeconds"/> the mob loses interest and stops
/// chasing, and a clear line wins it back at the next check. A different target restarts the count.
/// While the mob chases nothing (none noticed, or interest lost) it is idle, and an idle mob far from
/// every target may despawn (<see cref="ShouldIdleDespawn"/>). <see cref="Mob.PickTarget"/> owns one.
/// </summary>
public sealed class MobInterest
{
    private IMobTarget? _target;
    private float _unseenSeconds;
    private float _sightTimer;
    private bool _sightBlocked;

    /// <summary>Seconds the mob has chased nothing in a row.</summary>
    public float IdleSeconds { get; private set; }

    /// <summary>Whether the sight of the current target has been blocked long enough to give up on it.</summary>
    public bool IsLost => _unseenSeconds >= WorldGenerationConfig.MobLoseInterestSeconds;

    /// <summary>
    /// Advances the interest by <paramref name="deltaTime"/> with <paramref name="noticed"/> (the target
    /// the notice rule picked, or null) seen from <paramref name="sightOrigin"/>, and returns whether the
    /// mob chases it this frame.
    /// </summary>
    public bool Update(IMobTarget? noticed, Vector3 sightOrigin, float deltaTime, ChunkManager chunks)
    {
        if (noticed == null)
        {
            _target = null;
            _unseenSeconds = 0f;
            IdleSeconds += deltaTime;
            return false;
        }

        if (!ReferenceEquals(noticed, _target))
        {
            // A new target is judged afresh, its first check this very frame
            _target = noticed;
            _unseenSeconds = 0f;
            _sightTimer = 0f;
        }

        _sightTimer -= deltaTime;
        if (_sightTimer <= 0f)
        {
            _sightTimer = WorldGenerationConfig.MobSightCheckSeconds;
            _sightBlocked = BlockRaycaster.SegmentHitsBlock(sightOrigin, noticed.Eye, chunks);
        }

        _unseenSeconds = _sightBlocked ? _unseenSeconds + deltaTime : 0f;
        if (IsLost)
        {
            IdleSeconds += deltaTime;
            return false;
        }

        IdleSeconds = 0f;
        return true;
    }

    /// <summary>Whether a mob idle for <paramref name="idleSeconds"/> whose nearest target is
    /// <paramref name="minDistanceSquared"/> away (horizontally, squared) despawns: idle for at least
    /// <see cref="WorldGenerationConfig.MobIdleDespawnSeconds"/> and farther than
    /// <see cref="WorldGenerationConfig.MobIdleDespawnMinDistanceBlocks"/> from every target.</summary>
    public static bool ShouldIdleDespawn(float idleSeconds, float minDistanceSquared) =>
        idleSeconds >= WorldGenerationConfig.MobIdleDespawnSeconds
        && minDistanceSquared > WorldGenerationConfig.MobIdleDespawnMinDistanceBlocks
            * WorldGenerationConfig.MobIdleDespawnMinDistanceBlocks;

    /// <summary>A wander goal around <paramref name="origin"/> for an <paramref name="angleRoll"/> and a
    /// <paramref name="radiusRoll"/> in [0, 1): a heading and a distance between
    /// <see cref="WorldGenerationConfig.MobWanderMinRadiusBlocks"/> and
    /// <see cref="WorldGenerationConfig.MobWanderMaxRadiusBlocks"/>, at the origin's height.</summary>
    public static Vector3 WanderPoint(Vector3 origin, float angleRoll, float radiusRoll)
    {
        var angle = angleRoll * MathF.Tau;
        var radius = MathUtils.Lerp(WorldGenerationConfig.MobWanderMinRadiusBlocks,
            WorldGenerationConfig.MobWanderMaxRadiusBlocks, MathUtils.Clamp01(radiusRoll));
        return new Vector3(origin.X + MathF.Cos(angle) * radius, origin.Y, origin.Z + MathF.Sin(angle) * radius);
    }
}
