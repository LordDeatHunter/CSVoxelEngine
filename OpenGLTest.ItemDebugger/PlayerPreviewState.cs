using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// GL-free state behind the item debugger's player animations (the ITEMS tab): which animation the
/// <see cref="PlayerModel"/> is showing and the synthetic inputs that drive it, so every pose the game
/// can put the player in can be looked at without a world. The modes are data (<see cref="Modes"/>):
/// <b>continuous</b> poses (idle, walk, run, sneak, a jump loop, swimming) feed the model the speed and
/// state the game would, and <b>one-shots</b> (punch, slice, throw, hurt, equip, landing, death) fire
/// the model's own trigger, wait for it to finish, rest <see cref="RestBetweenPlays"/> and fire again,
/// so a swing can be watched repeatedly. Nothing here is a second implementation of an animation: it only
/// calls <see cref="PlayerModel.Update"/> and the triggers the world calls, with the same inputs.
/// </summary>
public sealed class PlayerPreviewState : IPreviewDriver
{
    public const string Idle = "idle";
    public const string Walk = "walk";
    public const string Run = "run";
    public const string Sneak = "sneak";
    public const string Jump = "jump";
    public const string SprintJump = "sprint jump";
    public const string Land = "land";
    public const string Swim = "swim";
    public const string SwimIdle = "swim idle";
    public const string Surface = "surface";
    public const string Punch = "punch";
    public const string HeavyPunch = "heavy punch";
    public const string Slice = "slice";
    public const string SliceBack = "slice back";
    public const string Throw = "throw";
    public const string Hurt = "hurt";
    public const string Equip = "equip";
    public const string Death = "death";

    /// <summary>Every mode, in the order the debugger lists them.</summary>
    public static readonly IReadOnlyList<string> AllModes =
    [
        Idle, Walk, Run, Sneak, Jump, SprintJump, Land, Swim, SwimIdle, Surface,
        Punch, HeavyPunch, Slice, SliceBack, Throw, Hurt, Equip, Death,
    ];

    /// <summary>Seconds a one-shot rests between the end of one play and the next.</summary>
    public const float RestBetweenPlays = 0.6f;

    /// <summary>The short upper-case text a mode's button wears (the longer names do not fit a quarter-width button).</summary>
    public static string LabelOf(string mode) => mode switch
    {
        SprintJump => "S-JUMP",
        SwimIdle => "FLOAT",
        HeavyPunch => "HEAVY",
        SliceBack => "BACK",
        _ => mode.ToUpperInvariant(),
    };

    // The game's own numbers: the walk speed the walk cycle is tuned against, a sprint, a jump's
    // launch speed and the gravity that brings it down (PlayerPhysics), the fall a landing is shown from.
    private const float WalkSpeed = 8f;
    private const float RunSpeed = 12f;
    private const float SneakSpeed = 3.2f;
    private const float JumpSpeed = 11f;
    private const float Gravity = 34f;
    private const float LandingFallSpeed = 15f;
    private const float SwimSpeedAhead = 4f;
    // How long the throw charge takes to build, how long it is held cocked, how long death holds the body down.
    private const float ThrowChargeSeconds = 0.8f;
    private const float ThrowHoldSeconds = 0.35f;
    private const float DeathHoldSeconds = 0.7f;
    private const float DeathFallSeconds = 0.9f;

    private PlayerModel _model;
    private float _rest;
    private float _clock;
    private float _airTime;
    private bool _jumping;
    private float _throwTimer;
    private bool _throwCharging;
    private float _deathTimer;
    private bool _playing;

    public PlayerPreviewState(PlayerModel model)
    {
        _model = model;
        Mode = Idle;
    }

    /// <summary>The model being posed. It is replaced by a fresh one on <see cref="Restart"/> (so a restart reproduces
    /// the same frames), so a renderer must read it every frame rather than keep it.</summary>
    public PlayerModel Model => _model;

    /// <summary>Whether a one-shot (and the jump loops) replay by themselves after a rest. Off, each plays once and
    /// holds; the <see cref="AnimationClock"/> turns it off and owns the replay.</summary>
    public bool Replay { get; set; } = true;

    /// <summary>True for the poses with no start or end (idle, walk, run, sneak, swim): a clock wraps them.</summary>
    public bool IsContinuous => Mode is Idle or Walk or Run or Sneak or Swim or SwimIdle or Surface;

    /// <summary>Seconds one play of the active mode lasts (a cycle for a continuous pose).</summary>
    public float Duration => Mode switch
    {
        Walk or Run or Sneak or Idle or SwimIdle => 2f,
        // About two slow sculls of the surface float
        Surface => 3f,
        Swim => PlayerPhysics.SwimStrokeSeconds * 2f,
        Jump or SprintJump => 2f * JumpSpeed / Gravity + 0.9f,
        Land => 0.25f + 0.9f,
        Punch => PlayerModel.MineDuration + 0.1f,
        HeavyPunch => PlayerModel.MineDuration / 0.7f + 0.1f,
        Slice or SliceBack => PlayerModel.SliceDuration + 0.1f,
        Throw => ThrowChargeSeconds + ThrowHoldSeconds + PlayerModel.ThrowDuration + 0.1f,
        Hurt => 1f,
        Equip => 1.2f,
        Death => DeathFallSeconds + DeathHoldSeconds,
        _ => 2f,
    };

    /// <summary>Goes back to the exact start of the active mode: a fresh <see cref="PlayerModel"/> on the same rig.</summary>
    public void Restart()
    {
        _model = new PlayerModel { Rig = _model.Rig, BodyYawLags = _model.BodyYawLags, AutoSwimStroke = _model.AutoSwimStroke };
        Reset();
    }

    // How long a play waits before firing again: a short rest, or never when replay is off.
    private float RestFor(float seconds) => Replay ? seconds : float.MaxValue / 4f;

    /// <summary>The active mode, always one of <see cref="AllModes"/>.</summary>
    public string Mode { get; private set; }

    /// <summary>Seconds fed to <see cref="Update"/> since the last <see cref="SetMode"/>.</summary>
    public float Elapsed => _clock;

    /// <summary>True for the modes that replay a swing or reaction (everything after the swim poses).</summary>
    public bool IsOneShot => Mode is Punch or HeavyPunch or Slice or SliceBack or Throw or Hurt or Equip or Death or Land;

    /// <summary>Switches to <paramref name="mode"/> (any case); false when there is no such mode.</summary>
    public bool SetMode(string mode)
    {
        foreach (var candidate in AllModes)
        {
            if (!string.Equals(candidate, mode, StringComparison.OrdinalIgnoreCase)) continue;
            Mode = candidate;
            Reset();
            return true;
        }
        return false;
    }

    private void Reset()
    {
        _rest = 0f;
        _clock = 0f;
        _airTime = 0f;
        _jumping = false;
        _throwTimer = 0f;
        _throwCharging = false;
        _deathTimer = 0f;
        _playing = false;
        // A leftover charge, fall or death from the previous mode must not carry over.
        _model.CancelThrowCharge();
        _model.DeathFall = 0f;
    }

    /// <summary>Advances the model by <paramref name="deltaTime"/> seconds in the active mode.</summary>
    public void Update(float deltaTime)
    {
        if (deltaTime <= 0f) return;
        _clock += deltaTime;
        _model.HoldingItem = true;

        switch (Mode)
        {
            case Walk:
                _model.Update(deltaTime, WalkSpeed);
                break;
            case Run:
                _model.Update(deltaTime, RunSpeed, sprinting: true);
                break;
            case Sneak:
                _model.Update(deltaTime, SneakSpeed, sneaking: true);
                break;
            case Jump:
                Jumping(deltaTime, WalkSpeed, sprinting: false);
                break;
            case SprintJump:
                Jumping(deltaTime, RunSpeed, sprinting: true);
                break;
            case Swim:
                Swimming(deltaTime, SwimSpeedAhead);
                break;
            case SwimIdle:
                Swimming(deltaTime, 0f);
                break;
            case Surface:
                // At rest at the top of deep water (WaterPhase.Float), exactly as the world feeds it
                _model.Update(deltaTime, 0f, grounded: true, swimming: true, floating: true);
                break;
            case Land:
                Landing(deltaTime);
                break;
            case Punch:
            case HeavyPunch:
            case Slice:
            case SliceBack:
            case Hurt:
            case Equip:
                OneShot(deltaTime);
                break;
            case Throw:
                Throwing(deltaTime);
                break;
            case Death:
                Dying(deltaTime);
                break;
            default:
                _model.Update(deltaTime, 0f);
                break;
        }
    }

    // A jump loop: stand for a beat, launch, and let the model's own landing detection do the rest.
    private void Jumping(float dt, float speed, bool sprinting)
    {
        if (!_jumping)
        {
            _rest -= dt;
            _model.Update(dt, speed, sprinting);
            if (_rest <= 0f)
            {
                _jumping = true;
                _airTime = 0f;
            }
            return;
        }

        _airTime += dt;
        var vy = JumpSpeed - Gravity * _airTime;
        if (_airTime > 2f * JumpSpeed / Gravity)
        {
            // Touched down: one frame supported lets the model see the landing.
            _jumping = false;
            _rest = RestFor(RestBetweenPlays);
            _model.Update(dt, speed, sprinting, grounded: true, verticalVelocity: 0f);
            return;
        }
        _model.Update(dt, speed, sprinting, grounded: false, verticalVelocity: vy);
    }

    // The landing alone: a fall at the speed that hurts a little, then the ground.
    private void Landing(float dt)
    {
        if (!_jumping)
        {
            _rest -= dt;
            _model.Update(dt, 0f);
            if (_rest <= 0f)
            {
                _jumping = true;
                _airTime = 0f;
            }
            return;
        }

        _airTime += dt;
        if (_airTime < 0.25f)
        {
            _model.Update(dt, 0f, grounded: false, verticalVelocity: -LandingFallSpeed);
            return;
        }
        _jumping = false;
        _rest = RestFor(RestBetweenPlays + 0.6f);
        _model.Update(dt, 0f, grounded: true);
    }

    // Swimming: a stroke starts as soon as the last one is over, exactly as holding forward would in
    // the game (idle: the arms rest in front and only the floating bob shows).
    private void Swimming(float dt, float speed)
    {
        if (speed > 0f && _model.SwimStrokeProgress >= 1f)
            _model.TriggerSwimStroke();
        _model.Update(dt, speed, grounded: true, swimming: true, swimForward: speed);
    }

    // Fires the mode's trigger, lets it play out and rests before firing again.
    private void OneShot(float dt)
    {
        if (_playing && !Busy())
        {
            _playing = false;
            _rest = RestFor(RestBetweenPlays);
        }

        if (!_playing)
        {
            _rest -= dt;
            if (_rest <= 0f)
            {
                Fire();
                _playing = true;
            }
        }

        _model.Update(dt, 0f);
    }

    private bool Busy() => Mode switch
    {
        Punch or HeavyPunch => _model.MineT < 1f,
        Slice or SliceBack => _model.IsSlicing,
        Hurt => MathF.Abs(_model.Flinch) > 0.02f,
        Equip => MathF.Abs(_model.EquipLowering) > 0.02f,
        _ => false,
    };

    private void Fire()
    {
        switch (Mode)
        {
            case Punch: _model.Punch(); break;
            case HeavyPunch: _model.Punch(1f); break;
            case Slice: _model.StartSlice(1); break;
            case SliceBack: _model.StartSlice(-1); break;
            case Hurt: _model.Hurt(1f); break;
            case Equip: _model.NotifyEquip(); break;
        }
    }

    // A throw: charge up, hold the cocked arm, release.
    private void Throwing(float dt)
    {
        _model.Update(dt, 0f);
        if (_model.IsThrowing) return;

        if (!_throwCharging)
        {
            _rest -= dt;
            if (_rest > 0f) return;
            _model.StartThrowCharge();
            _throwCharging = true;
            _throwTimer = 0f;
            return;
        }

        _throwTimer += dt;
        _model.UpdateThrowCharge(MathUtils.Clamp01(_throwTimer / ThrowChargeSeconds));
        if (_throwTimer >= ThrowChargeSeconds + ThrowHoldSeconds)
        {
            _model.ReleaseThrow(1f);
            _throwCharging = false;
            _rest = RestFor(RestBetweenPlays);
        }
    }

    // A death: the body falls over, stays down, then stands back up and starts again.
    private void Dying(float dt)
    {
        _model.Update(dt, 0f);
        _deathTimer += dt;
        var total = DeathFallSeconds + DeathHoldSeconds;
        // Replaying: stand back up after the hold and start over; otherwise stay down.
        if (Replay && _deathTimer >= total + RestBetweenPlays) _deathTimer = 0f;
        _model.DeathFall = Replay && _deathTimer >= total ? 0f : MathUtils.Clamp01(_deathTimer / DeathFallSeconds);
    }
}
