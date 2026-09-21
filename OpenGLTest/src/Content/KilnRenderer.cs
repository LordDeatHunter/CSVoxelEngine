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

    /// <summary>Where a crucible rests inside the hearth, on the floor toward the front so it
    /// can be reached. Consumed by the casting run.</summary>
    public static readonly Vector3 CrucibleLocal = new(1f, OpeningFloorY, 0.375f);

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
