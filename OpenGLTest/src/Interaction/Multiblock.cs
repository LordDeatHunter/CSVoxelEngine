using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Chunks;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free structure rules for a multiblock: how its 2x2x2 volume is written into the chunks,
/// how any of its cells finds the anchor again, and how the whole thing goes back to the source
/// block it was built from.
///
/// <para>
/// A multiblock is an <b>anchor cell plus seven part cells</b>, all real blocks in the chunk
/// storage. The per-block state byte carries the structure: <c>facing | dx&lt;&lt;2 |
/// dy&lt;&lt;3 | dz&lt;&lt;4</c> on a part (the offset from the anchor, which is always the
/// volume's minimum corner) and just <c>facing</c> on the anchor — so the facing lives in the low
/// two bits of every cell of the structure, which is exactly what the mesher reads when it picks
/// the anchor's pre-rotated model (see <see cref="MultiblockShape"/>). One state read therefore
/// resolves any cell to its anchor.
/// </para>
/// <para>
/// <b>Callers remesh afterwards.</b> <see cref="Convert"/> and <see cref="Revert"/> write through
/// <see cref="Chunk.SetBlockRaw"/> and only collect the chunk positions they touched: the caller
/// runs <see cref="Chunk.RemeshAfterBatchEdit"/> on each (the asynchronous
/// <see cref="ChunkManager.MarkAreaDirty"/> rebuild that a felled tree and a TNT crater already
/// use, so the far cells refresh a frame or two later) and
/// <see cref="ChunkManager.ImmediateRemeshWithBorders"/> on the clicked or mined cell's chunk so
/// the click frame already shows the change — never a loop of eight immediate remeshes.
/// </para>
/// </summary>
public static class Multiblock
{
    /// <summary>Edge length of the volume in cells; the geometry owns the definition.</summary>
    public const int Size = MultiblockShape.Size;

    /// <summary>
    /// The eight cells of the volume as offsets from the anchor, the anchor's own (0, 0, 0)
    /// first. Every structure write and every integrity check walks this array, so the anchor is
    /// always handled before its parts.
    /// </summary>
    public static readonly (int X, int Y, int Z)[] Offsets =
    [
        (0, 0, 0),
        (1, 0, 0),
        (0, 1, 0),
        (1, 1, 0),
        (0, 0, 1),
        (1, 0, 1),
        (0, 1, 1),
        (1, 1, 1),
    ];

    /// <summary>Packs a cell's facing (0..3) and its offset from the anchor into the state byte.
    /// The anchor's own state is just its facing, since its offset is (0, 0, 0).</summary>
    public static byte EncodeState(int facing, int dx, int dy, int dz) =>
        (byte)(facing | (dx << 2) | (dy << 3) | (dz << 4));

    /// <summary>Unpacks what <see cref="EncodeState"/> wrote.</summary>
    public static void DecodeState(byte state, out int facing, out int dx, out int dy, out int dz)
    {
        facing = state & 3;
        dx = (state >> 2) & 1;
        dy = (state >> 3) & 1;
        dz = (state >> 4) & 1;
    }

    /// <summary>Whether a block id belongs to a multiblock structure, anchor or part.</summary>
    public static bool IsMultiblockCell(byte id) =>
        BlockType.ById[id]?.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart;

    /// <summary>
    /// Resolves the anchor of the structure a cell belongs to. An anchor cell resolves to
    /// itself; a part subtracts the offset its state carries and then has to find a real anchor
    /// there — one that owns this very part id and agrees on the facing — so an orphaned part
    /// left behind by a blast reports false instead of pointing at a stranger.
    /// </summary>
    public static bool TryResolveAnchor(ChunkManager chunks, int x, int y, int z,
        out Vector3 anchor, out int facing, out BlockType? anchorType)
    {
        anchor = Vector3.Zero;
        facing = 0;
        anchorType = null;

        if (!chunks.TryGetCell(x, y, z, out var id, out var state)) return false;
        var type = BlockType.ById[id];
        if (type == null) return false;

        if (type.Renderer == BlockRenderer.Multiblock)
        {
            anchor = new Vector3(x, y, z);
            facing = state & 3;
            anchorType = type;
            return true;
        }

        if (type.Renderer != BlockRenderer.MultiblockPart) return false;

        DecodeState(state, out var partFacing, out var dx, out var dy, out var dz);
        var ax = x - dx;
        var ay = y - dy;
        var az = z - dz;
        if (!chunks.TryGetCell(ax, ay, az, out var candidateId, out var candidateState)) return false;

        var candidate = BlockType.ById[candidateId];
        if (candidate == null
            || candidate.Renderer != BlockRenderer.Multiblock
            || candidate.MultiblockPartId != id
            || (candidateState & 3) != partFacing)
            return false;

        anchor = new Vector3(ax, ay, az);
        facing = partFacing;
        anchorType = candidate;
        return true;
    }

    /// <summary>
    /// Whether all eight cells still hold exactly what <see cref="Convert"/> wrote.
    /// <paramref name="anyUnloaded"/> tells a broken structure apart from one that merely has a
    /// cell in an unloaded chunk: a sweep must freeze on the second, not revert a structure it
    /// cannot see.
    /// </summary>
    public static bool IsIntact(ChunkManager chunks, Vector3 anchor, int facing, BlockType anchorType,
        out bool anyUnloaded)
    {
        anyUnloaded = false;
        var intact = true;
        var (ax, ay, az) = VoxelCoords.BlockCellOf(anchor);

        foreach (var (dx, dy, dz) in Offsets)
        {
            if (!chunks.TryGetCell(ax + dx, ay + dy, az + dz, out var id, out var state))
            {
                anyUnloaded = true;
                intact = false;
                continue;
            }

            var expectedId = dx == 0 && dy == 0 && dz == 0 ? anchorType.Id : anchorType.MultiblockPartId;
            if (id != expectedId || state != EncodeState(facing, dx, dy, dz)) intact = false;
        }

        return intact;
    }

    /// <summary>
    /// Writes the structure over its eight cells: the anchor block at the minimum corner and a
    /// part in each of the other seven, every cell carrying the facing. Cells in unloaded chunks
    /// are skipped (the caller checked the volume was whole before asking). Touched chunk
    /// positions are collected for the caller's remesh; see the type remark.
    /// </summary>
    public static void Convert(ChunkManager chunks, Vector3 anchor, int facing, BlockType anchorType,
        HashSet<Vector3> touchedChunks)
    {
        var (ax, ay, az) = VoxelCoords.BlockCellOf(anchor);

        foreach (var (dx, dy, dz) in Offsets)
        {
            var x = ax + dx;
            var y = ay + dy;
            var z = az + dz;
            var chunkPos = VoxelCoords.ChunkPosOf(x, y, z);
            if (!chunks.Chunks.TryGetValue(chunkPos, out var chunk)) continue;

            var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, z);
            var id = dx == 0 && dy == 0 && dz == 0 ? anchorType.Id : anchorType.MultiblockPartId;
            chunk.SetBlockRaw(lx, ly, lz, id, EncodeState(facing, dx, dy, dz));
            touchedChunks.Add(chunkPos);
        }
    }

    /// <summary>
    /// Turns the structure back into the block it was built from: every cell that still holds
    /// the anchor id or its part id becomes the source block with a clean state byte, except
    /// <paramref name="clearedCell"/> (the mined one), which is cleared and unmarked instead —
    /// its loot is the caller's business.
    ///
    /// <para>
    /// Cells holding anything else are left alone, air included, so this is safe to call when a
    /// blast already took the anchor cell out: that is how <c>KilnManager</c>'s integrity sweep
    /// cleans up a structure whose anchor is gone and whose parts would otherwise stay as
    /// invisible solid blocks.
    /// </para>
    /// </summary>
    public static void Revert(ChunkManager chunks, Vector3 anchor, BlockType anchorType,
        Vector3? clearedCell, HashSet<Vector3> touchedChunks)
    {
        var (ax, ay, az) = VoxelCoords.BlockCellOf(anchor);
        var cleared = clearedCell.HasValue ? VoxelCoords.BlockCellOf(clearedCell.Value) : ((int X, int Y, int Z)?)null;

        foreach (var (dx, dy, dz) in Offsets)
        {
            var x = ax + dx;
            var y = ay + dy;
            var z = az + dz;
            var chunkPos = VoxelCoords.ChunkPosOf(x, y, z);
            if (!chunks.Chunks.TryGetValue(chunkPos, out var chunk)) continue;

            var (lx, ly, lz) = VoxelCoords.LocalOf(x, y, z);
            var existing = chunk.GetLocalBlock(lx, ly, lz);
            if (existing != anchorType.Id && existing != anchorType.MultiblockPartId) continue;

            if (cleared is { } mined && mined.X == x && mined.Y == y && mined.Z == z)
            {
                chunk.ClearBlockAt(new Vector3(x, y, z));
                chunks.UnmarkPlayerPlaced(x, y, z);
            }
            else
            {
                chunk.SetBlockRaw(lx, ly, lz, anchorType.MultiblockSourceId, 0);
            }

            touchedChunks.Add(chunkPos);
        }
    }
}
