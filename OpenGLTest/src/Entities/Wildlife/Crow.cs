using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Audio;
using VoxelEngine.Chunks;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The crow: a thief that comes in flocks. Left alone it soars about, lands (in a tree when one is near) to
/// strut, peck and caw, and takes off again; a dropped item within <see cref="WildlifeConfig.CrowThiefRangeBlocks"/>
/// draws it down to snatch it (<see cref="Errand"/>, the manager takes the item). It never attacks until a
/// player hurts it, and for <see cref="WildlifeConfig.CrowAngerSeconds"/> after the blow a player within
/// <see cref="WildlifeConfig.CrowNoticeRangeBlocks"/> draws it: it
/// circles over their head and swoops (its <c>swoop</c> clip flown at the chest), raking at the clip's
/// <see cref="ImpactEvent"/> whoever is within <see cref="WildlifeConfig.CrowStrikeReach"/>, at most every
/// <see cref="WildlifeConfig.CrowStrikeMinSeconds"/>..<c>Max</c>. One caught on the ground with a player
/// beside it pecks at their ankles instead of leaving. Each strike is small; a flock is the nuisance.
/// Main thread only.
/// </summary>
public sealed class Crow : FlyingAnimal
{
    /// <summary>The entity file name (<c>assets/data/entities/crow.json</c>).</summary>
    public const string EntityName = "crow";

    public const string SwoopClip = "swoop";
    public const string PeckClip = "peck";
    public const string CawClip = "caw";
    public const string TakeoffClip = "takeoff";
    public const string LandClip = "land";
    public const string GlideClip = "glide";
    public const string FlapClip = "flap";

    /// <summary>Timeline event on the swoop and peck clips at which the beak strikes.</summary>
    public const string ImpactEvent = "impact";

    /// <summary>Timeline event on the flap clip's downstroke, the moment the flap lifts the crow.</summary>
    public const string BeatEvent = "beat";

    // The counted action clips (RealmMob.StartAttack), which a client's mirror replays by this index
    private const int SwoopAction = 0;
    private const int PeckAction = 1;
    private const int CawAction = 2;
    private const int TakeoffActionIndex = 3;
    private const int LandActionIndex = 4;
    private const int FlapActionIndex = 5;

    /// <summary>How the crow flies.</summary>
    public static readonly FlightTuning Tuning = new(
        WildlifeConfig.CrowCruiseSpeed, WildlifeConfig.CrowVerticalSpeed, WildlifeConfig.CrowAcceleration,
        WildlifeConfig.CrowHoverMinBlocks, WildlifeConfig.CrowHoverMaxBlocks, WildlifeConfig.CrowMinClearanceBlocks,
        WildlifeConfig.CrowBobBlocks, WildlifeConfig.CrowBobHz);

    private float _strikeCooldown = WildlifeConfig.CrowStrikeMinSeconds;
    private float _angerLeft;
    private float _thiefCooldown;
    private float _flapIn = Roll(WildlifeConfig.CrowFlapMinSeconds, WildlifeConfig.CrowFlapMaxSeconds);
    private ItemDrop? _prize;
    private bool _rallyPending;
    private float _habitIn = Roll(WildlifeConfig.CrowHabitMinSeconds, WildlifeConfig.CrowHabitMaxSeconds);

    public Crow(EntityType type, Vector3 position, bool spawnIn = true) : base(type, position, Tuning, spawnIn)
    {
    }

    /// <inheritdoc />
    public override WildlifeKind Kind => WildlifeKind.Crow;

    /// <inheritdoc />
    public override string? HitSoundGroup => SfxLibrary.CrowHitGroup;

    /// <inheritdoc />
    public override string? DeathSoundGroup => SfxLibrary.CrowDeathGroup;

    /// <inheritdoc />
    public override float BodyHeight => WildlifeConfig.CrowHeight;

    /// <inheritdoc />
    public override float BodyHalfWidth => WildlifeConfig.CrowHalfExtent;

    /// <inheritdoc />
    public override float ShoveSpeed => WildlifeConfig.CrowShoveSpeed;

    /// <inheritdoc />
    public override string AttackClip => SwoopClip;

    /// <inheritdoc />
    protected override int AttackVariants => 6;

    /// <inheritdoc />
    protected override string AttackClipOf(int variant) => variant switch
    {
        PeckAction => PeckClip,
        CawAction => CawClip,
        TakeoffActionIndex => TakeoffClip,
        LandActionIndex => LandClip,
        FlapActionIndex => FlapClip,
        _ => SwoopClip,
    };

    /// <inheritdoc />
    protected override (string Idle, string Move) GroundClips => (EntityAnimator.IdleClip, EntityAnimator.WalkClip);

    // Chasing, even its idle is a wing beat; calm it holds set wings and glides down, and each flap
    // (TickFlaps) plays the flap clip once
    /// <inheritdoc />
    protected override (string Idle, string Move) AirClips(bool chasing) =>
        chasing ? (EntityAnimator.FlyClip, EntityAnimator.FlyClip) : (GlideClip, GlideClip);

    /// <inheritdoc />
    protected override int TakeoffAction => TakeoffActionIndex;

    /// <inheritdoc />
    protected override int LandAction => LandActionIndex;

    /// <summary>Whether a crow centered at <paramref name="center"/> on the ground can peck a player whose eye
    /// is at <paramref name="eye"/>.</summary>
    public static bool InPeckReach(Vector3 center, Vector3 eye) =>
        MobTargets.InMeleeReach(center, eye, WildlifeConfig.CrowPeckReach);

    /// <summary>Whether a swooping crow centered at <paramref name="center"/> rakes a player whose eye is at
    /// <paramref name="eye"/>: its chest within <see cref="WildlifeConfig.CrowStrikeReach"/> in 3D.</summary>
    public static bool InSwoopReach(Vector3 center, Vector3 eye) =>
        Vector3.DistanceSquared(center, ChestOf(eye)) <= WildlifeConfig.CrowStrikeReach * WildlifeConfig.CrowStrikeReach;

    /// <summary>Throws, naming the model file, unless the model is a flyer's rig with every clip the crow plays,
    /// the impact event on its swoop and its peck and the beat event on its flap.</summary>
    public static void ValidateContent(EntityType type)
    {
        EntityContract.RequireFlyer(type, "Crow", SwoopClip, ImpactEvent);
        EntityContract.RequireEvent(type, "Crow", EntityContract.RequireClip(type, "Crow", PeckClip), ImpactEvent);
        EntityContract.RequireEvent(type, "Crow", EntityContract.RequireClip(type, "Crow", FlapClip), BeatEvent);
        foreach (var clip in (string[])[EntityAnimator.WalkClip, GlideClip, CawClip, TakeoffClip, LandClip])
            EntityContract.RequireClip(type, "Crow", clip);
    }

    /// <summary>Whether somebody has hurt it lately, the only thing that makes it harass a player.</summary>
    public bool IsProvoked => _angerLeft > 0f;

    /// <summary>The dropped item it is flying to steal, or null.</summary>
    public ItemDrop? Prize => _prize;

    /// <summary>Whether it is free to go after a new prize: calm, not mid-grab and past its last theft's cooldown.</summary>
    public bool WantsPrize => _prize == null && _thiefCooldown <= 0f && !IsProvoked && !IsDying;

    /// <summary>Sets it on <paramref name="drop"/>, or off its errand with null.</summary>
    public void SetPrize(ItemDrop? drop) => _prize = drop;

    /// <summary>Whether it is close enough to its prize to take it.</summary>
    public bool PrizeInReach =>
        _prize != null && Vector3.DistanceSquared(Position, _prize.Position) <=
        WildlifeConfig.CrowThiefReach * WildlifeConfig.CrowThiefReach;

    /// <summary>It has taken its prize: the errand ends and the next waits out its cooldown.</summary>
    public void FinishErrand()
    {
        _prize = null;
        _thiefCooldown = WildlifeConfig.CrowThiefCooldownSeconds;
    }

    /// <inheritdoc />
    protected override Vector3? Errand => _prize?.Position;

    /// <inheritdoc />
    public override void TakeDamage(int amount)
    {
        base.TakeDamage(amount);
        if (amount <= 0) return;
        _angerLeft = WildlifeConfig.CrowAngerSeconds;
        _prize = null;
        _rallyPending = true;
    }

    /// <summary>Whether it was hurt since the manager last asked, so the flock must be called to arms. Reading it clears it.</summary>
    public bool ConsumeRallyCall()
    {
        var pending = _rallyPending;
        _rallyPending = false;
        return pending;
    }

    /// <summary>Answers a flockmate's distress: a calm crow turns on whoever is near, as if it had been hurt itself.
    /// A crow already angry (it has its own quarry) is left as it is.</summary>
    public void JoinFlock()
    {
        if (IsProvoked || IsDying) return;
        _angerLeft = WildlifeConfig.CrowAngerSeconds;
        _prize = null;
    }

    /// <inheritdoc />
    public override bool Heeds(IReadOnlyList<IMobTarget> targets) =>
        IsProvoked
        && NearestThreatSquared(targets) <= WildlifeConfig.CrowNoticeRangeBlocks * WildlifeConfig.CrowNoticeRangeBlocks;

    // A tree top within reach: a leaf or branch it could settle on, found by dropping a probe down sampled columns
    /// <inheritdoc />
    protected override Vector3? ChoosePerchSpot(ChunkManager chunks)
    {
        Vector3? best = null;
        var bestDistance = float.PositiveInfinity;
        var (cx, cy, cz) = Position.BlockCell();
        for (var i = 0; i < WildlifeConfig.FlyerPerchSamples; i++)
        {
            var angle = Random.Shared.NextSingle() * MathF.Tau;
            var radius = MathF.Sqrt(Random.Shared.NextSingle()) * WildlifeConfig.FlyerPerchSearchRadiusBlocks;
            var x = cx + (int)MathF.Round(MathF.Cos(angle) * radius);
            var z = cz + (int)MathF.Round(MathF.Sin(angle) * radius);
            for (var y = cy + 6; y >= cy - 20; y--)
            {
                var id = chunks.GetBlockGlobal(x, y, z);
                if (!BlockType.IsSolid(id)) continue;
                if (IsPerchBlock(id) && radius < bestDistance)
                {
                    bestDistance = radius;
                    best = new Vector3(x + 0.5f, y + 1.5f, z + 0.5f);
                }
                break;
            }
        }
        return best;
    }

    private static bool IsPerchBlock(ushort id) => BlockType.IsTreeWood(id) || BlockType.ById[id] is { Cutout: true };

    /// <inheritdoc />
    protected override bool StaysPerchedNear(IMobTarget target) => InPeckReach(Position, target.Eye);

    /// <inheritdoc />
    protected override void TickCooldowns(float scaledDelta)
    {
        _strikeCooldown = MathF.Max(0f, _strikeCooldown - scaledDelta);
        _angerLeft = MathF.Max(0f, _angerLeft - scaledDelta);
        _thiefCooldown = MathF.Max(0f, _thiefCooldown - scaledDelta);
    }

    /// <inheritdoc />
    protected override Vector3 Decide(float dist, Vector3 toward, ChunkManager chunks, IMobTarget target)
    {
        if (_strikeCooldown > 0f) return Vector3.Zero;

        if (IsPerched)
        {
            if (!InPeckReach(Position, target.Eye)) return Vector3.Zero;
            Strike(PeckAction);
            return Vector3.Zero;
        }

        if (dist <= WildlifeConfig.CrowSwoopRangeBlocks && SeesTarget(Position, target, chunks)) Strike(SwoopAction);
        return Vector3.Zero;
    }

    private void Strike(int action)
    {
        StartAttack(action);
        _strikeCooldown = Roll(WildlifeConfig.CrowStrikeMinSeconds, WildlifeConfig.CrowStrikeMaxSeconds);
    }

    /// <inheritdoc />
    protected override void IdleTick(float deltaTime)
    {
        // Its habits are a bird's on the ground: a peck at the dirt or a caw
        if (!IsPerched) return;
        _habitIn -= deltaTime;
        if (_habitIn > 0f || !IsGrounded || Animation.IsActionPlaying) return;
        _habitIn = Roll(WildlifeConfig.CrowHabitMinSeconds, WildlifeConfig.CrowHabitMaxSeconds);
        StartAttack(Random.Shared.Next(2) == 0 ? PeckAction : CawAction);
    }

    /// <inheritdoc />
    protected override void FlyToward(IMobTarget? target, Vector3? goal, float deltaTime, ChunkManager chunks)
    {
        if (target != null)
        {
            var swooping = Animation.IsPlaying(SwoopClip);
            var aim = swooping ? ChestOf(target.Eye) : FlightSteering.ChaseGoal(target.Eye, HoverHeight);
            Fly(aim, chasing: true, (swooping ? WildlifeConfig.CrowSwoopSpeedFactor : 1f) * MoveFactor, deltaTime, chunks);
            return;
        }

        // Down to a prize it flies low and fast; to a perch or a stroll it ambles
        if (_prize != null)
        {
            Fly(goal, chasing: true, MoveFactor, deltaTime, chunks);
            return;
        }

        // Calm, to a perch, a stroll or nowhere, it never holds a height: it glides down on set wings and flaps
        // itself back up whenever it has sunk below where it is headed. Nor does it stall over a stroll's
        // point: nearing one, it is already turning for the next.
        if (goal is { } point && !HeadsForPerch && NearHorizontally(point, MobConfig.MobFlightArriveBlocks))
            ForgetWanderGoal();
        var speedFactor = goal == null ? MoveFactor : MobConfig.MobWanderSpeedFactor * MoveFactor;
        Fly(goal, chasing: false, speedFactor, deltaTime, chunks,
            (WildlifeConfig.CrowGlideSinkSpeed, WildlifeConfig.CrowGlideGravity));
        TickFlaps(deltaTime);
    }

    private bool NearHorizontally(Vector3 point, float blocks) =>
        Vector2.DistanceSquared(new Vector2(Position.X, Position.Z), new Vector2(point.X, point.Z)) < blocks * blocks;

    // One flap is one play of the flap clip, and its downstroke (BeatEvent) is the one boost it gives. Below its
    // aim it flaps every CrowFlapMin..MaxSeconds, back to back while more than CrowFlapClimbBlocks under it;
    // above it, it only glides. Never over another clip (a take-off, a caw) or while dying.
    private void TickFlaps(float deltaTime)
    {
        if (Animation.ConsumeEvent(BeatEvent)) SetVelocity(Velocity with { Y = WildlifeConfig.CrowFlapBoostSpeed });
        _flapIn -= deltaTime;
        var below = FlightAimHeight - Position.Y;
        if (IsDying || below <= 0f || Animation.IsActionPlaying) return;
        if (_flapIn > 0f && below < WildlifeConfig.CrowFlapClimbBlocks) return;
        _flapIn = Roll(WildlifeConfig.CrowFlapMinSeconds, WildlifeConfig.CrowFlapMaxSeconds);
        StartAttack(FlapActionIndex);
    }

    /// <inheritdoc />
    public override bool TryTakeStrike() => Animation.ConsumeEvent(ImpactEvent);

    /// <inheritdoc />
    public override bool StrikeReaches(Vector3 eye) =>
        IsPerched ? InPeckReach(Position, eye) : InSwoopReach(Position, eye);

    /// <inheritdoc />
    public override int StrikeDamage => WildlifeConfig.CrowStrikeDamage;

    /// <inheritdoc />
    public override float StrikeKnockbackSpeed => WildlifeConfig.CrowKnockbackSpeed;

    /// <inheritdoc />
    public override float StrikeKnockbackLift => WildlifeConfig.CrowKnockbackLift;
}
