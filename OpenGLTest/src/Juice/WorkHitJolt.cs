using System;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free twin of the jolt a tool's hit gives what lies on a work station
/// (<see cref="Rendering.Actors.SmithingPasses"/>): the pieces of the stack jump off the surface, flattened and
/// spread a little by the blow, each knocked askew, and settle back exactly where they lay. The higher a piece lies
/// in the pile the further it jumps, so the pile comes apart in the air and closes again.
/// </summary>
public static class WorkHitJolt
{
    /// <summary>Seconds one jolt lasts.</summary>
    public const float Seconds = 0.42f;

    // Blocks the lowest piece jumps, and how much further each one above it does
    private const float Hop = 0.07f;
    private const float HopPerPiece = 0.035f;
    // The share a piece grows across (and shrinks in height) under the blow, gone by the top of its jump
    private const float Spread = 0.22f;
    // Radians a piece is knocked askew, back and forth as it settles
    private const float Skew = 0.5f;
    // The share of the jolt at which the jump tops out
    private const float PeakAt = 0.35f;

    /// <summary>What the jolt adds to one piece: <paramref name="Lift"/> blocks above where it rests,
    /// <paramref name="Spread"/> as a share of its size across (its height shrinks by the same share), and
    /// <paramref name="Twist"/> radians about the vertical.</summary>
    public readonly record struct Pose(float Lift, float Spread, float Twist);

    /// <summary>
    /// The pose of piece <paramref name="index"/> of a pile (0 the lowest) <paramref name="elapsed"/> seconds after
    /// the hit. Nothing before the hit and from <see cref="Seconds"/> on, and never below where the piece rests.
    /// </summary>
    public static Pose At(float elapsed, int index)
    {
        if (!(elapsed >= 0f) || elapsed >= Seconds) return default;
        var t = elapsed / Seconds;
        var hop = (Hop + HopPerPiece * index) * MathUtils.Pulse(t, PeakAt);
        var spread = Spread * (1f - MathUtils.SmoothStep(MathUtils.Clamp01(t / PeakAt)));
        var side = index % 2 == 0 ? 1f : -1f;
        var twist = side * Skew * MathF.Sin(t * MathF.Tau * 1.5f) * (1f - t) * (1f - t);
        return new Pose(hop, spread, twist);
    }
}
