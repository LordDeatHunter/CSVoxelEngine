using System;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Entities;
using VoxelEngine.Simulation;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// What every kiln draws the same way, whatever its model: the crucible bowl's own geometry (the
/// <c>crucible</c> item model), the size of a fuel lump, the waiting charge resting over the bowl's
/// rim and the curves that grow the molten pool and shrink that charge. Where those props stand
/// inside a given kiln's opening is its <see cref="KilnLayout"/>.
///
/// GL-free and pure, like <see cref="QuernRenderer"/>: the manager, the render pass and the
/// tests all read the same constants instead of re-deriving the geometry.
/// </summary>
public static class KilnRenderer
{
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

    /// <summary>Edge length one stored unit of fuel is drawn at: a lump the size of a dropped
    /// item.</summary>
    public const float FuelSize = ItemDrop.Size;

    /// <summary>Center-to-center distance between stored units along the opening. Never less
    /// than a unit's own width, so two sprites lying flat never overlap coplanarly and
    /// z-fight.</summary>
    public const float FuelSpacing = FuelSize;

    /// <summary>Clearance between the bowl's rim and the underside of the lump waiting to
    /// melt.</summary>
    public const float PendingGap = 0.02f;

    /// <summary>Edge length the lump of solid charge waiting to melt is drawn at, at full size:
    /// wider than the bowl's mouth (<see cref="CrucibleInnerMin"/>..<see cref="CrucibleInnerMax"/>)
    /// so it reads as resting across the rim rather than having fallen in, and narrower than the
    /// bowl itself.</summary>
    public const float PendingSize = 2f * CrucibleOuterHalf * 0.8f;

    /// <summary>
    /// How big the waiting lump is drawn, as a fraction 0..1 of <see cref="PendingSize"/>: full
    /// while a whole unit or more is still solid, then shrinking linearly to nothing as the last
    /// unit melts, so it vanishes exactly when the charge does instead of cutting out - the
    /// continuity <see cref="KilnManager.DisplayMoltenMl"/> gives the pool below it. A unit size
    /// of 0 (no melt recipe recorded) still reads as a whole lump while anything is pending.
    /// </summary>
    public static float PendingScale01(float displayPendingMl, int unitMl) =>
        unitMl > 0
            ? MathUtils.Clamp01(displayPendingMl / unitMl)
            : displayPendingMl > 0f ? 1f : 0f;

    /// <summary>The yaw that turns a prop's local +Z - an upright sprite's front face - out of
    /// the kiln's opening.</summary>
    public static float OpeningYaw(int facing)
    {
        var front = MultiblockShape.FrontNormal(facing);
        return MathF.Atan2(front.X, front.Z);
    }
}
