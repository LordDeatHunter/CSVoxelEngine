using VoxelEngine.Content.Models;
using VoxelEngine.Simulation;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// Split convention for the quern block: its model leaves an air gap between the static
/// base (top at y = 6/16) and the spinning plate plus handle (bottom at y = 7/16), so the
/// chunk keeps the base quads and a dynamic entity draws the top quads rotating about the
/// cell centre (see <see cref="QuernManager"/>). A quad never straddles the gap, which is
/// what makes the partition exact.
/// </summary>
public static class QuernRenderer
{
    /// <summary>Model-space Y (0..1) of the air gap between the quern's base and its top.</summary>
    public const float SplitY = 6.5f / 16f;

    /// <summary>Whether a baked quad belongs to the spinning top: every corner at or above
    /// the gap.</summary>
    public static bool IsTopQuad(BakedQuad quad)
    {
        foreach (var position in quad.Positions)
            if (position.Y < SplitY)
                return false;
        return true;
    }
}
