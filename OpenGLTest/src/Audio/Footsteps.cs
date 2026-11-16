using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Entities;
using VoxelEngine.Particles;
using VoxelEngine.Player;

namespace VoxelEngine.Audio;

/// <summary>One footfall that found a block underfoot: where the foot lands, the heading (a unit XZ vector, or
/// zero), the supporting cell and block id, and which foot (-1 left, 1 right).</summary>
public readonly record struct FootfallEvent(Vector3 Feet, Vector2 Heading, Vector3 Cell, ushort BlockId, int Side);

/// <summary>
/// The subtle footsteps: every footfall of the local player's gait
/// (<see cref="FootstepDust.FootfallsBetween"/> on <see cref="PlayerModel.WalkPhase"/>) and of each
/// walking mob (<see cref="Mob.TryTakeFootfall"/>) plays the <see cref="BlockType.StepSfx"/> group of the
/// block underfoot, when <see cref="FootstepSfx.Audible"/>. Owned by <see cref="World"/>; the
/// footfall is raised through <see cref="FootfallTaken"/> too, so a trail can follow the same steps.
/// Main thread only.
/// </summary>
public sealed class Footsteps
{
    private float _lastWalkPhase;
    private bool _hasWalkPhase;
    private Vector2 _runDirection;
    private Vector3 _lastFeet;

    private int _playerFoot;

    /// <summary>Raised for every footfall that found a block underfoot, silent block or not.</summary>
    public event System.Action<FootfallEvent>? FootfallTaken;

    /// <summary>One frame of the local player: sounds each footfall the gait crossed since the last call
    /// (at most two, so a hitch never bursts).</summary>
    public void UpdatePlayer(Camera camera, float walkPhase, float horizontalSpeed, ChunkManager chunks,
        WorldSounds sounds)
    {
        var feet = camera.Position - new Vector3(0f, Camera.EyeHeight, 0f);
        var moved = new Vector2(feet.X - _lastFeet.X, feet.Z - _lastFeet.Z);
        if (moved.LengthSquared() > 1e-8f) _runDirection = Vector2.Normalize(moved);
        _lastFeet = feet;

        var footfalls = _hasWalkPhase ? FootstepDust.FootfallsBetween(_lastWalkPhase, walkPhase) : 0;
        _lastWalkPhase = walkPhase;
        _hasWalkPhase = true;
        if (footfalls <= 0
            || !FootstepSfx.Audible(camera.IsGrounded, camera.IsFlying, camera.IsSneaking, camera.IsInWater,
                horizontalSpeed))
            return;
        if (!GroundDust.TryFindSurface(chunks, feet, PlayerPhysics.PlayerRadius, out var cell, out var blockId))
            return;

        if (BlockType.StepSfxOf(blockId) is { } group) sounds.Play(group);
        FootfallTaken?.Invoke(new FootfallEvent(feet, _runDirection, cell, blockId, (_playerFoot++ & 1) == 0 ? -1 : 1));
    }

    /// <summary>Takes every footfall the <paramref name="mobs"/> noted this frame and sounds it at the
    /// mob's feet, for a listener within <see cref="AudioConfig.MobFootstepRangeBlocks"/>
    /// (<paramref name="listener"/> null, a server, queues them all for the players near).</summary>
    public void TakeMobFootfalls(IReadOnlyList<Mob> mobs, ChunkManager chunks, WorldSounds sounds,
        Vector3? listener)
    {
        for (var i = 0; i < mobs.Count; i++)
        {
            var mob = mobs[i];
            if (!mob.TryTakeFootfall()) continue;
            var feet = mob.Position - new Vector3(0f, mob.BodyHalfHeight, 0f);
            var range = AudioConfig.MobFootstepRangeBlocks;
            if (listener is { } ear && Vector3.DistanceSquared(ear, feet) > range * range) continue;
            if (!GroundDust.TryFindSurface(chunks, feet, mob.BodyHalfExtent, out var cell, out var blockId)) continue;

            if (BlockType.StepSfxOf(blockId) is { } group) sounds.PlayAt(group, feet);
            var heading = new Vector2(-System.MathF.Sin(mob.Facing), -System.MathF.Cos(mob.Facing));
            FootfallTaken?.Invoke(new FootfallEvent(feet, heading, cell, blockId, (mob.FootfallCount & 1) == 0 ? -1 : 1));
        }
    }
}
