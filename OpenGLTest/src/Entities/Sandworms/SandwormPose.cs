using System;
using System.Numerics;

namespace VoxelEngine.Entities.Sandworms;

/// <summary>
/// The world matrices the sandworm model parts are drawn with, GL-free: the model is authored around its own
/// origin facing -Z (tools/generate-sandworm-model.py), so a segment is placed by a scale and a world matrix
/// facing along the chain, and the lower jaw hangs from the head hinge.
/// </summary>
public static class SandwormPose
{
    /// <summary>The jaw group pivot in blocks (the back of the mouth, from the model origin).</summary>
    public static readonly Vector3 JawHinge = new(0f, -1f, -1.125f);

    /// <summary>The direction segment <paramref name="index"/> faces: along the chain through it (the head and
    /// the tail along their one link, a body segment from the one behind it to the one ahead of it).</summary>
    public static Vector3 Direction(SandwormChain chain, int index)
    {
        if (index <= 0 || index >= chain.Count - 1) return chain.Forward(index);
        var along = chain[index - 1] - chain[index + 1];
        return along.LengthSquared() < 1e-8f ? chain.Forward(index) : Vector3.Normalize(along);
    }

    /// <summary>
    /// The matrix of segment <paramref name="index"/> part: its draw scale (times <paramref name="scaleFactor"/>)
    /// about the segment center, turned to face <see cref="Direction"/> with its top up, moved
    /// <paramref name="sink"/> blocks down.
    /// </summary>
    public static Matrix4x4 Segment(SandwormChain chain, int index, float scaleFactor = 1f, float sink = 0f)
    {
        var forward = Direction(chain, index);
        var up = MathF.Abs(forward.Y) > 0.98f ? Vector3.UnitZ : Vector3.UnitY;
        var center = chain[index] - new Vector3(0f, sink, 0f);
        return Matrix4x4.CreateScale(SandwormChain.ScaleOf(index) * scaleFactor) * Matrix4x4.CreateWorld(center, forward, up);
    }

    /// <summary>The lower jaw matrix: the <paramref name="head"/> matrix with the jaw swung
    /// <paramref name="openRadians"/> open about its hinge.</summary>
    public static Matrix4x4 Jaw(in Matrix4x4 head, float openRadians) =>
        // A positive rotation about +X lifts a point at -Z, so the jaw opens by turning the other way
        Matrix4x4.CreateRotationX(-openRadians) * Matrix4x4.CreateTranslation(JawHinge) * head;
}
