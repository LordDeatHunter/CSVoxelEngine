using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Entities.Crawlers;
using VoxelEngine.Entities.Posing;

namespace VoxelEngine.Entities.Wildlife;

/// <summary>
/// The centipede's body behind its head: a chain of its joints (<see cref="CentipedeRig"/>) laid along the path the
/// head took. The one simulated body is the head; the spine remembers where it has been and which way was up
/// there, and stands every joint its link distance further back along that trail, so the body follows the head
/// round a turn, up a wall and over a ledge segment by segment instead of swinging round whole. It is not physics
/// (nothing collides with a segment), but its joints are where a blow may land. Each side (the simulation, a
/// client's mirror) follows its own. A straight chain is exactly the rigid pose. GL-free.
/// </summary>
/// <param name="rig">The model's joints.</param>
/// <param name="halfHeight">How far the head's center rides over the surface it holds.</param>
public sealed class CentipedeSpine(CentipedeRig rig, float halfHeight)
{
    private readonly record struct Sample(Vector3 Point, Vector3 Up);

    // Where the head has been, oldest first; the head itself is not in it
    private readonly List<Sample> _trail = [];
    private readonly Vector3[] _points = new Vector3[rig.Count];
    private readonly Vector3[] _ups = new Vector3[rig.Count];
    private readonly Matrix4x4[] _roots = new Matrix4x4[rig.Count];

    /// <summary>Whether <see cref="Follow"/> has placed the chain yet.</summary>
    public bool IsLaid { get; private set; }

    /// <summary>The number of joints.</summary>
    public int Count => _points.Length;

    /// <summary>Where joint <paramref name="index"/> (0 is the head's) is: on the head's path, so as far off the
    /// surface as the head's center rides.</summary>
    public Vector3 this[int index] => _points[index];

    /// <summary>Which way is up for joint <paramref name="index"/>: away from the surface the head held there.</summary>
    public Vector3 UpAt(int index) => _ups[index];

    /// <summary>
    /// Takes the head's place for this frame (its <paramref name="center"/>, the <paramref name="up"/> of the
    /// surface it holds and its unit <paramref name="heading"/>) and stands the joints along the trail behind it.
    /// The first call, and one whose head jumped farther than the body is long (a teleport, a mirror's
    /// correction), lays the chain out straight behind the head instead.
    /// </summary>
    public void Follow(Vector3 center, Vector3 up, Vector3 heading)
    {
        var spacing = WildlifeConfig.CentipedeTrailSpacingBlocks;
        if (!IsLaid || Vector3.DistanceSquared(center, _points[0]) > rig.Length * rig.Length)
        {
            _trail.Clear();
            _trail.Add(new Sample(center - heading * (rig.Length + spacing), up));
            IsLaid = true;
        }
        else if (Vector3.DistanceSquared(center, _trail[^1].Point) >= spacing * spacing)
        {
            _trail.Add(new Sample(center, up));
        }

        Place(new Sample(center, up), -heading);
        for (var i = 0; i < _roots.Length; i++)
        {
            // The head looks where the body faces, so its bite aims true; the rest lie along the chain
            var forward = i == 0 ? heading : ChainFollow.Direction(Along(i), heading);
            // The chain's direction is kept and the up leaned to suit it, so a segment tips over a corner
            var across = _ups[i] - forward * Vector3.Dot(_ups[i], forward);
            var jointUp = across.LengthSquared() < 1e-6f ? _ups[i] : Vector3.Normalize(across);
            _roots[i] = Matrix4x4.CreateTranslation(-rig.Pivots[i].X, 0f, -rig.Pivots[i].Z)
                        * Matrix4x4.CreateFromQuaternion(CrawlFrame.Orientation(jointUp, forward))
                        * Matrix4x4.CreateTranslation(_points[i] - jointUp * halfHeight);
        }
    }

    /// <summary>
    /// The root rig part <paramref name="part"/> is posed onto (what <see cref="RigPose.PartMatrix"/> takes): its
    /// joint's, which stands the joint's origin on the surface under its chain point and turns it along the chain,
    /// or the head's for a part that rides no joint.
    /// </summary>
    public Matrix4x4 RootOf(string part) => _roots[Math.Max(rig.JointOf(part), 0)];

    // Stands every joint its distance back along the trail (the head, then the samples newest first), and forgets
    // the trail past the tail. A trail that ends short runs on straight.
    private void Place(Sample head, Vector3 back)
    {
        _points[0] = head.Point;
        _ups[0] = head.Up;

        var from = head;
        var walked = 0f;
        var next = _trail.Count - 1;
        var reach = 0f;
        for (var i = 1; i < _points.Length; i++)
        {
            reach += rig.Links[i];
            var length = 0f;
            for (; next >= 0; next--)
            {
                length = Vector3.Distance(from.Point, _trail[next].Point);
                if (walked + length >= reach) break;
                if (length > 1e-5f) back = (_trail[next].Point - from.Point) / length;
                walked += length;
                from = _trail[next];
            }

            if (next < 0)
            {
                _points[i] = from.Point + back * (reach - walked);
                _ups[i] = from.Up;
                continue;
            }

            var to = _trail[next];
            var share = length > 1e-5f ? (reach - walked) / length : 0f;
            _points[i] = Vector3.Lerp(from.Point, to.Point, share);
            _ups[i] = ChainFollow.Direction(Vector3.Lerp(from.Up, to.Up, share), from.Up);
        }

        if (next > 0) _trail.RemoveRange(0, next);
    }

    // The way the chain runs through joint i, back to front: between its neighbors, the tail along its one link
    private Vector3 Along(int i) =>
        i >= _points.Length - 1 ? _points[i - 1] - _points[i] : _points[i - 1] - _points[i + 1];
}
