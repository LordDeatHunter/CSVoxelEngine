using System;
using System.Numerics;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.TowerMobs;

/// <summary>
/// The Skeleton King: the battle tower's boss, raised once by the roof altar's ritual. A big undead humanoid driven
/// by a <see cref="SkeletonKingBrain"/>: in slam reach it smashes its club down (the slam clip; the shockwave goes out
/// at <see cref="ImpactEvent"/>, <see cref="TryTakeSlamImpact"/>, and the ground heaves where the club lands,
/// <see cref="TryTakeSlamLanded"/>), at range it throws a burst of bones (the volley clip: from
/// <see cref="ReleaseEvent"/> the bones leave one after another, <see cref="TryTakeBurstBone"/>), and from phase 2 it calls up skeletons
/// (the summon clip, at <see cref="SummonEvent"/>, <see cref="TryTakeSummon"/>); the manager lands each one. It is
/// leashed to its <see cref="Home"/> on the tower roof: past the leash it walks home (<see cref="HomeMove"/>), and
/// knocked well past it or off the roof it is put back (<see cref="EnforceLeash"/>). With no target engaged for
/// <see cref="MobConfig.SkeletonKingResetSeconds"/> a hurt king heals to full and starts the fight over. Not
/// difficulty-scaled: its pool is its kind's, so a client mirror's boss bar reads the same share. Three attack clips
/// share the replicated attack count by remainder (slam, volley, summon). Main thread only.
/// </summary>
public sealed class SkeletonKing : RealmMob
{
    /// <summary>The entity file name (<c>assets/data/entities/skeleton_king.json</c>).</summary>
    public const string EntityName = "skeleton_king";

    /// <summary>The slam clip, attack variant <see cref="SlamVariant"/>.</summary>
    public const string SlamClip = "slam";

    /// <summary>Timeline event on the slam clip at which the club hits the ground and the shockwave goes out.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>The volley clip, attack variant <see cref="VolleyVariant"/>.</summary>
    public const string VolleyClip = "volley";

    /// <summary>Timeline event on the volley clip at which the bones leave the hand.</summary>
    public const string ReleaseEvent = "release";

    /// <summary>The summon clip, attack variant <see cref="SummonVariant"/>.</summary>
    public const string SummonClip = "summon";

    /// <summary>Timeline event on the summon clip at which the skeletons rise.</summary>
    public const string SummonEvent = "summon";

    /// <summary>The attack variant of the slam.</summary>
    public const int SlamVariant = 0;

    /// <summary>The attack variant of the volley.</summary>
    public const int VolleyVariant = 1;

    /// <summary>The attack variant of the summon.</summary>
    public const int SummonVariant = 2;

    private Vector3 _aim;
    private float _unengaged;
    private readonly BoneBurst _burst = new();
    // Where the last slam's club landed, until the ground heave takes it (cosmetic; null when none is pending)
    private Vector3? _slamLanded;

    /// <param name="type">The king's kind, with its model and clips.</param>
    /// <param name="position">The body center it spawns at.</param>
    /// <param name="home">The tower it belongs to and the feet it is leashed to.</param>
    /// <param name="spawnIn">Plays the spawn clip (the rise) and the pop-in; false starts settled.</param>
    /// <param name="roll">The brain's roll source (0..1); null rolls at random.</param>
    public SkeletonKing(EntityType type, Vector3 position, SkeletonKingHome home, bool spawnIn = true,
        Func<float>? roll = null)
        : base(type, position, MobConfig.SkeletonKingWalkSpeed, spawnIn)
    {
        Home = home;
        Brain = new SkeletonKingBrain(roll ?? Random.Shared.NextSingle);
    }

    /// <summary>The tower the king belongs to and the feet it is leashed to, fixed for life.</summary>
    public SkeletonKingHome Home { get; }

    /// <summary>The king's decisions: phase, cooldowns, attack choice.</summary>
    public SkeletonKingBrain Brain { get; }

    /// <summary>The fight's phase, 1..3 (a mirror holds the server's).</summary>
    public int Phase => Brain.Phase;

    /// <inheritdoc />
    public override bool IsBoss => true;

    /// <inheritdoc />
    public override bool IsUndead => true;

    /// <inheritdoc />
    public override bool ShowsHealthbar => false;

    /// <inheritdoc />
    public override float BossBarRange => MobConfig.SkeletonKingBossBarRangeBlocks;

    /// <inheritdoc />
    public override float BodyHeight => MobConfig.SkeletonKingHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => MobConfig.SkeletonKingHalfExtent;

    /// <inheritdoc />
    protected override float WalkSpeed => MobConfig.SkeletonKingWalkSpeed;

    /// <inheritdoc />
    protected override float JumpSpeed => MobConfig.SkeletonKingJumpSpeed;

    /// <inheritdoc />
    protected override float GroundSpeedFactor => Brain.SpeedFactor;

    /// <inheritdoc />
    public override string AttackClip => SlamClip;

    /// <inheritdoc />
    protected override int AttackVariants => 3;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant switch
    {
        VolleyVariant => VolleyClip,
        SummonVariant => SummonClip,
        _ => SlamClip,
    };

    /// <summary>Where the bones leave the hand: a little in front of the body at shoulder height.</summary>
    public Vector3 VolleyOrigin => Position + Forward * 0.6f + new Vector3(0f, BodyHeight * 0.2f, 0f);

    /// <summary>Where the club strikes the ground: in front of the feet by <see cref="MobConfig.SkeletonKingSlamReachBlocks"/>.</summary>
    public Vector3 SlamPoint => Feet + Forward * MobConfig.SkeletonKingSlamReachBlocks;

    /// <summary>Throws, naming the model file, unless the model is a humanoid rig with idle, walk, a slam clip
    /// carrying the impact event, a volley clip carrying the release event and a summon clip carrying the summon
    /// event.</summary>
    public static void ValidateContent(EntityType type)
    {
        const string kind = "SkeletonKing";
        EntityContract.RequireHumanoid(type, kind, SlamClip, ImpactEvent);
        EntityContract.RequireEvent(type, kind, EntityContract.RequireClip(type, kind, VolleyClip), ReleaseEvent);
        EntityContract.RequireEvent(type, kind, EntityContract.RequireClip(type, kind, SummonClip), SummonEvent);
    }

    /// <summary>Whether a king centered at <paramref name="center"/> starts a slam at a player whose eye is at
    /// <paramref name="eye"/>.</summary>
    public static bool InSlamReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, MobConfig.SkeletonKingSlamTriggerReach);

    /// <summary>Whether the slam's shockwave from a king standing at <paramref name="feet"/> reaches a player whose
    /// eye is at <paramref name="eye"/>.</summary>
    public static bool InShockwave(Vector3 feet, Vector3 eye) =>
        MobTargets.InShockwave(feet, eye, MobConfig.SkeletonKingSlamRadius, MobConfig.SkeletonKingSlamHeight);

    /// <summary>The unit horizontal direction home for a king at <paramref name="position"/> that has walked past
    /// its leash radius; zero inside it.</summary>
    public static Vector3 HomeMove(Vector3 position, SkeletonKingHome home)
    {
        var to = new Vector3(home.Feet.X - position.X, 0f, home.Feet.Z - position.Z);
        var dist = to.Length();
        return dist > home.LeashRadius && dist > 1e-3f ? to / dist : Vector3.Zero;
    }

    /// <summary>
    /// One burst bone's launch velocity: <paramref name="baseVelocity"/> (the aimed throw) turned
    /// <paramref name="yawRadians"/> about the vertical axis, tipped <paramref name="pitchRadians"/> up (negative: down)
    /// in its own vertical plane, and its speed scaled by <paramref name="speedScale"/>. Zero offsets and a scale of 1
    /// give the aimed throw back.
    /// </summary>
    public static Vector3 ScatterVelocity(Vector3 baseVelocity, float yawRadians, float pitchRadians, float speedScale)
    {
        var (sin, cos) = MathF.SinCos(yawRadians);
        var turned = new Vector3(baseVelocity.X * cos - baseVelocity.Z * sin, baseVelocity.Y,
            baseVelocity.X * sin + baseVelocity.Z * cos);
        var flat = new Vector3(turned.X, 0f, turned.Z);
        var across = flat.Length();
        var speed = turned.Length() * speedScale;
        if (speed <= 0f) return Vector3.Zero;
        var elevation = MathF.Atan2(turned.Y, across) + pitchRadians;
        var heading = across > 1e-6f ? flat / across : Vector3.UnitX;
        var (up, out_) = MathF.SinCos(elevation);
        return (heading * out_ + Vector3.UnitY * up) * speed;
    }

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        Brain.Tick(scaledDelta);
        _burst.Tick(scaledDelta);
        _unengaged += scaledDelta;
        if (_unengaged < MobConfig.SkeletonKingResetSeconds || Health >= MaxHealth) return;

        // Left alone long enough: the king heals and the fight starts over
        Health = MaxHealth;
        Brain.Reset();
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        _unengaged = 0f;
        Brain.Observe(Health / (float)MaxHealth);

        var home = HomeMove(Position, Home);
        if (home != Vector3.Zero) return home;

        var inReach = InSlamReach(Position, target.Eye);
        switch (Brain.Choose(dist, inReach, SeesTarget(EyePoint, target, chunks), IsGrounded))
        {
            case SkeletonKingAttack.Slam:
                StartAttack(SlamVariant);
                return Vector3.Zero;
            case SkeletonKingAttack.Volley:
                _aim = ChestOf(target.Eye);
                StartAttack(VolleyVariant);
                return Vector3.Zero;
            case SkeletonKingAttack.Summon:
                StartAttack(SummonVariant);
                return Vector3.Zero;
            default:
                // Already in reach with the slam cooling down: hold ground instead of walking into the target
                return inReach ? Vector3.Zero : toward;
        }
    }

    /// <summary>True once per slam, at the slam clip's impact event; the manager finds the targets the shockwave
    /// reaches (<see cref="InShockwave"/>). The landing is also kept for the ground heave
    /// (<see cref="TryTakeSlamLanded"/>).</summary>
    public bool TryTakeSlamImpact()
    {
        if (!Animation.ConsumeEvent(ImpactEvent)) return false;
        _slamLanded = SlamPoint;
        return true;
    }

    /// <summary>True once per slam whose club has landed, with where it struck (<see cref="SlamPoint"/>): the cosmetic
    /// ground heave's cue, on the authoritative world (the manager took the impact) and on a client's mirror (its own
    /// clip reached the impact, <see cref="AnimateMirror"/>).</summary>
    public bool TryTakeSlamLanded(out Vector3 at)
    {
        at = _slamLanded ?? default;
        if (_slamLanded == null) return false;
        _slamLanded = null;
        return true;
    }

    /// <summary>
    /// True once for each bone of a volley's burst whose moment has come: the volley clip's release event starts a
    /// <see cref="BoneBurst"/> of <see cref="MobConfig.SkeletonKingBurstBones"/> (more when enraged) over
    /// <see cref="MobConfig.SkeletonKingBurstSeconds"/>, and every bone leaves the hand as it stands then, toward the
    /// chest the volley was aimed at when it started. A dying king's burst stops.
    /// </summary>
    public bool TryTakeBurstBone(out Vector3 origin, out Vector3 aim)
    {
        if (Animation.ConsumeEvent(ReleaseEvent))
            _burst.Start(MobConfig.SkeletonKingBurstBones + (Brain.IsEnraged ? MobConfig.SkeletonKingBurstEnrageExtraBones : 0),
                MobConfig.SkeletonKingBurstSeconds);
        origin = VolleyOrigin;
        aim = _aim;
        if (IsDying || IsDead)
        {
            _burst.Stop();
            return false;
        }
        return _burst.TryTake();
    }

    /// <inheritdoc />
    public override void AnimateMirror(float deltaTime)
    {
        base.AnimateMirror(deltaTime);
        // A mirror never lands the slam (the server bills it), but its club still strikes the ground: the heave plays
        if (Animation.ConsumeEvent(ImpactEvent)) _slamLanded = SlamPoint;
    }

    /// <summary>True once per summon, at the summon clip's summon event; the manager raises the skeletons.</summary>
    public bool TryTakeSummon() => Animation.ConsumeEvent(SummonEvent);

    /// <summary>
    /// Puts a king that fell more than <see cref="MobConfig.SkeletonKingLeashDropBlocks"/> below its home's feet, or
    /// was knocked more than <see cref="MobConfig.SkeletonKingLeashMarginBlocks"/> past its leash, back on its home
    /// with no velocity. True when it did.
    /// </summary>
    internal bool EnforceLeash()
    {
        var dx = Position.X - Home.Feet.X;
        var dz = Position.Z - Home.Feet.Z;
        var limit = Home.LeashRadius + MobConfig.SkeletonKingLeashMarginBlocks;
        var fell = Feet.Y < Home.Feet.Y - MobConfig.SkeletonKingLeashDropBlocks;
        if (!fell && dx * dx + dz * dz <= limit * limit) return false;

        Position = Home.Feet + new Vector3(0f, BodyHeight * 0.5f, 0f);
        SetVelocity(Vector3.Zero);
        return true;
    }

    /// <summary>A client's mirror takes the phase the server reported.</summary>
    public void MirrorPhase(int phase) => Brain.SetPhase(phase);
}
