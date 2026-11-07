using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Entities.HellMobs;

/// <summary>
/// A mob of hell's cavern pockets (<see cref="Hellbrute"/>, <see cref="Cinderwitch"/>): a
/// <see cref="RiggedMob"/> whose health, damage and attack pace are scaled by the hell layer it
/// spawned in (<see cref="LayerDifficulty"/>, read once from the spawn height, so a save restore and
/// a client's mirror at the same spot get the same numbers). Owns the shared chase frame: it faces
/// its target, lets the kind decide its move and attacks (<see cref="Decide"/>), and walks; nobody
/// chased, it strolls to its wander goal. Main thread only.
/// </summary>
public abstract class HellMob : RiggedMob
{
    // A floating hell mob paddles toward its goal at this fraction of its walk speed
    private const float SwimSpeedFactor = 0.5f;

    private float _losRetry;
    private bool _losClear;

    protected HellMob(EntityType type, Vector3 position, float walkSpeed, bool spawnIn)
        : base(type, position, walkSpeed, spawnIn)
    {
        Layer = LayerDifficulty.LayerAt(position.Y);
        Health = MaxHealth;
    }

    /// <summary>The hell layer this mob spawned in, fixed for life.</summary>
    public int Layer { get; }

    /// <inheritdoc />
    public override int MaxHealth => LayerDifficulty.Scale(Type.MaxHealth, Layer);

    /// <inheritdoc />
    public override float DeathSeconds => 0.8f;

    /// <summary>The kind's walk speed (before a status effect slows it).</summary>
    protected abstract float WalkSpeed { get; }

    /// <summary>The kind's auto-jump launch speed.</summary>
    protected abstract float JumpSpeed { get; }

    /// <summary>A multiplier on the ground walk this frame (a brute's charge); 1 by default.</summary>
    protected virtual float GroundSpeedFactor => 1f;

    /// <summary>Where the mob looks from at its target's chest: its eye, a little under the head top.</summary>
    public Vector3 EyePoint => Position + new Vector3(0f, BodyHeight * 0.35f, 0f);

    /// <summary>Where a missile or a look is aimed on a player whose eye is at <paramref name="eye"/>:
    /// the chest, a little under the eye.</summary>
    public static Vector3 ChestOf(Vector3 eye) => eye - new Vector3(0f, 0.4f, 0f);

    /// <summary>
    /// Advances the AI, the physics and the animation one frame against <paramref name="target"/>
    /// (null: nobody to chase, so the mob strolls toward <paramref name="wander"/> when given, or
    /// stands). A dying mob chases nobody.
    /// </summary>
    public void Update(float deltaTime, ChunkManager chunks, IMobTarget? target, Vector3? wander = null)
    {
        if (deltaTime <= 0f || chunks == null) return;

        TickBody(deltaTime);
        _losRetry = MathF.Max(0f, _losRetry - deltaTime);
        // A slowing status effect stretches the attack cooldowns (AttackFactor 1 = unslowed)
        TickCooldowns(deltaTime * AttackFactor);

        var move = Vector3.Zero;
        if (IsDying) target = null;
        if (target != null)
        {
            var to = new Vector3(target.Eye.X - Position.X, 0f, target.Eye.Z - Position.Z);
            var dist = to.Length();
            var toward = dist > 1e-3f ? to / dist : Vector3.Zero;
            if (dist > 1e-3f) FaceYaw(YawOf(to), deltaTime);
            move = Animation.IsActionPlaying ? Vector3.Zero : Decide(dist, toward, chunks, target);
        }
        else if (wander is { } goal)
        {
            move = WanderMove(goal, deltaTime);
        }

        Walk(move, target?.Eye, WalkSpeed * GroundSpeedFactor * MoveFactor, WalkSpeed * SwimSpeedFactor * MoveFactor,
            JumpSpeed, deltaTime, chunks);
    }

    /// <summary>Counts the kind's attack cooldowns down by <paramref name="scaledDelta"/> (the frame
    /// time already slowed by a status effect).</summary>
    protected abstract void TickCooldowns(float scaledDelta);

    /// <summary>The frame's horizontal move (a unit vector, zero or a fraction for a stroll) toward or
    /// away from a target <paramref name="dist"/> blocks off along <paramref name="toward"/>; starts an
    /// attack when one is due. Not called while an attack clip plays.</summary>
    protected abstract Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target);

    /// <summary>
    /// Whether the line from <paramref name="from"/> to the target's chest is clear of blocks, checked
    /// at most every <see cref="MobConfig.HellMobLosRetrySeconds"/>: between checks the last answer
    /// stands, so a mob behind a wall does not raycast every frame.
    /// </summary>
    protected bool SeesTarget(Vector3 from, IMobTarget target, ChunkManager chunks)
    {
        if (_losRetry > 0f) return _losClear;
        _losClear = !BlockRaycaster.SegmentHitsBlock(from, ChestOf(target.Eye), chunks);
        _losRetry = MobConfig.HellMobLosRetrySeconds;
        return _losClear;
    }
}
