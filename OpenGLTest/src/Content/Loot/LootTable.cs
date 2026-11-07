using System;
using System.Collections.Generic;
using VoxelEngine.Items;
using VoxelEngine.WorldGen.Features;

namespace VoxelEngine.Content.Loot;

/// <summary>
/// A compiled loot table: pools of weighted entries whose item names are already resolved to
/// byte ids. An entry is an item (with a count range, and for gear a rarity range), a weighted nothing, or a nested
/// table rolled in its place; a pool may be limited to a world-Y band. Rolling is allocation-free apart from the
/// caller's list. The plain overloads use <see cref="Random.Shared"/>; the ones taking a <see cref="Random"/> are
/// deterministic for a seeded one, which is how a ruin chest rolls the same contents whenever it is first opened or
/// broken. Safe from any thread that owns the output list.
/// </summary>
public sealed class LootTable
{
    /// <summary>Sentinel table that yields nothing; what <c>"loot": "none"</c> compiles to.</summary>
    public static readonly LootTable None = new("none", []);

    /// <summary>How deep nested tables may go; the compiler refuses a cycle, this only bounds a malformed chain.</summary>
    internal const int MaxNesting = 8;

    private readonly Pool[] _pools;

    internal LootTable(string name, Pool[] pools)
    {
        Name = name;
        _pools = pools;
    }

    /// <summary>File name the table was loaded from, without extension.</summary>
    public string Name { get; }

    /// <summary>Every entry of every pool, for the compiler nested-table resolution.</summary>
    internal IEnumerable<Entry> AllEntries()
    {
        foreach (var pool in _pools)
            foreach (var entry in pool.Entries)
                yield return entry;
    }

    /// <summary>Rolls every pool and appends the produced ids to <paramref name="into"/>.</summary>
    public void Roll(LootContext context, List<byte> into) => Roll(context, into, Random.Shared);

    /// <summary>Like <see cref="Roll(LootContext, List{byte})"/> with an explicit source of randomness.</summary>
    public void Roll(LootContext context, List<byte> into, Random random)
    {
        var sink = new ByteSink(into);
        RollCore(context, random, ref sink, 0);
    }

    /// <summary>
    /// Rolls every pool into stacks: an item and how many, and for a gear entry the rarity it rolled (the caller mints
    /// the modifiers from a seed, see <c>GearRoller</c>). What a chest holds.
    /// </summary>
    public void RollStacks(LootContext context, List<LootStack> into, Random random)
    {
        var sink = new StackSink(into);
        RollCore(context, random, ref sink, 0);
    }

    private void RollCore<TSink>(LootContext context, Random random, ref TSink sink, int depth)
        where TSink : struct, ISink
    {
        if (depth > MaxNesting) return;
        foreach (var pool in _pools)
        {
            if (context.Y < pool.MinY || context.Y > pool.MaxY) continue;
            if (pool.Chance < 1f && random.NextSingle() >= pool.Chance) continue;

            var rolls = pool.Rolls(context, random);
            for (var r = 0; r < rolls; r++)
            {
                var entry = pool.Pick(random);
                if (entry == null) continue;
                if (entry.Nested != null)
                {
                    entry.Nested.RollCore(context, random, ref sink, depth + 1);
                    continue;
                }
                sink.Add(entry, entry.Count.Sample(random), random);
            }
        }
    }

    /// <summary>An inclusive integer range; a fixed value is a range of one.</summary>
    internal readonly record struct IntRange(int Min, int Max)
    {
        public int Sample(Random random) => Min >= Max ? Min : random.Next(Min, Max + 1);
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

        /// <summary>For a nested entry, the table rolled in its place (resolved after every table compiled).</summary>
        public LootTable? Nested;

        /// <summary>For a nested entry, the table's file name; resolved into <see cref="Nested"/>.</summary>
        public string? NestedName;

        /// <summary>For a gear entry, the least and most rarity it may roll; null for everything else.</summary>
        public (Rarity Min, Rarity Max)? RarityRange;
    }

    internal sealed class Pool
    {
        public float Chance = 1f;
        public IntRange RollRange = new(1, 1);
        public RollProvider Provider = RollProvider.Fixed;
        public Entry[] Entries = [];
        public int TotalWeight;

        /// <summary>The world-Y band the pool applies in (inclusive); open by default.</summary>
        public int MinY = int.MinValue;
        public int MaxY = int.MaxValue;

        public int Rolls(LootContext context, Random random) => Provider switch
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
            _ => RollRange.Sample(random),
        };

        /// <summary>Weighted pick, or null when the winning entry is a weighted nothing.</summary>
        public Entry? Pick(Random random)
        {
            if (Entries.Length == 0 || TotalWeight <= 0) return null;

            var roll = random.Next(TotalWeight);
            foreach (var entry in Entries)
            {
                roll -= entry.Weight;
                if (roll >= 0) continue;
                return entry.IsEmpty ? null : entry;
            }

            return null;
        }
    }

    // What one rolled entry is added to: a flat list of ids, or stacks that keep a gear entry's rarity
    private interface ISink
    {
        void Add(Entry entry, int count, Random random);
    }

    private readonly struct ByteSink(List<byte> into) : ISink
    {
        public void Add(Entry entry, int count, Random random)
        {
            for (var c = 0; c < count; c++)
            {
                into.Add(entry.Item);
                // A gear entry spends the same randomness here as in a stack roll (the rarity is just not kept), so
                // a seed means the same contents whichever way it is rolled
                if (entry.RarityRange is { } range)
                    PickRarity(range, random);
            }
        }
    }

    private readonly struct StackSink(List<LootStack> into) : ISink
    {
        public void Add(Entry entry, int count, Random random)
        {
            if (entry.RarityRange is not { } range)
            {
                into.Add(new LootStack(entry.Item, count, null));
                return;
            }
            // Gear never stacks: each piece rolls its own rarity within the entry's range, by loot weight
            for (var c = 0; c < count; c++)
                into.Add(new LootStack(entry.Item, 1, PickRarity(range, random)));
        }
    }

    // The rarest tiers are still the least likely: weights of the range's tiers, picked by one roll
    private static Rarity PickRarity((Rarity Min, Rarity Max) range, Random random)
    {
        var total = 0;
        for (var r = range.Min; r <= range.Max; r++) total += Rarities.LootWeight(r);
        var roll = random.Next(Math.Max(1, total));
        for (var r = range.Min; r <= range.Max; r++)
        {
            roll -= Rarities.LootWeight(r);
            if (roll < 0) return r;
        }
        return range.Max;
    }
}
