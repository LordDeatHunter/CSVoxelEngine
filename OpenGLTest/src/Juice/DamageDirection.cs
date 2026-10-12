using System;
using System.Numerics;
using VoxelEngine.Player;
using VoxelEngine.Rendering;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free state of the damage direction arc: where the last billed blow came from, kept as a
/// world direction so the arc keeps pointing at the attacker while the player turns, and how
/// strongly the arc still shows. <see cref="Camera"/> owns one, feeds it each mob hit's direction
/// (<see cref="Hit"/>), ages it once a frame (<see cref="Update"/>) and turns it into a screen
/// direction from the current view yaw (<see cref="ScreenDirection"/>), which
/// <see cref="DamageVignetteRenderer"/> draws as a third term of the damage vignette's pass. A hit
/// with no direction (a fall, lava) shows nothing. The newest hit replaces the last one.
/// </summary>
public sealed class DamageDirection
{
    // Unit horizontal world direction from the player toward where the blow came from
    private Vector3 _from = -Vector3.UnitZ;
    // Seconds since that blow; infinite while there has been none
    private float _age = float.PositiveInfinity;

    /// <summary>
    /// A blow pushed the player along <paramref name="awayHorizontal"/> (the knockback direction,
    /// from the attacker toward the player): the arc turns to point back at its source and shows at
    /// full strength again. A zero, vertical or non-finite direction says nothing about where the
    /// blow came from and leaves the arc as it was.
    /// </summary>
    public void Hit(Vector3 awayHorizontal)
    {
        var from = new Vector3(-awayHorizontal.X, 0f, -awayHorizontal.Z);
        var lengthSquared = from.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1e-6f)
            return;
        _from = from / MathF.Sqrt(lengthSquared);
        _age = 0f;
    }

    /// <summary>Ages the arc by <paramref name="deltaTime"/> seconds.</summary>
    public void Update(float deltaTime)
    {
        if (float.IsFinite(_age))
            _age += MathF.Max(0f, deltaTime);
    }

    /// <summary>
    /// How strongly the arc shows, 0..1: full for <see cref="WorldGenerationConfig.DamageArcHoldSeconds"/>
    /// after a hit, then a smooth fade over <see cref="WorldGenerationConfig.DamageArcFadeSeconds"/>, and
    /// 0 with no hit yet. It only ever falls between hits.
    /// </summary>
    public float Intensity => float.IsFinite(_age)
        ? MathUtils.HoldThenFade(_age, WorldGenerationConfig.DamageArcHoldSeconds, WorldGenerationConfig.DamageArcFadeSeconds)
        : 0f;

    /// <summary>
    /// The angle of the blow's source relative to a view facing <paramref name="yawDegrees"/> (the
    /// camera's yaw convention: 0 faces +X, -90 faces -Z), in radians: 0 straight ahead, positive
    /// toward the view's right (the camera's <c>Cross(forward, up)</c>), ±π behind.
    /// </summary>
    public float ScreenAngle(float yawDegrees)
    {
        var yaw = MathUtils.ToRadians(yawDegrees);
        var forward = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));
        var right = new Vector2(-forward.Y, forward.X);
        var from = new Vector2(_from.X, _from.Z);
        return MathF.Atan2(Vector2.Dot(from, right), Vector2.Dot(from, forward));
    }

    /// <summary>
    /// Where on screen the arc sits for a view facing <paramref name="yawDegrees"/>, as a unit
    /// direction from the screen center with x to the right and y up: a blow from ahead points at
    /// the top edge, from the right at the right edge and from behind at the bottom edge.
    /// </summary>
    public Vector2 ScreenDirection(float yawDegrees)
    {
        var angle = ScreenAngle(yawDegrees);
        return new Vector2(MathF.Sin(angle), MathF.Cos(angle));
    }
}
