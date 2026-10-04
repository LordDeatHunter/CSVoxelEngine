using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>One player position from a server <c>PlayerList</c> broadcast.</summary>
public readonly record struct RemotePlayerSnapshot(int Id, string Name, Vector3 Eye, float Yaw, float Pitch);

/// <summary>
/// A player connected to the same server, as the client sees them: a target pose from the last
/// broadcast (smoothed toward between broadcasts) and a <see cref="PlayerModel"/> whose walk cycle
/// runs off the distance actually travelled. Rendering reads <see cref="Eye"/> and <see cref="Yaw"/>.
/// GL-free.
/// </summary>
public sealed class RemotePlayer
{
    private Vector3 _target;
    private float _targetYaw;
    private bool _hasTarget;
    private Vector3 _prev;
    private bool _hasPrev;

    public RemotePlayer(int id, string name, PlayerModel.PlayerRig rig)
    {
        Id = id;
        Name = name;
        Model = new PlayerModel { Rig = rig };
    }

    public int Id { get; }

    public string Name { get; set; }

    public Vector3 Eye { get; private set; }

    public float Yaw { get; private set; }

    public PlayerModel Model { get; }

    /// <summary>Adopts the latest broadcast pose; the first one snaps, later ones smooth.</summary>
    public void SetTarget(Vector3 eye, float yaw, float pitch)
    {
        if (!_hasTarget)
        {
            Eye = eye;
            Yaw = yaw;
            _prev = eye;
            _hasPrev = true;
            _hasTarget = true;
        }

        _target = eye;
        _targetYaw = yaw;
    }

    /// <summary>Eases the eye toward the target and drives the walk cycle from the distance moved.</summary>
    public void Update(double deltaTime)
    {
        if (deltaTime <= 0.0)
            return;

        var blend = MathF.Min(1f, (float)deltaTime * 15f);
        Eye = Vector3.Lerp(Eye, _target, blend);
        Yaw = _targetYaw;

        var speed = _hasPrev
            ? new Vector3(Eye.X - _prev.X, 0f, Eye.Z - _prev.Z).Length() / (float)deltaTime
            : 0f;
        _prev = Eye;
        _hasPrev = true;

        Model.Update((float)deltaTime, speed, sprinting: false, grounded: true, verticalVelocity: 0f);
    }
}
