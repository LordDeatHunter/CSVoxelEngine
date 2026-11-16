using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.UI.Quests;

/// <summary>
/// How an entity is laid out as an icon, GL-free: its rest pose (every cube of every part, through that part's matrix)
/// turned by <see cref="Rotation"/>, so the model looks toward the screen's left from slightly above, then centered and
/// scaled so its widest on-screen span is 1. The renderer multiplies by the icon's size and its screen placement.
/// </summary>
internal static class EntityIconLayout
{
    // A model faces -Z at rest. Turned 125 degrees about Y its front points to the screen's left and a little toward the
    // viewer (the UI looks along -Z), then tipped 25 degrees about X so its top shows.
    private const float YawDegrees = 125f;
    private const float PitchDegrees = 25f;

    public static readonly Matrix4x4 Rotation =
        Matrix4x4.CreateRotationY(YawDegrees * MathF.PI / 180f) * Matrix4x4.CreateRotationX(PitchDegrees * MathF.PI / 180f);

    /// <summary>The box around every cube of every part, each part's cubes taken through its matrix (then through
    /// <paramref name="view"/>).</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<(Matrix4x4 Part, IReadOnlyList<PlayerRig.RigCube> Cubes)> parts,
        Matrix4x4 view)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (part, cubes) in parts)
        {
            var matrix = part * view;
            foreach (var cube in cubes)
                for (var corner = 0; corner < 8; corner++)
                {
                    var local = new Vector3((corner & 1) == 0 ? cube.Min.X : cube.Max.X,
                        (corner & 2) == 0 ? cube.Min.Y : cube.Max.Y, (corner & 4) == 0 ? cube.Min.Z : cube.Max.Z);
                    var point = Vector3.Transform(local, matrix);
                    min = Vector3.Min(min, point);
                    max = Vector3.Max(max, point);
                }
        }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>The matrix that takes a rest-posed part (after its own part matrix) to the unit icon: turned by
    /// <see cref="Rotation"/>, centered on the turned bounds and scaled so their wider screen span is 1.</summary>
    public static Matrix4x4 Fit(IEnumerable<(Matrix4x4 Part, IReadOnlyList<PlayerRig.RigCube> Cubes)> parts)
    {
        var (min, max) = Bounds(parts, Rotation);
        var span = MathF.Max(MathF.Max(max.X - min.X, max.Y - min.Y), 1e-4f);
        return Rotation * Matrix4x4.CreateTranslation(-(min + max) * 0.5f) * Matrix4x4.CreateScale(1f / span);
    }
}
