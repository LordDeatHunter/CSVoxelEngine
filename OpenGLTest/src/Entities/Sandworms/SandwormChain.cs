using System;
using System.Numerics;

namespace VoxelEngine.Entities.Sandworms;

/// <summary>Which part of the model a chain segment is drawn with.</summary>
public enum SandwormSegmentKind
{
    Head,
    Body,
    Tail,
}

/// <summary>
/// The sandworm's body: a chain of rigid segments, each held a fixed link distance behind the one ahead, so
/// moving the head tugs every other segment along (follow-the-leader). GL-free, state is only the segment
/// centers, and the whole thing is a deterministic function of the head's path. A link may not bend sharper
/// than <see cref="SandwormConfig.MaxBendDegrees"/>, so a hard turn sweeps the body round in an arc rather than
/// folding it.
/// </summary>
public sealed class SandwormChain
{
    private readonly Vector3[] _points;
    private readonly float _maxBendRadians = SandwormConfig.MaxBendDegrees * MathF.PI / 180f;

    /// <summary>Lays the chain out straight behind <paramref name="head"/>, opposite <paramref name="forward"/>.</summary>
    public SandwormChain(Vector3 head, Vector3 forward)
    {
        _points = new Vector3[SandwormConfig.SegmentCount];
        var back = -Normalized(forward, -Vector3.UnitZ);
        _points[0] = head;
        for (var i = 1; i < _points.Length; i++)
            _points[i] = _points[i - 1] + back * LinkLength(i);
    }

    /// <summary>The number of segments.</summary>
    public int Count => _points.Length;

    /// <summary>The head segment's center.</summary>
    public Vector3 Head => _points[0];

    /// <summary>The center of segment <paramref name="index"/> (0 is the head).</summary>
    public Vector3 this[int index] => _points[index];

    /// <summary>The model part segment <paramref name="index"/> is drawn with.</summary>
    public static SandwormSegmentKind KindOf(int index) =>
        index == 0 ? SandwormSegmentKind.Head
        : index == SandwormConfig.SegmentCount - 1 ? SandwormSegmentKind.Tail
        : SandwormSegmentKind.Body;

    /// <summary>The distance from segment <paramref name="index"/> to the one ahead of it.</summary>
    public static float LinkLength(int index) =>
        index <= 1 ? SandwormConfig.HeadLinkBlocks
        : index == SandwormConfig.SegmentCount - 1 ? SandwormConfig.TailLinkBlocks
        : SandwormConfig.BodyLinkBlocks;

    /// <summary>The draw scale of segment <paramref name="index"/>: 1 for the head, tapering along the body to the tail.</summary>
    public static float ScaleOf(int index)
    {
        var last = SandwormConfig.SegmentCount - 1;
        if (index <= 0) return 1f;
        if (index >= last) return SandwormConfig.TailScale;
        var t = (index - 1) / (float)Math.Max(1, last - 2);
        return SandwormConfig.BodyScaleStart + (SandwormConfig.BodyScaleEnd - SandwormConfig.BodyScaleStart) * t;
    }

    /// <summary>
    /// The direction segment <paramref name="index"/> faces: from the segment behind it toward it (the head
    /// faces along the first link), a unit vector.
    /// </summary>
    public Vector3 Forward(int index)
    {
        var toward = index == 0 ? _points[0] - _points[1] : _points[index - 1] - _points[index];
        return Normalized(toward, -Vector3.UnitZ);
    }

    /// <summary>
    /// Moves the head to <paramref name="head"/> and drags every segment behind it to its link distance,
    /// each toward where it was, with the bend limit applied down the chain.
    /// </summary>
    public void Follow(Vector3 head)
    {
        _points[0] = head;
        for (var i = 1; i < _points.Length; i++)
        {
            var parent = _points[i - 1];
            var link = LinkLength(i);
            var dir = Normalized(_points[i] - parent, Vector3.UnitZ);
            if (i >= 2)
                dir = LimitBend(dir, Normalized(parent - _points[i - 2], Vector3.UnitZ));
            _points[i] = parent + dir * link;
        }
    }

    // The direction a link may take: dir itself, or the nearest direction to it within the bend limit of
    // `previous`, the direction of the link ahead of it (both point from the front of the chain toward its end).
    private Vector3 LimitBend(Vector3 dir, Vector3 previous)
    {
        var cos = Math.Clamp(Vector3.Dot(dir, previous), -1f, 1f);
        if (MathF.Acos(cos) <= _maxBendRadians) return dir;
        var side = dir - previous * cos;
        if (side.LengthSquared() < 1e-8f) return previous;
        side = Vector3.Normalize(side);
        return previous * MathF.Cos(_maxBendRadians) + side * MathF.Sin(_maxBendRadians);
    }

    private static Vector3 Normalized(Vector3 v, Vector3 fallback)
    {
        var lengthSquared = v.LengthSquared();
        return lengthSquared < 1e-10f ? fallback : v / MathF.Sqrt(lengthSquared);
    }
}
