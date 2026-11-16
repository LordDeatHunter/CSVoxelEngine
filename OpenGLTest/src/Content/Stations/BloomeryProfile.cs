using System;
using System.Numerics;
using VoxelEngine.Content.Shapes;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// How one kind of bloomery behaves, compiled from its anchor block's <c>bloomery</c> object
/// (<see cref="BlockType.Bloomery"/>): how many units of ore and of fuel its shaft holds and where its bellows go. The
/// heat model every bloomery shares lives in <see cref="Config.BloomeryConfig"/>. A bloomery's model is the mud brick
/// kiln's, so where its fire burns is <see cref="KilnLayout.Kiln"/>'s.
/// </summary>
/// <param name="OreCap">Units of crushed ore the shaft holds at once.</param>
/// <param name="FuelCap">Units of fuel the shaft holds at once.</param>
/// <param name="OreId">The one item the shaft smelts (the <c>ore</c> field).</param>
/// <param name="BloomId">The item a smelted unit becomes (<c>bloom</c>).</param>
/// <param name="Tuyeres">The cells just outside the volume, in its authored (north-facing) local space, where a bellows
/// works the bloomery (<c>tuyeres</c>).</param>
public sealed record BloomeryProfile(int OreCap, int FuelCap, ushort OreId, ushort BloomId,
    (int X, int Y, int Z)[] Tuyeres)
{
    /// <summary>Ore cap of a bloomery object that names none.</summary>
    public const int DefaultOreCap = 4;

    /// <summary>Fuel cap of a bloomery object that names none.</summary>
    public const int DefaultFuelCap = 4;

    /// <summary>Largest legal cap of either.</summary>
    public const int MaxCap = 64;

    /// <summary>A profile that smelts nothing, for a block id that is not (or no longer) a bloomery.</summary>
    public static readonly BloomeryProfile Default = new(DefaultOreCap, DefaultFuelCap, 0, 0, []);

    /// <summary>A validated profile: both caps in 1..<see cref="MaxCap"/> and at least one tuyere, each of which
    /// must lie outside a <paramref name="size"/>-cell volume but touch one of its side faces; anything else throws an
    /// <see cref="ArgumentException"/> saying which.</summary>
    public static BloomeryProfile Create(int oreCap, int fuelCap, ushort oreId, ushort bloomId,
        (int X, int Y, int Z)[] tuyeres, int size)
    {
        if (oreCap is < 1 or > MaxCap)
            throw new ArgumentException($"oreCap {oreCap}; a bloomery holds 1..{MaxCap} ore units.");
        if (fuelCap is < 1 or > MaxCap)
            throw new ArgumentException($"fuelCap {fuelCap}; a bloomery holds 1..{MaxCap} fuel units.");
        if (tuyeres.Length == 0)
            throw new ArgumentException("no tuyeres; a bloomery needs at least one cell where a bellows works it.");
        foreach (var t in tuyeres)
            if (!TouchesSide(t, size))
                throw new ArgumentException(
                    $"tuyere [{t.X}, {t.Y}, {t.Z}], which is not a cell against a side face of the {size}-cell volume.");
        return new BloomeryProfile(oreCap, fuelCap, oreId, bloomId, tuyeres);
    }

    /// <summary>
    /// Whether a bellows at <paramref name="offset"/> from the anchor works a bloomery of this profile turned to
    /// <paramref name="facing"/>: the offset is one of the tuyeres turned the way the structure is
    /// (<see cref="MultiblockShape.RotatePoint"/>, through the cell's center so it lands on a whole cell again).
    /// </summary>
    public bool IsTuyere((int X, int Y, int Z) offset, int facing, int size)
    {
        foreach (var t in Tuyeres)
        {
            var turned = MultiblockShape.RotatePoint(new Vector3(t.X + 0.5f, t.Y + 0.5f, t.Z + 0.5f), facing, size);
            if (MathUtils.FloorToInt(turned.X) == offset.X && MathUtils.FloorToInt(turned.Y) == offset.Y
                && MathUtils.FloorToInt(turned.Z) == offset.Z)
                return true;
        }
        return false;
    }

    // A cell just outside one side face of the volume: one step past it on exactly one horizontal axis, within it on
    // the other two
    private static bool TouchesSide((int X, int Y, int Z) t, int size)
    {
        bool Inside(int v) => v >= 0 && v < size;
        bool Beside(int v) => v == -1 || v == size;
        return Inside(t.Y) && ((Beside(t.X) && Inside(t.Z)) || (Inside(t.X) && Beside(t.Z)));
    }
}
