using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Ledge caution, GL-free (main thread: it reads the live chunk map): how far the ground lies under a
/// mob's feet in a column ahead of it, and whether stepping or hopping there is safe. A drop of more
/// than <see cref="WorldGenerationConfig.MobLedgeMaxDropBlocks"/> is refused unless the mob's target
/// stands below it, so a mob that is not chasing anything down never walks off a cliff that its fall
/// damage would punish. The probe reads the live cells (player edits and caves included), not the
/// generated heightmap; an unloaded cell or lava under the edge counts as a bottomless drop.
/// </summary>
public static class LedgeProbe
{
    /// <summary>
    /// Whole blocks the first ground in column (<paramref name="x"/>, <paramref name="z"/>) lies under
    /// <paramref name="feetY"/>: 0 when the cell the feet would rest on is ground, then one per empty
    /// cell under it. Water counts as ground (it breaks a fall); an unloaded cell, a burning fluid or no
    /// ground within <paramref name="maxScan"/> cells answers <c>maxScan + 1</c>.
    /// </summary>
    public static int DropBelow(ChunkManager chunks, float x, float feetY, float z, int maxScan)
    {
        var cx = MathUtils.FloorToInt(x);
        var cz = MathUtils.FloorToInt(z);
        var start = MathUtils.FloorToInt(feetY - 0.01f);
        for (var y = start; y >= start - maxScan; y--)
        {
            if (!chunks.TryGetCell(cx, y, cz, out var id, out _)) return maxScan + 1;
            if (BlockType.IsFluid(id)) return BlockType.Emission[id] > 0 ? maxScan + 1 : start - y;
            if (BlockType.IsSolid(id)) return start - y;
        }
        return maxScan + 1;
    }

    /// <summary>Whether a target whose feet are at <paramref name="targetFeetY"/> stands below a mob whose
    /// feet are at <paramref name="feetY"/>: lower by more than a ledge's worth.</summary>
    public static bool TargetBelow(float feetY, float targetFeetY) =>
        targetFeetY < feetY - WorldGenerationConfig.MobLedgeMaxDropBlocks;

    /// <summary>Whether a step onto ground <paramref name="drop"/> blocks down is taken: a short drop
    /// always, a longer one only when the target is below.</summary>
    public static bool IsSafe(int drop, bool targetBelow) =>
        targetBelow || drop <= WorldGenerationConfig.MobLedgeMaxDropBlocks;

    /// <summary>
    /// Whether moving a body (centre <paramref name="centre"/>, feet <paramref name="feetY"/>) along the
    /// horizontal <paramref name="direction"/> to <paramref name="distance"/> blocks from its centre would
    /// step off a ledge it should not: the drop there is not safe and the target (its eye
    /// <paramref name="targetEye"/>, or none) is not below.
    /// </summary>
    public static bool IsLedgeAhead(ChunkManager chunks, Vector3 centre, float feetY, Vector3 direction, float distance,
        Vector3? targetEye)
    {
        direction.Y = 0f;
        if (direction.LengthSquared() < 1e-8f) return false;
        var probe = centre + Vector3.Normalize(direction) * distance;
        var maxScan = (int)WorldGenerationConfig.MobLedgeMaxDropBlocks + 1;
        var drop = DropBelow(chunks, probe.X, feetY, probe.Z, maxScan);
        var below = targetEye is { } eye && TargetBelow(feetY, eye.Y - Camera.EyeHeight);
        return !IsSafe(drop, below);
    }
}
