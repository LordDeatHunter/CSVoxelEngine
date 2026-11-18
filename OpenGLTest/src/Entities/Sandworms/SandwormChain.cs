using System;
using System.Numerics;
using VoxelEngine.Entities.Posing;

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
    private static readonly float[] Links = BuildLinks();

    private readonly Vector3[] _points;
    private readonly float _maxBendRadians = SandwormConfig.MaxBendDegrees * MathF.PI / 180f;

    /// <summary>Lays the chain out straight behind <paramref name="head"/>, opposite <paramref name="forward"/>.</summary>
    public SandwormChain(Vector3 head, Vector3 forward)
    {
        _points = new Vector3[SandwormConfig.SegmentCount];
        var back = -ChainFollow.Direction(forward, -Vector3.UnitZ);
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
        return ChainFollow.Direction(toward, -Vector3.UnitZ);
    }

    /// <summary>
    /// Moves the head to <paramref name="head"/> and drags every segment behind it to its link distance,
    /// each toward where it was, with the bend limit applied down the chain.
    /// </summary>
    public void Follow(Vector3 head)
    {
        _points[0] = head;
        ChainFollow.Follow(_points, Links, _maxBendRadians);
    }

    private static float[] BuildLinks()
    {
        var links = new float[SandwormConfig.SegmentCount];
        for (var i = 1; i < links.Length; i++) links[i] = LinkLength(i);
        return links;
    }
}
