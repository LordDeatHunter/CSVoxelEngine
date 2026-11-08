using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Interaction;

namespace VoxelEngine.Entities;

/// <summary>
/// A mob of the layered realms (hell's <c>HellMob</c>s, the sky's <c>SkyMob</c>s): a <see cref="RiggedMob"/>
/// whose health and damage are scaled by where it spawned (each realm's own difficulty rule). Owns the
/// shared chase frame: it faces its target, lets the kind decide its move and attacks (<see cref="Decide"/>)
/// and moves (<see cref="Locomote"/>: a walk by default, a flyer steers its flight); nobody chased, it
/// strolls to its wander goal. Main thread only.
/// </summary>
public abstract class RealmMob : RiggedMob
{
    // A floating walker paddles toward its goal at this fraction of its walk speed
    private const float SwimSpeedFactor = 0.5f;

    private float _losRetry;
    private bool _losClear;
    private bool _attackCountMirrored;

    /// <param name="type">The kind, with its model and clips.</param>
    /// <param name="position">The body center it spawns at.</param>
    /// <param name="moveReferenceSpeed">The speed its walk (or fly) clip is authored at.</param>
    /// <param name="spawnIn">Plays the spawn clip and the pop-in; false starts settled.</param>
    /// <param name="flight">Makes the kind a flyer (<see cref="RiggedMob.Fly"/>); null walks.</param>
    protected RealmMob(EntityType type, Vector3 position, float moveReferenceSpeed, bool spawnIn,
        FlightTuning? flight = null)
        : base(type, position, moveReferenceSpeed, spawnIn, flight)
    {
    }

    /// <inheritdoc />
    public override float DeathSeconds => 0.8f;

    /// <summary>The kind's walk speed (before a status effect slows it).</summary>
    protected abstract float WalkSpeed { get; }

    /// <summary>The kind's auto-jump launch speed.</summary>
    protected abstract float JumpSpeed { get; }

    /// <summary>A multiplier on the ground walk this frame (a brute's charge); 1 by default.</summary>
    protected virtual float GroundSpeedFactor => 1f;

    /// <summary>The kind's first attack clip, variant 0 (a swing, a cast, a dive, a slam), started through
    /// <see cref="StartAttack()"/>; a kind with one attack has only this one.</summary>
    public abstract string AttackClip { get; }

    /// <summary>How many attack clips the kind has (<see cref="AttackClipOf"/>); 1 by default.</summary>
    protected virtual int AttackVariants => 1;

    /// <summary>The attack clip of <paramref name="variant"/> (0..<see cref="AttackVariants"/> - 1);
    /// <see cref="AttackClip"/> by default.</summary>
    protected virtual string AttackClipOf(int variant) => AttackClip;

    /// <summary>The attack clip an attack count names: its remainder modulo <see cref="AttackVariants"/>
    /// is the variant.</summary>
    public string AttackClipFor(int count) => AttackClipOf(VariantOf(count));

    /// <summary>
    /// How many attacks the mob has started (<see cref="StartAttack(int)"/>), the counter a server broadcasts in
    /// the mob's <see cref="Networking.NetEntity.Ammo"/> so a client's mirror can play each one
    /// (<see cref="MirrorAttackCount"/>); a mirror holds the server's count. Its remainder modulo
    /// <see cref="AttackVariants"/> names the clip a mirror plays (<see cref="AttackClipFor"/>).
    /// </summary>
    public int AttackCount { get; private set; }

    /// <summary>Plays the first attack clip and counts the attack (<see cref="AttackCount"/>).</summary>
    protected void StartAttack() => StartAttack(0);

    /// <summary>
    /// Plays the attack clip of <paramref name="variant"/> and advances <see cref="AttackCount"/> to the next
    /// count whose remainder names that variant, so the count always moves (by 1 for a one-attack kind).
    /// </summary>
    protected void StartAttack(int variant)
    {
        var n = AttackVariants;
        var step = ((variant - AttackCount) % n + n) % n;
        AttackCount += step == 0 ? n : step;
        Animation.Play(AttackClipOf(variant));
    }

    /// <summary>
    /// A client's mirror takes the server's <see cref="AttackCount"/> from a snapshot: a count that moved since
    /// the last snapshot means the server started an attack, so the mirror plays the clip that count names
    /// (once; the locomotion pose returns when the clip ends). The first count a mirror sees only sets it, and a
    /// dying mirror keeps its death.
    /// </summary>
    public void MirrorAttackCount(int count)
    {
        if (_attackCountMirrored && count != AttackCount && !IsDying) Animation.Play(AttackClipFor(count));
        AttackCount = count;
        _attackCountMirrored = true;
    }

    private int VariantOf(int count)
    {
        var n = AttackVariants;
        return ((count % n) + n) % n;
    }

    /// <summary>Where the mob looks from at its target's chest: its eye, a little under the head top.</summary>
    public Vector3 EyePoint => Position + new Vector3(0f, BodyHeight * 0.35f, 0f);

    /// <summary>Where a missile, a dive or a look is aimed on a player whose eye is at <paramref name="eye"/>:
    /// the chest, a little under the eye.</summary>
    public static Vector3 ChestOf(Vector3 eye) => eye - new Vector3(0f, 0.4f, 0f);

    /// <summary>
    /// Advances the AI, the physics and the animation one frame against <paramref name="target"/>
    /// (null: nobody to chase, so the mob strolls toward <paramref name="wander"/> when given, or
    /// stands or hovers). A dying mob chases nobody.
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

        Locomote(move, target, wander, deltaTime, chunks);
    }

    /// <summary>
    /// Moves the body one frame: by default a walk toward <paramref name="move"/> (the ledge caution, the
    /// auto-jump, paddling afloat). A flyer overrides it to steer its flight toward its own goal for
    /// <paramref name="target"/> or <paramref name="wander"/>.
    /// </summary>
    protected virtual void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks) =>
        Walk(move, target?.Eye, WalkSpeed * GroundSpeedFactor * MoveFactor, WalkSpeed * SwimSpeedFactor * MoveFactor,
            JumpSpeed, deltaTime, chunks);

    /// <summary>Counts the kind's attack cooldowns down by <paramref name="scaledDelta"/> (the frame
    /// time already slowed by a status effect).</summary>
    protected abstract void TickCooldowns(float scaledDelta);

    /// <summary>The frame's horizontal move (a unit vector, zero or a fraction for a stroll) toward or
    /// away from a target <paramref name="dist"/> blocks off along <paramref name="toward"/>; starts an
    /// attack when one is due. Not called while an attack clip plays.</summary>
    protected abstract Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target);

    /// <summary>
    /// Whether the line from <paramref name="from"/> to the target's chest is clear of blocks, checked
    /// at most every <see cref="MobConfig.RealmMobLosRetrySeconds"/>: between checks the last answer
    /// stands, so a mob behind a wall does not raycast every frame.
    /// </summary>
    protected bool SeesTarget(Vector3 from, IMobTarget target, ChunkManager chunks)
    {
        if (_losRetry > 0f) return _losClear;
        _losClear = !BlockRaycaster.SegmentHitsBlock(from, ChestOf(target.Eye), chunks);
        _losRetry = MobConfig.RealmMobLosRetrySeconds;
        return _losClear;
    }
}
