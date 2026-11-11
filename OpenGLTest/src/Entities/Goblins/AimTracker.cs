using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Player;

namespace VoxelEngine.Entities.Goblins;

/// <summary>
/// A ranged mob's eye on the target it winds up at, GL-free (main thread: it reads the live chunk map).
/// <see cref="Lock"/> names the target when the attack clip starts, <see cref="Track"/> follows it every frame of
/// the wind-up and <see cref="Solve"/> gives the launch velocity at the release, so the shot is aimed from where
/// the target is and how it moves when the projectile leaves the hand, not from what it did when the wind-up
/// began. The horizontal velocity is averaged over <see cref="MobConfig.RangedAimSmoothingSeconds"/>: a steady
/// runner is led in full, a player jiggling left and right is shot where they stand. The ground under the target
/// is probed too, so a jump or a fall is led down to where it lands and no further.
/// </summary>
public sealed class AimTracker
{
    private IMobTarget? _target;
    private ChunkManager? _chunks;
    private Vector3 _chest;
    private Vector3 _velocity;
    private float _floorY;

    /// <summary>Starts following <paramref name="target"/>, aimed at its chest, as the attack clip starts.</summary>
    public void Lock(IMobTarget target, ChunkManager chunks)
    {
        _target = target;
        _chunks = chunks;
        _velocity = target.Velocity;
        Sample(target, chunks);
    }

    /// <summary>
    /// Follows the locked target for one frame while <paramref name="attacking"/> (the attack clip plays); once
    /// the clip is over, or the target died, lets go of it and keeps the last aim.
    /// </summary>
    public void Track(float deltaTime, bool attacking)
    {
        if (_target == null || _chunks == null) return;
        if (!attacking || _target.IsDead)
        {
            _target = null;
            _chunks = null;
            _velocity = Vector3.Zero;
            return;
        }

        var seen = _target.Velocity;
        var blend = 1f - MathF.Exp(-deltaTime / MobConfig.RangedAimSmoothingSeconds);
        // The vertical speed is not averaged: a jump's arc is known exactly from the speed it has right now
        _velocity = new Vector3(float.Lerp(_velocity.X, seen.X, blend), seen.Y, float.Lerp(_velocity.Z, seen.Z, blend));
        Sample(_target, _chunks);
    }

    /// <summary>
    /// The launch velocity (length <paramref name="speed"/>) from <paramref name="origin"/> that meets the
    /// tracked target (<see cref="GoblinAim.TryLeadVelocity"/>); false when it is out of reach, the velocity
    /// then being the maximum-range toss toward it.
    /// </summary>
    public bool Solve(Vector3 origin, float speed, float gravity, out Vector3 velocity) =>
        GoblinAim.TryLeadVelocity(origin, _chest, _velocity, _floorY, speed, gravity, out velocity);

    // Where the target's chest is now and how low it can come: the chest's height over the feet above the first
    // ground under them. A flyer neither falls nor lands, so its vertical speed is not led.
    private void Sample(IMobTarget target, ChunkManager chunks)
    {
        var eye = target.Eye;
        _chest = RealmMob.ChestOf(eye);
        if (target.IsFlying) _velocity.Y = 0f;

        var feetY = eye.Y - Camera.EyeHeight;
        var ground = LedgeProbe.GroundBelow(chunks, eye.X, feetY, eye.Z, MobConfig.RangedAimFloorScanBlocks);
        _floorY = ground + (_chest.Y - feetY);
    }
}
