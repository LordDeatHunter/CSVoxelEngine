using System;
using System.Collections.Generic;
using System.Text.Json;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Items;

namespace VoxelEngine.Content.Loot;

/// <summary>Compiles <c>data/loot</c> into the <see cref="LootTable"/>s every destruction path rolls: pools, weighted entries, roll counts or a roll provider, and count ranges.</summary>
internal static class LootTableCompiler
{
    internal static void LoadLootTables(string folder)
    {
        var paths = new Dictionary<string, string>();
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
        {
            ContentRegistry.LootTables[name] = CompileLootTable(name, ContentFiles.ReadJson<LootTableDefinition>(path), path);
            paths[name] = path;
        }
        ResolveNested(ContentRegistry.LootTables, paths);
    }

    // Points every nested entry at its table, once all of them exist, and refuses a table that (through any chain)
    // contains itself, which would roll forever
    internal static void ResolveNested(Dictionary<string, LootTable> tables, Dictionary<string, string> paths)
    {
        foreach (var (name, table) in tables)
            foreach (var entry in table.AllEntries())
            {
                if (entry.NestedName == null) continue;
                if (!tables.TryGetValue(entry.NestedName, out var nested))
                    throw new InvalidOperationException(
                        $"Loot table '{paths.GetValueOrDefault(name, name)}' nests '{entry.NestedName}', which is not a loot table.");
                entry.Nested = nested;
            }

        foreach (var (name, table) in tables)
            if (ReachesItself(table, table, new HashSet<LootTable>()))
                throw new InvalidOperationException(
                    $"Loot table '{paths.GetValueOrDefault(name, name)}' contains itself through its nested tables.");
    }

    private static bool ReachesItself(LootTable start, LootTable current, HashSet<LootTable> seen)
    {
        foreach (var entry in current.AllEntries())
        {
            if (entry.Nested == null) continue;
            if (entry.Nested == start) return true;
            if (seen.Add(entry.Nested) && ReachesItself(start, entry.Nested, seen)) return true;
        }
        return false;
    }

    internal static LootTable CompileLootTable(string name, LootTableDefinition definition, string path)
    {
        var pools = new List<LootTable.Pool>();
        foreach (var poolDefinition in definition.Pools ?? [])
        {
            var pool = new LootTable.Pool
            {
                Chance = poolDefinition.Chance ?? 1f,
                MinY = poolDefinition.MinY ?? int.MinValue,
                MaxY = poolDefinition.MaxY ?? int.MaxValue,
            };
            if (pool.MinY > pool.MaxY)
                throw new InvalidOperationException($"Loot table '{path}' has a pool whose minY is above its maxY.");
            ReadRolls(poolDefinition.Rolls, pool, path);

            var entries = new List<LootTable.Entry>();
            foreach (var entryDefinition in poolDefinition.Entries ?? [])
            {
                var isEmpty = entryDefinition.Empty ?? false;
                var isNested = entryDefinition.Table != null;
                if ((isEmpty ? 1 : 0) + (entryDefinition.Item != null ? 1 : 0) + (isNested ? 1 : 0) != 1)
                    throw new InvalidOperationException(
                        $"Loot table '{path}' has an entry that is not exactly one of an item, a nested table and an empty weight.");

                var weight = entryDefinition.Weight ?? 1;
                if (weight <= 0)
                    throw new InvalidOperationException($"Loot table '{path}' has an entry with weight {weight}; expected 1 or more.");

                ushort item = 0;
                if (!isEmpty && !isNested && !ContentFiles.TryResolve(entryDefinition.Item!, out item))
                    throw new InvalidOperationException(
                        $"Loot table '{path}' names '{entryDefinition.Item}', which is not a registered block or item.");
                var rarity = ReadRarity(entryDefinition.Rarity, path);
                if (rarity != null && (isEmpty || isNested || ItemRegistry.TryGet(item) is not GearItem))
                    throw new InvalidOperationException(
                        $"Loot table '{path}' gives a rarity to '{entryDefinition.Item}', which is not a piece of gear.");

                entries.Add(new LootTable.Entry
                {
                    Item = item,
                    Weight = weight,
                    IsEmpty = isEmpty,
                    NestedName = entryDefinition.Table,
                    RarityRange = rarity,
                    Count = ReadRange(entryDefinition.Count, new LootTable.IntRange(1, 1), path, "count"),
                });
            }

            pool.Entries = entries.ToArray();
            foreach (var entry in entries) pool.TotalWeight += entry.Weight;
            pools.Add(pool);
        }

        return new LootTable(name, pools.ToArray());
    }

    // A rarity as a name or a {min, max} range; null when the entry names none
    private static (Rarity Min, Rarity Max)? ReadRarity(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
            {
                var one = ParseRarity(element.GetString(), path);
                return (one, one);
            }
            case JsonValueKind.Object:
            {
                var min = element.TryGetProperty("min", out var minElement) ? ParseRarity(minElement.GetString(), path) : Rarity.Common;
                var max = element.TryGetProperty("max", out var maxElement) ? ParseRarity(maxElement.GetString(), path) : min;
                if (max < min)
                    throw new InvalidOperationException($"Loot table '{path}' has a rarity whose max is below its min.");
                return (min, max);
            }
            default:
                throw new InvalidOperationException($"Loot table '{path}' has a rarity that is neither a name nor an object.");
        }
    }

    private static Rarity ParseRarity(string? name, string path) =>
        Rarities.TryParse(name, out var rarity)
            ? rarity
            : throw new InvalidOperationException($"Loot table '{path}' has unknown rarity '{name}'.");

    private static void ReadRolls(JsonElement rolls, LootTable.Pool pool, string path)
    {
        if (rolls.ValueKind == JsonValueKind.Object && rolls.TryGetProperty("provider", out var provider))
        {
            var providerName = provider.GetString();
            pool.Provider = providerName switch
            {
                "pebble_count" => LootTable.RollProvider.PebbleCount,
                "pebble_flint_count" => LootTable.RollProvider.PebbleFlintCount,
                "pebble_obsidian_count" => LootTable.RollProvider.PebbleObsidianCount,
                "pile_count" => LootTable.RollProvider.PileCount,
                _ => throw new InvalidOperationException(
                    $"Loot table '{path}' uses unknown roll provider '{providerName}'."),
            };
            return;
        }

        pool.RollRange = ReadRange(rolls, new LootTable.IntRange(1, 1), path, "rolls");
    }

    private static LootTable.IntRange ReadRange(JsonElement element, LootTable.IntRange fallback, string path, string field)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return fallback;
            case JsonValueKind.Number:
                var value = element.GetInt32();
                return new LootTable.IntRange(value, value);
            case JsonValueKind.Object:
                var min = element.TryGetProperty("min", out var minElement) ? minElement.GetInt32() : fallback.Min;
                var max = element.TryGetProperty("max", out var maxElement) ? maxElement.GetInt32() : min;
                if (max < min)
                    throw new InvalidOperationException($"Loot table '{path}' has a '{field}' whose max {max} is below its min {min}.");
                return new LootTable.IntRange(min, max);
            default:
                throw new InvalidOperationException(
                    $"Loot table '{path}' has a '{field}' that is neither a number nor an object.");
        }
    }
}
