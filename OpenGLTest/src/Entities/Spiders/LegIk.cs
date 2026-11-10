using System;
using System.Numerics;

namespace VoxelEngine.Entities.Spiders;

/// <summary>
/// The analytic two-bone solver of a spider leg: given the hip and the point the foot should reach, both in the
/// model's space, it gives the rotations that fold the upper and lower segments so the tip lands there, the knee
/// bent toward a pole direction (up, so the knees stand above the body like a spider's). The segments rest along
/// the model's X axis (+X for a right leg, -X for a left), which is what the rotations are relative to. GL-free
/// and state-free.
/// </summary>
public static class LegIk
{
    // A leg is never solved fully straight or fully folded, where the knee has no defined side
    private const float StraightShare = 0.999f;
    private const float FoldSlack = 1e-3f;

    /// <summary>
    /// Solves one leg. <paramref name="side"/> is +1 for a leg resting along +X and -1 for one along -X.
    /// <paramref name="upper"/> is the upper segment's rotation in model space and <paramref name="lowerLocal"/>
    /// the lower segment's rotation relative to the upper (what a child bone's local transform holds);
    /// <paramref name="knee"/> is where the joint ends up. A foot out of reach is aimed at with the leg nearly
    /// straight; one too close folds the leg as far as it goes.
    /// </summary>
    public static void Solve(Vector3 hip, Vector3 foot, float side, float upperLength, float lowerLength,
        Vector3 pole, out Quaternion upper, out Quaternion lowerLocal, out Vector3 knee)
    {
        var toFoot = foot - hip;
        var distance = toFoot.Length();
        var line = distance > 1e-5f ? toFoot / distance : new Vector3(side, 0f, 0f);
        distance = Math.Clamp(distance, MathF.Abs(upperLength - lowerLength) + FoldSlack,
            (upperLength + lowerLength) * StraightShare);

        // The knee bends toward the pole's part across the hip-to-foot line
        var bend = pole - line * Vector3.Dot(pole, line);
        if (bend.LengthSquared() < 1e-6f)
        {
            var outward = new Vector3(side, 0f, 0f);
            bend = outward - line * Vector3.Dot(outward, line);
        }
        if (bend.LengthSquared() < 1e-6f) bend = Vector3.UnitZ - line * line.Z;
        bend = Vector3.Normalize(bend);

        var along = (upperLength * upperLength - lowerLength * lowerLength + distance * distance) / (2f * distance);
        var rise = MathF.Sqrt(MathF.Max(0f, upperLength * upperLength - along * along));
        knee = hip + line * along + bend * rise;

        var planeNormal = Vector3.Normalize(Vector3.Cross(line, bend));
        var upperDirection = (knee - hip) / upperLength;
        var lowerDirection = Vector3.Normalize(hip + line * distance - knee);
        upper = SegmentRotation(upperDirection, planeNormal, side);
        var lower = SegmentRotation(lowerDirection, planeNormal, side);
        // The child's rotation runs first, then the parent's: lower = upper * lowerLocal
        lowerLocal = Quaternion.Normalize(Quaternion.Inverse(upper) * lower);
    }

    // The rotation that lays a segment resting along side * X onto a direction, keeping its Z across the leg's
    // plane, so a square segment never rolls about its own length
    private static Quaternion SegmentRotation(Vector3 direction, Vector3 planeNormal, float side)
    {
        var x = direction * side;
        var z = planeNormal * side;
        var y = Vector3.Cross(z, x);
        return Quaternion.CreateFromRotationMatrix(new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f));
    }
}
