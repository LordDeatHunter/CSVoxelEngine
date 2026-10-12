using System;
using System.Numerics;
using VoxelEngine.UI;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// Authoring contract of how an extruded item sprite is posed when it lies on a surface.
/// <see cref="Primitives.CreateExtrudedSprite"/> builds a sprite upright: the image's top maps
/// to local +Y, its front face looks along +Z and it is thin along Z. A lump of coal on a
/// hearth floor or a cooled cast in a mold's pocket has to be turned face-up instead, and
/// lifted by half its own thickness so neither of its faces z-fights the surface it rests on.
///
/// GL-free and pure, like <see cref="KilnRenderer"/> and <see cref="CastingRenderer"/>: the
/// render passes and the tests read the same numbers instead of each composing their own
/// rotation.
/// </summary>
public static class SpritePose
{
    /// <summary>Frame width every sprite under <c>assets/textures/items</c> is authored at - a
    /// spritesheet's frame, not the whole sheet - and therefore the width
    /// <c>ItemMeshCache</c> hands <see cref="Primitives.CreateExtrudedSprite"/>.</summary>
    public const int ItemSpriteFrame = 32;

    /// <summary>Half the extrusion depth <see cref="Primitives.CreateExtrudedSprite"/> gives a
    /// sprite of <paramref name="size"/> built from a <paramref name="pixelWidth"/>-wide
    /// texture (its depth is <c>size / width * 2</c>), i.e. how far the front and back faces
    /// stand off the sprite's center plane.</summary>
    public static float HalfThickness(float size, int pixelWidth) => size / pixelWidth;

    /// <summary>Gap kept between a face-up sprite's back face and the surface it rests on, so
    /// neither face z-fights that surface.</summary>
    public const float RestingClearance = 0.004f;

    /// <summary>Height of a face-up sprite's center over the surface it rests on: half its own
    /// thickness plus <see cref="RestingClearance"/>, so a wider sprite - which
    /// <see cref="Primitives.CreateExtrudedSprite"/> extrudes thicker - is lifted further.</summary>
    public static float RestingLift(float size) =>
        HalfThickness(size, ItemSpriteFrame) + RestingClearance;

    /// <summary>
    /// Turns an upright sprite face-up (its front face toward +Y), then yaws it about world Y.
    /// With <paramref name="yaw"/> = 0 the image top points to -Z; a viewer standing at +Z
    /// looking down reads it the right way up, so pass the yaw that turns local +Z toward the
    /// viewer (<see cref="KilnRenderer.OpeningYaw"/> does) and the top lands away from them.
    /// Callers compose <c>CreateScale(size) * LyingFaceUp(yaw) * CreateTranslation(...)</c>.
    /// </summary>
    public static Matrix4x4 LyingFaceUp(float yaw) =>
        Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateRotationY(yaw);
}
