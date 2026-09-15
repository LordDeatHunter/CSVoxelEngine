using System;
using System.Collections.Generic;

namespace silkgltriangle;

/// <summary>
/// A compiled loot table: pools of weighted entries whose item names are already resolved to
/// byte ids. Rolling is allocation-free apart from the caller's list and uses
/// <see cref="Random.Shared"/>, so it is safe from any thread that owns the output list.
/// </summary>
public sealed class LootTable
{
    /// <summary>Sentinel table that yields nothing; what <c>"loot": "none"</c> compiles to.</summary>
    public static readonly LootTable None = new("none", []);

    private readonly Pool[] _pools;

    internal LootTable(string name, Pool[] pools)
    {
        Name = name;
        _pools = pools;
    }

    /// <summary>File name the table was loaded from, without extension.</summary>
    public string Name { get; }

    /// <summary>Rolls every pool and appends the produced ids to <paramref name="into"/>.</summary>
    public void Roll(LootContext context, List<byte> into)
    {
        foreach (var pool in _pools)
        {
            if (pool.Chance < 1f && Random.Shared.NextSingle() >= pool.Chance) continue;

            var rolls = pool.Rolls(context);
            for (var r = 0; r < rolls; r++)
            {
                var entry = pool.Pick();
                if (entry == null) continue;
                var count = entry.Count.Sample();
                for (var c = 0; c < count; c++)
                    into.Add(entry.Item);
            }
        }
    }

    /// <summary>An inclusive integer range; a fixed value is a range of one.</summary>
    internal readonly record struct IntRange(int Min, int Max)
    {
        public int Sample() => Min >= Max ? Min : Random.Shared.Next(Min, Max + 1);
    }

    /// <summary>Named roll-count providers; <see cref="Fixed"/> uses the pool's range instead.</summary>
    internal enum RollProvider
    {
        Fixed,
        PebbleCount,
        PebbleFlintCount,
        PebbleObsidianCount,
        PileCount,
    }

    internal sealed class Entry
    {
        public byte Item;
        public int Weight = 1;
        public IntRange Count = new(1, 1);
        public bool IsEmpty;
    }

    internal sealed class Pool
    {
        public float Chance = 1f;
        public IntRange RollRange = new(1, 1);
        public RollProvider Provider = RollProvider.Fixed;
        public Entry[] Entries = [];
        public int TotalWeight;

        public int Rolls(LootContext context) => Provider switch
        {
            RollProvider.PebbleCount => context.Count > 0
                ? context.Count
                : PebbleGenerator.CountAt(context.X, context.Z),
            RollProvider.PebbleFlintCount => context.Count > 0
                ? context.Count - context.Secondary
                : PebbleGenerator.CountAt(context.X, context.Z)
                    - PebbleGenerator.ObsidianCountWithState(context.X, context.Z, 0, 3),
            RollProvider.PebbleObsidianCount => context.Count > 0
                ? context.Secondary
                : PebbleGenerator.ObsidianCountWithState(context.X, context.Z, 0, 3),
            // Player-only piles never read the world: a bare block is one unit.
            RollProvider.PileCount => context.Count > 0 ? context.Count : 1,
            _ => RollRange.Sample(),
        };

        /// <summary>Weighted pick, or null when the winning entry is a weighted nothing.</summary>
        public Entry? Pick()
        {
            if (Entries.Length == 0 || TotalWeight <= 0) return null;

            var roll = Random.Shared.Next(TotalWeight);
            foreach (var entry in Entries)
            {
                roll -= entry.Weight;
                if (roll >= 0) continue;
                return entry.IsEmpty ? null : entry;
            }

            return null;
        }
    }
}
