using System;
using static VoxelEngine.Player.PlayerRig;

namespace VoxelEngine.Player.Posing;

/// <summary>The two-bone arm math: where a shoulder pitch and elbow put the hand, and the joints that reach a given hand offset.</summary>
internal static class ArmSolver
{
    // Hand offset from the shoulder in the arm's bend plane (y, z), before the sweep yaw, for a
    // given shoulder pitch and elbow. Matches GetPartMatrix's arm chain exactly, so the two-bone
    // solve below can invert it.
    internal static void ArmHandOffset(float shoulder, float elbow, out float y, out float z)
    {
        var a = ForearmLength * MathF.Cos(elbow) + UpperArmLength;
        var b = ForearmLength * MathF.Sin(elbow);
        y = -a * MathF.Cos(shoulder) + b * MathF.Sin(shoulder);
        z = -(a * MathF.Sin(shoulder) + b * MathF.Cos(shoulder));
    }

    // Two-bone solve: the shoulder pitch and elbow that reach a hand offset (y, z) with the
    // shoulder itself fixed. Lets the slice drop the hand by re-posing the arm instead of
    // translating it, which would detach the shoulder from the torso (visible in third person).
    internal static (float Shoulder, float Elbow) SolveArm(float y, float z)
    {
        var span = UpperArmLength + ForearmLength;
        var reach = MathF.Min(MathF.Sqrt(y * y + z * z), span - 1e-4f);
        var elbow = 2f * MathF.Acos(Math.Clamp(reach / span, 0f, 1f));
        var a = ForearmLength * MathF.Cos(elbow) + UpperArmLength;
        var b = ForearmLength * MathF.Sin(elbow);
        var det = a * a + b * b;
        return (MathF.Atan2((b * y - a * z) / det, -(a * y + b * z) / det), elbow);
    }
}
