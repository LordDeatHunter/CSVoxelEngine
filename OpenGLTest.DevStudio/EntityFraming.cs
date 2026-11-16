using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Player;

namespace VoxelEngine.DevStudio;

/// <summary>
/// The fixed camera an entity thumbnail is drawn with, GL-free: the model's posed bounds (every cube of every part, through
/// that part's matrix) seen from above and to the front-right, at the distance that fits the bounds' sphere in the view.
/// The caller says which way the posed model faces.
/// </summary>
public static class EntityFraming
{
    /// <summary>How far the camera sits round from straight in front, toward the model's right.</summary>
    public const float YawDegrees = 35f;

    /// <summary>How far the camera looks down.</summary>
    public const float PitchDegrees = 30f;

    /// <summary>The thumbnail's vertical field of view.</summary>
    public const float FieldOfViewY = 30f * MathF.PI / 180f;

    /// <summary>The box around every cube of every part, each part's cubes taken through its matrix.</summary>
    public static (Vector3 Min, Vector3 Max) Bounds(IEnumerable<(Matrix4x4 Part, IReadOnlyList<PlayerRig.RigCube> Cubes)> parts)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var (matrix, cubes) in parts)
        foreach (var cube in cubes)
            for (var corner = 0; corner < 8; corner++)
            {
                var local = new Vector3((corner & 1) == 0 ? cube.Min.X : cube.Max.X, (corner & 2) == 0 ? cube.Min.Y : cube.Max.Y,
                    (corner & 4) == 0 ? cube.Min.Z : cube.Max.Z);
                var world = Vector3.Transform(local, matrix);
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        return min.X > max.X ? (Vector3.Zero, Vector3.Zero) : (min, max);
    }

    /// <summary>The view-projection (square aspect) and the eye that frame the box, the whole box in view, from in front
    /// of a model facing <paramref name="front"/> (a horizontal unit vector), turned toward its right and looking down.</summary>
    public static (Matrix4x4 ViewProjection, Vector3 Eye) Frame(Vector3 min, Vector3 max, Vector3 front)
    {
        var center = (min + max) * 0.5f;
        var radius = MathF.Max((max - min).Length() * 0.5f, 0.1f);
        var distance = radius / MathF.Sin(FieldOfViewY * 0.5f);
        var yaw = YawDegrees * MathF.PI / 180f;
        var pitch = PitchDegrees * MathF.PI / 180f;
        var right = Vector3.Cross(front, Vector3.UnitY);
        var direction = (front * MathF.Cos(yaw) + right * MathF.Sin(yaw)) * MathF.Cos(pitch) + Vector3.UnitY * MathF.Sin(pitch);
        var eye = center + direction * distance;
        var view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(FieldOfViewY, 1f, MathF.Max(0.01f, distance - radius * 1.5f),
            distance + radius * 1.5f);
        return (view * projection, eye);
    }
}
