using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Parent/child connection rules and shape-accurate hitboxes for tapered wood.
///
/// Worldgen grows every limb as a strict taper: each cell is exactly one level thinner
/// than the cell it grew from (<see cref="WorldGen.TreeGenerator"/>), so a real
/// parent/child edge always steps the taper level by exactly one (vertical trunk stacks
/// are the exception: repeated log pours and multi-block upper-trunk bands stack the
/// same level). Sibling limbs or neighbouring trees that merely touch side by side are
/// usually at the same level or two apart, which is what used to weld them into
/// "circular" loops: the mesher joined every adjacent wood cell regardless of level.
///
/// Both the mesher (<see cref="Chunk"/>) and the targeting/selection code
/// (<see cref="Camera"/>, <see cref="World"/>) read these rules, so the drawn tubes and
/// the boxes the raycast hits can never disagree about which neighbours are joined.
/// Neighbour arrays follow <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
/// </summary>
public static class WoodShape
{
    /// <summary>Any wood block: the full-cube log (level 0) through the twig (level 5).</summary>
    public static bool IsWood(byte blockId) => BlockType.ById[blockId]?.WoodTaperLevel >= 0;

    /// <summary>Taper level of a wood block, or -1 for anything else (leaves included).</summary>
    public static int WoodLevel(byte blockId) => BlockType.ById[blockId]?.WoodTaperLevel ?? -1;

    /// <summary>Whether the block draws the tapered node-and-tubes mesh (levels 1..5).
    /// The log is a plain full cube and keeps its full-cube hitbox.</summary>
    public static bool IsTapered(byte blockId)
    {
        var type = BlockType.ById[blockId];
        return type != null && type.WoodTaperLevel >= 1 && type.Renderer == BlockRenderer.TaperedWood;
    }

    /// <summary>Whether tapered wood may grow into this block without a parent/child edge:
    /// a member of the <c>tree_branch_connectable</c> tag (leaves), never terrain or rock.</summary>
    public static bool IsBranchConnectable(byte blockId) => BlockType.IsBranchConnectable(blockId);

    /// <summary>
    /// Whether two vertically adjacent cells belong to the same wood run or a branch grows
    /// into the tag: stacked trunk cells share a level, every other vertical wood step
    /// thins by one, and any wood joined to a <see cref="IsBranchConnectable"/> block above
    /// or below extends a tube into it.
    /// </summary>
    public static bool ConnectsVertical(byte a, byte b)
    {
        if (!IsWood(a)) return false;
        if (IsBranchConnectable(b)) return true;
        if (!IsWood(b)) return false;
        return Math.Abs(WoodLevel(a) - WoodLevel(b)) <= 1;
    }

    /// <summary>
    /// Whether two horizontally adjacent cells join: a limb step always thins by exactly
    /// one, so same-level or wider-apart touches are foreign wood (a sibling limb, another
    /// tree) and must not weld — unless the neighbour is <see cref="IsBranchConnectable"/>,
    /// which always takes an arm.
    /// </summary>
    public static bool ConnectsHorizontal(byte a, byte b)
    {
        if (!IsWood(a)) return false;
        if (IsBranchConnectable(b)) return true;
        if (!IsWood(b)) return false;
        return Math.Abs(WoodLevel(a) - WoodLevel(b)) == 1;
    }

    /// <summary>
    /// Whether a tapered segment grows a foot tube down onto the cell below: only onto
    /// <see cref="IsBranchConnectable"/> blocks (leaves). Terrain, rock and foreign wood
    /// get nothing — a branch over grass floats on its node instead of welding to it.
    /// Mirrors the down-tube condition in the mesher.
    /// </summary>
    public static bool HasFoot(byte belowId) => IsBranchConnectable(belowId);

    /// <summary>
    /// Hitboxes matching the emitted mesh: one central column spanning only the drawn
    /// vertical extent (full height only while a tube continues up/down, otherwise just
    /// the node), plus one arm box per connected horizontal neighbour reaching the
    /// cell face exactly like the drawn arm. Non-tapered blocks fall back to their
    /// registered hitboxes.
    /// </summary>
    /// <param name="neighbours">Six neighbour ids in CubeFaces order (-Z, +Z, -X, +X, +Y, -Y).</param>
    public static BlockHitbox[] GetBoxes(byte blockId, ReadOnlySpan<byte> neighbours)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return [BlockHitbox.FullCube];
        if (!IsTapered(blockId)) return type.Hitboxes;

        var w = Math.Clamp(type.HalfWidth, 0f, 0.5f);
        const float c = 0.5f;

        var hasUp = neighbours.Length > 4 && ConnectsVertical(blockId, neighbours[4]);
        var belowWood = neighbours.Length > 5 && IsWood(neighbours[5]);
        var hasDown = (neighbours.Length > 5 && belowWood && ConnectsVertical(blockId, neighbours[5]))
                      || (neighbours.Length > 5 && HasFoot(neighbours[5]));

        var y0 = hasDown ? 0f : c - w;
        var y1 = hasUp ? 1f : c + w;
        var lo = c - w;
        var hi = c + w;

        // Count arms first so the result is exactly sized.
        var arms = 0;
        for (var i = 0; i < 4 && i < neighbours.Length; i++)
            if (ConnectsHorizontal(blockId, neighbours[i])) arms++;

        var boxes = new BlockHitbox[1 + arms];
        boxes[0] = new BlockHitbox(new Vector3(lo, y0, lo), new Vector3(hi, y1, hi));

        // Arm boxes copy EmitArm's extents: from the node wall out to the cell face,
        // riding the node's [lo, hi] band in Y.
        var out_ = 1;
        for (var i = 0; i < 4 && i < neighbours.Length; i++)
        {
            if (!ConnectsHorizontal(blockId, neighbours[i])) continue;
            GetArmBox(i, lo, hi, out var min, out var max);
            boxes[out_++] = new BlockHitbox(min, max);
        }

        return boxes;
    }

    /// <summary>
    /// Reads the six face neighbours of a world cell and returns its shape-accurate
    /// hitboxes. For use sites that only have a block getter (raycast, selection).
    /// </summary>
    public static BlockHitbox[] GetBoxesAt(byte blockId, Func<int, int, int, byte> getBlock, int x, int y, int z)
    {
        if (!IsTapered(blockId))
            return BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];

        Span<byte> nb = stackalloc byte[6];
        nb[0] = getBlock(x, y, z - 1);
        nb[1] = getBlock(x, y, z + 1);
        nb[2] = getBlock(x - 1, y, z);
        nb[3] = getBlock(x + 1, y, z);
        nb[4] = getBlock(x, y + 1, z);
        nb[5] = getBlock(x, y - 1, z);
        return GetBoxes(blockId, nb);
    }

    /// <summary>
    /// Whether a world-space body box touches the block's drawn shape: the
    /// shape-accurate boxes for tapered wood, the registered hitboxes otherwise.
    /// Touching a face exactly counts, matching the cell-granular collision test this
    /// replaces for full cubes (a floored body bound always includes the touched cell).
    /// </summary>
    public static bool OverlapsShape(byte blockId, Func<int, int, int, byte> getBlock,
        int x, int y, int z, Vector3 min, Vector3 max)
    {
        var boxes = IsTapered(blockId)
            ? GetBoxesAt(blockId, getBlock, x, y, z)
            : BlockType.ById[blockId]?.Hitboxes ?? [BlockHitbox.FullCube];
        var cell = new Vector3(x, y, z);
        foreach (var box in boxes)
        {
            var bMin = cell + box.Min;
            var bMax = cell + box.Max;
            if (bMin.X <= max.X && bMax.X >= min.X
                && bMin.Y <= max.Y && bMax.Y >= min.Y
                && bMin.Z <= max.Z && bMax.Z >= min.Z)
                return true;
        }
        return false;
    }

    /// <summary>Cell-local extents of one horizontal arm: from the node wall out to
    /// the cell face, riding the node's [lo, hi] band in Y (mirrors EmitArm).</summary>
    internal static void GetArmBox(int dir, float lo, float hi, out Vector3 min, out Vector3 max)
    {
        // dir follows CubeFaces order: 0 = -Z, 1 = +Z, 2 = -X, 3 = +X.
        min = dir switch
        {
            0 => new Vector3(lo, lo, 0f),
            1 => new Vector3(lo, lo, hi),
            2 => new Vector3(0f, lo, lo),
            _ => new Vector3(hi, lo, lo),
        };
        max = dir switch
        {
            0 => new Vector3(hi, hi, lo),
            1 => new Vector3(hi, hi, 1f),
            2 => new Vector3(lo, hi, hi),
            _ => new Vector3(1f, hi, hi),
        };
    }
}
