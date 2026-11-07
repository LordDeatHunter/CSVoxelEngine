using System;
using System.Numerics;
using VoxelEngine.Config;

namespace VoxelEngine.Entities.Sandworms;

/// <summary>What the worm is doing; the brain moves between these.</summary>
public enum SandwormPhase
{
    /// <summary>Summoned: rumbling in place, underground, before it starts to hunt.</summary>
    Thump,

    /// <summary>Burrowed and hunting: tunnels toward the target and locks a point to surface at.</summary>
    Dig,

    /// <summary>Erupting from the sand, head first, upward.</summary>
    Breach,

    /// <summary>Airborne, steering toward the target; at the apex it picks a dive or a slide.</summary>
    Air,

    /// <summary>Sliding along the surface, half in the sand, charging the target with a slow turn.</summary>
    Dash,

    /// <summary>Sinking away for good; <see cref="SandwormBrain.IsFinished"/> once it is under.</summary>
    Retreat,
}

/// <summary>What the head's contact does to a target it overlaps.</summary>
public enum SandwormContact
{
    None,

    /// <summary>The breaching and airborne head: heavy damage and an upward launch.</summary>
    Chomp,

    /// <summary>The surface dash: lighter, from any exposed segment.</summary>
    Slide,
}

/// <summary>
/// What the brain reads each frame: where the <see cref="Head"/> is, the nearest non-immune
/// <see cref="Target"/>'s position (null for none), the sand surface height under the head
/// (<see cref="GroundY"/>) and whether that surface is sand or sandstone (<see cref="OnSand"/>; a dash ends
/// when it runs out).
/// </summary>
public readonly record struct SandwormSenses(Vector3 Head, Vector3? Target, float GroundY, bool OnSand);

/// <summary>
/// The sandworm's behavior as a pure state machine (plans/SANDWORM.md): thump, dig toward the target under
/// the sand, breach under it, fly, then either dive and dig again or slide and dash, and retreat when nobody
/// is left. It owns only the head's motion (<see cref="Velocity"/> and <see cref="Heading"/>) and the signals
/// the world reads (telegraph, shake, contact); the manager moves the head by the velocity and the chain
/// follows it. GL-free and deterministic given its roll source.
/// </summary>
public sealed class SandwormBrain
{
    private readonly Func<float> _roll;
    private bool _planned;
    private bool _slidePlanned;
    private bool _lastWasSlide;
    private float _idleSeconds;

    /// <param name="heading">Initial yaw in radians (see <see cref="Heading"/>).</param>
    /// <param name="roll">A uniform 0..1 source for the dive-or-slide pick.</param>
    public SandwormBrain(float heading, Func<float> roll)
    {
        Heading = heading;
        _roll = roll;
    }

    /// <summary>The current phase.</summary>
    public SandwormPhase Phase { get; private set; } = SandwormPhase.Thump;

    /// <summary>Seconds spent in the current phase.</summary>
    public float PhaseSeconds { get; private set; }

    /// <summary>The head's velocity this frame; the manager adds it to the head position.</summary>
    public Vector3 Velocity { get; private set; }

    /// <summary>The head's yaw in radians: it faces and travels along (sin, cos) = (X, Z).</summary>
    public float Heading { get; private set; }

    /// <summary>The point the worm has locked to surface at (valid while <see cref="HasLockedPoint"/>).</summary>
    public Vector3 LockedPoint { get; private set; }

    /// <summary>True from the moment the dig locks its surfacing point until the next breach or dig restart.</summary>
    public bool HasLockedPoint { get; private set; }

    /// <summary>How close the worm is to surfacing, 0..1, for the sand telegraph: 0 far or not digging, 1 about to breach.</summary>
    public float Telegraph { get; private set; }

    /// <summary>The earthquake strength, 0..1, while digging; 0 in every other phase.</summary>
    public float Shake { get; private set; }

    /// <summary>Where the worm is heading to surface: the locked point once locked, else the target spot on the
    /// sand; the sand puffs gather there. Meaningful while digging.</summary>
    public Vector3 SurfacePoint { get; private set; }

    /// <summary>True once the worm has sunk away after retreating and should be removed.</summary>
    public bool IsFinished { get; private set; }

    /// <summary>The plan made at the apex (true a slide, false a dive); meaningful once <see cref="HasPlan"/>.</summary>
    public bool PlansSlide => _slidePlanned;

    /// <summary>True in the air once the apex pick has been made.</summary>
    public bool HasPlan => _planned;

    /// <summary>What the head's contact does now; the manager still checks the overlap and a cooldown.</summary>
    public SandwormContact Contact => Phase switch
    {
        SandwormPhase.Breach or SandwormPhase.Air => SandwormContact.Chomp,
        SandwormPhase.Dash => SandwormContact.Slide,
        _ => SandwormContact.None,
    };

    /// <summary>The chance of picking a slide at the apex, given whether the previous pick was one: the repeat is made less likely.</summary>
    public static float SlideChanceAfter(bool lastWasSlide) =>
        lastWasSlide
            ? SandwormConfig.SlideChance * SandwormConfig.RepeatPickFactor
            : 1f - (1f - SandwormConfig.SlideChance) * SandwormConfig.RepeatPickFactor;

    /// <summary>Advances the brain by <paramref name="dt"/> seconds.</summary>
    public void Update(float dt, in SandwormSenses senses)
    {
        PhaseSeconds += dt;
        Telegraph = 0f;
        Shake = 0f;
        switch (Phase)
        {
            case SandwormPhase.Thump: UpdateThump(); break;
            case SandwormPhase.Dig: UpdateDig(dt, senses); break;
            case SandwormPhase.Breach: UpdateBreach(dt, senses); break;
            case SandwormPhase.Air: UpdateAir(dt, senses); break;
            case SandwormPhase.Dash: UpdateDash(dt, senses); break;
            case SandwormPhase.Retreat: UpdateRetreat(senses); break;
        }
    }

    /// <summary>Skips the summon's thump and starts hunting at once (a worm restored from a save).</summary>
    public void SkipThump()
    {
        if (Phase == SandwormPhase.Thump) EnterDig();
    }

    /// <summary>Gives the hunt up: the worm sinks away and finishes (no target left to hunt).</summary>
    public void BeginRetreat()
    {
        if (Phase == SandwormPhase.Retreat) return;
        HasLockedPoint = false;
        Enter(SandwormPhase.Retreat);
    }

    private void Enter(SandwormPhase phase)
    {
        Phase = phase;
        PhaseSeconds = 0f;
    }

    private void EnterDig()
    {
        HasLockedPoint = false;
        _idleSeconds = 0f;
        Enter(SandwormPhase.Dig);
    }

    private void UpdateThump()
    {
        Velocity = Vector3.Zero;
        if (PhaseSeconds >= SandwormConfig.ThumpSeconds) EnterDig();
    }

    private void UpdateDig(float dt, in SandwormSenses senses)
    {
        var vertical = EaseVertical(senses.Head.Y, senses.GroundY - SandwormConfig.BurrowDepthBlocks, dt);
        if (senses.Target is not { } target)
        {
            _idleSeconds += dt;
            Velocity = new Vector3(0f, vertical, 0f);
            if (_idleSeconds >= SandwormConfig.RetreatAfterSeconds) Enter(SandwormPhase.Retreat);
            return;
        }

        _idleSeconds = 0f;
        var targetDistance = Horizontal(target - senses.Head).Length();
        if (!HasLockedPoint && targetDistance <= SandwormConfig.LockDistanceBlocks)
        {
            HasLockedPoint = true;
            LockedPoint = new Vector3(target.X, senses.GroundY, target.Z);
        }

        var goal = HasLockedPoint ? LockedPoint : target;
        var toGoal = Horizontal(goal - senses.Head);
        var goalDistance = toGoal.Length();
        SurfacePoint = new Vector3(goal.X, senses.GroundY, goal.Z);
        Telegraph = 1f - MathUtils.Clamp01(goalDistance / SandwormConfig.TelegraphRangeBlocks);
        Shake = 1f - MathUtils.Clamp01(targetDistance / SandwormConfig.ShakeRangeBlocks);

        if (goalDistance <= SandwormConfig.BreachTriggerBlocks)
        {
            Velocity = new Vector3(0f, SandwormConfig.BreachUpSpeed, 0f);
            HasLockedPoint = false;
            Telegraph = 0f;
            Shake = 0f;
            Enter(SandwormPhase.Breach);
            return;
        }

        var direction = toGoal / goalDistance;
        Heading = MathF.Atan2(direction.X, direction.Z);
        var speed = MathF.Min(SandwormConfig.DigSpeed, goalDistance / MathF.Max(dt, 1e-4f));
        Velocity = new Vector3(direction.X * speed, vertical, direction.Z * speed);
    }

    private void UpdateBreach(float dt, in SandwormSenses senses)
    {
        Velocity = new Vector3(Velocity.X, Velocity.Y - BodyMotion.Gravity * dt, Velocity.Z);
        if (senses.Head.Y < senses.GroundY + SandwormConfig.BreachClearBlocks) return;
        _planned = false;
        Enter(SandwormPhase.Air);
    }

    private void UpdateAir(float dt, in SandwormSenses senses)
    {
        var horizontal = Horizontal(Velocity);
        var desired = Vector3.Zero;
        var diving = _planned && !_slidePlanned;
        if (!diving && senses.Target is { } target)
        {
            var toTarget = Horizontal(target - senses.Head);
            if (toTarget.LengthSquared() > 1e-6f)
            {
                desired = Vector3.Normalize(toTarget) * SandwormConfig.AirSpeed;
                Heading = MathF.Atan2(toTarget.X, toTarget.Z);
            }
        }

        horizontal += (desired - horizontal) * (1f - MathF.Exp(-SandwormConfig.AirSteerRate * dt));
        var vy = Velocity.Y - BodyMotion.Gravity * dt;
        if (!_planned && vy <= 0f) Plan();
        if (_planned && !_slidePlanned) vy -= SandwormConfig.DiveAcceleration * dt;
        Velocity = new Vector3(horizontal.X, vy, horizontal.Z);

        if (!_planned) return;
        if (_slidePlanned)
        {
            if (senses.Head.Y <= senses.GroundY + SandwormConfig.SlideHeightBlocks) Enter(SandwormPhase.Dash);
            return;
        }

        if (senses.Head.Y <= senses.GroundY - SandwormConfig.BurrowDepthBlocks * 0.5f) EnterDig();
    }

    private void Plan()
    {
        _slidePlanned = _roll() < SlideChanceAfter(_lastWasSlide);
        _lastWasSlide = _slidePlanned;
        _planned = true;
    }

    private void UpdateDash(float dt, in SandwormSenses senses)
    {
        if (senses.Target is { } target)
        {
            var toTarget = Horizontal(target - senses.Head);
            if (toTarget.LengthSquared() > 1e-6f)
            {
                var want = MathF.Atan2(toTarget.X, toTarget.Z);
                Heading = TurnToward(Heading, want, SandwormConfig.DashTurnDegreesPerSecond * MathF.PI / 180f * dt);
            }
        }

        var vertical = EaseVertical(senses.Head.Y, senses.GroundY + SandwormConfig.SlideHeightBlocks, dt);
        Velocity = new Vector3(MathF.Sin(Heading) * SandwormConfig.DashSpeed, vertical, MathF.Cos(Heading) * SandwormConfig.DashSpeed);
        if (PhaseSeconds >= SandwormConfig.DashMaxSeconds || !senses.OnSand) EnterDig();
    }

    private void UpdateRetreat(in SandwormSenses senses)
    {
        Velocity = new Vector3(0f, -SandwormConfig.DigSpeed, 0f);
        if (senses.Head.Y <= senses.GroundY - SandwormConfig.BurrowDepthBlocks - 2f) IsFinished = true;
    }

    // The vertical velocity that eases the head's height from currentY toward targetY.
    private static float EaseVertical(float currentY, float targetY, float dt) =>
        dt <= 0f ? 0f : (MathUtils.Damp(currentY, targetY, SandwormConfig.VerticalEaseRate, dt) - currentY) / dt;

    private static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

    private static float TurnToward(float current, float target, float maxStep)
    {
        var delta = target - current;
        delta -= MathF.Round(delta / (2f * MathF.PI)) * 2f * MathF.PI;
        return current + Math.Clamp(delta, -maxStep, maxStep);
    }
}
