using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Authoring contract of the mud brick kiln's model: where its opening is and where the things
/// it holds sit inside it. Every number here mirrors
/// <c>assets/models/block/mud_brick_kiln.json</c>, which is authored in 0..32 model units with
/// the opening on north (-Z) - so these are cell-space values in the volume's local frame,
/// 0..<see cref="MultiblockShape.Size"/> on every axis, and <see cref="ToWorld"/> turns one of
/// them onto the kiln's actual facing.
///
/// GL-free and pure, like <see cref="QuernRenderer"/>: the manager, the render pass and the
/// tests all read the same constants instead of re-deriving the geometry.
/// </summary>
public static class KilnRenderer
{
    /// <summary>West edge of the opening (model x = 8, between the two piers).</summary>
    public const float OpeningMinX = 8f / 16f;

    /// <summary>East edge of the opening (model x = 24).</summary>
    public const float OpeningMaxX = 24f / 16f;

    /// <summary>Top of the hearth floor the fire and the crucible stand on (model y = 2).</summary>
    public const float OpeningFloorY = 2f / 16f;

    /// <summary>Underside of the lintel closing the opening (model y = 18).</summary>
    public const float OpeningCeilingY = 18f / 16f;

    /// <summary>Depth of the opening: the hearth's back wall (model z = 12), measured from the
    /// structure's front face at z = 0.</summary>
    public const float OpeningDepthZ = 12f / 16f;

    /// <summary>Centre of the bowl a crucible rests on inside the hearth: on the floor and
    /// toward the front so it can be reached. The bowl spans z 0.03..0.53 around this, which
    /// leaves <see cref="FireLocal"/>'s flame at 0.55 standing just behind its back wall.</summary>
    public static readonly Vector3 CrucibleLocal = new(1f, OpeningFloorY, 0.28f);

    /// <summary>Half the crucible bowl's outer footprint (model x/z 4..12 of 16).</summary>
    public const float CrucibleOuterHalf = 4f / 16f;

    /// <summary>Top of the bowl's floor slab, where a molten pool starts (model y = 2).</summary>
    public const float CrucibleFloorY = 2f / 16f;

    /// <summary>The bowl's rim (model y = 8).</summary>
    public const float CrucibleTopY = 8f / 16f;

    /// <summary>Inside face of the bowl's west/north walls (model x/z = 5), in cell space.</summary>
    public const float CrucibleInnerMin = 5f / 16f;

    /// <summary>Inside face of the bowl's east/south walls (model x/z = 11), in cell space.</summary>
    public const float CrucibleInnerMax = 11f / 16f;

    /// <summary>
    /// Height above the crucible's base at which its molten surface is drawn, so a pool rises
    /// smoothly from just over the bowl floor to just under its rim as it fills. Clamps past
    /// full, and reads as empty for a capacity of 0 (an item that is not a crucible).
    /// </summary>
    public static float LiquidHeight(float ml, int capacityMl) =>
        MathUtils.Lerp(CrucibleFloorY + 0.03f, CrucibleTopY - 0.03f,
            capacityMl > 0 ? MathUtils.Clamp01(ml / capacityMl) : 0f);

    /// <summary>Where the kiln's flame burns: centred between the piers, just above the hearth
    /// floor and behind the crucible.</summary>
    public static readonly Vector3 FireLocal = new(1f, 0.25f, 0.55f);

    /// <summary>Where the stored fuel sits, at the back of the hearth. Consumed by the casting
    /// run's render pass.</summary>
    public static readonly Vector3 FuelLocal = new(1f, OpeningFloorY, 0.65f);

    /// <summary>Turns a local point of the volume into a world position for a kiln whose anchor
    /// cell (the volume's minimum corner) is <paramref name="anchor"/> and which faces
    /// <paramref name="facing"/>.</summary>
    public static Vector3 ToWorld(Vector3 anchor, int facing, Vector3 local) =>
        anchor + MultiblockShape.RotatePoint(local, facing);
}
