using System;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Player;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Content.Shapes;

/// <summary>
/// The one definition of how <see cref="BlockRenderer.TreeWood"/> cells join and what shape
/// they take. A cell's thickness and parent direction live in its state byte
/// (<see cref="WoodState"/>); two face-adjacent cells are connected when they are the same
/// wood block, neither is detached (player-placed) and one's parent points at the other. The
/// rule is graph-only: wood never welds to leaves, terrain, another species or a sibling limb
/// that merely touches it.
///
/// Three shapes exist. A thin cell is a centred node box <see cref="HalfWidth"/> wide plus one
/// tube per connected neighbor, running from the node face out to the cell face at the thinner
/// of the two cells' widths, so the neighbor's tube meets it flush; toward a solid occluding
/// neighbor that is not wood - the ground, a wall - it instead runs a same-width plug out to the
/// cell face (<see cref="PlugMask"/>), so a thin trunk or root rooted on the ground meets it
/// rather than floating the node clear of it. A full-thickness (or
/// detached) cell is a plain cube. A trunk shell of a <c>rim: "pixel"</c> species
/// (<see cref="BlockType.HasPixelRim"/>) is a cube with each exposed corner bevelled into a
/// pixel-stepped quarter round: the stair-stepped full-height <see cref="RimBoxes"/> of its
/// <see cref="RimCornerMask"/>, decided statelessly from its four edge neighbors. The mesher
/// (<see cref="Chunk"/>), the raycast and selection outline (<see cref="Camera"/>,
/// <see cref="World"/>) and body collision all read these boxes, so what is drawn and what is
/// hit can never disagree. The outline draws every rim box, so a bevelled shell shows the seams
/// between its boxes (accepted residue). Direction indices follow
/// <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
/// </summary>
public static class TreeWoodShape
{
    // Shared result for a cell with no registered type, so a lookup never allocates
    private static readonly BlockHitbox[] FullCubeBoxes = [BlockHitbox.FullCube];

    /// <summary>Pixels across one cell edge, the resolution the rim bevel is stepped at.</summary>
    private const int CellPx = WoodState.PixelsPerBlock;

    /// <summary>Upper bound on <see cref="RimBoxes"/>' count: each bevelled Z edge contributes
    /// at most one box per bevel row, plus the unbevelled middle.</summary>
    public const int MaxRimBoxes = 2 * WorldGenerationConfig.TreeRimBevelPx + 1;

    /// <summary>Rim corner bit of the (-X, -Z) corner; see <see cref="RimCornerMask"/>.</summary>
    public const int CornerMinXMinZ = 1 << 0;

    /// <summary>Rim corner bit of the (+X, -Z) corner.</summary>
    public const int CornerMaxXMinZ = 1 << 1;

    /// <summary>Rim corner bit of the (-X, +Z) corner.</summary>
    public const int CornerMinXMaxZ = 1 << 2;

    /// <summary>Rim corner bit of the (+X, +Z) corner.</summary>
    public const int CornerMaxXMaxZ = 1 << 3;

    /// <summary>Half the edge length of a node of thickness code <paramref name="thickness"/>,
    /// in blocks: <c>(n + 1)</c> pixels across, so a full cube is 0.5.</summary>
    public static float HalfWidth(int thickness) => (thickness + 1) / 32f;

    /// <summary>The direction opposite <paramref name="dir"/>: <see cref="Chunk.CubeFaces"/>
    /// lists each axis as an adjacent pair, so flipping the low bit swaps the pair.</summary>
    public static int Opposite(int dir) => dir ^ 1;

    /// <summary>
    /// Whether cell A connects to its neighbor B lying in direction <paramref name="dirAtoB"/>:
    /// both are the same tree-wood block, neither is detached, and A's parent is B or B's
    /// parent is A. Symmetric by construction: swapping the cells and flipping the direction
    /// asks the same two parent questions.
    /// </summary>
    public static bool Connects(byte idA, byte stateA, byte idB, byte stateB, int dirAtoB)
    {
        if (idA != idB || !BlockType.IsTreeWood(idA)) return false;
        if (WoodState.IsDetached(stateA) || WoodState.IsDetached(stateB)) return false;
        return (WoodState.HasParentCell(stateA) && WoodState.Parent(stateA) == dirAtoB)
               || (WoodState.HasParentCell(stateB) && WoodState.Parent(stateB) == Opposite(dirAtoB));
    }

    /// <summary>Bit <c>i</c> set when the cell connects to its neighbor in direction
    /// <c>i</c>; the spans hold the six neighbor ids and states in CubeFaces order.</summary>
    public static int ConnectionMask(byte id, byte state, ReadOnlySpan<byte> nbIds, ReadOnlySpan<byte> nbStates)
    {
        var mask = 0;
        for (var dir = 0; dir < 6; dir++)
            if (Connects(id, state, nbIds[dir], nbStates[dir], dir))
                mask |= 1 << dir;
        return mask;
    }

    /// <summary>
    /// Bit <c>i</c> set when a thin cell plugs into its neighbor in direction <c>i</c>: the
    /// neighbor is a solid occluding block that is not tree wood (the ground, a wall), so the
    /// node geometry runs out to the cell face there instead of ending in mid-air. The plug is
    /// the node's own width, so it continues the node face; a full-cube or detached cell never
    /// plugs, and a tree-wood neighbor is handled by <see cref="Connects"/> instead.
    /// </summary>
    public static int PlugMask(byte state, ReadOnlySpan<byte> nbIds)
    {
        if (!IsThin(state)) return 0;
        var mask = 0;
        for (var dir = 0; dir < 6; dir++)
        {
            var nb = nbIds[dir];
            if (nb != 0 && !BlockType.IsTreeWood(nb) && BlockType.Occludes(nb))
                mask |= 1 << dir;
        }

        return mask;
    }

    /// <summary>Whether a tree-wood cell draws the node-and-tubes shape rather than a cube:
    /// thinner than a full cube and attached to a tree.</summary>
    public static bool IsThin(byte state) => !WoodState.IsFullCube(state) && !WoodState.IsDetached(state);

    /// <summary>Cell-local extents of the centred node box of half width <paramref name="half"/>.</summary>
    public static void NodeBox(float half, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(0.5f - half);
        max = new Vector3(0.5f + half);
    }

    /// <summary>
    /// Cell-local extents of the tube toward <paramref name="dir"/>: along that axis from the
    /// node face (<c>0.5 ± nodeHalf</c>) out to the cell face (0 or 1), and
    /// <c>0.5 ± tubeHalf</c> on the other two axes.
    /// </summary>
    public static void TubeBox(int dir, float nodeHalf, float tubeHalf, out Vector3 min, out Vector3 max)
    {
        var axis = (dir >> 1) switch { 0 => 2, 1 => 0, _ => 1 }; // -Z/+Z, -X/+X, +Y/-Y
        // Positive end of the pair: +Z (1), +X (3), +Y (4)
        var positive = dir is 1 or 3 or 4;
        Span<float> lo = [0.5f - tubeHalf, 0.5f - tubeHalf, 0.5f - tubeHalf];
        Span<float> hi = [0.5f + tubeHalf, 0.5f + tubeHalf, 0.5f + tubeHalf];
        if (positive)
        {
            lo[axis] = 0.5f + nodeHalf;
            hi[axis] = 1f;
        }
        else
        {
            lo[axis] = 0f;
            hi[axis] = 0.5f - nodeHalf;
        }

        min = new Vector3(lo[0], lo[1], lo[2]);
        max = new Vector3(hi[0], hi[1], hi[2]);
    }

    /// <summary>The tube half width toward a connected neighbor: the thinner of the two.</summary>
    public static float TubeHalf(byte state, byte neighbourState) =>
        MathF.Min(HalfWidth(WoodState.Thickness(state)), HalfWidth(WoodState.Thickness(neighbourState)));

    /// <summary>Whether a neighbor counts as rim mass for <paramref name="id"/>'s bevel: the
    /// same wood, a full cube and attached to a tree. A detached log or a thin limb beside a
    /// shell leaves its edge exposed, so the shell rounds off there.</summary>
    public static bool IsRimMass(byte id, byte nbId, byte nbState) =>
        nbId == id && WoodState.IsFullCube(nbState) && !WoodState.IsDetached(nbState);

    /// <summary>Whether a cell bevels at all: an attached full-cube trunk shell of a pixel-rim
    /// species. Everything else keeps its plain shape and never reads its neighbors.</summary>
    public static bool CanBevel(byte id, byte state) =>
        BlockType.HasPixelRim(id) && WoodState.IsShell(state) && WoodState.IsFullCube(state)
        && !WoodState.IsDetached(state);

    /// <summary>
    /// The bevelled corners of a pixel-rim shell, as <see cref="CornerMinXMinZ"/>-style bits; 0
    /// for any cell that cannot bevel (<see cref="CanBevel"/>). The spans hold the six neighbor
    /// ids and states in CubeFaces order, of which only the four horizontal ones (0..3) are read.
    ///
    /// A corner is carved when neither of its two edge neighbors (the X-side and the Z-side one)
    /// is rim mass (<see cref="IsRimMass"/>). That is the marching-squares outcome for the
    /// trunk's outline: with the cell present, the 2x2 sample square around a corner has the
    /// convex-corner case exactly when both edge neighbors are absent, and the diagonal sample
    /// never changes it. A present diagonal with both edges absent is the saddle, which each cell
    /// resolves by carving only its own material, so the diagonal cell rounds its own corner and
    /// the two quarter rounds face each other. Because a cell only ever removes its own material,
    /// the diagonal never matters and no diagonal neighbor has to be read or remeshed.
    /// </summary>
    public static int RimCornerMask(byte id, byte state, ReadOnlySpan<byte> nbIds, ReadOnlySpan<byte> nbStates)
    {
        if (!CanBevel(id, state)) return 0;
        var minZ = IsRimMass(id, nbIds[0], nbStates[0]);
        var maxZ = IsRimMass(id, nbIds[1], nbStates[1]);
        var minX = IsRimMass(id, nbIds[2], nbStates[2]);
        var maxX = IsRimMass(id, nbIds[3], nbStates[3]);
        var mask = 0;
        if (!minX && !minZ) mask |= CornerMinXMinZ;
        if (!maxX && !minZ) mask |= CornerMaxXMinZ;
        if (!minX && !maxZ) mask |= CornerMinXMaxZ;
        if (!maxX && !maxZ) mask |= CornerMaxXMaxZ;
        return mask;
    }

    /// <summary>
    /// Whether a rim cell with corner mask <paramref name="mask"/> fills its whole face toward
    /// <paramref name="dirFromCellToViewer"/> (CubeFaces order). A top or bottom face is whole
    /// only with no corner carved; a side face is whole unless one of the two corners on that
    /// side is carved. This is the culling predicate a neighbor uses: a face that butts onto a
    /// whole rim face is hidden, one that butts onto a carved face is drawn.
    /// </summary>
    public static bool RimSideFull(int mask, int dirFromCellToViewer) => dirFromCellToViewer switch
    {
        0 => (mask & (CornerMinXMinZ | CornerMaxXMinZ)) == 0,
        1 => (mask & (CornerMinXMaxZ | CornerMaxXMaxZ)) == 0,
        2 => (mask & (CornerMinXMinZ | CornerMinXMaxZ)) == 0,
        3 => (mask & (CornerMaxXMinZ | CornerMaxXMaxZ)) == 0,
        _ => mask == 0,
    };

    /// <summary>
    /// How many pixels the bevel cuts into a carved corner's X extent at pixel row
    /// <paramref name="row"/>, counted from the corner's Z edge: the pixel-stepped quarter round
    /// of radius <see cref="WorldGenerationConfig.TreeRimBevelPx"/>, sampled at the row's
    /// centre. 0 from row r on, so a round never reaches past half the cell.
    /// </summary>
    public static int RimInsetPx(int row)
    {
        const int r = WorldGenerationConfig.TreeRimBevelPx;
        if (row < 0 || row >= r) return 0;
        var d = r - row - 0.5f;
        return (int)MathF.Round(r - MathF.Sqrt(r * r - d * d));
    }

    /// <summary>
    /// Fills <paramref name="dst"/> with the full-height boxes of a rim cell with corner mask
    /// <paramref name="mask"/> and returns how many it wrote (at most <see cref="MaxRimBoxes"/>).
    /// Each of the 16 Z pixel rows spans from the deepest -X corner inset reaching it to 16 minus
    /// the deepest +X one; consecutive equal rows merge into one box, in increasing Z. Mask 0 is
    /// one full cube.
    /// </summary>
    public static int RimBoxes(int mask, Span<BlockHitbox> dst)
    {
        var count = 0;
        var runStart = 0;
        RowSpan(mask, 0, out var runLo, out var runHi);
        for (var z = 1; z <= CellPx; z++)
        {
            int lo = 0, hi = 0;
            if (z < CellPx) RowSpan(mask, z, out lo, out hi);
            if (z < CellPx && lo == runLo && hi == runHi) continue;
            dst[count++] = new BlockHitbox(
                new Vector3(runLo / (float)CellPx, 0f, runStart / (float)CellPx),
                new Vector3(runHi / (float)CellPx, 1f, z / (float)CellPx));
            runStart = z;
            runLo = lo;
            runHi = hi;
        }

        return count;
    }

    // One Z pixel row's covered X pixel span [lo, hi) under the corner mask
    private static void RowSpan(int mask, int z, out int lo, out int hi)
    {
        var fromMinZ = RimInsetPx(z);
        var fromMaxZ = RimInsetPx(CellPx - 1 - z);
        lo = Math.Max((mask & CornerMinXMinZ) != 0 ? fromMinZ : 0, (mask & CornerMinXMaxZ) != 0 ? fromMaxZ : 0);
        hi = CellPx - Math.Max((mask & CornerMaxXMinZ) != 0 ? fromMinZ : 0, (mask & CornerMaxXMaxZ) != 0 ? fromMaxZ : 0);
    }

    /// <summary>
    /// Cell-local hitboxes matching the drawn shape. A full-thickness or detached cell, and
    /// anything that is not tree wood, returns its registered hitboxes without allocating; a
    /// thin cell returns its node box plus one tube box per connection; a bevelled pixel-rim
    /// shell returns its <see cref="RimBoxes"/>.
    /// </summary>
    public static BlockHitbox[] GetBoxes(byte id, byte state, ReadOnlySpan<byte> nbIds, ReadOnlySpan<byte> nbStates)
    {
        var type = BlockType.ById[id];
        if (type == null) return FullCubeBoxes;
        if (!BlockType.IsTreeWood(id)) return type.Hitboxes;
        if (!IsThin(state))
        {
            var rim = RimCornerMask(id, state, nbIds, nbStates);
            if (rim == 0) return type.Hitboxes;
            Span<BlockHitbox> scratch = stackalloc BlockHitbox[MaxRimBoxes];
            return scratch[..RimBoxes(rim, scratch)].ToArray();
        }

        var mask = ConnectionMask(id, state, nbIds, nbStates);
        var plugs = PlugMask(state, nbIds);
        var half = HalfWidth(WoodState.Thickness(state));
        var boxes = new BlockHitbox[1 + System.Numerics.BitOperations.PopCount((uint)(mask | plugs))];
        NodeBox(half, out var nodeMin, out var nodeMax);
        boxes[0] = new BlockHitbox(nodeMin, nodeMax);
        var next = 1;
        for (var dir = 0; dir < 6; dir++)
        {
            var connected = (mask & 1 << dir) != 0;
            if (!connected && (plugs & 1 << dir) == 0) continue;
            var tubeHalf = connected ? TubeHalf(state, nbStates[dir]) : half;
            TubeBox(dir, half, tubeHalf, out var min, out var max);
            boxes[next++] = new BlockHitbox(min, max);
        }

        return boxes;
    }

    /// <summary>
    /// <see cref="GetBoxes"/> for a world cell, reading its state and six neighbors from the
    /// chunk manager. A full cube that cannot bevel reads nothing beyond its own state and
    /// allocates nothing.
    /// </summary>
    public static BlockHitbox[] GetBoxesAt(byte id, ChunkManager chunks, int x, int y, int z)
    {
        var type = BlockType.ById[id];
        if (type == null) return FullCubeBoxes;
        if (!BlockType.IsTreeWood(id)) return type.Hitboxes;

        var state = chunks.GetStateGlobal(x, y, z);
        if (!IsThin(state) && !CanBevel(id, state)) return type.Hitboxes;

        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        ReadNeighbours(chunks, x, y, z, ids, states);
        return GetBoxes(id, state, ids, states);
    }

    /// <summary>
    /// Whether a world-space body box touches the cell's drawn shape. Non-tree blocks test their
    /// live boxes (<see cref="BlockShape.BoxesAt"/>: the mould slab's registered ones, a pane's
    /// arms), full tree-wood cubes their registered hitboxes, thin wood its node and tube
    /// boxes and a bevelled pixel-rim shell its rim boxes, all without allocating. Only thin
    /// wood and bevel-capable shells read their neighbors. Touching a face exactly counts,
    /// matching the cell-granular collision test this refines.
    /// </summary>
    public static bool OverlapsShape(byte id, ChunkManager chunks, int x, int y, int z, Vector3 min, Vector3 max)
    {
        var cell = new Vector3(x, y, z);
        var type = BlockType.ById[id];
        var state = BlockType.IsTreeWood(id) ? chunks.GetStateGlobal(x, y, z) : (byte)0;
        var bevels = type != null && BlockType.IsTreeWood(id) && CanBevel(id, state);
        if (type == null || !BlockType.IsTreeWood(id) || (!IsThin(state) && !bevels))
        {
            // Any other shape-colliding block's live boxes (a pane's arms follow its neighbors)
            var boxes = type == null ? FullCubeBoxes
                : BlockType.IsTreeWood(id) ? type.Hitboxes
                : BlockShape.BoxesAt(id, chunks, x, y, z);
            foreach (var box in boxes)
                if (Touches(cell + box.Min, cell + box.Max, min, max))
                    return true;
            return false;
        }

        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        ReadNeighbours(chunks, x, y, z, ids, states);
        if (bevels)
        {
            Span<BlockHitbox> rimBoxes = stackalloc BlockHitbox[MaxRimBoxes];
            var count = RimBoxes(RimCornerMask(id, state, ids, states), rimBoxes);
            for (var i = 0; i < count; i++)
                if (Touches(cell + rimBoxes[i].Min, cell + rimBoxes[i].Max, min, max))
                    return true;
            return false;
        }

        var half = HalfWidth(WoodState.Thickness(state));
        NodeBox(half, out var nodeMin, out var nodeMax);
        if (Touches(cell + nodeMin, cell + nodeMax, min, max)) return true;

        var mask = ConnectionMask(id, state, ids, states);
        var plugs = PlugMask(state, ids);
        for (var dir = 0; dir < 6; dir++)
        {
            var connected = (mask & 1 << dir) != 0;
            if (!connected && (plugs & 1 << dir) == 0) continue;
            var tubeHalf = connected ? TubeHalf(state, states[dir]) : half;
            TubeBox(dir, half, tubeHalf, out var tubeMin, out var tubeMax);
            if (Touches(cell + tubeMin, cell + tubeMax, min, max)) return true;
        }

        return false;
    }

    // Six face neighbors in CubeFaces order, read through the manager (unloaded reads air)
    private static void ReadNeighbours(ChunkManager chunks, int x, int y, int z, Span<byte> ids, Span<byte> states)
    {
        for (var dir = 0; dir < 6; dir++)
        {
            var n = Chunk.CubeFaces[dir].normal;
            var nx = x + (int)n.X;
            var ny = y + (int)n.Y;
            var nz = z + (int)n.Z;
            ids[dir] = chunks.GetBlockGlobal(nx, ny, nz);
            states[dir] = ids[dir] == 0 ? (byte)0 : chunks.GetStateGlobal(nx, ny, nz);
        }
    }

    private static bool Touches(Vector3 bMin, Vector3 bMax, Vector3 min, Vector3 max) =>
        bMin.X <= max.X && bMax.X >= min.X
        && bMin.Y <= max.Y && bMax.Y >= min.Y
        && bMin.Z <= max.Z && bMax.Z >= min.Z;
}
