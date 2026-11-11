using System;

namespace VoxelEngine.Player;

/// <summary>
/// The sink-block rule, GL-free: a block with a <c>sink</c> share (a cloud block) collides with the
/// player body only below its top <c>sink</c> share of the cell, and that top share is a soft layer
/// the body descends through slowly (<see cref="PlayerConfig.SinkBlockSpeed"/>) until it rests on the
/// solid part. A body whose feet stand in the layer counts as standing on ground, so it may jump,
/// step and walk as on land, the fall tracker reads its entry as a landing, and the server's hover
/// rule never takes it for a hoverer. The client's physics (<see cref="PlayerPhysics"/>), the
/// collision (<see cref="BodyCollision"/>) and the server's probe (<see cref="PlayerBodyProbe"/>)
/// all read this one rule.
/// </summary>
public static class SinkLayer
{
    /// <summary>The largest share of a cell a body may sink into: half a block.</summary>
    public const float MaxSink = 0.5f;

    /// <summary>
    /// How far above the layer's top a body's feet may stand and still be in it, and below the
    /// solid part (a resting body stops a hair above it): the same hair the bounce probe reads.
    /// </summary>
    public const float FeetTolerance = 0.05f;

    /// <summary>The height, from the cell's floor, of the part of a sink block that collides with the
    /// player body (the whole cell for a plain block with <paramref name="sink"/> 0).</summary>
    public static float SolidHeight(float sink) => 1f - sink;

    /// <summary>
    /// Whether feet at <paramref name="feetY"/> stand in the soft layer of a sink block in the cell
    /// at <paramref name="cellY"/> with share <paramref name="sink"/>: from just above its top down to
    /// resting on its solid part. Always false for a plain block (<paramref name="sink"/> 0).
    /// </summary>
    public static bool FeetIn(float feetY, int cellY, float sink) =>
        sink > 0f
        && feetY <= cellY + 1f + FeetTolerance
        && feetY >= cellY + SolidHeight(sink) - FeetTolerance;

    /// <summary>
    /// Where feet that moved from <paramref name="feetBefore"/> to <paramref name="feetAfter"/> this
    /// frame stand once the soft layer whose top is <paramref name="layerTop"/> has held them: never
    /// more than <paramref name="sinkSpeed"/> times <paramref name="dt"/> below where they were (or
    /// below the top, for a body falling in from above it), so a fall is caught at the top and every
    /// body then sinks slowly. A rising or slower move is left as it was.
    /// </summary>
    public static float HoldFeet(float feetBefore, float feetAfter, float layerTop, float sinkSpeed, float dt) =>
        MathF.Max(feetAfter, MathF.Min(feetBefore, layerTop) - sinkSpeed * dt);
}
