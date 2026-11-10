using System;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Content.Structures;

/// <summary>What a template stands for: which placement stamps it.</summary>
public enum StructureKind
{
    Surface,
    Underground,
    Hell,
    Sky,

    /// <summary>Dresses one floor room of a battle tower (BT-2); never a ruin.</summary>
    TowerFloor,

    /// <summary>Dresses a battle tower's roof around the altar cell (BT-2); never a ruin.</summary>
    TowerAltar
}

/// <summary>
/// One compiled structure template: a square footprint, a height and the four quarter turns of its cells, built once at load.
/// A cell is <see cref="StructureCell.None"/> (write nothing) or a <see cref="StructureCell"/> packing a block and its state;
/// the index is <c>(y * size + z) * size + x</c>.
/// </summary>
public sealed class StructureTemplate
{
    /// <summary>Engine facing after one clockwise quarter turn seen from above: north to east, south to west, west to north, east to south.</summary>
    public static readonly byte[] TurnedFacing = [3, 2, 0, 1];

    private readonly int[][] _turns;

    internal StructureTemplate(string name, StructureKind kind, bool satellite, float weight, int size, int height,
        int groundLayer, Biome[]? biomes, int[] cells, bool[] facingCells)
    {
        Name = name;
        Kind = kind;
        Satellite = satellite;
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

    /// <summary>Whether it is a waystone satellite, stamped beside a ruin of its kind and never picked as a ruin.</summary>
    public bool Satellite { get; }

    /// <summary>Half the footprint edge: the farthest a cell lies from the center column.</summary>
    public int Half => Size / 2;

    /// <summary>Relative pick weight among the templates of its kind and role (ruin or satellite).</summary>
    public float Weight { get; }

    /// <summary>Edge length of the square footprint in cells.</summary>
    public int Size { get; }

    /// <summary>Layers high.</summary>
    public int Height { get; }

    /// <summary>The layer standing on the anchor's floor level.</summary>
    public int GroundLayer { get; }

    /// <summary>The surface biomes it may stand in, or null for every one.</summary>
    public Biome[]? Biomes { get; }

    /// <summary>The cell at (x, y, z) after <paramref name="turns"/> clockwise quarter turns: <see cref="StructureCell.None"/>
    /// or a packed <see cref="StructureCell"/>.</summary>
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
                var state = StructureCell.StateOf(cell);
                cell = StructureCell.Pack(StructureCell.BlockOf(cell), (byte)((state & ~3) | TurnedFacing[state & 3]));
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
