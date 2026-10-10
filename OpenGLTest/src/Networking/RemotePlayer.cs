using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>One player position from a server <c>PlayerList</c> broadcast, with the cosmetic state
/// that poses their model: the movement <paramref name="Flags"/> and the last swing
/// (<paramref name="Action"/>, counted by <paramref name="ActionSeq"/> so a repeat is noticed).</summary>
public readonly record struct RemotePlayerSnapshot(int Id, string Name, Vector3 Eye, float Yaw, float Pitch,
    NetPlayerFlags Flags = NetPlayerFlags.None, NetPlayerAction Action = NetPlayerAction.None, byte ActionSeq = 0);

/// <summary>
/// A player connected to the same server, as the client sees them: a target pose from the last
/// broadcast (smoothed toward between broadcasts) and a <see cref="PlayerModel"/> posed from what
/// the broadcast says they are doing: the walk cycle runs off the distance actually travelled, and
/// the sprint, jump, crouch and swim poses and their swings (punch, slice, throw) come from the
/// flags and the action counter. GL-free.
/// </summary>
public sealed class RemotePlayer
{
    // The look turns smoothly between the 10 Hz broadcasts.
    private const float LookRate = 18f;

    private Vector3 _target;
    private float _targetYaw;
    private float _targetPitch;
    private NetPlayerFlags _flags;
    private bool _hasTarget;
    private Vector3 _prev;
    private bool _hasPrev;
    private bool _hasAction;
    private byte _lastActionSeq;
    private float _prevY;

    public RemotePlayer(int id, string name, PlayerRig rig)
    {
        Id = id;
        Name = name;
        // The body trails the look, so a turning player's head leads and the body follows.
        Model = new PlayerModel { Rig = rig, BodyYawLags = true, AutoSwimStroke = true };
    }

    public int Id { get; }

    public string Name { get; set; }

    public Vector3 Eye { get; private set; }

    public float Yaw { get; private set; }

    /// <summary>The look pitch in degrees, smoothed.</summary>
    public float Pitch { get; private set; }

    public PlayerModel Model { get; }

    /// <summary>Adopts the latest broadcast pose; the first one snaps, later ones smooth.</summary>
    public void SetTarget(Vector3 eye, float yaw, float pitch,
        NetPlayerFlags flags = NetPlayerFlags.None, NetPlayerAction action = NetPlayerAction.None, byte actionSeq = 0)
    {
        if (!_hasTarget)
        {
            Eye = eye;
            Yaw = yaw;
            Pitch = pitch;
            _prev = eye;
            _prevY = eye.Y;
            _hasPrev = true;
            _hasTarget = true;
        }

        _target = eye;
        _targetYaw = yaw;
        _targetPitch = pitch;
        _flags = flags;

        // The counter moves on every swing; the first sighting only records it, so a player already
        // mid-session does not swing the moment they come into view.
        if (_hasAction && actionSeq != _lastActionSeq)
            PlayAction(action);
        _lastActionSeq = actionSeq;
        _hasAction = true;
    }

    private void PlayAction(NetPlayerAction action)
    {
        switch (action)
        {
            case NetPlayerAction.Punch: Model.Punch(); break;
            case NetPlayerAction.Slice: Model.StartSlice(1); break;
            case NetPlayerAction.SliceReturn: Model.StartSlice(-1); break;
            case NetPlayerAction.Throw: Model.ReleaseThrow(1f); break;
        }
    }

    /// <summary>Eases the pose toward the target and drives the model from the distance moved and the flags.</summary>
    public void Update(double deltaTime)
    {
        if (deltaTime <= 0.0)
            return;

        var dt = (float)deltaTime;
        var blend = MathF.Min(1f, dt * 15f);
        Eye = Vector3.Lerp(Eye, _target, blend);
        Yaw = MathUtils.DampAngle(Yaw, _targetYaw, LookRate, dt);
        Pitch = MathUtils.Damp(Pitch, _targetPitch, LookRate, dt);

        var flying = _flags.HasFlag(NetPlayerFlags.Flying);
        var speed = _hasPrev && !flying
            ? new Vector3(Eye.X - _prev.X, 0f, Eye.Z - _prev.Z).Length() / dt
            : 0f;
        var vy = _hasPrev ? (Eye.Y - _prevY) / dt : 0f;
        // The heading along and across the look, which tips a swimmer toward where it is going.
        var step = _hasPrev ? Eye - _prev : Vector3.Zero;
        var yawRad = MathUtils.ToRadians(Yaw);
        var fx = MathF.Cos(yawRad);
        var fz = MathF.Sin(yawRad);
        var headingForward = (step.X * fx + step.Z * fz) / dt;
        var headingSide = (-step.X * fz + step.Z * fx) / dt;
        _prev = Eye;
        _prevY = Eye.Y;
        _hasPrev = true;

        var airborne = _flags.HasFlag(NetPlayerFlags.Airborne);
        Model.SetLook(Yaw, Pitch);
        Model.Update(dt, speed,
            sprinting: _flags.HasFlag(NetPlayerFlags.Sprinting),
            grounded: !airborne,
            verticalVelocity: airborne ? vy : 0f,
            sneaking: _flags.HasFlag(NetPlayerFlags.Sneaking),
            swimming: _flags.HasFlag(NetPlayerFlags.Swimming),
            swimForward: headingForward, swimSide: headingSide);
    }
}
