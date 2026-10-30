using System;
using System.Collections.Generic;
using System.IO;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Entities;

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

            types[name] = new EntityType
            {
                Name = name,
                DisplayName = definition.Name ?? name,
                MaxHealth = hp,
                LootTable = loot,
                ModelPath = model,
                Model = parsed,
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
        };
    }
}
