using System;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Content.Structures;

/// <summary>What a template stands for: which placement stamps it.</summary>
public enum StructureKind
{
    Surface,
    Underground,
    Hell,
    Sky
}

/// <summary>
/// One compiled structure template: a square footprint, a height and the four quarter turns of its cells, built once at load.
/// A cell is -1 (write nothing) or <c>block | state &lt;&lt; 8</c>; the index is <c>(y * size + z) * size + x</c>.
/// </summary>
public sealed class StructureTemplate
{
    /// <summary>Engine facing after one clockwise quarter turn seen from above: north to east, south to west, west to north, east to south.</summary>
    public static readonly byte[] TurnedFacing = [3, 2, 0, 1];

    private readonly int[][] _turns;

    internal StructureTemplate(string name, StructureKind kind, float weight, int size, int height, int groundLayer,
        Biome[]? biomes, int[] cells, bool[] facingCells)
    {
        Name = name;
        Kind = kind;
        Weight = weight;
        Size = size;
        Height = height;
        GroundLayer = groundLayer;
        Biomes = biomes;
        _turns = new int[4][];
        _turns[0] = cells;
        var current = cells;
        var facing = facingCells;
        for (var t = 1; t < 4; t++)
        {
            current = Turn(current, facing, size, height);
            facing = TurnFlags(facing, size, height);
            _turns[t] = current;
        }
    }

    /// <summary>The template's file name.</summary>
    public string Name { get; }

    /// <summary>Which placement stamps it.</summary>
    public StructureKind Kind { get; }

    /// <summary>Relative pick weight among its kind.</summary>
    public float Weight { get; }

    /// <summary>Edge length of the square footprint in cells.</summary>
    public int Size { get; }

    /// <summary>Layers high.</summary>
    public int Height { get; }

    /// <summary>The layer standing on the anchor's floor level.</summary>
    public int GroundLayer { get; }

    /// <summary>The surface biomes it may stand in, or null for every one.</summary>
    public Biome[]? Biomes { get; }

    /// <summary>The cell at (x, y, z) after <paramref name="turns"/> clockwise quarter turns: -1 or block | state &lt;&lt; 8.</summary>
    public int CellAt(int turns, int x, int y, int z) => _turns[turns & 3][(y * Size + z) * Size + x];

    // A clockwise quarter turn seen from above: (x, z) goes to (size - 1 - z, x), and a facing cell's facing turns with it
    private static int[] Turn(int[] cells, bool[] facing, int size, int height)
    {
        var result = new int[cells.Length];
        for (var y = 0; y < height; y++)
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
        {
            var from = (y * size + z) * size + x;
            var cell = cells[from];
            if (cell >= 0 && facing[from])
            {
                var state = (cell >> 8) & 0xFF;
                cell = (cell & 0xFF) | (((state & ~3) | TurnedFacing[state & 3]) << 8);
            }
            result[(y * size + x) * size + (size - 1 - z)] = cell;
        }
        return result;
    }

    private static bool[] TurnFlags(bool[] flags, int size, int height)
    {
        var result = new bool[flags.Length];
        for (var y = 0; y < height; y++)
        for (var z = 0; z < size; z++)
        for (var x = 0; x < size; x++)
            result[(y * size + x) * size + (size - 1 - z)] = flags[(y * size + z) * size + x];
        return result;
    }
}
