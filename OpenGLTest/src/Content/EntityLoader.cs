using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;
using VoxelEngine.Items;

namespace VoxelEngine.Content;

/// <summary>Loads <c>data/entities</c> into <see cref="VoxelEngine.Entities.EntityRegistry"/>, resolving each entity's loot table and parsing its Blockbench model once.</summary>
internal static class EntityLoader
{
    /// <summary>
    /// Parses <c>data/entities</c> into <see cref="EntityType"/>s: display name, health and the
    /// loot table rolled on death, resolved against the already-compiled tables. The model is
    /// checked and parsed here (<see cref="EntityType.Model"/>) so a missing or malformed
    /// Blockbench project fails at startup naming the file.
    /// </summary>
    internal static void LoadEntities(string assetsRoot)
    {
        var types = new Dictionary<string, EntityType>(StringComparer.Ordinal);
        foreach (var (name, path) in ContentFiles.JsonFilesIn(Path.Combine(assetsRoot, "data", "entities")))
        {
            var definition = ContentFiles.ReadJson<EntityDefinition>(path);

            // No hp is no health pool (MaxHealth 0), which a mob refuses; an authored hp is 1+.
            var hp = definition.Hp ?? 0;
            if (definition.Hp is < 1)
                throw new InvalidOperationException($"Entity '{path}' has hp {hp}; expected 1 or more.");

            LootTable? loot = null;
            if (definition.Loot is { Length: > 0 } lootName && lootName != "none")
            {
                loot = ContentRegistry.LootTables.GetValueOrDefault(lootName)
                    ?? throw new InvalidOperationException(
                        $"Entity '{path}' names loot table '{lootName}', which does not exist.");
            }

            if (definition.Variants != null)
            {
                types[name] = LoadVariants(name, path, definition, hp, loot);
                continue;
            }

            var model = definition.Model is { Length: > 0 } authored
                ? authored
                : Path.Combine(assetsRoot, "models", "entity", name + ".bbmodel");
            if (!File.Exists(model))
                throw new InvalidOperationException($"Entity '{path}' names model '{model}', which does not exist.");
            // Parsed once here, GL-free, so the entity, the renderer and the debugger share one
            // parse; the loader's own errors already name the model file.
            var parsed = BbmodelLoader.Load(model, Array.Empty<string>());
            var held = ParseHeld(definition.Held, path, [parsed]);

            types[name] = new EntityType
            {
                Name = name,
                DisplayName = definition.Name ?? name,
                MaxHealth = hp,
                LootTable = loot,
                ModelPath = model,
                Model = parsed,
                HeldItemId = held.ItemId,
                HeldPart = held.Part,
                HeldTransform = held.Transform,
            };
        }

        EntityRegistry.Publish(types);
    }

    /// <summary>
    /// An entity authored as a list of <c>variants</c>: every project must exist and carry a skin,
    /// and the first one is the kind's <see cref="EntityType.Model"/>.
    /// </summary>
    private static EntityType LoadVariants(string name, string path, EntityDefinition definition, int hp, LootTable? loot)
    {
        var variants = definition.Variants!;
        if (variants.Length == 0)
            throw new InvalidOperationException($"Entity '{path}' has an empty variants list.");
        if (definition.Model is { Length: > 0 })
            throw new InvalidOperationException($"Entity '{path}' sets both model and variants; use one.");

        var parsed = new BbmodelData[variants.Length];
        for (var i = 0; i < variants.Length; i++)
        {
            var variant = variants[i];
            if (string.IsNullOrEmpty(variant) || !File.Exists(variant))
                throw new InvalidOperationException($"Entity '{path}' names variant '{variant}', which does not exist.");
            parsed[i] = BbmodelLoader.Load(variant, Array.Empty<string>());
            EntityModel.RequireTexture(parsed[i], variant);
        }
        var held = ParseHeld(definition.Held, path, parsed);

        return new EntityType
        {
            Name = name,
            DisplayName = definition.Name ?? name,
            MaxHealth = hp,
            LootTable = loot,
            ModelPath = variants[0],
            Model = parsed[0],
            VariantPaths = variants,
            Variants = parsed,
            HeldItemId = held.ItemId,
            HeldPart = held.Part,
            HeldTransform = held.Transform,
        };
    }

    /// <summary>
    /// An entity's <c>held</c> item: a registered pure item (items load before entities) in a hand that
    /// every one of its models has, posed by the same <c>display</c> shape and parser as an item file.
    /// Nothing held is item 0 with an identity transform.
    /// </summary>
    private static (ushort ItemId, string? Part, Matrix4x4 Transform) ParseHeld(EntityDefinition.HeldDefinition? held,
        string path, IReadOnlyList<BbmodelData> models)
    {
        if (held == null) return (0, null, Matrix4x4.Identity);

        if (string.IsNullOrEmpty(held.Item) || !ContentRegistry.ItemIds.TryGetValue(held.Item, out var id)
            || !ItemRegistry.IsItem(id))
            throw new InvalidOperationException($"Entity '{path}' holds '{held.Item}', which is not a registered item.");
        if (string.IsNullOrEmpty(held.Part))
            throw new InvalidOperationException($"Entity '{path}' has a held item without a held.part.");
        foreach (var model in models)
            if (!model.Parts.ContainsKey(held.Part))
                throw new InvalidOperationException(
                    $"Entity '{path}' holds its item in part '{held.Part}', which its model does not have.");

        return (id, held.Part, ItemLoader.ParseDisplay(held.Display, path).Matrix);
    }
}
