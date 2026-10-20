using System;
using System.Numerics;
using VoxelEngine.Juice;
using VoxelEngine.UI;

namespace VoxelEngine.Player;

/// <summary>
/// The camera's per-frame view juice: head bob, run roll, landing dip, hit kick, FOV punch, sprint widening,
/// throw zoom, strafe tilt, crouch drop and death view, plus the damage arc and surfacing droplets it ages.
/// Everything folds into <see cref="Roll"/>, <see cref="BobHeight"/>, <see cref="BobStrafe"/> and
/// <see cref="CurrentFov"/>, which Camera's view cache key reads.
/// </summary>
internal sealed class HeadMotion
{
    private readonly Camera _owner;

    internal HeadMotion(Camera owner) => _owner = owner;

    private const float BobDampRate = 8f;
    private const float RunBlendRate = 6f;
    private const float WalkBobHeight = 0.035f;
    private const float RunBobHeight = 0.06f;
    private const float WalkBobStrafe = 0.02f;
    private const float RunBobStrafe = 0.035f;
    private const float RunRoll = 0.02f;
    private static float DefaultFov => GameSettings.FieldOfView;

    private float _bobPhase;
    private float _bobBlend;
    private float _runBlend;
    private float _bobHeight;
    private float _bobStrafe;
    private float _roll;

    // Camera juice. Every effect folds into the fields the view cache is already keyed on
    // (_bobHeight, _roll, CurrentFov), so none of it needs a cache key of its own: a landing
    // dips the view, a hit kicks its roll toward the blow, a landed punch pinches the FOV, a
    // crouch lowers the eye and a death tips the view over.
    private Spring _landDip = new(3f, 0.45f);
    private Spring _kickRoll = new(4f, 0.35f);
    private Spring _fovPunch = new(5f, 0.4f);
    // Extra degrees of the sprint widening, eased by SprintFov.Step; added to the FOV, never set over it
    private float _sprintFov;
    // Roll toward the strafe side in radians, eased by StrafeTilt.Step; added to _roll, never set over it
    // The throw charge zoom's spring and its clamped degrees (ThrowZoom); added to the FOV, never set over it
    private Spring _throwZoom = ThrowZoom.CreateSpring();
    private float _throwZoomDegrees;
    // Where the last billed blow came from, for the damage direction arc (aged in UpdateHeadMotion)
    private readonly DamageDirection _damageArc = new();
    // The surfacing droplets: aged in UpdateHeadMotion, told the eye's fluid state in RefreshEyeWaterState
    private readonly SurfacingDroplets _droplets = new();
    private float _sneakBlend;
    private float _prevVerticalVelocity;
    private bool _wasGrounded;
    private float _deathProgress;
    private const float SneakEyeDrop = 0.22f;
    private const float LandDipPerSpeed = 0.55f;
    private const float LandDipScale = 0.8f;
    private const float DeathRollRadians = 1.2f;
    private const float DeathDropBlocks = 1.1f;

    /// <summary>Current vertical FOV in degrees: the base FOV plus the eased sprint widening plus the punch
    /// pinch plus the throw charge zoom.</summary>
    internal float CurrentFov { get; set; } = DefaultFov;
    internal float Roll { get => _roll; set => _roll = value; }
    internal float BobHeight { get => _bobHeight; set => _bobHeight = value; }
    internal float BobStrafe { get => _bobStrafe; set => _bobStrafe = value; }
    internal DamageDirection DamageArc => _damageArc;
    internal SurfacingDroplets Droplets => _droplets;
    internal float LandingDip => _landDip.Value;
    internal float KickRoll => _kickRoll.Value;
    internal float StrafeTiltRoll { get; private set; }
    internal float DeathProgress { set => _deathProgress = MathUtils.Clamp01(value); }

    internal void AddHitKick(Vector3 awayHorizontal, float strength)
    {
        var forward = _owner.GetForwardVector();
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var side = awayHorizontal.LengthSquared() > 1e-6f
            ? Vector3.Dot(Vector3.Normalize(new Vector3(awayHorizontal.X, 0f, awayHorizontal.Z)), right)
            : 0f;
        // Pushed to the right (blow from the left) rolls the view clockwise.
        _kickRoll.Impulse((0.9f * side + 0.25f) * strength);
        _landDip.Impulse(5f * strength);
    }

    internal void PunchFov(float degrees) => _fovPunch.Impulse(degrees * 14f);

    /// <summary>
    /// The per-frame view juice: FOV (sprint widening, punch pinch and throw zoom), head bob, landing dip,
    /// crouch drop and roll. <paramref name="sprintHeld"/> is the sprint key, on foot or flying;
    /// <see cref="SprintFov"/> decides whether that actually widens the view.
    /// <paramref name="strafe"/> is the sideways movement input along the camera's right (-1..1),
    /// which <see cref="StrafeTilt"/> turns into a lean.
    /// </summary>
    internal void UpdateHeadMotion(float deltaTime, float intendedSpeed, bool moving, bool swimming,
        bool sprintHeld, float strafe)
    {
        _sprintFov = SprintFov.Step(_sprintFov,
            SprintFov.TargetDegrees(sprintHeld, moving, _owner.IsSneaking, swimming, intendedSpeed), deltaTime);
        if (!_owner.HoldPunchFov)
            _fovPunch.Step(0f, deltaTime);
        _throwZoomDegrees = ThrowZoom.Step(ref _throwZoom, ThrowZoom.TargetDegrees(_owner.ThrowCharge), deltaTime);
        _damageArc.Update(deltaTime);
        _droplets.Update(deltaTime);
        CurrentFov = DefaultFov + _sprintFov + MathUtils.Clamp(_fovPunch.Value, -6f, 6f) + _throwZoomDegrees;

        // A landing dips the view in proportion to the fall that ended just now.
        if (!_wasGrounded && _owner.IsGrounded && !_owner.IsFlying && _prevVerticalVelocity < -4f)
            _landDip.Impulse(MathF.Min(-_prevVerticalVelocity, 25f) * LandDipPerSpeed);
        _wasGrounded = _owner.IsGrounded;
        _prevVerticalVelocity = _owner.VerticalVelocity;
        _landDip.Step(0f, deltaTime);
        _kickRoll.Step(0f, deltaTime);
        _sneakBlend = MathUtils.Damp(_sneakBlend, _owner.IsSneaking ? 1f : 0f, 12f, deltaTime);

        _runBlend = MathUtils.Damp(_runBlend, _owner.IsSprinting ? 1f : 0f, RunBlendRate, deltaTime);
        var bobTarget = moving && !_owner.IsFlying && (_owner.IsGrounded || swimming) ? 1f : 0f;
        _bobBlend = MathUtils.Damp(_bobBlend, bobTarget, BobDampRate, deltaTime);
        if (moving && !_owner.IsFlying)
            _bobPhase += deltaTime * (4f + intendedSpeed * 0.9f + _runBlend * 2.5f);

        var height = MathUtils.Lerp(WalkBobHeight, RunBobHeight, _runBlend);
        var bobStrafe = MathUtils.Lerp(WalkBobStrafe, RunBobStrafe, _runBlend);
        var death = MathUtils.EaseInCubic(_deathProgress);
        _bobHeight = MathF.Sin(_bobPhase * 2f) * height * _bobBlend
                     - MathUtils.Clamp(_landDip.Value, -4f, 12f) * LandDipScale
                     - SneakEyeDrop * _sneakBlend - DeathDropBlocks * death;
        _bobStrafe = MathF.Cos(_bobPhase) * bobStrafe * _bobBlend;
        StrafeTiltRoll = StrafeTilt.Step(StrafeTiltRoll,
            StrafeTilt.TargetRadians(strafe, _owner.IsFlying, dead: _deathProgress > 0f), deltaTime);
        _roll = MathF.Sin(_bobPhase) * RunRoll * _runBlend * _bobBlend
                + MathUtils.Clamp(_kickRoll.Value, -0.12f, 0.12f) + DeathRollRadians * death + StrafeTiltRoll;
    }
}
