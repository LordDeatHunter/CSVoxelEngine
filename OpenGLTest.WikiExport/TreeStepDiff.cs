using System.Collections.Generic;
using VoxelEngine.WorldGen.Trees.Shape;

namespace VoxelEngine.WikiExport;

/// <summary>
/// What changed in a grown tree's cells from one growth step to the next. A step is not just the
/// previous one plus new cells: wood thickens bottom-up, trunk shells appear and leaves come and go
/// as tips stop being tips, so a step carries the cells to write and the cells to remove.
/// </summary>
public static class TreeStepDiff
{
    /// <summary>One cell as the wiki draws it: wood or leaf, plus its state byte.</summary>
    public readonly record struct Cell(bool IsLeaf, byte State);

    /// <summary>The cells to write (new or changed) and the cells to remove, as flat int arrays.</summary>
    /// <param name="Set">x, y, z, isLeaf (0 or 1), state, repeated.</param>
    /// <param name="Del">x, y, z, repeated.</param>
    public readonly record struct Step(int[] Set, int[] Del);

    /// <summary>The cells keyed by position, the later of two cells at one position winning.</summary>
    public static Dictionary<(int X, int Y, int Z), Cell> ToMap(IReadOnlyList<TreeCell> cells, byte leavesId)
    {
        var map = new Dictionary<(int X, int Y, int Z), Cell>(cells.Count);
        foreach (var c in cells)
            map[(c.X, c.Y, c.Z)] = new Cell(leavesId != 0 && c.Block == leavesId, c.State);
        return map;
    }

    /// <summary>The step that turns <paramref name="previous"/> into <paramref name="current"/>.</summary>
    public static Step Between(Dictionary<(int X, int Y, int Z), Cell> previous, Dictionary<(int X, int Y, int Z), Cell> current)
    {
        var set = new List<int>();
        foreach (var (p, cell) in current)
        {
            if (previous.TryGetValue(p, out var before) && before == cell) continue;
            set.AddRange([p.X, p.Y, p.Z, cell.IsLeaf ? 1 : 0, cell.State]);
        }

        var del = new List<int>();
        foreach (var p in previous.Keys)
        {
            if (current.ContainsKey(p)) continue;
            del.AddRange([p.X, p.Y, p.Z]);
        }
        return new Step([.. set], [.. del]);
    }

    /// <summary>Applies a step to a cell map in place (what the wiki viewer does as the slider moves).</summary>
    public static void Apply(Dictionary<(int X, int Y, int Z), Cell> map, Step step)
    {
        for (var i = 0; i < step.Del.Length; i += 3)
            map.Remove((step.Del[i], step.Del[i + 1], step.Del[i + 2]));
        for (var i = 0; i < step.Set.Length; i += 5)
            map[(step.Set[i], step.Set[i + 1], step.Set[i + 2])] = new Cell(step.Set[i + 3] == 1, (byte)step.Set[i + 4]);
    }
}
