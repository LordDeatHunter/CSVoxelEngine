using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Block-entity registry for campfires: lit full branch piles with a burn timer.
/// A max-count branch pile is the unlit firepit; igniting it adds an entry here (the
/// "fire" toggle) without touching block ids or the state byte, so meshing is unaffected.
/// Main thread only, like the other managers.
/// </summary>
public sealed class CampfireManager
{
    public const float BurnSeconds = 600f;
    public const int AshPerExtinguish = 4;

    private readonly Dictionary<(int X, int Y, int Z), float> _lit = new();
    private readonly List<(int X, int Y, int Z)> _scratch = new();
    private readonly List<(int X, int Y, int Z)> _stale = new();
    private readonly List<Vector3> _cells = new();

    public int LitCount => _lit.Count;

    public IReadOnlyList<Vector3> LitCells => _cells;

    public bool IsLit(int x, int y, int z) => _lit.ContainsKey((x, y, z));

    public bool Ignite(int x, int y, int z)
    {
        var key = (x, y, z);
        if (_lit.ContainsKey(key)) return false;
        _lit[key] = BurnSeconds;
        RefreshCells();
        return true;
    }

    public void NotifyBlockRemoved(int x, int y, int z)
    {
        if (_lit.Remove((x, y, z)))
            RefreshCells();
    }

    public void Update(float deltaTime, ChunkManager chunks, DropManager drops)
    {
        if (deltaTime <= 0f || _lit.Count == 0) return;
        _scratch.Clear();
        _stale.Clear();
        foreach (var (key, remaining) in _lit)
        {
            if (chunks.ChunkAt(key.X, key.Y, key.Z) == null) continue;
            if (!chunks.TryGetCell(key.X, key.Y, key.Z, out var id, out _)
                || BlockType.ById[id]?.Renderer != BlockRenderer.BranchPile)
            {
                _stale.Add(key);
                continue;
            }
            var left = remaining - deltaTime;
            if (left <= 0f)
                _scratch.Add(key);
            else
                _lit[key] = left;
        }
        foreach (var key in _stale)
            _lit.Remove(key);
        foreach (var key in _scratch)
            Extinguish(key.X, key.Y, key.Z, chunks, drops);
        if (_scratch.Count > 0 || _stale.Count > 0)
            RefreshCells();
    }

    private void Extinguish(int x, int y, int z, ChunkManager chunks, DropManager drops)
    {
        _lit.Remove((x, y, z));
        if (!chunks.TryGetCell(x, y, z, out var id, out var state)) return;
        var pile = BlockType.ById[id];
        if (pile == null || pile.Renderer != BlockRenderer.BranchPile) return;
        var count = BranchPileGenerator.CountWithState(state, pile.StackMax);
        var chunk = chunks.ChunkAt(x, y, z);
        if (chunk == null) return;
        var cell = new Vector3(x, y, z);
        if (count > 1)
            chunk.SetStateAt(cell, (byte)(count - 1), chunks);
        else
            chunk.RemoveBlockAt(cell, chunks);
        try
        {
            var ash = ContentRegistry.RequireItem("ash").Id;
            for (var i = 0; i < AshPerExtinguish; i++)
                drops.Spawn(ash, cell);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshCells()
    {
        _cells.Clear();
        foreach (var (x, y, z) in _lit.Keys)
            _cells.Add(new Vector3(x, y, z));
    }
}
