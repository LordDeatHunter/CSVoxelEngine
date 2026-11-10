using System;

namespace VoxelEngine.Content.Stations;

/// <summary>
/// How one kind of bloomery behaves, compiled from its anchor block's <c>bloomery</c> object
/// (<see cref="BlockType.Bloomery"/>): how many units of ore and of fuel its shaft holds. The heat model every
/// bloomery shares lives in <see cref="Config.BloomeryConfig"/>. A bloomery's model is the mud brick kiln's, so
/// where its fire burns is <see cref="KilnLayout.Kiln"/>'s.
/// </summary>
/// <param name="OreCap">Units of crushed ore the shaft holds at once.</param>
/// <param name="FuelCap">Units of fuel the shaft holds at once.</param>
/// <param name="OreId">The one item the shaft smelts (the <c>ore</c> field).</param>
/// <param name="BloomId">The item a well-smelted unit becomes (<c>bloom</c>).</param>
/// <param name="SlagId">The item a burnt unit leaves (<c>slag</c>).</param>
public sealed record BloomeryProfile(int OreCap, int FuelCap, ushort OreId, ushort BloomId, ushort SlagId)
{
    /// <summary>Ore cap of a bloomery object that names none.</summary>
    public const int DefaultOreCap = 4;

    /// <summary>Fuel cap of a bloomery object that names none.</summary>
    public const int DefaultFuelCap = 4;

    /// <summary>Largest legal cap of either.</summary>
    public const int MaxCap = 64;

    /// <summary>A profile that smelts nothing, for a block id that is not (or no longer) a bloomery.</summary>
    public static readonly BloomeryProfile Default = new(DefaultOreCap, DefaultFuelCap, 0, 0, 0);

    /// <summary>A validated profile: both caps in 1..<see cref="MaxCap"/>, anything else throws an
    /// <see cref="ArgumentException"/> saying which.</summary>
    public static BloomeryProfile Create(int oreCap, int fuelCap, ushort oreId, ushort bloomId, ushort slagId)
    {
        if (oreCap is < 1 or > MaxCap)
            throw new ArgumentException($"oreCap {oreCap}; a bloomery holds 1..{MaxCap} ore units.");
        if (fuelCap is < 1 or > MaxCap)
            throw new ArgumentException($"fuelCap {fuelCap}; a bloomery holds 1..{MaxCap} fuel units.");
        return new BloomeryProfile(oreCap, fuelCap, oreId, bloomId, slagId);
    }
}

/// <summary>
/// How one kind of anvil behaves, compiled from its block's <c>anvil</c> object (<see cref="BlockType.Anvil"/>):
/// how many hammer hits turn the bloom lying on it into wrought iron. The pace of the hits is the hammer's own
/// <c>tool.hitDelayMs</c>.
/// </summary>
/// <param name="Hits">Hits a bloom takes.</param>
/// <param name="InputId">The one item laid on the anvil (<c>input</c>).</param>
/// <param name="ResultId">What the hits make of it (<c>result</c>).</param>
/// <param name="SlagId">What the hits squeeze out (<c>slag</c>).</param>
public sealed record AnvilProfile(int Hits, ushort InputId, ushort ResultId, ushort SlagId)
{
    /// <summary>Hits of an anvil object that names none.</summary>
    public const int DefaultHits = 3;

    /// <summary>Largest legal hit count.</summary>
    public const int MaxHits = 32;

    /// <summary>A profile that works nothing, for a block id that is not (or no longer) an anvil.</summary>
    public static readonly AnvilProfile Default = new(DefaultHits, 0, 0, 0);

    /// <summary>A validated profile: <paramref name="hits"/> in 1..<see cref="MaxHits"/>, anything else throws an
    /// <see cref="ArgumentException"/>.</summary>
    public static AnvilProfile Create(int hits, ushort inputId, ushort resultId, ushort slagId)
    {
        if (hits is < 1 or > MaxHits)
            throw new ArgumentException($"hits {hits}; a bloom takes 1..{MaxHits} hits.");
        return new AnvilProfile(hits, inputId, resultId, slagId);
    }
}
