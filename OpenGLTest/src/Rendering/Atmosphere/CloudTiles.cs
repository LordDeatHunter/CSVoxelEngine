using System;
using System.Numerics;
using VoxelEngine.Data;
using VoxelEngine.Rendering.Geometry;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Rendering.Atmosphere;

/// <summary>
/// The GL-free half of the cloud layer's tile cull: the static field mesh cut into square tiles of
/// cells, each tile's box from the vertices its triangles really reference, and the per-pass list of
/// indirect draw commands that draws, copy by copy, only the tiles that may rasterize.
///
/// <c>CloudField.BuildMesh</c> emits cells x-major, so cell (x, z) owns one contiguous index run
/// and a column x of a tile row tz is one contiguous run too. <see cref="Gather"/> walks the copies
/// in their slot order and, inside each, the columns and tile rows in ascending index order, and
/// merges adjacent runs, so the commands are an order-preserving subsequence of the old instanced
/// draw (instance-major, ascending index): every surviving triangle is drawn with the same vertices,
/// the same instance offset (<see cref="DrawElementsIndirectCommand.BaseInstance"/> is the copy's
/// slot) and in the same relative order, which is what keeps depth ties and the blended color sweep
/// pixel-identical. When every tile of a copy is in view, the copy is one command over the whole
/// index list, exactly the old draw of that instance.
///
/// A tile is dropped only when <see cref="MayRasterize"/> rejects it: its vertex box, grown by the
/// margin, lies wholly outside one frustum plane. The boxes are built from referenced vertices, not
/// from cell footprints, because fillets and riser patches reach across cell borders. The margin
/// covers GL's z_clip &gt;= -w near clip, which <see cref="Frustum"/> (the Direct3D z_clip &gt;= 0
/// plane) does not model; see <see cref="SkyConfig.CloudTileCullMarginBlocks"/>.
///
/// What is NOT covered: the GL draw itself — the command upload, the
/// <c>glMultiDrawElementsIndirect</c> of both sweeps and their state — which only runs in the game.
/// </summary>
internal sealed class CloudTiles
{
    private readonly int _cells;
    private readonly int _tileCells;
    private readonly float _margin;

    // Per (column x, tile row tz), indexed x * TilesPerSide + tz: where the run starts and how long it is.
    private readonly uint[] _runStart;
    private readonly uint[] _runCount;

    // Per tile (tx, tz), indexed tx * TilesPerSide + tz: the unmargined box of the vertices its runs
    // reference, in mesh-local space, and whether it has any triangles at all.
    private readonly Vector3[] _tileMin;
    private readonly Vector3[] _tileMax;
    private readonly bool[] _tileEmpty;

    /// <summary>Tiles along one side of the field.</summary>
    internal int TilesPerSide { get; }

    /// <summary>
    /// The most commands <see cref="Gather"/> can write for a full ring of copies: one per column and
    /// tile row of every copy. A destination of this length is always long enough.
    /// </summary>
    internal int MaxCommands => CloudCopies.MaxCopies * _cells * TilesPerSide;

    /// <param name="vertices">The field mesh's vertices, mesh-local in x and z.</param>
    /// <param name="indices">The field mesh's triangle list.</param>
    /// <param name="cellIndexStart">Where each cell's index run starts (x-major), plus the total, from
    /// <c>CloudField.BuildMesh</c>.</param>
    /// <param name="cells">Cells per side of the square field.</param>
    /// <param name="tileCells">Tile side, in cells.</param>
    /// <param name="margin">How far each tile box is grown before the frustum test.</param>
    internal CloudTiles(CloudVertex[] vertices, uint[] indices, int[] cellIndexStart, int cells, int tileCells,
        float margin)
    {
        if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
        if (tileCells < 1) throw new ArgumentOutOfRangeException(nameof(tileCells));
        if (cellIndexStart.Length != cells * cells + 1)
            throw new ArgumentException(
                $"Expected {cells * cells + 1} cell index starts, got {cellIndexStart.Length}.", nameof(cellIndexStart));

        _cells = cells;
        _tileCells = tileCells;
        _margin = margin;
        TilesPerSide = (cells + tileCells - 1) / tileCells;

        var tps = TilesPerSide;
        _runStart = new uint[cells * tps];
        _runCount = new uint[cells * tps];
        _tileMin = new Vector3[tps * tps];
        _tileMax = new Vector3[tps * tps];
        _tileEmpty = new bool[tps * tps];
        Array.Fill(_tileMin, new Vector3(float.PositiveInfinity));
        Array.Fill(_tileMax, new Vector3(float.NegativeInfinity));
        Array.Fill(_tileEmpty, true);

        for (var x = 0; x < cells; x++)
        for (var tz = 0; tz < tps; tz++)
        {
            var start = cellIndexStart[x * cells + tz * tileCells];
            var end = cellIndexStart[x * cells + Math.Min((tz + 1) * tileCells, cells)];
            var run = x * tps + tz;
            _runStart[run] = (uint)start;
            _runCount[run] = (uint)(end - start);
            if (end == start) continue;

            var tile = x / tileCells * tps + tz;
            _tileEmpty[tile] = false;
            for (var i = start; i < end; i++)
            {
                var p = vertices[indices[i]].Position;
                _tileMin[tile] = Vector3.Min(_tileMin[tile], p);
                _tileMax[tile] = Vector3.Max(_tileMax[tile], p);
            }
        }
    }

    /// <summary>
    /// The one cull rule: a box may rasterize unless, grown by <paramref name="margin"/> on every
    /// side, it lies wholly outside a frustum plane.
    /// </summary>
    internal static bool MayRasterize(in Frustum frustum, Vector3 min, Vector3 max, float margin) =>
        frustum.IntersectsAABB(min - new Vector3(margin), max + new Vector3(margin));

    /// <summary>
    /// Writes the indirect commands that draw every copy in <paramref name="copyOffsets"/> minus the
    /// tiles that cannot rasterize, in copy-slot order and ascending index order inside a copy, with
    /// adjacent runs merged, and returns how many there are. Allocation-free.
    /// </summary>
    /// <param name="frustum">The pass's own frustum (main view or mirror).</param>
    /// <param name="copyOffsets">The surviving copies' world (x, z) origins; slot i is instance i.</param>
    /// <param name="visibleScratch">Scratch of at least <see cref="TilesPerSide"/>² entries.</param>
    /// <param name="dest">Destination, at least copies * cells * <see cref="TilesPerSide"/> long.</param>
    /// <exception cref="ArgumentException">The destination or the scratch is too short.</exception>
    internal int Gather(in Frustum frustum, ReadOnlySpan<Vector2> copyOffsets, Span<bool> visibleScratch,
        Span<DrawElementsIndirectCommand> dest)
    {
        var tps = TilesPerSide;
        if (dest.Length < copyOffsets.Length * _cells * tps)
            throw new ArgumentException(
                $"Destination must hold at least {copyOffsets.Length * _cells * tps} commands, got {dest.Length}.",
                nameof(dest));
        if (visibleScratch.Length < tps * tps)
            throw new ArgumentException(
                $"Scratch must hold at least {tps * tps} tiles, got {visibleScratch.Length}.", nameof(visibleScratch));

        var count = 0;
        for (var c = 0; c < copyOffsets.Length; c++)
        {
            var offset = new Vector3(copyOffsets[c].X, 0f, copyOffsets[c].Y);
            if (!MarkVisibleTiles(frustum, offset, visibleScratch))
                continue;

            for (var x = 0; x < _cells; x++)
            {
                var tileRow = x / _tileCells * tps;
                for (var tz = 0; tz < tps; tz++)
                {
                    if (!visibleScratch[tileRow + tz])
                        continue;

                    var run = x * tps + tz;
                    var length = _runCount[run];
                    if (length == 0)
                        continue;

                    count = Append(dest, count, _runStart[run], length, (uint)c);
                }
            }
        }

        return count;
    }

    // Fills the scratch with each tile's verdict for one copy; false when no tile of it is visible.
    private bool MarkVisibleTiles(in Frustum frustum, Vector3 offset, Span<bool> visible)
    {
        var any = false;
        for (var t = 0; t < _tileEmpty.Length; t++)
        {
            visible[t] = !_tileEmpty[t] && MayRasterize(frustum, _tileMin[t] + offset, _tileMax[t] + offset, _margin);
            any |= visible[t];
        }

        return any;
    }

    // Appends one run, merged into the previous command when it continues it in the same copy.
    private static int Append(Span<DrawElementsIndirectCommand> dest, int count, uint start, uint length,
        uint copy)
    {
        if (count > 0)
        {
            ref var last = ref dest[count - 1];
            if (last.BaseInstance == copy && last.FirstIndex + last.Count == start)
            {
                last.Count += length;
                return count;
            }
        }

        dest[count] = new DrawElementsIndirectCommand
        {
            Count = length,
            InstanceCount = 1,
            FirstIndex = start,
            BaseVertex = 0,
            BaseInstance = copy,
        };
        return count + 1;
    }
}
