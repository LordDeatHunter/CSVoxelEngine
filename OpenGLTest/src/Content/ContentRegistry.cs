using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// Loads every block, item, loot table and model from <c>assets/data</c> and
/// <c>assets/models</c>, hands out byte ids and fills the static tables on
/// <see cref="BlockType"/> and <see cref="ItemType"/>.
///
/// Two phases, both on the main thread at startup and both before the <see cref="World"/> (and
/// therefore any worker thread) exists:
/// <list type="number">
/// <item><see cref="Load"/> parses everything and assigns ids. No GL, no atlas.</item>
/// <item><see cref="BakeModels"/> runs after <see cref="TextureAtlas.Build"/> and turns the
/// parsed models into atlas-ready quads, which is also when the defaults that depend on a
/// model being a full cube (occlusion, light passing) are settled.</item>
/// </list>
/// Code that has to name one specific piece of content asks for it by file name afterwards
/// through <see cref="RequireBlock"/> / <see cref="RequireItem"/>, which throw rather than
/// return a wrong id.
///
/// Every failure names the file it came from: content errors are authoring mistakes, and a
/// startup crash pointing at the offending JSON beats a block that silently renders wrong.
/// </summary>
public static class ContentRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Texture variables the built-in parents leave to their children; the startup self-check
    // fills them all with one real texture so the parents can be baked in isolation.
    private static readonly string[] ParentTextureVariables =
        ["all", "end", "side", "top", "bottom", "north", "south", "west", "east", "up", "down"];

    private static readonly Dictionary<string, byte> BlockIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, byte> ItemIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, LootTable> LootTables = new(StringComparer.Ordinal);
    private static readonly Dictionary<byte, BlockModel> BlockModels = new();
    private static readonly Dictionary<byte, BlockDefinition> BlockDefinitions = new();

    private static string _assetsRoot = "assets";
    private static ModelLoader? _models;

    /// <summary>
    /// Parses <c>data/items</c>, <c>data/blocks</c>, <c>data/loot</c> and every model they
    /// reference, assigns ids (air = 0, then blocks by name, then items by name) and publishes
    /// the runtime tables. Models are flattened here but not baked — that needs the atlas.
    /// </summary>
    public static void Load(string assetsRoot)
    {
        _assetsRoot = assetsRoot;
        Reset();

        _models = new ModelLoader(Path.Combine(assetsRoot, "models"), JsonOptions);

        var blockFiles = JsonFilesIn(Path.Combine(assetsRoot, "data", "blocks"));
        var itemFiles = JsonFilesIn(Path.Combine(assetsRoot, "data", "items"));

        // Chunk storage is a byte and 0 is air, so blocks and items share 255 slots
        var total = 1 + blockFiles.Count + itemFiles.Count;
        if (total > 256)
            throw new InvalidOperationException(
                $"{blockFiles.Count} blocks + {itemFiles.Count} items exceed the 255 ids a byte block id can hold.");

        byte nextId = 1;
        foreach (var (name, path) in blockFiles)
        {
            if (name == "air")
                throw new InvalidOperationException($"'{path}' may not be named 'air': id 0 is reserved for empty cells.");
            BlockIds[name] = nextId++;
        }

        foreach (var (name, _) in itemFiles)
            ItemIds[name] = nextId++;

        LoadItems(itemFiles);
        LoadLootTables(Path.Combine(assetsRoot, "data", "loot"));
        LoadBlocks(blockFiles);
        ValidateItemUses(itemFiles);
    }

    /// <summary>
    /// Bakes every block's model against the stitched atlas and settles the defaults that
    /// depend on the result. Call once, right after <see cref="TextureAtlas.Build"/>.
    /// </summary>
    public static void BakeModels()
    {
        foreach (var (id, model) in BlockModels)
        {
            var type = BlockType.ById[id]!;
            var definition = BlockDefinitions[id];

            BakedModel baked;
            try
            {
                baked = ModelBaker.Bake(model);
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Failed to bake the model of block '{type.Name}' ('{model.Source}'): {e.Message}", e);
            }

            type.BakedModel = baked;
            type.FaceTiles = baked.FaceTiles;
            type.Top = baked.FaceTiles[4];
            type.Bottom = baked.FaceTiles[5];
            type.Side = baked.FaceTiles[0];

            // A cutout block must never hide a neighbour's face, whatever the author wrote:
            // a face culled behind one shows through the texture's holes as an X-ray.
            // A translucent block must never hide a neighbour's face either, or the
            // neighbour shows a hole through the glass.
            type.OccludesNeighbours = !type.Cutout && !type.Translucent && (definition.Occludes ?? baked.IsFullCube);
            type.PassesLight = definition.Light?.Passes ?? !baked.IsFullCube;

            // A fluid's model is a full cube (it only supplies the tile), but its surface sits
            // below the cell top and it must never hide a neighbour or shade its own cell dark
            if (type.Fluid)
            {
                type.OccludesNeighbours = false;
                type.PassesLight = true;
            }

            BlockType.OccludesById[id] = type.OccludesNeighbours;
            BlockType.PassesLightById[id] = type.PassesLight;
        }

        SelfCheckBuiltInParents();
    }

    /// <summary>
    /// Byte id of a block or item by name. A bare name resolves against blocks first, then
    /// items; a <c>block/</c> or <c>item/</c> prefix pins it to one of the two.
    /// </summary>
    public static byte Require(string name)
    {
        if (TryResolve(name, out var id)) return id;
        throw new InvalidOperationException($"No block or item named '{name}' is registered.");
    }

    /// <summary>
    /// The block registered under <paramref name="name"/> (its JSON file name), for the few
    /// features that have to name one specific block. Blocks only, so an item sharing the name
    /// can never be picked up by mistake.
    /// </summary>
    public static BlockType RequireBlock(string name)
    {
        if (BlockIds.TryGetValue(name, out var id)) return BlockType.ById[id]!;
        throw new InvalidOperationException($"No block named '{name}' is registered.");
    }

    /// <summary>The item registered under that JSON file name; items only, like
    /// <see cref="RequireBlock"/>.</summary>
    public static ItemType RequireItem(string name)
    {
        if (ItemIds.TryGetValue(name, out var id)) return ItemType.Types[id];
        throw new InvalidOperationException($"No item named '{name}' is registered.");
    }

    /// <summary>The compiled loot table of that name, or null when there is none.</summary>
    public static LootTable? LootTableFor(string name) =>
        LootTables.GetValueOrDefault(name);

    private static void Reset()
    {
        BlockIds.Clear();
        ItemIds.Clear();
        LootTables.Clear();
        BlockModels.Clear();
        BlockDefinitions.Clear();

        Array.Clear(BlockType.ById);
        Array.Clear(BlockType.Emission);
        Array.Clear(BlockType.PassesLightById);
        Array.Clear(BlockType.SolidById);
        Array.Clear(BlockType.OccludesById);
        Array.Clear(BlockType.CastsAoById);
        Array.Clear(BlockType.FluidById);
        Array.Clear(BlockType.ReplaceableById);
        Array.Clear(BlockType.FallsById);

        // Air owns no definition but still has to let light through and stop nothing
        BlockType.PassesLightById[BlockType.Air] = true;
        BlockType.ReplaceableById[BlockType.Air] = true;
    }

    private static void LoadItems(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, ItemType>();
        foreach (var (name, path) in files)
        {
            var definition = ReadJson<ItemDefinition>(path);
            if (string.IsNullOrWhiteSpace(definition.Texture))
                throw new InvalidOperationException($"Item '{path}' has no texture.");

            var use = definition.Use;
            var action = use?.Action switch
            {
                null => ItemUseAction.None,
                "pickup_fluid" => ItemUseAction.PickupFluid,
                "place_fluid" => ItemUseAction.PlaceFluid,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.action '{use.Action}'; expected pickup_fluid or place_fluid."),
            };

            var id = ItemIds[name];
            types[id] = new ItemType(id, definition.Name ?? name, definition.Texture)
            {
                UseAction = action,
                UseResultId = ResolveUseTarget(use?.Result, "use.result", path),
                UseFluidId = ResolveUseTarget(use?.Fluid, "use.fluid", path),
            };
        }

        ItemType.Types = types;
    }

    // Items are parsed before blocks, so only the id maps exist here; what those ids turn out
    // to be is checked in ValidateItemUses once the runtime tables are filled.
    private static byte ResolveUseTarget(string? name, string field, string path)
    {
        if (name == null) return 0;

        if (TryResolve(name, out var id)) return id;
        throw new InvalidOperationException(
            $"Item '{path}' names '{name}' in {field}, which is not a registered block or item.");
    }

    /// <summary>
    /// Checks each item's declared use against the finished tables: a fluid action needs a real
    /// fluid block to work on and an item to turn into (the hotbar swaps one for the other).
    /// Runs after <see cref="LoadBlocks"/>, which is when the fluid flags first exist.
    /// </summary>
    private static void ValidateItemUses(List<(string Name, string Path)> files)
    {
        foreach (var (name, path) in files)
        {
            var item = ItemType.Types[ItemIds[name]];
            if (item.UseAction == ItemUseAction.None) continue;

            if (!BlockType.IsFluid(item.UseFluidId))
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' but its use.fluid is not a fluid block.");

            if (!ItemType.IsItem(item.UseResultId))
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' but its use.result is not an item.");
        }
    }

    private static void LoadLootTables(string folder)
    {
        foreach (var (name, path) in JsonFilesIn(folder))
            LootTables[name] = CompileLootTable(name, ReadJson<LootTableDefinition>(path), path);
    }

    private static void LoadBlocks(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, BlockType>();

        foreach (var (name, path) in files)
        {
            var definition = ReadJson<BlockDefinition>(path);
            var id = BlockIds[name];

            var renderer = definition.Renderer switch
            {
                null or "model" => BlockRenderer.Model,
                "tapered_wood" => BlockRenderer.TaperedWood,
                "pebbles" => BlockRenderer.Pebbles,
                "fluid" => BlockRenderer.Fluid,
                _ => throw new InvalidOperationException(
                    $"Block '{path}' has unknown renderer '{definition.Renderer}'; expected model, tapered_wood, pebbles or fluid."),
            };

            var onBreak = definition.OnBreak switch
            {
                null or "drop" => BlockBreakAction.Drop,
                "prime_tnt" => BlockBreakAction.PrimeTnt,
                _ => throw new InvalidOperationException(
                    $"Block '{path}' has unknown onBreak '{definition.OnBreak}'; expected drop or prime_tnt."),
            };

            var taperLevel = definition.Wood?.TaperLevel ?? -1;
            if (definition.Wood != null && (taperLevel < 0 || taperLevel > 5))
                throw new InvalidOperationException(
                    $"Block '{path}' has wood.taperLevel {taperLevel}; expected 0..5.");

            var emission = definition.Light?.Emission ?? 0;
            if (emission > 15)
                throw new InvalidOperationException($"Block '{path}' has light.emission {emission}; expected 0..15.");

            // A fluid's behaviour follows from being a fluid, so the registry forces it rather
            // than trusting the file: nothing collides with water, it darkens no face, it has
            // no inventory slot or loot, and it is never support-fragile (it is what pops
            // those blocks). It stays replaceable, which is set below for every block.
            var fluid = definition.Fluid ?? false;
            var falls = definition.Falls ?? false;
            if (fluid && falls)
                throw new InvalidOperationException(
                    $"Block '{path}' sets both fluid and falls: a fluid cell has no block to topple.");
            var needsSupport = !fluid && (definition.NeedsSupport ?? false);
            if (falls && needsSupport)
                throw new InvalidOperationException(
                    $"Block '{path}' sets both falls and needsSupport: a falling block topples instead of popping.");
            var solid = !fluid && (definition.Solid ?? true);
            var type = new BlockType
            {
                Name = name,
                Id = id,
                MaxHealth = definition.Hardness ?? BlockType.DefaultMaxHealth,
                Solid = solid,
                CullSelf = definition.CullSelf ?? false,
                CastsAmbientOcclusion = !fluid && (definition.CastsAO ?? solid),
                Cutout = definition.Cutout ?? false,
                Translucent = definition.Translucent ?? false,
                Sway = definition.Sway ?? false,
                LightEmission = emission,
                Renderer = renderer,
                WoodTaperLevel = taperLevel,
                HalfWidth = definition.Wood == null ? -1f : definition.Wood.HalfWidth ?? 0.5f,
                HasItem = !fluid && (definition.HasItem ?? true),
                NeedsSupport = needsSupport,
                Falls = falls,
                Fluid = fluid,
                OnBreak = onBreak,
                LootTable = fluid ? LootTable.None : ResolveLoot(name, definition.Loot, path),
            };

            BlockModels[id] = ResolveModel(definition.Model, path);
            BlockDefinitions[id] = definition;
            types[id] = type;

            BlockType.ById[id] = type;
            BlockType.Emission[id] = type.LightEmission;
            BlockType.SolidById[id] = type.Solid;
            BlockType.CastsAoById[id] = type.CastsAmbientOcclusion;
            BlockType.FluidById[id] = type.Fluid;
            BlockType.FallsById[id] = type.Falls;
            // Fragile blocks are washed away by a flow instead of stopping it
            BlockType.ReplaceableById[id] = type.NeedsSupport;
        }

        BlockType.Types = types;
        BlockType.WoodByLevel = BuildWoodLevels(types);
    }

    // Taper levels must cover 0..5 exactly once, or TreeGenerator would grow gaps
    private static byte[] BuildWoodLevels(Dictionary<byte, BlockType> types)
    {
        var byLevel = new byte[6];
        var seen = new string?[6];
        foreach (var type in types.Values)
        {
            if (type.WoodTaperLevel < 0) continue;
            if (seen[type.WoodTaperLevel] != null)
                throw new InvalidOperationException(
                    $"Blocks '{seen[type.WoodTaperLevel]}' and '{type.Name}' both claim wood.taperLevel {type.WoodTaperLevel}.");
            seen[type.WoodTaperLevel] = type.Name;
            byLevel[type.WoodTaperLevel] = type.Id;
        }

        for (var level = 0; level < seen.Length; level++)
            if (seen[level] == null)
                throw new InvalidOperationException($"No block declares wood.taperLevel {level}.");

        return byLevel;
    }

    private static LootTable? ResolveLoot(string blockName, string? loot, string path)
    {
        // Omitted: the table sharing this block's name, or "drop yourself" when there is none
        if (loot == null) return LootTables.GetValueOrDefault(blockName);
        if (loot == "none") return LootTable.None;

        return LootTables.TryGetValue(loot, out var table)
            ? table
            : throw new InvalidOperationException($"Block '{path}' names loot table '{loot}', which does not exist.");
    }

    private static BlockModel ResolveModel(JsonElement model, string path)
    {
        try
        {
            switch (model.ValueKind)
            {
                case JsonValueKind.String:
                    return _models!.Resolve(model.GetString()!);
                case JsonValueKind.Object:
                    var inline = model.Deserialize<ModelDefinition>(JsonOptions)
                                 ?? throw new InvalidOperationException("the inline model is empty");
                    return _models!.ResolveInline(inline, path);
                default:
                    throw new InvalidOperationException("'model' must be a model name or an inline model object");
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Block '{path}': {e.Message}", e);
        }
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
                if (!isEmpty && !TryResolve(entryDefinition.Item!, out item))
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

    // Bakes the four built-in parents against a real texture: a plain cube must come out as six
    // full faces that cull against their own side, which is what every data-driven block and
    // every BlockBench export inherits.
    private static void SelfCheckBuiltInParents()
    {
        var texturesFolder = Path.Combine(_assetsRoot, "textures", "blocks");
        var probe = Directory.GetFiles(texturesFolder, "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault();
        if (probe == null) return;

        foreach (var parent in new[] { "block/cube", "block/cube_all", "block/cube_column", "block/cube_bottom_top" })
        {
            var resolved = _models!.Resolve(parent);
            var textures = new Dictionary<string, string>(resolved.Textures, StringComparer.Ordinal);
            foreach (var variable in ParentTextureVariables)
                textures.TryAdd(variable, probe);

            var baked = ModelBaker.Bake(new BlockModel(parent, textures, resolved.Elements));
            if (!baked.IsFullCube || baked.Quads.Length != 6)
                throw new InvalidOperationException(
                    $"Built-in model '{parent}' baked to {baked.Quads.Length} quads and IsFullCube={baked.IsFullCube}; expected 6 full faces.");

            foreach (var quad in baked.Quads)
                if (!quad.IsFullFace || quad.CullFace != quad.Face)
                    throw new InvalidOperationException(
                        $"Built-in model '{parent}' baked a quad on face {quad.Face} with cullface {quad.CullFace} (full face: {quad.IsFullFace}).");
        }
    }

    private static bool TryResolve(string name, out byte id)
    {
        if (name.StartsWith("block/", StringComparison.Ordinal))
            return BlockIds.TryGetValue(name["block/".Length..], out id);
        if (name.StartsWith("item/", StringComparison.Ordinal))
            return ItemIds.TryGetValue(name["item/".Length..], out id);

        // Bare names try blocks first, so "branch" is the block and "item/branch" the pickup
        return BlockIds.TryGetValue(name, out id) || ItemIds.TryGetValue(name, out id);
    }

    private static List<(string Name, string Path)> JsonFilesIn(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Content folder '{folder}' does not exist.");

        return Directory.GetFiles(folder, "*.json")
            .Select(path => (Name: Path.GetFileNameWithoutExtension(path), Path: path))
            .OrderBy(file => file.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static T ReadJson<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
                   ?? throw new InvalidOperationException($"'{path}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"'{path}' is not valid JSON: {e.Message}", e);
        }
    }
}
