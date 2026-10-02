using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// The one definition of how <see cref="BlockRenderer.TreeWood"/> cells join and what shape
/// they take. A cell's thickness and parent direction live in its state byte
/// (<see cref="WoodState"/>); two face-adjacent cells are connected when they are the same
/// wood block, neither is detached (player-placed) and one's parent points at the other. The
/// rule is graph-only: wood never welds to leaves, terrain, another species or a sibling limb
/// that merely touches it.
///
/// A full-thickness (or detached) cell is a plain cube. A thinner cell is a centred node box
/// <see cref="HalfWidth"/> wide plus one tube per connected neighbour, running from the node
/// face out to the cell face at the thinner of the two cells' widths, so the neighbour's tube
/// meets it flush. The mesher (<see cref="Chunk"/>), the raycast and selection outline
/// (<see cref="Camera"/>, <see cref="World"/>) and body collision all read these boxes, so what
/// is drawn and what is hit can never disagree. Direction indices follow
/// <see cref="Chunk.CubeFaces"/> order: -Z, +Z, -X, +X, +Y, -Y.
/// </summary>
public static class TreeWoodShape
{
    // Shared result for a cell with no registered type, so a lookup never allocates
    private static readonly BlockHitbox[] FullCubeBoxes = [BlockHitbox.FullCube];

    /// <summary>Half the edge length of a node of thickness code <paramref name="thickness"/>,
    /// in blocks: <c>(n + 1)</c> pixels across, so a full cube is 0.5.</summary>
    public static float HalfWidth(int thickness) => (thickness + 1) / 32f;

    /// <summary>The direction opposite <paramref name="dir"/>: <see cref="Chunk.CubeFaces"/>
    /// lists each axis as an adjacent pair, so flipping the low bit swaps the pair.</summary>
    public static int Opposite(int dir) => dir ^ 1;

    /// <summary>
    /// Whether cell A connects to its neighbour B lying in direction <paramref name="dirAtoB"/>:
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

    /// <summary>Bit <c>i</c> set when the cell connects to its neighbour in direction
    /// <c>i</c>; the spans hold the six neighbour ids and states in CubeFaces order.</summary>
    public static int ConnectionMask(byte id, byte state, ReadOnlySpan<byte> nbIds, ReadOnlySpan<byte> nbStates)
    {
        var mask = 0;
        for (var dir = 0; dir < 6; dir++)
            if (Connects(id, state, nbIds[dir], nbStates[dir], dir))
                mask |= 1 << dir;
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

    /// <summary>The tube half width toward a connected neighbour: the thinner of the two.</summary>
    public static float TubeHalf(byte state, byte neighbourState) =>
        MathF.Min(HalfWidth(WoodState.Thickness(state)), HalfWidth(WoodState.Thickness(neighbourState)));

    /// <summary>
    /// Cell-local hitboxes matching the drawn shape. A full-thickness or detached cell, and
    /// anything that is not tree wood, returns its registered hitboxes without allocating; a
    /// thin cell returns its node box plus one tube box per connection.
    /// </summary>
    public static BlockHitbox[] GetBoxes(byte id, byte state, ReadOnlySpan<byte> nbIds, ReadOnlySpan<byte> nbStates)
    {
        var type = BlockType.ById[id];
        if (type == null) return FullCubeBoxes;
        if (!BlockType.IsTreeWood(id) || !IsThin(state)) return type.Hitboxes;

        var mask = ConnectionMask(id, state, nbIds, nbStates);
        var half = HalfWidth(WoodState.Thickness(state));
        var boxes = new BlockHitbox[1 + System.Numerics.BitOperations.PopCount((uint)mask)];
        NodeBox(half, out var nodeMin, out var nodeMax);
        boxes[0] = new BlockHitbox(nodeMin, nodeMax);
        var next = 1;
        for (var dir = 0; dir < 6; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            TubeBox(dir, half, TubeHalf(state, nbStates[dir]), out var min, out var max);
            boxes[next++] = new BlockHitbox(min, max);
        }

        return boxes;
    }

    /// <summary>
    /// <see cref="GetBoxes"/> for a world cell, reading its state and six neighbours from the
    /// chunk manager. A full cube reads nothing beyond its own state and allocates nothing.
    /// </summary>
    public static BlockHitbox[] GetBoxesAt(byte id, ChunkManager chunks, int x, int y, int z)
    {
        var type = BlockType.ById[id];
        if (type == null) return FullCubeBoxes;
        if (!BlockType.IsTreeWood(id)) return type.Hitboxes;

        var state = chunks.GetStateGlobal(x, y, z);
        if (!IsThin(state)) return type.Hitboxes;

        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        ReadNeighbours(chunks, x, y, z, ids, states);
        return GetBoxes(id, state, ids, states);
    }

    /// <summary>
    /// Whether a world-space body box touches the cell's drawn shape. Non-tree blocks and full
    /// cubes test their registered hitboxes (the mould slab path), thin wood its node and tube
    /// boxes, all without allocating. Touching a face exactly counts, matching the
    /// cell-granular collision test this refines.
    /// </summary>
    public static bool OverlapsShape(byte id, ChunkManager chunks, int x, int y, int z, Vector3 min, Vector3 max)
    {
        var cell = new Vector3(x, y, z);
        var type = BlockType.ById[id];
        var state = BlockType.IsTreeWood(id) ? chunks.GetStateGlobal(x, y, z) : (byte)0;
        if (type == null || !BlockType.IsTreeWood(id) || !IsThin(state))
        {
            foreach (var box in type?.Hitboxes ?? FullCubeBoxes)
                if (Touches(cell + box.Min, cell + box.Max, min, max))
                    return true;
            return false;
        }

        Span<byte> ids = stackalloc byte[6];
        Span<byte> states = stackalloc byte[6];
        ReadNeighbours(chunks, x, y, z, ids, states);
        var half = HalfWidth(WoodState.Thickness(state));
        NodeBox(half, out var nodeMin, out var nodeMax);
        if (Touches(cell + nodeMin, cell + nodeMax, min, max)) return true;

        var mask = ConnectionMask(id, state, ids, states);
        for (var dir = 0; dir < 6; dir++)
        {
            if ((mask & 1 << dir) == 0) continue;
            TubeBox(dir, half, TubeHalf(state, states[dir]), out var tubeMin, out var tubeMax);
            if (Touches(cell + tubeMin, cell + tubeMax, min, max)) return true;
        }

        return false;
    }

    // Six face neighbours in CubeFaces order, read through the manager (unloaded reads air)
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
