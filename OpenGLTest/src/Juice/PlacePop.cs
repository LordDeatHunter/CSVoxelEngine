using System;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free twin of the placement pop: the growth a freshly placed block's pop box is drawn with
/// (<see cref="Systems.RenderManager.RenderBlockPop"/>), so the block appears a little too big and
/// settles into its cell with one small overshoot (<see cref="MathUtils.EaseOutBack"/>) rather than
/// the mining hit's out-and-back <see cref="MathUtils.Pulse"/>. The chunk mesh already holds the
/// block at rest size, so only the grown part of the curve is ever visible; the tail's dip below
/// full size is hidden inside it.
/// </summary>
public static class PlacePop
{
    /// <summary>Growth in blocks per axis at <paramref name="elapsed"/> seconds into a
    /// <paramref name="seconds"/>-long pop: the full <paramref name="grow"/> at 0, settling to
    /// exactly 0 at the end and staying there.</summary>
    public static float Grow(float elapsed, float seconds, float grow) =>
        seconds <= 0f ? 0f : grow * (1f - MathUtils.EaseOutBack(elapsed / seconds));
}
