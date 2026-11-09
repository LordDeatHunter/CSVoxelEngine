using System;
using System.Collections.Generic;
using VoxelEngine.Chunks;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen.Structures;

namespace VoxelEngine.Simulation.Tower;

/// <summary>
/// The staged collapse of one defeated battle tower into its ruin (BT-8): every cell of the tower's box is checked against a
/// ruin rule (<see cref="TowerRuin.RuinAt"/> for a real tower) and rewritten through the <see cref="TowerBlockWriter"/>, layer by
/// layer from the top down, under the writer's per-frame cap and a per-frame scan budget. A cell whose chunk is unloaded puts its
/// chunk on a waiting list and the pass moves on; once the main pass has reached the bottom, each waiting chunk is swept (top
/// down) as soon as it loads. The collapse is done when the main pass is finished and nothing waits, and done is permanent.
/// Re-scanning is idempotent (a cell no longer holding its expected block reads gone), so only the layer cursor, the waiting
/// chunks and the done flag are saved. Main thread only, authoritative worlds only.
/// </summary>
public sealed class TowerCollapse
{
    /// <summary>The ruin of the cell at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>): -1 to keep it, else
    /// <c>block | state &lt;&lt; 8</c>, with <paramref name="expected"/> the block the cell must still hold to be rewritten.</summary>
    public delegate int CellRule(int x, int y, int z, out byte expected);

    private readonly int _minX, _maxX, _bottomY, _topY, _minZ, _maxZ;
    private readonly int _width;
    private readonly int _layerSize;
    private readonly CellRule _rule;

    private int _index;
    private (int X, int Y, int Z)? _active;
    private int _activeIndex;

    /// <summary>A collapse of the box <paramref name="minX"/>..<paramref name="maxX"/>, <paramref name="bottomY"/>..<paramref name="topY"/>,
    /// <paramref name="minZ"/>..<paramref name="maxZ"/> by <paramref name="rule"/>, starting after <paramref name="delaySeconds"/>.</summary>
    public TowerCollapse(int minX, int maxX, int bottomY, int topY, int minZ, int maxZ, CellRule rule, float delaySeconds)
    {
        _minX = minX;
        _maxX = maxX;
        _bottomY = bottomY;
        _topY = topY;
        _minZ = minZ;
        _maxZ = maxZ;
        _width = maxX - minX + 1;
        _layerSize = _width * (maxZ - minZ + 1);
        _rule = rule;
        NextY = topY;
        DelayLeft = delaySeconds;
    }

    /// <summary>The collapse of <paramref name="site"/> into its <see cref="TowerRuin"/>.</summary>
    public static TowerCollapse ForSite(TowerSite site, float delaySeconds) =>
        new(site.MinX, site.MaxX, site.BottomY, site.TopY, site.MinZ, site.MaxZ,
            (int x, int y, int z, out byte expected) =>
            {
                var ruin = TowerRuin.RuinAt(site, x, y, z, out var original);
                expected = (byte)Math.Max(original, 0);
                return ruin;
            }, delaySeconds);

    /// <summary>The main pass's next layer (world y); below the box's bottom once the main pass is finished.</summary>
    public int NextY { get; private set; }

    /// <summary>Chunks whose cells were unloaded when the main pass reached them, still to sweep once they load.</summary>
    public HashSet<(int X, int Y, int Z)> Waiting { get; } = [];

    /// <summary>Seconds left before the collapse starts.</summary>
    public float DelayLeft { get; private set; }

    /// <summary>Whether the collapse has finished; permanent.</summary>
    public bool Done { get; private set; }

    /// <summary>Whether the main top-down pass has reached the bottom of the box.</summary>
    public bool MainPassDone => NextY < _bottomY;

    /// <summary>
    /// Advances the collapse by one frame of <paramref name="dt"/> seconds, checking at most <paramref name="scanBudget"/> cells
    /// (decremented by every cell checked). A cell <paramref name="occupied"/> reports a body in is never filled with a solid
    /// block. Returns false only when the writer reported its frame cap, so the caller writes nothing more this frame.
    /// </summary>
    internal bool Step(float dt, TowerBlockWriter writer, ChunkManager chunks, ref int scanBudget,
        Func<int, int, int, bool>? occupied)
    {
        if (Done) return true;
        if (DelayLeft > 0)
        {
            DelayLeft = Math.Max(0, DelayLeft - dt);
            return true;
        }

        while (!MainPassDone && scanBudget > 0)
        {
            var x = _minX + _index % _width;
            var z = _minZ + _index / _width;
            var chunk = (VoxelCoords.WorldToChunk(x), VoxelCoords.WorldToChunk(NextY), VoxelCoords.WorldToChunk(z));
            // Every cell costs budget, a waiting chunk's too, so a pass over an unloaded tower never hitches a frame
            scanBudget--;
            if (!Waiting.Contains(chunk))
            {
                var result = Visit(x, NextY, z, writer, occupied);
                if (result == TowerWriteResult.Capped) return false;
                if (result == TowerWriteResult.Unloaded) Waiting.Add(chunk);
            }
            AdvanceMain();
        }

        if (MainPassDone && !SweepWaiting(writer, chunks, ref scanBudget, occupied)) return false;
        Done = MainPassDone && Waiting.Count == 0;
        return true;
    }

    // Rewrites one cell by the rule; a kept or skipped cell reads as written
    private TowerWriteResult Visit(int x, int y, int z, TowerBlockWriter writer, Func<int, int, int, bool>? occupied)
    {
        var target = _rule(x, y, z, out var expected);
        if (target < 0) return TowerWriteResult.Written;
        var block = (byte)target;
        if (BlockType.IsSolid(block) && !BlockType.IsSolid(expected) && occupied?.Invoke(x, y, z) == true)
            return TowerWriteResult.Written;
        return writer.TryReplace(x, y, z, expected, block, (byte)(target >> 8));
    }

    private void AdvanceMain()
    {
        if (++_index < _layerSize) return;
        _index = 0;
        NextY--;
    }

    // Sweeps loaded waiting chunks top-down; false when the writer is capped
    private bool SweepWaiting(TowerBlockWriter writer, ChunkManager chunks, ref int scanBudget, Func<int, int, int, bool>? occupied)
    {
        while (scanBudget > 0)
        {
            if (_active is not { } c)
            {
                if (!TryPickLoaded(chunks)) return true;
                continue;
            }

            var x0 = Math.Max(_minX, c.X * Chunk.Size);
            var x1 = Math.Min(_maxX, c.X * Chunk.Size + Chunk.Size - 1);
            var y0 = Math.Max(_bottomY, c.Y * Chunk.Size);
            var y1 = Math.Min(_topY, c.Y * Chunk.Size + Chunk.Size - 1);
            var z0 = Math.Max(_minZ, c.Z * Chunk.Size);
            var z1 = Math.Min(_maxZ, c.Z * Chunk.Size + Chunk.Size - 1);
            int w = x1 - x0 + 1, d = z1 - z0 + 1, h = y1 - y0 + 1;
            if (w <= 0 || d <= 0 || h <= 0 || _activeIndex >= w * d * h)
            {
                Waiting.Remove(c);
                _active = null;
                continue;
            }

            var layer = _activeIndex / (w * d);
            var inLayer = _activeIndex % (w * d);
            scanBudget--;
            var result = Visit(x0 + inLayer % w, y1 - layer, z0 + inLayer / w, writer, occupied);
            if (result == TowerWriteResult.Capped) return false;
            if (result == TowerWriteResult.Unloaded) _active = null;
            else _activeIndex++;
        }
        return true;
    }

    // Picks a waiting chunk that is loaded now; an unloaded one costs no scan budget
    private bool TryPickLoaded(ChunkManager chunks)
    {
        foreach (var c in Waiting)
        {
            if (!chunks.TryGetCell(c.X * Chunk.Size, c.Y * Chunk.Size, c.Z * Chunk.Size, out _, out _)) continue;
            _active = c;
            _activeIndex = 0;
            return true;
        }
        return false;
    }

    /// <summary>The collapse's saved progress.</summary>
    internal StationSaveData.TowerCollapseData Capture()
    {
        List<int[]>? waiting = null;
        foreach (var c in Waiting) (waiting ??= []).Add([c.X, c.Y, c.Z]);
        return new StationSaveData.TowerCollapseData { NextY = NextY, Waiting = waiting, Done = Done };
    }

    /// <summary>Restores saved progress: a finished collapse stays finished; otherwise the main pass resumes at the saved layer
    /// (its start, since the position inside a layer is never saved) and the waiting chunks inside the box come back. A restored
    /// collapse starts at once.</summary>
    internal void Apply(StationSaveData.TowerCollapseData data)
    {
        DelayLeft = 0;
        _active = null;
        _index = 0;
        Waiting.Clear();
        if (data.Done)
        {
            Done = true;
            NextY = _bottomY - 1;
            return;
        }
        NextY = Math.Clamp(data.NextY ?? _topY, _bottomY - 1, _topY);
        if (data.Waiting == null) return;
        foreach (var entry in data.Waiting)
        {
            if (entry is not { Length: 3 } || !InChunkRange(entry[0], entry[1], entry[2])) continue;
            Waiting.Add((entry[0], entry[1], entry[2]));
        }
    }

    private bool InChunkRange(int cx, int cy, int cz) =>
        cx >= VoxelCoords.WorldToChunk(_minX) && cx <= VoxelCoords.WorldToChunk(_maxX)
        && cy >= VoxelCoords.WorldToChunk(_bottomY) && cy <= VoxelCoords.WorldToChunk(_topY)
        && cz >= VoxelCoords.WorldToChunk(_minZ) && cz <= VoxelCoords.WorldToChunk(_maxZ);
}
