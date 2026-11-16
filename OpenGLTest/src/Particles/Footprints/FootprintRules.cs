using System;
using System.Numerics;

namespace VoxelEngine.Particles.Footprints;

/// <summary>
/// The footprint rules, GL-free: how a print fades over its life (<see cref="Alpha"/>) and where its quad's
/// four corners lie on the ground (<see cref="Corners"/>).
/// </summary>
public static class FootprintRules
{
    /// <summary>
    /// A print's opacity <paramref name="age"/> seconds in: its full <paramref name="visibility"/> while
    /// young, fading to nothing over the last <see cref="FootprintConfig.FadeShare"/> of its
    /// <paramref name="persistence"/> seconds; 0 once that is over (or for a print that cannot last).
    /// </summary>
    public static float Alpha(float visibility, float persistence, float age)
    {
        if (persistence <= 0f || age >= persistence) return 0f;
        var left = 1f - Math.Max(age, 0f) / persistence;
        return Math.Clamp(visibility, 0f, 1f) * Math.Min(1f, left / FootprintConfig.FadeShare);
    }

    /// <summary>
    /// The quad's corners (back-left, front-left, front-right, back-right) of a foot landing at
    /// <paramref name="feet"/> heading <paramref name="heading"/> (a unit XZ vector, X and Z), the foot
    /// <paramref name="side"/> (-1 left, 1 right) off the walking line, lifted <see cref="FootprintConfig.Lift"/>.
    /// </summary>
    public static void Corners(Vector3 feet, Vector2 heading, int side, Span<Vector3> corners)
    {
        var forward = new Vector3(heading.X, 0f, heading.Y);
        var right = new Vector3(-forward.Z, 0f, forward.X);
        var center = feet + right * (side * FootprintConfig.FootSpread) + new Vector3(0f, FootprintConfig.Lift, 0f);
        var halfLength = forward * (FootprintConfig.Length * 0.5f);
        var halfWidth = right * (FootprintConfig.Width * 0.5f);
        corners[0] = center - halfLength - halfWidth;
        corners[1] = center + halfLength - halfWidth;
        corners[2] = center + halfLength + halfWidth;
        corners[3] = center - halfLength + halfWidth;
    }
}
