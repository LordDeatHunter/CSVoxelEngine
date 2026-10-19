using System;
using System.Collections.Generic;
using System.Text.Json;
using VoxelEngine.Content.Definitions;

namespace VoxelEngine.Content.Loot;

/// <summary>Compiles <c>data/loot</c> into the <see cref="LootTable"/>s every destruction path rolls: pools, weighted entries, roll counts or a roll provider, and count ranges.</summary>
internal static class LootTableCompiler
{
    internal static void LoadLootTables(string folder)
    {
        foreach (var (name, path) in ContentFiles.JsonFilesIn(folder))
            ContentRegistry.LootTables[name] = CompileLootTable(name, ContentFiles.ReadJson<LootTableDefinition>(path), path);
    }

    private static LootTable CompileLootTable(string name, LootTableDefinition definition, string path)
    {
        var pools = new List<LootTable.Pool>();
        foreach (var poolDefinition in definition.Pools ?? [])
        {
            var pool = new LootTable.Pool { Chance = poolDefinition.Chance ?? 1f };
            ReadRolls(poolDefinition.Rolls, pool, path);

            var entries = new List<LootTable.Entry>();
            foreach (var entryDefinition in poolDefinition.Entries ?? [])
            {
                var isEmpty = entryDefinition.Empty ?? false;
                if (isEmpty == (entryDefinition.Item != null))
                    throw new InvalidOperationException(
                        $"Loot table '{path}' has an entry that is neither an item nor an empty weight.");

                var weight = entryDefinition.Weight ?? 1;
                if (weight <= 0)
                    throw new InvalidOperationException($"Loot table '{path}' has an entry with weight {weight}; expected 1 or more.");

                byte item = 0;
                if (!isEmpty && !ContentFiles.TryResolve(entryDefinition.Item!, out item))
                    throw new InvalidOperationException(
                        $"Loot table '{path}' names '{entryDefinition.Item}', which is not a registered block or item.");

                entries.Add(new LootTable.Entry
                {
                    Item = item,
                    Weight = weight,
                    IsEmpty = isEmpty,
                    Count = ReadRange(entryDefinition.Count, new LootTable.IntRange(1, 1), path, "count"),
                });
            }

            pool.Entries = entries.ToArray();
            foreach (var entry in entries) pool.TotalWeight += entry.Weight;
            pools.Add(pool);
        }

        return new LootTable(name, pools.ToArray());
    }

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
