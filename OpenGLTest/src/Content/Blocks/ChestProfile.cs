using System;
using System.Collections.Generic;
using System.Linq;

namespace VoxelEngine.Content.Blocks;

/// <summary>
/// What makes a block a chest, from its <c>chest</c> JSON object: how many slots it holds and, for a ruin chest, the
/// loot table it rolls the first time it is opened or broken (see <c>ChestManager</c>). A crafted chest names no table
/// and starts empty. A tiered chest (the tower chest) names one table per tier instead, and rolls the one its cell's
/// state tier picks (<see cref="TableFor"/>): worldgen stamps the tier into state bits 2-7, bits 0-1 being the chest's
/// facing.
/// </summary>
/// <param name="Slots">How many stacks it holds.</param>
/// <param name="LootTable">The loot table file name a never-opened chest rolls, or null.</param>
/// <param name="TierTables">The loot table per tier of a tiered chest (the last entry used for any higher tier), or
/// null.</param>
public sealed record ChestProfile(int Slots, string? LootTable, IReadOnlyList<string>? TierTables = null)
{
    /// <summary>The first state bit of a chest's tier: bits 0-1 are a facing chest's facing.</summary>
    public const int TierShift = 2;

    /// <summary>How many tiers the six state bits above the facing hold.</summary>
    public const int MaxTiers = 64;

    /// <summary>The state bits that carry <paramref name="tier"/> (clamped to 0..<see cref="MaxTiers"/> - 1), to OR
    /// onto a facing.</summary>
    public static byte TierState(int tier) => (byte)(Math.Clamp(tier, 0, MaxTiers - 1) << TierShift);

    /// <summary>The tier a chest cell's state byte carries.</summary>
    public static int TierOf(byte state) => state >> TierShift;

    /// <summary>The loot table a never-opened chest with this cell state rolls: a tiered chest's table for the state's
    /// tier (clamped to its last entry), else the plain <see cref="LootTable"/>.</summary>
    public string? TableFor(byte state) =>
        TierTables is { Count: > 0 } tiers ? tiers[Math.Min(TierOf(state), tiers.Count - 1)] : LootTable;

    /// <summary>Every loot table this chest can roll, for content checks.</summary>
    public IEnumerable<string> Tables
    {
        get
        {
            if (LootTable != null) yield return LootTable;
            if (TierTables == null) yield break;
            foreach (var table in TierTables.Distinct()) yield return table;
        }
    }
}
