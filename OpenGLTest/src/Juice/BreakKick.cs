using VoxelEngine.Config;
using VoxelEngine.Core;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free rule of the camera nudge a heavy mining hit gives (a chop at a trunk, an ore breaking):
/// how hard the kick is for the block's hardness. Soft blocks give none, the strength rises with the
/// hardness to a cap, and the hit that finally breaks the block kicks harder than one that only
/// chips it. The caller feeds it to <c>Camera.AddHitKick</c>.
/// </summary>
public static class BreakKick
{
    /// <summary>
    /// The kick strength for one landed hit on a block of <paramref name="hardness"/>: 0 at or below
    /// <see cref="PlayerConfig.BreakKickMinHardness"/>, rising to <see cref="PlayerConfig.BreakKickMaxStrength"/>
    /// at <see cref="PlayerConfig.BreakKickFullHardness"/> and held there; <paramref name="breaks"/> scales
    /// it by <see cref="PlayerConfig.BreakKickFinalFactor"/> (the cap included).
    /// </summary>
    public static float Strength(float hardness, bool breaks)
    {
        var strength = PlayerConfig.BreakKickMaxStrength * MathUtils.ImpactStrength(
            hardness, PlayerConfig.BreakKickMinHardness, PlayerConfig.BreakKickFullHardness);
        return breaks ? strength * PlayerConfig.BreakKickFinalFactor : strength;
    }
}
