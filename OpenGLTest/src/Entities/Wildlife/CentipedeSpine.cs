using System;
using System.Numerics;
using VoxelEngine.Entities.Posing;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The centipede's body as it is drawn: a chain of its joints (<see cref="CentipedeRig"/>) on the ground plane,
/// the head's pinned where the one simulated body puts it and every other trailing the one ahead
/// (<see cref="ChainFollow"/>), so a turn runs down the body segment by segment instead of swinging it whole. It
/// is a pose only: the collision body, the bite and the wire know one position and one facing, and each side (the
/// simulation, a client's mirror) follows its own. A straight chain is exactly the rigid pose. GL-free.
/// </summary>
public sealed class CentipedeSpine(CentipedeRig rig)
{
    private readonly Vector3[] _points = new Vector3[rig.Count];
    private readonly Matrix4x4[] _roots = new Matrix4x4[rig.Count];
    private readonly float _maxBendRadians = WildlifeConfig.CentipedeMaxBendDegrees * MathF.PI / 180f;
    private Matrix4x4 _body = Matrix4x4.Identity;

    /// <summary>Whether <see cref="Follow"/> has placed the chain yet.</summary>
    public bool IsLaid { get; private set; }

    /// <summary>The number of joints.</summary>
    public int Count => _points.Length;

    /// <summary>Where joint <paramref name="index"/> (0 is the head's) stands on the ground plane (y = 0).</summary>
    public Vector3 this[int index] => _points[index];

    /// <summary>
    /// Moves the head's joint to where a body standing at <paramref name="feet"/> and facing
    /// <paramref name="facing"/> (a yaw in radians) holds it, and drags the rest behind. The first call, and one
    /// whose head jumped farther than the body is long (a teleport, a mirror's correction), lays the chain out
    /// straight behind the head instead.
    /// </summary>
    public void Follow(Vector3 feet, float facing)
    {
        var turn = Matrix4x4.CreateRotationY(facing);
        var ground = new Vector3(feet.X, 0f, feet.Z);
        var head = ground + Vector3.Transform(OnGround(rig.Pivots[0]), turn);
        // The model faces -Z, so a straight body leaves the head along its +Z
        var back = Vector3.Transform(Vector3.UnitZ, turn);

        if (!IsLaid || Vector3.DistanceSquared(head, _points[0]) > rig.Length * rig.Length)
        {
            _points[0] = head;
            for (var i = 1; i < _points.Length; i++) _points[i] = _points[i - 1] + back * rig.Links[i];
            IsLaid = true;
        }
        else
        {
            _points[0] = head;
            ChainFollow.Follow(_points, rig.Links, _maxBendRadians, back);
        }

        _body = turn * Matrix4x4.CreateTranslation(feet);
        for (var i = 0; i < _roots.Length; i++)
        {
            // The head looks where the body faces, so its bite aims true; the rest lie along the chain
            var along = i == 0 ? Vector3.Zero : Along(i);
            var yaw = along.LengthSquared() < 1e-8f ? facing : RiggedMob.YawOf(along);
            _roots[i] = Matrix4x4.CreateTranslation(-OnGround(rig.Pivots[i]))
                        * Matrix4x4.CreateRotationY(yaw)
                        * Matrix4x4.CreateTranslation(_points[i].X, feet.Y, _points[i].Z);
        }
    }

    /// <summary>
    /// The root rig part <paramref name="part"/> is posed onto (what <see cref="RigPose.PartMatrix"/> takes): its
    /// joint's, which stands the joint's origin on its chain point and turns it along the chain, or the whole
    /// body's for a part that rides no joint.
    /// </summary>
    public Matrix4x4 RootOf(string part)
    {
        var joint = rig.JointOf(part);
        return joint < 0 ? _body : _roots[joint];
    }

    // The way the chain runs through joint i, back to front: between its neighbors, the tail along its one link
    private Vector3 Along(int i) =>
        i >= _points.Length - 1 ? _points[i - 1] - _points[i] : _points[i - 1] - _points[i + 1];

    private static Vector3 OnGround(Vector3 pivot) => new(pivot.X, 0f, pivot.Z);
}
