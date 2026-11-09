using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json;
using VoxelEngine.Audio;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Loot;
using VoxelEngine.Content.Models;
using VoxelEngine.Content.Shapes;
using VoxelEngine.Effects.Lighting;
using VoxelEngine.Items;

namespace VoxelEngine.Content.Blocks;

/// <summary>Loads <c>data/blocks</c>: validates each block file, builds its <see cref="BlockType"/>, fills the per-id tables and the models and tint lists <see cref="ContentRegistry.BakeModels"/> bakes, then resolves the cross-block references (hardening, multiblock part and source, wall block, upper half).</summary>
internal static class BlockLoader
{
    /// <summary>The <c>light.color</c> a block's JSON declared, or null when it declared none.</summary>
    internal static float[]? DeclaredLightColor(byte id) =>
        ContentRegistry.BlockDefinitions.TryGetValue(id, out var definition) ? definition.Light?.Color : null;

    /// <summary>
    /// The block-light palette slot of a block's <c>light.color</c>: 0 (the default warm
    /// color) when none is declared, else the color's registered slot. Validated like a
    /// molten material's color; a color on a block that emits nothing is an error.
    /// </summary>
    private static byte ResolveLightColor(float[]? color, int emission, string path)
    {
        if (color == null) return 0;
        if (emission == 0)
            throw new InvalidOperationException(
                $"Block '{path}' sets light.color but emits no light; light.color needs light.emission > 0.");
        if (color.Length != 3)
            throw new InvalidOperationException(
                $"Block '{path}' has {color.Length} light.color components; expected exactly 3 (r, g, b in 0..255).");
        foreach (var component in color)
            if (!float.IsFinite(component) || component < 0f || component > 255f)
                throw new InvalidOperationException(
                    $"Block '{path}' has light.color component {component}; expected 0..255.");
        return BlockLightPalette.Register(new Vector3(color[0], color[1], color[2]) / 255f, path);
    }

    internal static void LoadBlocks(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, BlockType>();
        var links = new BlockLinkResolver();

        foreach (var (name, path) in files)
        {
            var definition = ContentFiles.ReadJson<BlockDefinition>(path);
            var id = ContentRegistry.BlockIds[name];

            var type = BuildType(definition, name, id, path, out var stackSolidAt);
            links.Queue(id, definition, path);
            if (definition.BiomeTint != null)
            {
                var (palette, faces) = BiomeTintFaces.Expand(definition.BiomeTint, path);
                ContentRegistry.BiomeTintPending.Add((id, palette, faces, path));
            }

            if (definition.ModelTint != null)
                ContentRegistry.ModelTintPending.Add((id, definition.ModelTint, path));

            ContentRegistry.BlockModels[id] = ResolveModel(definition.Model, path);
            ContentRegistry.BlockDefinitions[id] = definition;
            types[id] = type;
            RegisterTables(type, stackSolidAt);
        }

        links.Resolve(types);
        BlockType.Types = types;

        // The vectorized set of covering ids, built once every block's tables are written
        var coverIds = new List<byte>();
        for (var id = 0; id < BlockType.CoversSkyById.Length; id++)
            if (BlockType.CoversSkyById[id]) coverIds.Add((byte)id);
        BlockType.SkyCoverIds = SearchValues.Create(coverIds.ToArray());
    }

    /// <summary>
    /// Validates one block file and builds its <see cref="BlockType"/>. The rules run in a fixed
    /// order, so a file with several mistakes always reports the same one first.
    /// <paramref name="stackSolidAt"/> is the stack count at which the block turns solid, which
    /// the per-id tables carry but the type does not.
    /// </summary>
    private static BlockType BuildType(BlockDefinition definition, string name, byte id, string path,
        out int stackSolidAt)
    {
        var renderer = BlockFieldRules.ParseRenderer(definition, path);
        var paneProfile = BlockFieldRules.ParsePane(definition, renderer, path);
        BlockFieldRules.ValidateMultiblock(definition, renderer, path);
        var multiblockSize = BlockSchemaParser.ParseMultiblockSize(definition.Multiblock, path);
        BlockFieldRules.ValidateStatusFlags(definition, path);
        BlockFieldRules.ValidateUnbreakable(definition, renderer, path);
        BlockFieldRules.ValidateCoversSky(definition, renderer, path);
        var onBreak = BlockFieldRules.ParseOnBreak(definition, path);
        var (tipHardness, woodLootBands) = BlockFieldRules.ParseWood(definition, renderer, path);

        var emission = BlockFieldRules.ParseEmission(definition, path);
        var lightColorSlot = ResolveLightColor(definition.Light?.Color, emission, path);
        var flamePoints = BlockSchemaParser.ParseFlame(definition, renderer, path);
        var flowTuning = BlockFieldRules.ParseFlow(definition, path);

        // A fluid's behavior follows from being a fluid, so the registry forces it rather
        // than trusting the file: nothing collides with water, it darkens no face, it has
        // no inventory slot or loot, and it is never support-fragile (it is what pops
        // those blocks). It stays replaceable, which is set in RegisterTables for every block.
        var fluid = definition.Fluid ?? false;
        var falls = definition.Falls ?? false;
        var stack = BlockFieldRules.ParseStacking(definition, fluid, falls, path);
        stackSolidAt = stack.StackSolidAt;
        var support = BlockFieldRules.ParseSupport(definition, fluid, falls, path);
        var (horizontalFacing, supportAttached) =
            BlockSchemaParser.ParseFacingAndSupport(definition, renderer, fluid, falls, stack.StackMax,
                support.NeedsSupport, path, out var supportAbove);
        var facingFromLook = BlockSchemaParser.ParseFacingFromLook(definition, horizontalFacing, supportAttached, path);
        var openHitboxes = BlockSchemaParser.ParseOpen(definition, horizontalFacing, path);
        var solid = !fluid && (definition.Solid ?? true);
        BlockFieldRules.ValidateStackSolidity(stack.StackSolidAt, solid, path);
        var replaceableByPlacement = BlockFieldRules.ParseReplaceableByPlacement(definition, fluid, solid, path);

        var ground = BlockFieldRules.ParseGroundTraits(definition, fluid, path);
        var material = BlockSchemaParser.ParseMaterial(definition.Material, path);
        var hardness = BlockFieldRules.ParseHardness(definition, path);
        var (requiresTool, tools) = BlockFieldRules.ParseTools(definition, path);

        var hitboxes = BlockSchemaParser.ParseHitboxes(definition.Hitbox, path);
        var collidesByHitbox = BlockSchemaParser.ResolveCollidesByHitbox(definition, hitboxes, solid, fluid, stack.StackMax, path);
        // A pane's boxes follow its arms (PaneShape); the registered set is the lone post, and
        // a solid pane always collides by that live shape rather than the whole cell
        if (renderer == BlockRenderer.Pane)
        {
            hitboxes = paneProfile!.Boxes(0);
            collidesByHitbox = solid;
        }
        var bodySurface = BlockFieldRules.ParseBodySurface(definition, renderer, fluid, solid, ground.MovementSpeed,
            collidesByHitbox, stack.StackMax, path);

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
            MovementSpeed = ground.MovementSpeed,
            Friction = ground.Friction,
            PebbleWeight = ground.PebbleWeight,
            FootstepDust = ground.FootstepDust,
            Bounces = definition.Bounce ?? false,
            SoftLanding = bodySurface.SoftLanding,
            Sink = bodySurface.Sink,
            BlocksSight = definition.BlocksSight ?? true,
            ContactDamage = ground.ContactDamage,
            CullSelf = definition.CullSelf ?? false,
            // Tree wood is drawn at any thickness down to a 1 px twig, so it never darkens
            // its neighbors, whatever the file says (forced here, where CastsAoById is mirrored)
            CastsAmbientOcclusion = !fluid && (definition.CastsAO ?? solid) && renderer != BlockRenderer.TreeWood,
            Cutout = definition.Cutout ?? false,
            Translucent = definition.Translucent ?? false,
            Sway = definition.Sway ?? false,
            Material = material,
            LightEmission = emission,
            LightColorSlot = lightColorSlot,
            FlamePoints = flamePoints,
            Renderer = renderer,
            Pane = paneProfile,
            MultiblockSize = multiblockSize,
            Kiln = BlockSchemaParser.ParseKiln(definition.Kiln, path, multiblockSize),
            Bloomery = BlockSchemaParser.ParseBloomery(definition.Bloomery, path),
            Anvil = BlockSchemaParser.ParseAnvil(definition.Anvil, renderer, path),
            SpellBuilder = definition.SpellBuilder ?? false,
            Waystone = definition.Waystone ?? false,
            Chest = BlockSchemaParser.ParseChest(definition.Chest, renderer, path),
            StackMax = (byte)stack.StackMax,
            TipHardness = tipHardness,
            WoodLootBands = woodLootBands ?? [new WoodLootBand(0, null, true)],
            HasItem = !fluid && (definition.HasItem ?? true),
            NeedsSupport = support.NeedsSupport,
            FlowResistant = support.FlowResistant,
            SupportAttached = supportAttached,
            SupportAbove = supportAbove,
            HasHorizontalFacing = horizontalFacing,
            FacingFromLook = facingFromLook,
            Openable = openHitboxes != null,
            ReplaceableByPlacement = replaceableByPlacement,
            Falls = falls,
            Fluid = fluid,
            FluidTickDivisor = (byte)flowTuning.TickDivisor,
            FluidRange = (byte)flowTuning.Range,
            FluidInfinite = definition.Flow?.Infinite ?? true,
            OnBreak = onBreak,
            LootTable = fluid ? LootTable.None : ResolveLoot(name, definition.Loot, path),
            Hitboxes = hitboxes ?? [BlockHitbox.FullCube],
            CollidesByHitbox = collidesByHitbox,
            HardensToId = 0,
            HardensTouchingId = 0,
            Flammable = definition.Flammable ?? false,
            Unbreakable = definition.Unbreakable ?? false,
            CoversSky = definition.CoversSky ?? false
        };
        // A facing block's selection boxes turn with it, exactly like its model will in BakeModels
        if (horizontalFacing)
            type.RotatedHitboxes = HorizontalFacing.RotateBoxes(type.Hitboxes);
        // ...and an openable block's open boxes follow the closed ones (DoorState.ShapeIndex)
        if (openHitboxes != null)
        {
            type.RotatedHitboxes = [.. type.RotatedHitboxes!, .. HorizontalFacing.RotateBoxes(openHitboxes)];
            ContentRegistry.OpenModels[id] = ResolveModel(definition.Open!.Model, path);
        }
        return type;
    }

    /// <summary>Writes the flat per-id tables hot paths read instead of <see cref="BlockType.ById"/>.</summary>
    private static void RegisterTables(BlockType type, int stackSolidAt)
    {
        var id = type.Id;
        BlockType.ById[id] = type;
        BlockType.Emission[id] = type.LightEmission;
        BlockType.LightSlot[id] = type.LightColorSlot;
        BlockType.SolidById[id] = type.Solid;
        BlockType.CollidesByHitboxById[id] = type.CollidesByHitbox;
        BlockType.StackSolidStateById[id] = (byte)stackSolidAt;
        BlockType.MovementSpeedById[id] = type.MovementSpeed;
        BlockType.FrictionById[id] = type.Friction;
        BlockType.PebbleWeightById[id] = type.PebbleWeight;
        BlockType.FootstepDustById[id] = type.FootstepDust;
        BlockType.BounceById[id] = type.Bounces;
        BlockType.SoftLandingById[id] = type.SoftLanding;
        BlockType.SinkById[id] = type.Sink;
        BlockType.BlocksSightById[id] = type.BlocksSight;
        BlockType.ContactDamageById[id] = type.ContactDamage;
        BlockType.CastsAoById[id] = type.CastsAmbientOcclusion;
        BlockType.TreeWoodById[id] = type.Renderer == BlockRenderer.TreeWood;
        BlockType.PaneById[id] = type.Renderer == BlockRenderer.Pane;
        BlockType.PaneProfileById[id] = type.Pane;
        BlockType.FluidById[id] = type.Fluid;
        BlockType.FluidTickDivisorById[id] = type.FluidTickDivisor;
        BlockType.FluidRangeById[id] = type.FluidRange;
        BlockType.FluidInfiniteById[id] = type.FluidInfinite;
        BlockType.HardensToById[id] = type.HardensToId;
        BlockType.HardensTouchingById[id] = type.HardensTouchingId;
        BlockType.FlammableById[id] = type.Flammable;
        BlockType.UnbreakableById[id] = type.Unbreakable;
        BlockType.CoversSkyById[id] = type.CoversSky;
        BlockType.FreezesToById[id] = type.FreezesToId;
        BlockType.MeltsToById[id] = type.MeltsToId;
        BlockType.FallsById[id] = type.Falls;
        // Fragile blocks are washed away by a flow instead of stopping it; a flow-resistant
        // one (a cave formation) is rock and stays standing
        BlockType.ReplaceableById[id] = type.NeedsSupport && !type.FlowResistant;
        // A flagged block is taken over in place by a placement aimed at it
        BlockType.ReplaceableByPlacementById[id] = type.ReplaceableByPlacement;
    }

    private static LootTable? ResolveLoot(string blockName, string? loot, string path)
    {
        return loot switch
        {
            // Omitted: the table sharing this block's name, or "drop yourself" when there is none
            null => ContentRegistry.LootTables.GetValueOrDefault(blockName),
            "none" => LootTable.None,
            _ => ContentRegistry.LootTables.TryGetValue(loot, out var table)
                ? table
                : throw new InvalidOperationException(
                    $"Block '{path}' names loot table '{loot}', which does not exist.")
        };
    }

    // A block's sfx is non-fatal: an unknown group is a warning and the default, not a crash,
    // so a block still plays *something* while the audio asset is being authored.
    internal static string ResolveSfx(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return SfxLibrary.DefaultGroup;
        if (ContentRegistry.SfxGroups.ContainsKey(name)) return name;

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
                    return ContentRegistry.ModelFiles!.Resolve(model.GetString()!);
                case JsonValueKind.Object:
                    var inline = model.Deserialize<ModelDefinition>(ContentFiles.JsonOptions)
                                 ?? throw new InvalidOperationException("the inline model is empty");
                    return ContentRegistry.ModelFiles!.ResolveInline(inline, path);
                default:
                    throw new InvalidOperationException("'model' must be a model name or an inline model object");
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Block '{path}': {e.Message}", e);
        }
    }
}
