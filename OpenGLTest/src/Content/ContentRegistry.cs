using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace silkgltriangle;

/// <summary>
/// Loads every block, item, loot table, tag and model from <c>assets/data</c> and
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
    private static readonly Dictionary<string, HashSet<byte>> Tags = new(StringComparer.Ordinal);
    private static readonly Dictionary<byte, BlockModel> BlockModels = new();
    private static readonly Dictionary<byte, BlockDefinition> BlockDefinitions = new();
    private static readonly List<CraftingRecipe> Recipes = new();

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
        LoadTags(Path.Combine(assetsRoot, "data", "tags"));
        LoadRecipes(Path.Combine(assetsRoot, "data", "recipes"));
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
            BlockType.GreedyById[id] = type.Renderer == BlockRenderer.Model && !type.Fluid && BakesGreedy(baked);
        }

        SelfCheckBuiltInParents();
    }

    /// <summary>
    /// Whether a baked model is exactly the six untransformed cube faces, so the greedy mesher
    /// can rebuild any of its faces from (face, extent) alone: one full, shaded quad per cube
    /// face, culled against its own side, positioned on the <see cref="Chunk.CubeFaces"/>
    /// corners and textured with the plain <see cref="Chunk.TiledUv"/> orientation. A rotated
    /// or cropped face UV, a sub-cell element or an unshaded face keeps the per-quad path.
    /// Cutout (leaves) and translucent (glass) cubes qualify: their culling rule is the same
    /// one, and the mesher picks the destination list per block type.
    /// </summary>
    internal static bool BakesGreedy(BakedModel baked)
    {
        if (!baked.IsFullCube || baked.Quads.Length != 6) return false;

        var seen = 0;
        foreach (var quad in baked.Quads)
        {
            if (!quad.IsFullFace || !quad.Shade || quad.CullFace != quad.Face) return false;
            if ((seen & (1 << quad.Face)) != 0) return false;
            seen |= 1 << quad.Face;
            if (quad.Tile != baked.FaceTiles[quad.Face]) return false;

            var corners = Chunk.CubeFaces[quad.Face].face;
            for (var j = 0; j < 4; j++)
            {
                if (quad.Positions[j] != corners[j]) return false;
                if (quad.Uvs[j] != Chunk.TiledUv(quad.Face, corners[j], Vector3.One)) return false;
            }
        }

        return seen == 0x3F;
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

    /// <summary>Every compiled crafting recipe, in file-name order.</summary>
    public static IReadOnlyList<CraftingRecipe> CraftingRecipes => Recipes;

    /// <summary>First recipe making this id, or null when nothing crafts it.</summary>
    public static CraftingRecipe? RecipeForResult(byte id) =>
        Recipes.FirstOrDefault(r => r.ResultId == id);

    /// <summary>Recipe by file name, or null when there is none.</summary>
    public static CraftingRecipe? RecipeByName(string name) =>
        Recipes.FirstOrDefault(r => r.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>Member ids of a loaded tag, sorted ascending, or null when there is no such tag.</summary>
    public static byte[]? TagMembers(string tag) =>
        Tags.TryGetValue(tag, out var members)
            ? members.OrderBy(id => id).ToArray()
            : null;

    private static void Reset()
    {
        BlockIds.Clear();
        ItemIds.Clear();
        LootTables.Clear();
        Tags.Clear();
        Recipes.Clear();
        BlockModels.Clear();
        BlockDefinitions.Clear();

        Array.Clear(BlockType.ById);
        Array.Clear(BlockType.Emission);
        Array.Clear(BlockType.PassesLightById);
        Array.Clear(BlockType.BranchConnectableById);
        Array.Clear(BlockType.SolidById);
        Array.Clear(BlockType.OccludesById);
        Array.Clear(BlockType.CastsAoById);
        Array.Clear(BlockType.FluidById);
        Array.Clear(BlockType.FluidTickDivisorById);
        Array.Clear(BlockType.FluidRangeById);
        Array.Clear(BlockType.FluidInfiniteById);
        Array.Clear(BlockType.HardensToById);
        Array.Clear(BlockType.HardensTouchingById);
        Array.Clear(BlockType.ReplaceableById);
        Array.Clear(BlockType.FallsById);

        // Air owns no definition but still has to let light through and stop nothing
        BlockType.PassesLightById[BlockType.Air] = true;
        BlockType.ReplaceableById[BlockType.Air] = true;
        BlockType.FluidTickDivisorById[BlockType.Air] = 1;
        BlockType.FluidRangeById[BlockType.Air] = 7;
        BlockType.FluidInfiniteById[BlockType.Air] = true;
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
                "place_stack" => ItemUseAction.PlaceStack,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.action '{use.Action}'; expected pickup_fluid, place_fluid or place_stack."),
            };

            var id = ItemIds[name];
            var sheet = ParseSpritesheet(definition.Spritesheet, path);
            var display = ParseDisplay(definition.Display, path);
            types[id] = new ItemType(id, definition.Name ?? name, definition.Texture)
            {
                UseAction = action,
                UseResultId = ResolveUseTarget(use?.Result, "use.result", path),
                UseFluidId = ResolveUseTarget(use?.Fluid, "use.fluid", path),
                UseBlockId = ResolveUseTarget(use?.Block, "use.block", path),
                SpriteCols = sheet.Cols,
                SpriteRows = sheet.Rows,
                FrameCount = sheet.Frames,
                Animation = sheet.Mode,
                AnimationFps = sheet.Fps,
                DisplayRotation = display.Rotation,
                DisplayTranslation = display.Translation,
                DisplayScale = display.Scale,
            };
        }

        ItemType.Types = types;
    }

    private readonly record struct ParsedSheet(int Cols, int Rows, int Frames, ItemAnimationMode Mode, float Fps);

    private static ParsedSheet ParseSpritesheet(ItemDefinition.SpritesheetDefinition? sheet, string path)
    {
        if (sheet == null) return new ParsedSheet(1, 1, 1, ItemAnimationMode.None, 8f);

        if (sheet.Cols is not { } cols || cols < 1)
            throw new InvalidOperationException(
                $"Item '{path}' has a spritesheet without a positive cols.");
        if (sheet.Rows is not { } rows || rows < 1)
            throw new InvalidOperationException(
                $"Item '{path}' has a spritesheet without a positive rows.");
        var frames = sheet.Frames ?? cols * rows;
        if (frames < 1 || frames > cols * rows)
            throw new InvalidOperationException(
                $"Item '{path}' has spritesheet.frames {sheet.Frames}; expected 1..{cols * rows}.");
        var mode = sheet.Mode?.Trim().ToLowerInvariant() switch
        {
            "heading" => ItemAnimationMode.Heading,
            "loop" => ItemAnimationMode.Loop,
            _ => throw new InvalidOperationException(
                $"Item '{path}' has spritesheet.mode '{sheet.Mode}'; expected heading or loop."),
        };
        var fps = sheet.Fps ?? 8f;
        if (!float.IsFinite(fps) || fps <= 0f)
            throw new InvalidOperationException(
                $"Item '{path}' has spritesheet.fps {sheet.Fps}; expected a positive number.");

        return new ParsedSheet(cols, rows, frames, mode, fps);
    }

    private readonly record struct ParsedDisplay(Vector3 Rotation, Vector3 Translation, float Scale);

    /// <summary>
    /// Reads an item's held-in-hand transform, defaulting to the upright side-facing tool
    /// pose when the file says nothing.
    /// </summary>
    private static ParsedDisplay ParseDisplay(ItemDefinition.DisplayDefinition? display, string path)
    {
        if (display == null)
            return new ParsedDisplay(new Vector3(-30f, 10f, 0f), Vector3.Zero, 1f);

        Vector3 ReadTriple(float[]? values, string field)
        {
            if (values == null) return field == "rotation" ? new Vector3(-30f, 10f, 0f) : Vector3.Zero;
            if (values.Length != 3 || values.Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException(
                    $"Item '{path}' has display.{field} with {values?.Length ?? 0} entries; expected 3 finite numbers.");
            return new Vector3(values[0], values[1], values[2]);
        }

        var scale = display.Scale ?? 1f;
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new InvalidOperationException(
                $"Item '{path}' has display.scale {display.Scale}; expected a positive number.");

        return new ParsedDisplay(ReadTriple(display.Rotation, "rotation"),
            ReadTriple(display.Translation, "translation"), scale);
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

    private static byte ResolveHardenTarget(string? name, string field, string path, bool requireFluid, byte ownerId)
    {
        if (name == null) return 0;

        if (!TryResolve(name, out var id) || id == BlockType.Air || ItemType.Types.ContainsKey(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a registered block.");
        if (requireFluid && (!BlockType.FluidById[id] || id == ownerId))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a different fluid block.");
        if (!requireFluid && BlockType.FluidById[id])
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is a fluid; hardening must produce a solid block.");
        return id;
    }

    /// <summary>
    /// Checks each item's declared use against the finished tables: a fluid action needs a real
    /// fluid block to work on and an item to turn into (the hotbar swaps one for the other),
    /// while a stack action needs a real stackable block and no result (it spends from its
    /// own stack instead of morphing, so it stays a plain pickup).
    /// Runs after <see cref="LoadBlocks"/>, which is when the fluid and stack flags first exist.
    /// </summary>
    private static void ValidateItemUses(List<(string Name, string Path)> files)
    {
        foreach (var (name, path) in files)
        {
            var item = ItemType.Types[ItemIds[name]];
            if (item.UseAction == ItemUseAction.None) continue;

            if (item.UseAction == ItemUseAction.PlaceStack)
            {
                var target = BlockType.ById[item.UseBlockId];
                if (target == null)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'place_stack' but its use.block is not a block.");
                if (target.Fluid)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'place_stack' but '{target.Name}' is a fluid, whose state byte holds levels.");
                if (target.StackMax < 2)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'place_stack' but block '{target.Name}' sets no stackMax.");
                if (item.UseResultId != 0)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'place_stack' with a use.result: a stack action spends from its stack, it never morphs.");
                if (item.UseFluidId != 0)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'place_stack' with a use.fluid: that field belongs to the bucket actions.");
                continue;
            }

            if (item.UseBlockId != 0)
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' with a use.block: that field belongs to place_stack.");

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

    private static void LoadRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in JsonFilesIn(folder))
            Recipes.Add(CompileRecipe(name, ReadJson<RecipeDefinition>(path), path));
    }

    private static CraftingRecipe CompileRecipe(string name, RecipeDefinition definition, string path)
    {
        if (definition.Result == null || !TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Recipe '{path}' has count {resultCount}; expected 1 or more.");

        var ingredients = new List<CraftingRecipe.Ingredient>();
        foreach (var ingredient in definition.Ingredients ?? [])
        {
            var count = ingredient.Count ?? 1;
            if (count <= 0)
                throw new InvalidOperationException($"Recipe '{path}' has an ingredient with count {count}; expected 1 or more.");
            if (ingredient.Tag != null && ingredient.Item != null)
                throw new InvalidOperationException(
                    $"Recipe '{path}' names both item '{ingredient.Item}' and tag '{ingredient.Tag}'; an ingredient takes one or the other.");
            if (ingredient.Tag != null)
            {
                if (!Tags.TryGetValue(ingredient.Tag, out var members) || members.Count == 0)
                    throw new InvalidOperationException(
                        $"Recipe '{path}' names tag '{ingredient.Tag}', which has no loaded members.");
                ingredients.Add(new CraftingRecipe.Ingredient(0, count, ingredient.Tag,
                    members.OrderBy(id => id).ToArray()));
                continue;
            }
            if (ingredient.Item == null || !TryResolve(ingredient.Item, out var id))
                throw new InvalidOperationException(
                    $"Recipe '{path}' names ingredient '{ingredient.Item}', which is not a registered block or item.");
            ingredients.Add(new CraftingRecipe.Ingredient(id, count));
        }
        if (ingredients.Count == 0)
            throw new InvalidOperationException($"Recipe '{path}' has no ingredients.");

        return new CraftingRecipe(name, resultId, resultCount, ingredients.ToArray());
    }

    private static void LoadBlocks(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, BlockType>();
        var hardenPending = new List<(byte Id, string? To, string? Touching, string Path)>();

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

            var flow = definition.Flow;
            if (flow != null && !(definition.Fluid ?? false))
                throw new InvalidOperationException(
                    $"Block '{path}' sets flow.* without fluid: true; flow tuning only applies to fluids.");
            var tickDivisor = flow?.TickDivisor ?? 1;
            if (tickDivisor < 1 || tickDivisor > 8)
                throw new InvalidOperationException(
                    $"Block '{path}' has flow.tickDivisor {tickDivisor}; expected 1..8.");
            var range = flow?.Range ?? 7;
            if (range < 1 || range > 7)
                throw new InvalidOperationException(
                    $"Block '{path}' has flow.range {range}; expected 1..7 (the state byte holds levels 1..7).");

            // A fluid's behaviour follows from being a fluid, so the registry forces it rather
            // than trusting the file: nothing collides with water, it darkens no face, it has
            // no inventory slot or loot, and it is never support-fragile (it is what pops
            // those blocks). It stays replaceable, which is set below for every block.
            var fluid = definition.Fluid ?? false;
            var falls = definition.Falls ?? false;
            if (fluid && falls)
                throw new InvalidOperationException(
                    $"Block '{path}' sets both fluid and falls: a fluid cell has no block to topple.");
            var stackMax = definition.StackMax ?? 0;
            if (definition.StackMax.HasValue && (stackMax < 2 || stackMax > 8))
                throw new InvalidOperationException(
                    $"Block '{path}' has stackMax {stackMax}; expected 2..8.");
            if (stackMax >= 2 && fluid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackMax on a fluid: the state byte already holds its levels.");
            if (stackMax >= 2 && falls)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackMax with falls: a toppled pile would lose its count.");
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
                StackMax = (byte)stackMax,
                WoodTaperLevel = taperLevel,
                HalfWidth = definition.Wood == null ? -1f : definition.Wood.HalfWidth ?? 0.5f,
                HasItem = !fluid && (definition.HasItem ?? true),
                NeedsSupport = needsSupport,
                Falls = falls,
                Fluid = fluid,
                FluidTickDivisor = (byte)tickDivisor,
                FluidRange = (byte)range,
                FluidInfinite = flow?.Infinite ?? true,
                OnBreak = onBreak,
                LootTable = fluid ? LootTable.None : ResolveLoot(name, definition.Loot, path),
                Hitboxes = ParseHitboxes(definition.Hitbox, path)
                    ?? DefaultWoodHitbox(taperLevel, definition.Wood?.HalfWidth ?? 0.5f),
            };
            type.HardensToId = 0;
            type.HardensTouchingId = 0;
            if ((flow?.HardensTo == null) != (flow?.HardensTouching == null))
                throw new InvalidOperationException(
                    $"Block '{path}' sets only one of flow.hardensTo / flow.hardensTouching; hardening needs both.");
            hardenPending.Add((id, flow?.HardensTo, flow?.HardensTouching, path));

            BlockModels[id] = ResolveModel(definition.Model, path);
            BlockDefinitions[id] = definition;
            types[id] = type;

            BlockType.ById[id] = type;
            BlockType.Emission[id] = type.LightEmission;
            BlockType.SolidById[id] = type.Solid;
            BlockType.CastsAoById[id] = type.CastsAmbientOcclusion;
            BlockType.FluidById[id] = type.Fluid;
            BlockType.FluidTickDivisorById[id] = type.FluidTickDivisor;
            BlockType.FluidRangeById[id] = type.FluidRange;
            BlockType.FluidInfiniteById[id] = type.FluidInfinite;
            BlockType.HardensToById[id] = type.HardensToId;
            BlockType.HardensTouchingById[id] = type.HardensTouchingId;
            BlockType.FallsById[id] = type.Falls;
            // Fragile blocks are washed away by a flow instead of stopping it
            BlockType.ReplaceableById[id] = type.NeedsSupport;
        }

        // Contact targets resolve here, not inline: files load alphabetically, so lava's
        // flow.hardensTouching (water) may not have its fluid flag yet when lava is read.
        foreach (var (pendingId, to, touching, pendingPath) in hardenPending)
        {
            if (to == null) continue;
            var type = types[pendingId];
            type.HardensToId = ResolveHardenTarget(to, "flow.hardensTo", pendingPath, requireFluid: false, ownerId: pendingId);
            type.HardensTouchingId = ResolveHardenTarget(touching, "flow.hardensTouching", pendingPath, requireFluid: true, ownerId: pendingId);
            BlockType.HardensToById[pendingId] = type.HardensToId;
            BlockType.HardensTouchingById[pendingId] = type.HardensTouchingId;
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

    /// <summary>Tag tapered wood may grow tubes and arms into, beyond parent/child wood.</summary>
    public const string BranchConnectableTag = "tree_branch_connectable";

    /// <summary>Whether a block id belongs to a loaded tag.</summary>
    public static bool IsInTag(string tag, byte id) =>
        Tags.TryGetValue(tag, out var members) && members.Contains(id);

    // Block and item tags: named sets from data/tags/<name>.json ({"blocks": [...],
    // "items": [...]}), resolved here so gameplay and crafting share them. Tapered wood reads
    // its tag through the flat BranchConnectableById mirror (see BlockType); the dictionary
    // stays for error messages, tests and recipe ingredients.
    private static void LoadTags(string folder)
    {
        foreach (var (name, path) in JsonFilesIn(folder))
        {
            var definition = ReadJson<TagDefinition>(path);
            var members = new HashSet<byte>();
            foreach (var entry in definition.Blocks ?? [])
            {
                var id = ResolveTagMember(entry, path);
                if (!members.Add(id))
                    throw new InvalidOperationException($"Tag '{path}' lists '{entry}' twice.");
            }
            foreach (var entry in definition.Items ?? [])
            {
                var id = ResolveTagMember(entry, path);
                if (!members.Add(id))
                    throw new InvalidOperationException($"Tag '{path}' lists '{entry}' twice.");
            }
            Tags[name] = members;
        }

        if (!Tags.TryGetValue(BranchConnectableTag, out var connectable))
            throw new InvalidOperationException(
                $"Missing required block tag '{BranchConnectableTag}': expected '{Path.Combine(folder, BranchConnectableTag)}.json'.");
        foreach (var id in connectable)
        {
            if (BlockType.ById[id] == null || ItemType.IsItem(id))
                throw new InvalidOperationException(
                    $"Tag '{BranchConnectableTag}' names id {id}: tapered wood only grows into blocks.");
            BlockType.BranchConnectableById[id] = true;
        }
    }

    private static byte ResolveTagMember(string name, string path)
    {
        var bare = name.StartsWith("block/", StringComparison.Ordinal) ? name["block/".Length..]
            : name.StartsWith("item/", StringComparison.Ordinal) ? name["item/".Length..]
            : name;
        if (name.StartsWith("block/", StringComparison.Ordinal))
        {
            if (BlockIds.TryGetValue(bare, out var blockId)) return blockId;
            throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered block.");
        }
        if (name.StartsWith("item/", StringComparison.Ordinal))
        {
            if (ItemIds.TryGetValue(bare, out var itemId)) return itemId;
            throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered item.");
        }
        if (TryResolve(name, out var id)) return id;
        throw new InvalidOperationException($"Tag '{path}' names '{name}', which is not a registered block or item.");
    }

    private static BlockHitbox[] DefaultWoodHitbox(int taperLevel, float halfWidth)
    {
        // Tapered wood renders as a thin column, not the full cube its model bakes to, so the
        // outline, raycast and mining pop would all float around empty air on the default. The
        // column spans the full height (node plus the up/down tubes); side arms welding to
        // neighbours poke out of it, which the single-box shape accepts.
        if (taperLevel < 0) return [BlockHitbox.FullCube];
        var hw = Math.Clamp(halfWidth, 0f, 0.5f);
        return [new BlockHitbox(new Vector3(0.5f - hw, 0f, 0.5f - hw), new Vector3(0.5f + hw, 1f, 0.5f + hw))];
    }

    private static BlockHitbox[]? ParseHitboxes(JsonElement hitbox, string path)
    {
        if (hitbox.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (hitbox.ValueKind == JsonValueKind.Array)
        {
            if (hitbox.GetArrayLength() == 0)
                throw new InvalidOperationException($"Block '{path}' has an empty hitbox array; omit hitbox for a full cube.");
            var boxes = new BlockHitbox[hitbox.GetArrayLength()];
            var i = 0;
            foreach (var element in hitbox.EnumerateArray())
                boxes[i++] = ParseOneBox(element, path);
            return boxes;
        }

        if (hitbox.ValueKind == JsonValueKind.Object)
            return [ParseOneBox(hitbox, path)];

        throw new InvalidOperationException($"Block '{path}' has a hitbox that is neither an object nor an array.");
    }

    private static BlockHitbox ParseOneBox(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Block '{path}' has a hitbox entry that is not an object.");
        if (!element.TryGetProperty("min", out var minElement) || !element.TryGetProperty("max", out var maxElement))
            throw new InvalidOperationException($"Block '{path}' has a hitbox entry without min and max.");
        var min = ReadBoxCorner(minElement, "min", path);
        var max = ReadBoxCorner(maxElement, "max", path);
        if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            throw new InvalidOperationException($"Block '{path}' has a hitbox whose min {min} is not below its max {max}.");
        return new BlockHitbox(min, max);
    }

    private static Vector3 ReadBoxCorner(JsonElement element, string field, string path)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
            throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} that is not a 3-number array.");
        var values = new float[3];
        var i = 0;
        foreach (var number in element.EnumerateArray())
        {
            if (number.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} that is not a 3-number array.");
            var value = number.GetSingle();
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new InvalidOperationException($"Block '{path}' has a hitbox.{field} value {value}; expected 0..1.");
            values[i++] = value;
        }
        return new Vector3(values[0], values[1], values[2]);
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

            // Every full-cube block derives from one of these, so the greedy mesher must be
            // able to take all of them or most of the world falls back to the per-quad path
            if (!BakesGreedy(baked))
                throw new InvalidOperationException(
                    $"Built-in model '{parent}' does not bake to plain cube faces the greedy mesher can merge.");
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
