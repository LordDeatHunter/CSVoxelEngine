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
/// <see cref="BlockType"/> and <see cref="ItemRegistry"/>.
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
    private static readonly List<CraftingRecipe> Recipes = [];
    private static readonly List<CookRecipe> CookRecipes = [];
    private static readonly List<CrushRecipe> CrushRecipes = [];

    private static string _assetsRoot = "assets";
    private static ModelLoader? _models;

    // Every sfx group discovered under assets/sfx at load, used to validate block "sfx" names.
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> _sfxGroups =
        new Dictionary<string, IReadOnlyList<string>>();

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
        _sfxGroups = SfxLibrary.Scan(Path.Combine(assetsRoot, "sfx"));

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
        PublishBlockItems();
        LoadTags(Path.Combine(assetsRoot, "data", "tags"));
        LoadRecipes(Path.Combine(assetsRoot, "data", "recipes"));
        LoadCookRecipes(Path.Combine(assetsRoot, "data", "recipes", "cook"));
        LoadCrushRecipes(Path.Combine(assetsRoot, "data", "recipes", "crush"));
        LoadEntities(assetsRoot);
        ValidateItemUses(itemFiles);
    }

    /// <summary>
    /// Wraps every placeable block in a <see cref="BlockItem"/> and registers those alongside
    /// the pure items, so drops, recipes and the hotbar resolve blocks and items through one
    /// <see cref="ItemRegistry"/>. Runs after <see cref="LoadBlocks"/>, which is when the
    /// HasItem/fluid flags first exist; the mirror is <see cref="Inventory.CategoryOf"/>.
    /// </summary>
    private static void PublishBlockItems()
    {
        var blocks = new List<BlockItem>();
        foreach (var type in BlockType.Types.Values)
            if (type is { HasItem: true, Fluid: false })
                blocks.Add(new BlockItem(type));
        ItemRegistry.PublishBlockItems(blocks);
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

            // A multiblock cell's model says nothing about the cell it sits in: the anchor holds
            // the whole 2x2x2 structure (so its bake is never a full cube of its own cell) and a
            // part draws nothing at all. The surface of the structure really does cover its
            // neighbours, so it occludes by default; and its quads carry no cullface, so they
            // are lit from the anchor's own cell, which means light has to reach that cell.
            // Explicit JSON still wins, and the cutout/translucent guard above is kept.
            if (type.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            {
                type.OccludesNeighbours = !type.Cutout && !type.Translucent && (definition.Occludes ?? true);
                type.PassesLight = definition.Light?.Passes ?? true;
            }

            if (type.Renderer == BlockRenderer.Multiblock)
            {
                ValidateMultiblockBake(type, baked);
                type.RotatedModels =
                [
                    baked,
                    MultiblockShape.Rotate(baked, 1),
                    MultiblockShape.Rotate(baked, 2),
                    MultiblockShape.Rotate(baked, 3),
                ];
            }

            BlockType.OccludesById[id] = type.OccludesNeighbours;
            BlockType.PassesLightById[id] = type.PassesLight;
            BlockType.GreedyById[id] = type.Renderer == BlockRenderer.Model && !type.Fluid && BakesGreedy(baked);
        }

        // Only now do the blocks know their FaceTiles, which is what the material table is keyed by.
        TileMaterials.Build(TextureAtlas.LayerCount, BlockType.Types.Values);

        SelfCheckBuiltInParents();
    }

    /// <summary>
    /// Checks that a multiblock anchor's model can actually be drawn as a structure: it must stay
    /// inside its 2x2x2 volume, cull against nothing (a cullface tests the anchor's world
    /// neighbour, which is the structure's own part cell) and carry no full-cell face (the
    /// per-corner AO path indexes its neighbour deltas by <see cref="Chunk.CubeFaces"/> corner
    /// order, which rotation breaks, so a multiblock shades flat on every facing).
    /// </summary>
    private static void ValidateMultiblockBake(BlockType type, BakedModel baked)
    {
        const float slack = 1e-4f;
        foreach (var quad in baked.Quads)
        {
            if (quad.CullFace >= 0)
                throw new InvalidOperationException(
                    $"The model of multiblock '{type.Name}' has a face with a cullface; a multiblock's faces are culled against nothing, since the anchor's neighbours are its own part cells.");
            if (quad.IsFullFace)
                throw new InvalidOperationException(
                    $"The model of multiblock '{type.Name}' has a face covering a whole cell face; a multiblock must shade flat on every facing, so no face may sit on a cell plane spanning the full cell.");
            foreach (var position in quad.Positions)
                if (position.X < -slack || position.X > MultiblockShape.Size + slack ||
                    position.Y < -slack || position.Y > MultiblockShape.Size + slack ||
                    position.Z < -slack || position.Z > MultiblockShape.Size + slack)
                    throw new InvalidOperationException(
                        $"The model of multiblock '{type.Name}' has a corner at {position}, outside its {MultiblockShape.Size}x{MultiblockShape.Size}x{MultiblockShape.Size} volume (0..{MultiblockShape.Size * 16} in model units).");
        }
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
    /// <summary>
    /// Reads a block's <c>material</c> object, or null when it declared none. Every field is
    /// range-checked here rather than clamped in the shader, so a typo names its file at startup
    /// instead of quietly rendering wrong.
    /// </summary>
    private static TileMaterial? ParseMaterial(BlockDefinition.MaterialDefinition? definition, string path)
    {
        if (definition == null) return null;

        var metallic = definition.Metallic ?? 0f;
        if (!float.IsFinite(metallic) || metallic < 0f || metallic > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.metallic {definition.Metallic}; expected 0..1.");

        var roughness = definition.Roughness ?? 1f;
        if (!float.IsFinite(roughness) || roughness <= 0f || roughness > 1f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.roughness {definition.Roughness}; expected a positive value up to 1.");

        var normalStrength = definition.NormalStrength ?? 1f;
        if (!float.IsFinite(normalStrength) || normalStrength < 0f || normalStrength > 4f)
            throw new InvalidOperationException(
                $"Block '{path}' has material.normalStrength {definition.NormalStrength}; expected 0..4.");

        return new TileMaterial(metallic, roughness, normalStrength);
    }

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
    public static Item RequireItem(string name) => ItemRegistry.RequireItem(name);

    /// <summary>Id of a loaded wood family (e.g. "tree", "cactus"), for content that has to
    /// name one at startup.</summary>
    public static byte RequireWoodFamily(string name) =>
        BlockType.WoodFamilyIdsByName.TryGetValue(name, out var id)
            ? id
            : throw new InvalidOperationException($"No wood family named '{name}' is registered.");

    /// <summary>The entity kind registered under that JSON file name.</summary>
    public static EntityType RequireEntity(string name) => EntityRegistry.Require(name);

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

    /// <summary>Every compiled campfire recipe, in file-name order.</summary>
    public static IReadOnlyList<CookRecipe> CookRecipesList => CookRecipes;

    /// <summary>First cook recipe taking this id, or null when it is not cookable.</summary>
    public static CookRecipe? CookRecipeForInput(byte id) =>
        CookRecipes.FirstOrDefault(r => r.InputId == id);

    /// <summary>Every compiled quern recipe, in file-name order.</summary>
    public static IReadOnlyList<CrushRecipe> CrushRecipesList => CrushRecipes;

    /// <summary>First crush recipe taking this id, or null when it is not crushable.</summary>
    public static CrushRecipe? CrushRecipeForInput(byte id) =>
        CrushRecipes.FirstOrDefault(r => r.InputId == id);

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
        CookRecipes.Clear();
        CrushRecipes.Clear();
        BlockModels.Clear();
        BlockDefinitions.Clear();
        _sfxGroups = new Dictionary<string, IReadOnlyList<string>>();
        ItemRegistry.Reset();
        EntityRegistry.Reset();

        Array.Clear(BlockType.ById);
        Array.Clear(BlockType.Emission);
        Array.Clear(BlockType.PassesLightById);
        Array.Clear(BlockType.BranchConnectableById);
        Array.Clear(BlockType.SolidById);
        Array.Clear(BlockType.CollidesByHitboxById);
        Array.Clear(BlockType.MovementSpeedById);
        Array.Clear(BlockType.FrictionById);
        Array.Fill(BlockType.FrictionById, BlockType.DefaultFriction);
        Array.Clear(BlockType.PebbleWeightById);
        TileMaterials.Reset();
        Array.Clear(BlockType.OccludesById);
        Array.Clear(BlockType.CastsAoById);
        Array.Clear(BlockType.FluidById);
        Array.Clear(BlockType.FluidTickDivisorById);
        Array.Clear(BlockType.FluidRangeById);
        Array.Clear(BlockType.FluidInfiniteById);
        Array.Clear(BlockType.HardensToById);
        Array.Clear(BlockType.HardensTouchingById);
        Array.Clear(BlockType.ReplaceableById);
        Array.Clear(BlockType.ReplaceableByPlacementById);
        Array.Clear(BlockType.FallsById);
        Array.Fill(BlockType.WoodFamilyById, BlockType.NoWoodFamily);
        BlockType.WoodByLevelByFamily.Clear();
        BlockType.WoodFamilyIdsByName.Clear();

        // Air owns no definition but still has to let light through and stop nothing
        BlockType.PassesLightById[BlockType.Air] = true;
        BlockType.ReplaceableById[BlockType.Air] = true;
        BlockType.FluidTickDivisorById[BlockType.Air] = 1;
        BlockType.FluidRangeById[BlockType.Air] = 7;
        BlockType.FluidInfiniteById[BlockType.Air] = true;
    }

    private static void LoadItems(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, Item>();
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
                "ignite_campfire" => ItemUseAction.IgniteCampfire,
                "convert_multiblock" => ItemUseAction.ConvertMultiblock,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.action '{use.Action}'; expected pickup_fluid, place_fluid, place_stack, ignite_campfire or convert_multiblock."),
            };

            var durability = definition.Durability ?? 0;
            if (durability < 0 || durability > 1000)
                throw new InvalidOperationException(
                    $"Item '{path}' has durability {durability}; expected 0..1000.");
            var useDelay = definition.UseDelay ?? 0f;
            if (!float.IsFinite(useDelay) || useDelay < 0f || useDelay > 60f)
                throw new InvalidOperationException(
                    $"Item '{path}' has useDelay {definition.UseDelay}; expected 0..60 seconds.");
            var chance = use?.Chance ?? 1f;
            if (!float.IsFinite(chance) || chance <= 0f || chance > 1f)
                throw new InvalidOperationException(
                    $"Item '{path}' has use.chance {use?.Chance}; expected 0..1 exclusive of 0.");

            // A fuel item is anything declaring how long one unit burns; a kiln reads nothing else
            var fuelSeconds = 0f;
            if (definition.Fuel is { } fuel)
            {
                fuelSeconds = fuel.BurnSeconds ?? 0f;
                if (!float.IsFinite(fuelSeconds) || fuelSeconds < 1f || fuelSeconds > 3600f)
                    throw new InvalidOperationException(
                        $"Item '{path}' has fuel.burnSeconds {fuel.BurnSeconds}; expected 1..3600 seconds.");
            }

            var id = ItemIds[name];
            var sheet = ParseSpritesheet(definition.Spritesheet, path);
            var display = ParseDisplay(definition.Display, path);

            var tool = definition.Tool;
            var power = tool?.Power ?? Mining.DefaultPower;
            if (!float.IsFinite(power) || power <= 0f)
                throw new InvalidOperationException(
                    $"Item '{path}' has tool.power {tool?.Power}; expected a positive number.");
            var hitDelay = tool?.HitDelayMs ?? Mining.DefaultHitGapMs;
            if (!float.IsFinite(hitDelay) || hitDelay < 1f || hitDelay > 60000f)
                throw new InvalidOperationException(
                    $"Item '{path}' has tool.hitDelayMs {tool?.HitDelayMs}; expected 1..60000 milliseconds.");

            var attack = definition.Attack?.Trim().ToLowerInvariant() switch
            {
                null or "" => AttackPattern.Punch,
                "punch" => AttackPattern.Punch,
                "slice" => AttackPattern.Slice,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown attack '{definition.Attack}'; expected punch or slice."),
            };

            var blockDamage = definition.BlockDamage ?? Mining.DefaultBlockDamage;
            if (!float.IsFinite(blockDamage) || blockDamage <= 0f || blockDamage > 1000f)
                throw new InvalidOperationException(
                    $"Item '{path}' has blockDamage {definition.BlockDamage}; expected a positive number up to 1000.");
            var entityDamage = definition.EntityDamage ?? Mining.DefaultEntityDamage;
            if (!float.IsFinite(entityDamage) || entityDamage < 0f || entityDamage > 1000f)
                throw new InvalidOperationException(
                    $"Item '{path}' has entityDamage {definition.EntityDamage}; expected 0..1000.");
            float? throwDamage = null;
            if (definition.ThrowDamage.HasValue)
            {
                throwDamage = definition.ThrowDamage.Value;
                if (!float.IsFinite(throwDamage.Value) || throwDamage.Value < 0f || throwDamage.Value > 1000f)
                    throw new InvalidOperationException(
                        $"Item '{path}' has throwDamage {definition.ThrowDamage}; expected 0..1000.");
            }

            types[id] = ItemFactory.Create(name, new ItemFactory.ItemSpec(
                id, definition.Name ?? name, definition.Texture,
                action,
                ResolveUseTarget(use?.Result, "use.result", path),
                ResolveUseTarget(use?.Fluid, "use.fluid", path),
                ResolveUseTarget(use?.Block, "use.block", path),
                chance, durability, useDelay,
                sheet.Cols, sheet.Rows, sheet.Frames, sheet.Mode, sheet.Fps,
                display.Rotation, display.Translation, display.Scale,
                tool != null, power, hitDelay, attack, blockDamage, entityDamage, throwDamage, name,
                FuelSeconds: fuelSeconds));
        }

        ItemRegistry.Publish(types, new Dictionary<string, byte>(ItemIds, StringComparer.Ordinal));
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

        if (!TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
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
    /// Resolves a multiblock anchor's <c>multiblock.part</c>: the block that fills the seven
    /// non-anchor cells, which must itself use the part renderer (nothing else draws nothing).
    /// </summary>
    private static byte ResolveMultiblockPart(string? name, string path)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.part.");
        if (!TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.part, which is not a registered block.");
        if (BlockType.ById[id]!.Renderer != BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.part, which does not use renderer \"multiblock_part\".");
        return id;
    }

    /// <summary>
    /// Resolves a multiblock anchor's <c>multiblock.source</c>: the plain block the structure is
    /// built from and reverts to, which must not itself be part of a multiblock.
    /// </summary>
    private static byte ResolveMultiblockSource(string? name, string path, byte ownerId)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.source.");
        if (!TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is not a registered block.");
        if (id == ownerId || BlockType.ById[id]!.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is itself a multiblock cell; a structure is built from a plain block.");
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
            var item = ItemRegistry.ById[ItemIds[name]];
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

            if (item.UseAction == ItemUseAction.IgniteCampfire)
            {
                var target = BlockType.ById[item.UseBlockId];
                if (target == null)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'ignite_campfire' but its use.block is not a block.");
                if (target.StackMax < 2)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'ignite_campfire' but block '{target.Name}' sets no stackMax: the firepit must be a max-count pile.");
                if (item.UseResultId != 0)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'ignite_campfire' with a use.result: an ignite action spends durability, it never morphs.");
                if (item.UseFluidId != 0)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'ignite_campfire' with a use.fluid: that field belongs to the bucket actions.");
                if (item.MaxDurability < 1)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'ignite_campfire' without durability: ignite items must declare uses before breaking.");
                continue;
            }

            if (item.UseAction == ItemUseAction.ConvertMultiblock)
            {
                var source = BlockType.ById[item.UseBlockId];
                if (source == null)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'convert_multiblock' but its use.block is not a block.");
                var anchor = BlockType.ById[item.UseResultId];
                if (anchor == null)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'convert_multiblock' but its use.result is not a block.");
                if (anchor.Renderer != BlockRenderer.Multiblock)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'convert_multiblock' but block '{anchor.Name}' is not a multiblock anchor.");
                if (anchor.MultiblockSourceId != item.UseBlockId)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'convert_multiblock' naming '{source.Name}' in use.block, but '{anchor.Name}' is built from '{BlockType.ById[anchor.MultiblockSourceId]?.Name}'.");
                if (item.UseFluidId != 0)
                    throw new InvalidOperationException(
                        $"Item '{path}' has use.action 'convert_multiblock' with a use.fluid: that field belongs to the bucket actions.");
                continue;
            }

            if (item.UseBlockId != 0)
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' with a use.block: that field belongs to place_stack.");

            if (!BlockType.IsFluid(item.UseFluidId))
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' but its use.fluid is not a fluid block.");

            if (!ItemRegistry.IsItem(item.UseResultId))
                throw new InvalidOperationException(
                    $"Item '{path}' has use.action '{item.UseAction}' but its use.result is not an item.");
        }
    }

    private static void LoadLootTables(string folder)
    {
        foreach (var (name, path) in JsonFilesIn(folder))
            LootTables[name] = CompileLootTable(name, ReadJson<LootTableDefinition>(path), path);
    }

    /// <summary>
    /// Parses <c>data/entities</c> into <see cref="EntityType"/>s: display name, health and the
    /// loot table rolled on death, resolved against the already-compiled tables. The model path
    /// is checked here so a missing Blockbench project fails at startup naming the file.
    /// </summary>
    private static void LoadEntities(string assetsRoot)
    {
        var types = new Dictionary<string, EntityType>(StringComparer.Ordinal);
        foreach (var (name, path) in JsonFilesIn(Path.Combine(assetsRoot, "data", "entities")))
        {
            var definition = ReadJson<EntityDefinition>(path);

            var hp = definition.Hp
                ?? throw new InvalidOperationException($"Entity '{path}' has no hp.");
            if (hp < 1)
                throw new InvalidOperationException($"Entity '{path}' has hp {hp}; expected 1 or more.");

            LootTable? loot = null;
            if (definition.Loot is { Length: > 0 } lootName && lootName != "none")
            {
                loot = LootTables.GetValueOrDefault(lootName)
                    ?? throw new InvalidOperationException(
                        $"Entity '{path}' names loot table '{lootName}', which does not exist.");
            }

            var model = definition.Model is { Length: > 0 } authored
                ? authored
                : Path.Combine(assetsRoot, "models", "entity", name + ".bbmodel");
            if (!File.Exists(model))
                throw new InvalidOperationException($"Entity '{path}' names model '{model}', which does not exist.");

            types[name] = new EntityType
            {
                Name = name,
                DisplayName = definition.Name ?? name,
                MaxHealth = hp,
                LootTable = loot,
                ModelPath = model,
            };
        }

        EntityRegistry.Publish(types);
    }

    private static void LoadRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in JsonFilesIn(folder))
            Recipes.Add(CompileRecipe(name, ReadJson<RecipeDefinition>(path), path));
    }

    private static void LoadCookRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in JsonFilesIn(folder))
            CookRecipes.Add(CompileCookRecipe(name, ReadJson<CookRecipeDefinition>(path), path));
    }

    private static void LoadCrushRecipes(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var (name, path) in JsonFilesIn(folder))
            CrushRecipes.Add(CompileCrushRecipe(name, ReadJson<CrushRecipeDefinition>(path), path));
    }

    private static CookRecipe CompileCookRecipe(string name, CookRecipeDefinition definition, string path)
    {
        if (definition.Input == null || !TryResolve(definition.Input, out var inputId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' names input '{definition.Input}', which is not a registered block or item.");
        if (definition.Result == null || !TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Cook recipe '{path}' has count {resultCount}; expected 1 or more.");
        var cookSeconds = definition.CookSeconds ?? 0f;
        if (!float.IsFinite(cookSeconds) || cookSeconds <= 0f || cookSeconds > 600f)
            throw new InvalidOperationException(
                $"Cook recipe '{path}' has cookSeconds {definition.CookSeconds}; expected 0..600 exclusive of 0.");
        if (CookRecipes.Any(r => r.InputId == inputId))
            throw new InvalidOperationException(
                $"Cook recipe '{path}' duplicates the input of '{CookRecipes.First(r => r.InputId == inputId).Name}': one cook recipe per input.");
        return new CookRecipe(name, inputId, resultId, resultCount, cookSeconds);
    }

    private static CrushRecipe CompileCrushRecipe(string name, CrushRecipeDefinition definition, string path)
    {
        if (definition.Input == null || !TryResolve(definition.Input, out var inputId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' names input '{definition.Input}', which is not a registered block or item.");
        if (definition.Result == null || !TryResolve(definition.Result, out var resultId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' names result '{definition.Result}', which is not a registered block or item.");
        var resultCount = definition.Count ?? 1;
        if (resultCount <= 0)
            throw new InvalidOperationException($"Crush recipe '{path}' has count {resultCount}; expected 1 or more.");
        if (CrushRecipes.Any(r => r.InputId == inputId))
            throw new InvalidOperationException(
                $"Crush recipe '{path}' duplicates the input of '{CrushRecipes.First(r => r.InputId == inputId).Name}': one crush recipe per input.");
        return new CrushRecipe(name, inputId, resultId, resultCount);
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
        var multiblockPending = new List<(byte Id, string? Part, string? Source, string Path)>();
        var woodFamilies = new Dictionary<byte, string>();

        foreach (var (name, path) in files)
        {
            var definition = ReadJson<BlockDefinition>(path);
            var id = BlockIds[name];

            var renderer = definition.Renderer switch
            {
                null or "model" => BlockRenderer.Model,
                "tapered_wood" => BlockRenderer.TaperedWood,
                "pebbles" => BlockRenderer.Pebbles,
                "branch_pile" => BlockRenderer.BranchPile,
                "snow_layers" => BlockRenderer.SnowLayers,
                "fluid" => BlockRenderer.Fluid,
                "quern" => BlockRenderer.Quern,
                "multiblock" => BlockRenderer.Multiblock,
                "multiblock_part" => BlockRenderer.MultiblockPart,
                _ => throw new InvalidOperationException(
                    $"Block '{path}' has unknown renderer '{definition.Renderer}'; expected model, tapered_wood, pebbles, branch_pile, snow_layers, fluid, quern, multiblock or multiblock_part."),
            };

            // A multiblock is a structure built out of eight cells: the anchor names the part
            // that fills the other seven and the plain block it is built from and reverts to,
            // and only an anchor can be a kiln. Both halves are ordinary solid full cells to
            // everything but the mesher, so the anchor's surface really does cover its
            // neighbours - which cutout, translucent and fluid would all break.
            var isMultiblock = renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart;
            if ((renderer == BlockRenderer.Multiblock) != (definition.Multiblock != null))
                throw new InvalidOperationException(
                    $"Block '{path}' {(definition.Multiblock != null ? "declares multiblock.* without renderer: \"multiblock\"" : "uses renderer \"multiblock\" without a multiblock object naming its part and source")}.");
            if ((definition.Kiln ?? false) && renderer != BlockRenderer.Multiblock)
                throw new InvalidOperationException(
                    $"Block '{path}' sets kiln: true without renderer \"multiblock\"; only a multiblock anchor can be a kiln.");
            if (isMultiblock)
            {
                if (definition.HasItem ?? true)
                    throw new InvalidOperationException(
                        $"Block '{path}' uses a multiblock renderer without hasItem: false; a structure is built from its source block, never held.");
                if ((definition.Cutout ?? false) || (definition.Translucent ?? false) || (definition.Fluid ?? false))
                    throw new InvalidOperationException(
                        $"Block '{path}' uses a multiblock renderer with cutout, translucent or fluid; a multiblock cell must be an opaque solid so the structure occludes its neighbours.");
            }

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
            if (definition.StackMax.HasValue && (stackMax < 2 || stackMax > 16))
                throw new InvalidOperationException(
                    $"Block '{path}' has stackMax {stackMax}; expected 2..16.");
            if (stackMax >= 2 && fluid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackMax on a fluid: the state byte already holds its levels.");
            if (stackMax >= 2 && falls)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackMax with falls: a toppled pile would lose its count.");
            var stackSolidAt = definition.StackSolidAt ?? 0;
            if (definition.StackSolidAt.HasValue && (stackMax < 2 || stackSolidAt < 1 || stackSolidAt > stackMax))
                throw new InvalidOperationException(
                    $"Block '{path}' has stackSolidAt {stackSolidAt}; expected 1..stackMax on a stack block.");
            var needsSupport = !fluid && (definition.NeedsSupport ?? false);
            if (falls && needsSupport)
                throw new InvalidOperationException(
                    $"Block '{path}' sets both falls and needsSupport: a falling block topples instead of popping.");
            var solid = !fluid && (definition.Solid ?? true);
            if (stackSolidAt > 0 && solid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackSolidAt on a solid block; state-dependent solidity needs solid: false.");

            // A placement only takes over a non-solid cell, so an always-solid block could never
            // honour the flag: the author's intent cannot work, so it is a content error rather
            // than an inert extra
            var replaceableByPlacement = !fluid && (definition.ReplaceableByPlacement ?? false);
            if (replaceableByPlacement && solid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets replaceableByPlacement but is always solid; a placement only takes over a non-solid cell, so the flag needs solid: false.");

            var movementSpeed = definition.MovementSpeed ?? 0f;
            if (!float.IsFinite(movementSpeed) || movementSpeed < 0f || movementSpeed > 1f)
                throw new InvalidOperationException(
                    $"Block '{path}' has movementSpeed {definition.MovementSpeed}; expected 0..1.");
            if (movementSpeed > 0f && fluid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets movementSpeed on a fluid: swimming handles how fast a body moves through it.");

            var friction = definition.Friction ?? BlockType.DefaultFriction;
            if (!float.IsFinite(friction) || friction < 0f || friction > 1f)
                throw new InvalidOperationException(
                    $"Block '{path}' has friction {definition.Friction}; expected 0..1.");

            var pebbleWeight = definition.PebbleWeight ?? 0f;
            if (!float.IsFinite(pebbleWeight) || pebbleWeight < 0f || pebbleWeight > 1f)
                throw new InvalidOperationException(
                    $"Block '{path}' has pebbleWeight {definition.PebbleWeight}; expected 0..1.");
            // A fluid is never the ground beneath a pebble cell, like its other forced-off traits
            if (fluid) pebbleWeight = 0f;

            var material = ParseMaterial(definition.Material, path);

            var hardness = definition.Hardness ?? BlockType.DefaultHardness;
            if (!float.IsFinite(hardness) || hardness < 0f)
                throw new InvalidOperationException(
                    $"Block '{path}' has hardness {definition.Hardness}; expected 0 or more.");

            var requiresTool = definition.RequiresTool ?? false;
            IReadOnlyDictionary<string, float>? tools = null;
            if (definition.Tools is { Count: > 0 } authoredTools)
            {
                var table = new Dictionary<string, float>(authoredTools.Count, StringComparer.Ordinal);
                foreach (var (tag, required) in authoredTools)
                {
                    if (string.IsNullOrWhiteSpace(tag))
                        throw new InvalidOperationException($"Block '{path}' has a tools entry with no tag name.");
                    if (!float.IsFinite(required) || required < 0f)
                        throw new InvalidOperationException(
                            $"Block '{path}' has tools.{tag} {required}; expected 0 or more.");
                    table[tag] = required;
                }
                tools = table;
            }
            if (requiresTool && tools == null)
                Log.Warn($"Block '{path}' sets requiresTool but lists no tools; the gate is inert and any tool mines it.");

            var hitboxes = ParseHitboxes(definition.Hitbox, path);
            var collidesByHitbox = ResolveCollidesByHitbox(definition, hitboxes, solid, fluid, stackMax, path);

            var type = new BlockType
            {
                Name = name,
                DisplayName = definition.Name ?? name,
                Id = id,
                Sfx = ResolveSfx(path, definition.Sfx),
                Hardness = hardness,
                RequiresTool = requiresTool,
                Tools = tools,
                Solid = solid,
                MovementSpeed = movementSpeed,
                Friction = friction,
                PebbleWeight = pebbleWeight,
                CullSelf = definition.CullSelf ?? false,
                CastsAmbientOcclusion = !fluid && (definition.CastsAO ?? solid),
                Cutout = definition.Cutout ?? false,
                Translucent = definition.Translucent ?? false,
                Sway = definition.Sway ?? false,
                Material = material,
                LightEmission = emission,
                Renderer = renderer,
                Kiln = definition.Kiln ?? false,
                StackMax = (byte)stackMax,
                WoodTaperLevel = taperLevel,
                HalfWidth = definition.Wood == null ? -1f : definition.Wood.HalfWidth ?? 0.5f,
                HasItem = !fluid && (definition.HasItem ?? true),
                NeedsSupport = needsSupport,
                ReplaceableByPlacement = replaceableByPlacement,
                Falls = falls,
                Fluid = fluid,
                FluidTickDivisor = (byte)tickDivisor,
                FluidRange = (byte)range,
                FluidInfinite = flow?.Infinite ?? true,
                OnBreak = onBreak,
                LootTable = fluid ? LootTable.None : ResolveLoot(name, definition.Loot, path),
                Hitboxes = hitboxes ?? DefaultWoodHitbox(taperLevel, definition.Wood?.HalfWidth ?? 0.5f),
                CollidesByHitbox = collidesByHitbox,
            };
            type.HardensToId = 0;
            type.HardensTouchingId = 0;
            if ((flow?.HardensTo == null) != (flow?.HardensTouching == null))
                throw new InvalidOperationException(
                    $"Block '{path}' sets only one of flow.hardensTo / flow.hardensTouching; hardening needs both.");
            hardenPending.Add((id, flow?.HardensTo, flow?.HardensTouching, path));
            if (definition.Multiblock != null)
                multiblockPending.Add((id, definition.Multiblock.Part, definition.Multiblock.Source, path));

            BlockModels[id] = ResolveModel(definition.Model, path);
            BlockDefinitions[id] = definition;
            types[id] = type;
            if (definition.Wood != null)
                woodFamilies[id] = definition.Wood.Family ?? "tree";

            BlockType.ById[id] = type;
            BlockType.Emission[id] = type.LightEmission;
            BlockType.SolidById[id] = type.Solid;
            BlockType.CollidesByHitboxById[id] = type.CollidesByHitbox;
            BlockType.StackSolidStateById[id] = (byte)stackSolidAt;
            BlockType.MovementSpeedById[id] = type.MovementSpeed;
            BlockType.FrictionById[id] = type.Friction;
            BlockType.PebbleWeightById[id] = type.PebbleWeight;
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
            // A flagged block is taken over in place by a placement aimed at it
            BlockType.ReplaceableByPlacementById[id] = type.ReplaceableByPlacement;
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

        // Same reason as the hardening targets above: files load alphabetically, so an anchor's
        // part and source blocks may not exist yet while the anchor's own file is read.
        foreach (var (pendingId, part, source, pendingPath) in multiblockPending)
        {
            var type = types[pendingId];
            type.MultiblockPartId = ResolveMultiblockPart(part, pendingPath);
            type.MultiblockSourceId = ResolveMultiblockSource(source, pendingPath, pendingId);
        }

        BlockType.Types = types;
        BuildWoodLevels(types, woodFamilies);
    }

    /// <summary>
    /// Builds the per-family taper tables. A family's levels must be contiguous from 0, so a
    /// growth walk that clamps to the top can never land on a missing step; the tree family
    /// additionally keeps all six steps, since <see cref="TreeGenerator"/> walks 0..5.
    /// </summary>
    private static void BuildWoodLevels(Dictionary<byte, BlockType> types, Dictionary<byte, string> families)
    {
        // Pinned so a family id is stable across runs and across content reloads in tests
        var familyIds = new Dictionary<string, byte>(StringComparer.Ordinal)
        {
            ["tree"] = BlockType.TreeWoodFamily,
            ["cactus"] = BlockType.CactusWoodFamily,
            ["spruce"] = BlockType.SpruceWoodFamily,
        };
        byte nextFamily = BlockType.SpruceWoodFamily + 1;

        var byFamily = new Dictionary<string, Dictionary<int, byte>>(StringComparer.Ordinal);
        foreach (var (id, family) in families)
        {
            if (!byFamily.TryGetValue(family, out var levels))
            {
                levels = new Dictionary<int, byte>();
                byFamily[family] = levels;
            }

            var level = types[id].WoodTaperLevel;
            if (levels.TryGetValue(level, out var existing))
                throw new InvalidOperationException(
                    $"Blocks '{types[existing].Name}' and '{types[id].Name}' both claim family '{family}' wood.taperLevel {level}.");
            levels[level] = id;
        }

        foreach (var (family, levels) in byFamily)
        {
            if (!familyIds.TryGetValue(family, out var familyId))
                familyIds[family] = familyId = nextFamily++;

            var max = 0;
            foreach (var level in levels.Keys)
                if (level > max) max = level;

            var table = new byte[max + 1];
            for (var level = 0; level <= max; level++)
            {
                if (!levels.TryGetValue(level, out var id))
                    throw new InvalidOperationException(
                        $"Wood family '{family}' has no block at taper level {level}.");
                table[level] = id;
                BlockType.WoodFamilyById[id] = familyId;
            }

            BlockType.WoodByLevelByFamily[familyId] = table;
            BlockType.WoodFamilyIdsByName[family] = familyId;
        }

        if (!byFamily.TryGetValue("tree", out var tree) || tree.Count != 6)
            throw new InvalidOperationException("Wood family 'tree' must declare taper levels 0..5.");
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
            if (BlockType.ById[id] == null || ItemRegistry.IsItem(id))
                throw new InvalidOperationException(
                    $"Tag '{BranchConnectableTag}' names id {id}: tapered wood only grows into blocks.");
            BlockType.BranchConnectableById[id] = true;
        }

        // Tool tags are free-form strings, so a block may gate on one no item provides: a typo,
        // or content authored ahead of its tool. Warn and leave the gate as written (nothing can
        // mine the block until a matching item exists) rather than abort startup.
        foreach (var type in BlockType.Types.Values)
        {
            if (type.Tools == null) continue;
            foreach (var tag in type.Tools.Keys)
                if (!Tags.TryGetValue(tag, out var members) || !members.Any(ItemRegistry.IsItem))
                    Log.Warn($"Block '{type.Name}' gates on tool tag '{tag}', which no item provides.");
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

    /// <summary>Settles <c>collidesByHitbox</c>: only a solid, non-fluid, non-stack block that declares
    /// a hitbox may collide by shape, because <see cref="Camera"/>'s collision reads exactly those
    /// boxes; anything else throws naming the file.</summary>
    internal static bool ResolveCollidesByHitbox(BlockDefinition definition, BlockHitbox[]? declaredHitboxes,
        bool solid, bool fluid, int stackMax, string path)
    {
        if (!(definition.CollidesByHitbox ?? false)) return false;
        if (declaredHitboxes == null)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox without a hitbox; declare the boxes the body should collide with.");
        if (fluid)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox on a fluid; nothing collides with a fluid.");
        if (stackMax > 0)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox on a stack block; a stack's solidity is state-driven (see stackSolidAt).");
        if (!solid)
            throw new InvalidOperationException(
                $"Block '{path}' sets collidesByHitbox with solid: false; a non-solid block never collides.");
        return true;
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

    // A block's sfx is non-fatal: an unknown group is a warning and the default, not a crash,
    // so a block still plays *something* while the audio asset is being authored.
    internal static string ResolveSfx(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SfxLibrary.DefaultGroup;
        if (_sfxGroups.ContainsKey(name)) return name;

        Log.Warn($"Block '{path}' names sfx group '{name}', which has no .wav files under assets/sfx; using '{SfxLibrary.DefaultGroup}'.");
        return SfxLibrary.DefaultGroup;
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
