using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VoxelEngine.Audio;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Quests;
using VoxelEngine.Content.Recipes;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.Stations;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Content.Structures;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Entities;
using VoxelEngine.Graphics;
using VoxelEngine.Items;
using VoxelEngine.Saves;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Content;

/// <summary>
/// Loads every block, item, loot table, tag and model from <c>assets/data</c> and
/// <c>assets/models</c>, hands out ids and fills the static tables on
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
    internal static readonly Dictionary<string, ushort> BlockIds = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, ushort> ItemIds = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, LootTable> LootTables = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, HashSet<ushort>> Tags = new(StringComparer.Ordinal);
    internal static readonly Dictionary<ushort, BlockModel> BlockModels = new();

    // The open model of every openable block (its "open" object), flattened at load and baked
    // beside the closed model in BakeModels
    internal static readonly Dictionary<ushort, BlockModel> OpenModels = new();

    // Items that declare a "model": flattened at load like a block's, baked in BakeModels so a
    // station can draw the item as real geometry (the crucible standing in a kiln's hearth).
    internal static readonly Dictionary<ushort, BlockModel> ItemModels = new();
    internal static readonly Dictionary<ushort, BlockDefinition> BlockDefinitions = new();
    internal static readonly List<CraftingRecipe> Recipes = [];
    internal static readonly List<CookRecipe> CookRecipes = [];
    internal static readonly List<CrushRecipe> CrushRecipes = [];
    internal static readonly List<MoltenMaterial> MoltenMaterialsList = [];
    internal static readonly List<MeltRecipe> MeltRecipes = [];
    internal static readonly List<CastRecipe> CastRecipes = [];
    internal static readonly List<AlloyRecipe> AlloyRecipesList = [];
    internal static readonly List<ForgeRecipe> ForgeRecipesList = [];

    // Blocks declaring a "biomeTint", validated at load (so a bad face names its file before any
    // GL) and resolved to atlas tiles in BakeModels, the first point the tiles exist.
    internal static readonly List<(ushort Id, BiomeTintPalette Palette, BlockDefinition.BiomeTintFaceDefinition?[] Faces, string Path)>
        BiomeTintPending = [];

    // Blocks declaring a "modelTint": a sub-cell model block whose own quads are climate-tinted,
    // validated and resolved in BakeModels beside the cube-face biomeTint list.
    internal static readonly List<(ushort Id, string? Palette, string Path)> ModelTintPending = [];

    internal static string AssetsRoot = "assets";
    internal static ModelLoader? ModelFiles;

    // Every sfx group discovered under assets/sfx at load, used to validate block "sfx" names.
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> SfxGroups =
        new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Parses <c>data/items</c>, <c>data/blocks</c>, <c>data/loot</c> and every model they
    /// reference, assigns ids (air = 0, then blocks by name, then items by name) and publishes
    /// the runtime tables, plus the <c>data/effects</c> status effects and the <c>data/spell</c> parts, which take no block or item ids. Models are flattened here but not baked — that needs the atlas.
    /// </summary>
    public static void Load(string assetsRoot)
    {
        AssetsRoot = assetsRoot;
        Reset();

        ModelFiles = new ModelLoader(Path.Combine(assetsRoot, "models"), ContentFiles.JsonOptions);
        SfxGroups = SfxLibrary.Scan(Path.Combine(assetsRoot, "sfx"));

        var blockFiles = ContentFiles.JsonFilesIn(Path.Combine(assetsRoot, "data", "blocks"));
        var itemFiles = ContentFiles.JsonFilesIn(Path.Combine(assetsRoot, "data", "items"));

        // 0 is air, so blocks and items share the remaining ids; the per-id tables are sized to
        // exactly this many before any loader writes one. ushort.MaxValue is never assigned: it is the
        // wire's "the server has no such id" (WireIds.NotOnServer)
        var total = 1 + blockFiles.Count + itemFiles.Count;
        if (total > ushort.MaxValue)
            throw new InvalidOperationException(
                $"{blockFiles.Count} blocks + {itemFiles.Count} items exceed the {ushort.MaxValue - 1} ids a block or item id can hold.");
        BlockTables.Allocate(total);

        var nextId = 1;
        foreach (var (name, path) in blockFiles)
        {
            if (name == "air")
                throw new InvalidOperationException($"'{path}' may not be named 'air': id 0 is reserved for empty cells.");
            BlockIds[name] = (ushort)nextId++;
        }

        foreach (var (name, _) in itemFiles)
            ItemIds[name] = (ushort)nextId++;

        ItemLoader.LoadItems(itemFiles);
        LootTableCompiler.LoadLootTables(Path.Combine(assetsRoot, "data", "loot"));
        BlockLoader.LoadBlocks(blockFiles);
        PublishBlockItems();
        TagLoader.LoadTags(Path.Combine(assetsRoot, "data", "tags"));
        RecipeLoader.LoadRecipes(Path.Combine(assetsRoot, "data", "recipes"));
        RecipeLoader.LoadCookRecipes(Path.Combine(assetsRoot, "data", "recipes", "cook"));
        RecipeLoader.LoadCrushRecipes(Path.Combine(assetsRoot, "data", "recipes", "crush"));
        RecipeLoader.LoadMoltenMaterials(Path.Combine(assetsRoot, "data", "molten"));
        RecipeLoader.LoadMeltRecipes(Path.Combine(assetsRoot, "data", "recipes", "melt"));
        RecipeLoader.LoadCastRecipes(Path.Combine(assetsRoot, "data", "recipes", "cast"));
        RecipeLoader.LoadAlloyRecipes(Path.Combine(assetsRoot, "data", "recipes", "alloy"));
        RecipeLoader.LoadForgeRecipes(Path.Combine(assetsRoot, "data", "recipes", "forge"));
        EntityLoader.LoadEntities(assetsRoot);
        ChestModel.Load(assetsRoot);
        TreeSpeciesLoader.LoadTreeSpecies(Path.Combine(assetsRoot, "data", "trees"));
        StatusEffectLoader.LoadEffects(Path.Combine(assetsRoot, "data", "effects"));
        SpellPartLoader.LoadSpellParts(Path.Combine(assetsRoot, "data", "spell"));
        ItemLoader.ValidateItemUses(itemFiles);
        ItemLoader.ValidateGear();
        EntityLoader.ValidateImmunities();
        GearSetLoader.LoadSets(Path.Combine(assetsRoot, "data", "sets"));
        StructureLoader.LoadStructures(Path.Combine(assetsRoot, "data", "structures"));
        QuestLoader.LoadQuests(Path.Combine(assetsRoot, "data", "quests"));
    }

    /// <summary>
    /// Wraps every placeable block in a <see cref="BlockItem"/> and registers those alongside
    /// the pure items, so drops, recipes and the hotbar resolve blocks and items through one
    /// <see cref="ItemRegistry"/>. Runs after <see cref="BlockLoader.LoadBlocks"/>, which is when the
    /// HasItem/fluid flags first exist; the mirror is <see cref="Inventory.CategoryOf"/>.
    /// </summary>
    private static void PublishBlockItems()
    {
        var blocks = BlockType.Types.Values
            .Where(type => type is { HasItem: true, Fluid: false })
            .Select(type => new BlockItem(type))
            .ToList();

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

            // A cutout block must never hide a neighbor's face, whatever the author wrote:
            // a face culled behind one shows through the texture's holes as an X-ray.
            // A translucent block must never hide a neighbor's face either, or the
            // neighbor shows a hole through the glass.
            type.OccludesNeighbors = !type.Cutout && !type.Translucent && (definition.Occludes ?? baked.IsFullCube);
            type.PassesLight = definition.Light?.Passes ?? !baked.IsFullCube;

            // A fluid's model is a full cube (it only supplies the tile), but its surface sits
            // below the cell top and it must never hide a neighbor or shade its own cell dark
            if (type.Fluid)
            {
                type.OccludesNeighbors = false;
                type.PassesLight = true;
            }

            switch (type.Renderer)
            {
                // A multiblock cell's model says nothing about the cell it sits in: the anchor holds
                // the whole structure (so its bake is never a full cube of its own cell) and a
                // part draws nothing at all. The surface of the structure really does cover its
                // neighbors, so it occludes by default; and its quads carry no cullface, so they
                // are lit from the anchor's own cell, which means light has to reach that cell.
                // Explicit JSON still wins, and the cutout/translucent guard above is kept.
                // Tree wood is a full cube at full thickness and a thin node and tubes below it, and
                // the per-id mirrors cannot see the state byte: it never hides a neighbor (the mesher
                // culls same-block full-thickness faces itself) and always lets light through
                case BlockRenderer.TreeWood:
                    type.OccludesNeighbors = false;
                    type.PassesLight = true;
                    break;
                case BlockRenderer.Multiblock or BlockRenderer.MultiblockPart:
                    type.OccludesNeighbors = !type.Cutout && !type.Translucent && (definition.Occludes ?? true);
                    type.PassesLight = definition.Light?.Passes ?? true;
                    break;
            }

            if (type.Renderer == BlockRenderer.Multiblock)
            {
                ModelBakeSteps.ValidateMultiblockBake(type, baked);
                type.RotatedModels =
                [
                    baked,
                    MultiblockShape.Rotate(baked, 1, type.MultiblockSize),
                    MultiblockShape.Rotate(baked, 2, type.MultiblockSize),
                    MultiblockShape.Rotate(baked, 3, type.MultiblockSize),
                ];
            }

            // A swaying sub-cell model is a plant standing on the ground, not a canopy cube hung
            // between its neighbors: its sway grows from its own base to its own top.
            if (type.Sway && type.Renderer == BlockRenderer.Model && !baked.IsFullCube && baked.Quads.Length > 0)
            {
                var minY = float.MaxValue;
                var maxY = float.MinValue;
                foreach (var quad in baked.Quads)
                foreach (var position in quad.Positions)
                {
                    minY = MathF.Min(minY, position.Y);
                    maxY = MathF.Max(maxY, position.Y);
                }

                type.SwaysFromRoot = true;
                type.SwayBaseY = minY;
                type.SwayTopY = maxY;
            }

            // A facing block (a wall torch) is authored facing north and turned about its own cell
            // to the other three; the mesher picks the model by the state byte's low two bits
            if (type.HasHorizontalFacing)
            {
                ModelBakeSteps.ValidateFacingBake(type, baked);
                type.RotatedModels =
                [
                    baked,
                    HorizontalFacing.RotateModel(baked, 1, 1f),
                    HorizontalFacing.RotateModel(baked, 2, 1f),
                    HorizontalFacing.RotateModel(baked, 3, 1f),
                ];

                // An openable block's open model follows its four closed ones, indexed by the
                // state byte's open bit beside the facing (DoorState.ShapeIndex)
                if (type.Openable)
                {
                    var open = ModelBakeSteps.BakeOpenModel(type, OpenModels[id]);
                    ModelBakeSteps.ValidateFacingBake(type, open);
                    type.RotatedModels =
                    [
                        .. type.RotatedModels,
                        open,
                        HorizontalFacing.RotateModel(open, 1, 1f),
                        HorizontalFacing.RotateModel(open, 2, 1f),
                        HorizontalFacing.RotateModel(open, 3, 1f),
                    ];
                }
            }

            BlockType.OccludesById[id] = type.OccludesNeighbors;
            BlockType.FullCubeById[id] = type.Solid && type.Renderer == BlockRenderer.Model && !type.Fluid && baked.IsFullCube;
            BlockType.PassesLightById[id] = type.PassesLight;
            BlockType.GreedyById[id] = type.Renderer == BlockRenderer.Model && !type.Fluid && ModelBakeSteps.BakesGreedy(baked);
        }

        // Needs every block's greedy flag and face tiles, settled just above.
        ModelBakeSteps.ResolveBiomeTints();
        ModelBakeSteps.ResolveModelTints();

        // An item that declares a model bakes exactly like a block's, minus every cell-shaped
        // conclusion (occlusion, light, greedy): it is drawn as an entity mesh, never as a cell.
        foreach (var (id, model) in ItemModels)
        {
            var item = ItemRegistry.TryGet(id)
                ?? throw new InvalidOperationException(
                    $"Item id {id} declares model '{model.Source}' but no item with that id is registered.");
            try
            {
                item.BakedModel = ModelBaker.Bake(model);
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Failed to bake the model of item '{item.Name}' ('{model.Source}'): {e.Message}", e);
            }
        }

        // A molten pool is drawn as one tinted atlas tile, so its slice resolves with the blocks'.
        foreach (var material in MoltenMaterialsList)
        {
            try
            {
                material.Tile = TextureAtlas.TileOf(material.TextureName);
            }
            catch (KeyNotFoundException e)
            {
                throw new InvalidOperationException(
                    $"Molten material 'data/molten/{material.Name}.json' names texture '{material.TextureName}', which is not an atlas tile.", e);
            }
        }

        // Only now do the blocks know their FaceTiles, which is what the material table is keyed by.
        TileMaterials.Build(TextureAtlas.LayerCount, BlockType.Types.Values);

        ModelBakeSteps.SelfCheckBuiltInParents();
    }

    /// <summary>
    /// Id of a block or item by name. A bare name resolves against blocks first, then
    /// items; a <c>block/</c> or <c>item/</c> prefix pins it to one of the two.
    /// </summary>
    public static ushort Require(string name)
    {
        if (ContentFiles.TryResolve(name, out var id)) return id;
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

    /// <summary>
    /// The id of the block registered under <paramref name="name"/> (its JSON file name), or
    /// false when no block has that name. Blocks only, like <see cref="RequireBlock"/>; the chunk
    /// save loader uses it to map a saved palette name back to this run's id, so a renamed or
    /// removed block degrades to air instead of failing the load. Read-only after
    /// <see cref="Load"/>, so worker threads may call it.
    /// </summary>
    public static bool TryResolveBlock(string name, out ushort id) => BlockIds.TryGetValue(name, out id);

    /// <summary>
    /// The id of the pure item registered under <paramref name="name"/> (its JSON file name), or
    /// false when no item has that name. Items only, the counterpart of
    /// <see cref="TryResolveBlock"/>; the player and entity save loaders use it (through
    /// <see cref="SaveNames"/>) to map a saved item name back to this run's id, so a removed item
    /// is dropped instead of failing the load. Read-only after <see cref="Load"/>, so any thread
    /// may call it.
    /// </summary>
    public static bool TryResolveItem(string name, out ushort id) => ItemIds.TryGetValue(name, out id);

    /// <summary>The item registered under that JSON file name; items only, like
    /// <see cref="RequireBlock"/>.</summary>
    public static Item RequireItem(string name) => ItemRegistry.RequireItem(name);

    /// <summary>The compiled loot table of that name, or null when there is none.</summary>
    public static LootTable? LootTableFor(string name) =>
        LootTables.GetValueOrDefault(name);

    /// <summary>Every compiled crafting recipe, in file-name order.</summary>
    public static IReadOnlyList<CraftingRecipe> CraftingRecipes => Recipes;

    /// <summary>First recipe making this id, or null when nothing crafts it.</summary>
    public static CraftingRecipe? RecipeForResult(ushort id)
    {
        foreach (var recipe in Recipes)
            if (recipe.ResultId == id) return recipe;
        return null;
    }

    /// <summary>Recipe by file name, or null when there is none.</summary>
    public static CraftingRecipe? RecipeByName(string name)
    {
        foreach (var recipe in Recipes)
            if (recipe.Name.Equals(name, StringComparison.Ordinal)) return recipe;
        return null;
    }

    /// <summary>First cook recipe taking this id, or null when it is not cookable.</summary>
    public static CookRecipe? CookRecipeForInput(ushort id)
    {
        foreach (var recipe in CookRecipes)
            if (recipe.InputId == id) return recipe;
        return null;
    }

    /// <summary>First crush recipe taking this id, or null when it is not crushable.</summary>
    public static CrushRecipe? CrushRecipeForInput(ushort id)
    {
        foreach (var recipe in CrushRecipes)
            if (recipe.InputId == id) return recipe;
        return null;
    }

    /// <summary>Every molten material, in file-name order; ids are 1-based positions in it.</summary>
    public static IReadOnlyList<MoltenMaterial> MoltenMaterials => MoltenMaterialsList;

    /// <summary>Molten material by file name, or null when there is none.</summary>
    public static MoltenMaterial? MoltenMaterialByName(string name)
    {
        foreach (var m in MoltenMaterialsList)
            if (m.Name.Equals(name, StringComparison.Ordinal)) return m;
        return null;
    }

    /// <summary>Molten material by id, or null for 0 ("no material") and anything out of range.</summary>
    public static MoltenMaterial? MoltenMaterialById(byte id) =>
        id >= 1 && id <= MoltenMaterialsList.Count ? MoltenMaterialsList[id - 1] : null;

    /// <summary>The melt recipe taking this id, or null when it does not melt.</summary>
    public static MeltRecipe? MeltRecipeForInput(ushort id)
    {
        foreach (var recipe in MeltRecipes)
            if (recipe.InputId == id) return recipe;
        return null;
    }

    /// <summary>Every compiled metalworks recipe, in file-name order.</summary>
    public static IReadOnlyList<CastRecipe> CastRecipesList => CastRecipes;

    /// <summary>The cast recipe for this mold and molten material, or null when the pair casts nothing.</summary>
    public static CastRecipe? CastRecipeFor(ushort moldId, byte materialId)
    {
        foreach (var recipe in CastRecipes)
            if (recipe.MoldId == moldId && recipe.MaterialId == materialId) return recipe;
        return null;
    }

    /// <summary>Every compiled alloy recipe, in file-name order: what an alloy kiln makes of a
    /// crucible's mixed metals (<see cref="Simulation.AlloyRules"/>).</summary>
    public static IReadOnlyList<AlloyRecipe> AlloyRecipes => AlloyRecipesList;

    /// <summary>Every compiled anvil recipe, in file-name order (<see cref="Simulation.Smithing.AnvilManager"/>).</summary>
    public static IReadOnlyList<ForgeRecipe> ForgeRecipes => ForgeRecipesList;

    /// <summary>The forge recipe making this id, or null when no anvil forges it.</summary>
    public static ForgeRecipe? ForgeRecipeForResult(ushort id)
    {
        foreach (var recipe in ForgeRecipesList)
            if (recipe.ResultId == id) return recipe;
        return null;
    }

    /// <summary>Whether this id is a forge metal: some forge recipe takes it, so it stacks on an anvil.</summary>
    public static bool IsForgeMetal(ushort id)
    {
        if (id == BlockType.Air) return false;
        foreach (var recipe in ForgeRecipesList)
            if (recipe.MetalId == id) return true;
        return false;
    }

    /// <summary>Whether any cast recipe pours into this block, i.e. whether it is a usable mold.</summary>
    public static bool IsCastMold(ushort id)
    {
        if (id == BlockType.Air) return false;
        foreach (var recipe in CastRecipes)
            if (recipe.MoldId == id) return true;
        return false;
    }

    private static void Reset()
    {
        BlockIds.Clear();
        ItemIds.Clear();
        LootTables.Clear();
        Tags.Clear();
        Recipes.Clear();
        CookRecipes.Clear();
        CrushRecipes.Clear();
        MoltenMaterialsList.Clear();
        MeltRecipes.Clear();
        CastRecipes.Clear();
        AlloyRecipesList.Clear();
        ForgeRecipesList.Clear();
        BlockModels.Clear();
        OpenModels.Clear();
        ItemModels.Clear();
        BlockDefinitions.Clear();
        TextureSheetRegistry.Reset();
        BiomeTintPending.Clear();
        ModelTintPending.Clear();
        SfxGroups = new Dictionary<string, IReadOnlyList<string>>();
        ItemRegistry.Reset();
        EntityRegistry.Reset();
        ChestModel.Reset();
        TreeSpeciesRegistry.Reset();
        SpellPartRegistry.Reset();
        StatusEffectRegistry.Reset();
        QuestRegistry.Reset();

        BlockLightPalette.Reset();
        TileMaterials.Reset();
        ModelFiles = null;
    }

    /// <summary>Whether a block id belongs to a loaded tag.</summary>
    public static bool IsInTag(string tag, ushort id) =>
        Tags.TryGetValue(tag, out var members) && members.Contains(id);
}
