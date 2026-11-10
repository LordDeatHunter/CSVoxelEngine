using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// An animal of the wild (<see cref="Deer"/>, <see cref="Boar"/>): a <see cref="RealmMob"/> that is not
/// hostile by nature. Each frame it either heeds the players near it (<see cref="Heeds"/>: it flees,
/// watches or, provoked, attacks) or goes about its wandering, where its species' idle habit plays
/// (<see cref="IdleTick"/>: grazing, rooting). A walker with a run clip switches its gait by its speed
/// (<see cref="ChooseGait"/>), on a client's mirror as on the server, and breaks off an idle clip when it
/// starts to run. Its counted action clips (<see cref="RealmMob.StartAttack(int)"/>) reach the clients
/// through the attack count like any realm mob's. Main thread only.
/// </summary>
public abstract class Animal : RealmMob
{
    private bool _running;

    /// <param name="walkSpeed">The speed its walk clip is authored at.</param>
    protected Animal(EntityType type, Vector3 position, float walkSpeed, bool spawnIn)
        : base(type, position, walkSpeed, spawnIn)
    {
    }

    /// <summary>Which species this is.</summary>
    public abstract WildlifeKind Kind { get; }

    /// <summary>How fast a player walking into it shoves it aside.</summary>
    public abstract float ShoveSpeed { get; }

    // A herd at full health would otherwise fill the view with bars: an animal shows its own once it is hurt
    /// <inheritdoc />
    public override bool ShowsHealthbar => Health < MaxHealth;

    /// <summary>
    /// Whether the animal reacts to <paramref name="targets"/> this frame: true hands it the one the shared
    /// notice rule picks (<see cref="Mob.PickTarget"/>) to flee from, watch or charge; false leaves it
    /// wandering as if nobody were there.
    /// </summary>
    public abstract bool Heeds(IReadOnlyList<IMobTarget> targets);

    /// <summary>The clip a walker moves with above its walk (a deer's or a boar's <c>run</c>), or null for
    /// one that only walks.</summary>
    protected virtual string? RunClip => null;

    /// <summary>The speed <see cref="RunClip"/> is authored at, and the top of the range the gait is judged in.</summary>
    protected virtual float RunSpeed => WalkSpeed;

    /// <summary>Whether the action clip now playing is an idle habit (grazing, rooting, a look around) that
    /// starting to run breaks off, rather than an attack that must play out.</summary>
    protected virtual bool IdleActionPlaying => false;

    /// <summary>Called each frame the animal heeds nobody: its species' idle habits. Nothing by default.</summary>
    protected virtual void IdleTick(float deltaTime)
    {
    }

    /// <summary>The squared horizontal distance to the nearest target a hit could hurt, or
    /// <see cref="float.PositiveInfinity"/>: what an animal's wariness is judged by (a creative or flying
    /// player does not spook it).</summary>
    protected float NearestThreatSquared(IReadOnlyList<IMobTarget> targets)
    {
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i].IsImmune) continue;
            var eye = targets[i].Eye;
            var dx = eye.X - Position.X;
            var dz = eye.Z - Position.Z;
            nearest = MathF.Min(nearest, dx * dx + dz * dz);
        }
        return nearest;
    }

    /// <inheritdoc />
    protected override void Locomote(Vector3 move, IMobTarget? target, Vector3? wander, float deltaTime,
        ChunkManager chunks)
    {
        if (target == null && !IsDying) IdleTick(deltaTime);
        ChooseGait();
        // Running away, the ledge caution must not make an exception for the player it flees from
        base.Locomote(move, FacesTarget ? target : null, wander, deltaTime, chunks);
    }

    /// <inheritdoc />
    public override void AnimateMirror(float deltaTime)
    {
        ChooseGait();
        base.AnimateMirror(deltaTime);
    }

    /// <summary>
    /// Picks the locomotion clip by the horizontal speed: the run above
    /// <see cref="WildlifeConfig.RunGaitFraction"/> of the way from the walk speed to the run speed, the walk
    /// again below <see cref="WildlifeConfig.RunGaitFallFraction"/>. Breaking into the run cuts an idle habit
    /// short. Reads only the velocity, so a client's mirror walks and runs exactly as the server's animal does.
    /// </summary>
    private void ChooseGait()
    {
        if (RunClip is not { } run) return;

        var velocity = Velocity;
        var speed = MathF.Sqrt(velocity.X * velocity.X + velocity.Z * velocity.Z);
        var fraction = _running ? WildlifeConfig.RunGaitFallFraction : WildlifeConfig.RunGaitFraction;
        var running = speed > MathUtils.Lerp(WalkSpeed, RunSpeed, fraction);
        if (running == _running) return;

        _running = running;
        Animation.SetGait(EntityAnimator.IdleClip, running ? run : EntityAnimator.WalkClip, running ? RunSpeed : WalkSpeed);
        if (running && IdleActionPlaying) Animation.CutAction();
    }

    /// <summary>Whether the animal moves in its run gait right now.</summary>
    public bool IsRunning => _running;

    /// <summary>A uniform roll in <paramref name="min"/>..<paramref name="max"/>, for the idle habits' timers.</summary>
    protected static float Roll(float min, float max) => MathUtils.Lerp(min, max, Random.Shared.NextSingle());
}
