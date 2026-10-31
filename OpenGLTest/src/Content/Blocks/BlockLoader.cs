using System;
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
    private static byte ResolveHardenTarget(string? name, string field, string path, bool requireFluid, byte ownerId)
    {
        if (name == null) return 0;

        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a registered block.");
        return requireFluid switch
        {
            true when !BlockType.FluidById[id] || id == ownerId => throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is not a different fluid block."),
            false when BlockType.FluidById[id] => throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in {field}, which is a fluid; hardening must produce a solid block."),
            _ => id
        };
    }

    /// <summary>
    /// Resolves a multiblock anchor's <c>multiblock.part</c>: the block that fills the seven
    /// non-anchor cells, which must itself use the part renderer (nothing else draws nothing).
    /// </summary>
    private static byte ResolveMultiblockPart(string? name, string path)
    {
        if (name == null)
            throw new InvalidOperationException($"Block '{path}' names no multiblock.part.");
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
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
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is not a registered block.");
        if (id == ownerId || BlockType.ById[id]!.Renderer is BlockRenderer.Multiblock or BlockRenderer.MultiblockPart)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in multiblock.source, which is itself a multiblock cell; a structure is built from a plain block.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>wallBlock</c>: the variant a placement against a block's side becomes.
    /// It must be another block that faces horizontally and hangs off the wall behind it, and the
    /// owner itself must stand on the floor (support below, no facing of its own), since a placement
    /// on top keeps it. A wall block belongs to one owner, which is what it is picked and named as.
    /// </summary>
    private static byte ResolveWallBlock(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which is not a registered block.");
        var owner = BlockType.ById[ownerId]!;
        var wall = BlockType.ById[id]!;
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in wallBlock.");
        if (!owner.NeedsSupport || owner.SupportAttached || owner.SupportAbove || owner.HasHorizontalFacing)
            throw new InvalidOperationException(
                $"Block '{path}' sets wallBlock but is not a floor block (needsSupport, support below, no facing); a placement on top keeps it.");
        if (!wall.HasHorizontalFacing || !wall.SupportAttached)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which needs facing: \"horizontal\" and support: \"attached\".");
        if (wall.ItemBlockId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in wallBlock, which another block already names; a wall block belongs to one floor block.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>pickAs</c>: the block a middle-click picks instead of it. It must be
    /// another block that has an item, while the owner itself has none (a worldgen-only block picked
    /// as its plain counterpart) and is not already picked as another block through a
    /// <c>wallBlock</c> or <c>upperHalf</c>.
    /// </summary>
    private static byte ResolvePickAs(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in pickAs, which is not a registered block.");
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in pickAs.");
        var owner = BlockType.ById[ownerId]!;
        var target = BlockType.ById[id]!;
        if (!target.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in pickAs, which has no item to pick.");
        if (owner.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' sets pickAs but has an item of its own; only a block with hasItem: false is picked as another.");
        if (owner.ItemBlockId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' sets pickAs but is already picked as another block through a wallBlock or upperHalf.");
        return id;
    }

    /// <summary>
    /// Resolves a block's <c>upperHalf</c>: the block filling the cell above it (a door's top). The
    /// two halves share one state byte, so they must agree on <c>facing</c> and <c>open</c>; the
    /// lower half is the item and the drop, so only it may be held; each half belongs to one pair;
    /// and neither may topple, pop or flow away from the other.
    /// </summary>
    private static byte ResolveUpperHalf(string name, string path, byte ownerId)
    {
        if (!ContentFiles.TryResolve(name, out var id) || id == BlockType.Air || ItemRegistry.IsItem(id))
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf, which is not a registered block.");
        if (id == ownerId)
            throw new InvalidOperationException($"Block '{path}' names itself in upperHalf.");
        var lower = BlockType.ById[ownerId]!;
        var upper = BlockType.ById[id]!;
        if (!lower.HasItem || upper.HasItem)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf; the lower half must be held and the upper half must set hasItem: false.");
        if (upper.UpperHalfId != 0 || upper.LowerHalfId != 0 || upper.ItemBlockId != 0 || lower.LowerHalfId != 0)
            throw new InvalidOperationException(
                $"Block '{path}' names '{name}' in upperHalf, but one of the two already belongs to another block; a half belongs to one pair.");
        if (lower.HasHorizontalFacing != upper.HasHorizontalFacing || lower.Openable != upper.Openable)
            throw new InvalidOperationException(
                $"Block '{path}' and its upperHalf '{name}' disagree on facing or open; the halves share one state byte.");
        foreach (var half in new[] { lower, upper })
            if (half.Fluid || half.Falls || half.NeedsSupport || half.StackMax >= 2)
                throw new InvalidOperationException(
                    $"Block '{path}' pairs with upperHalf '{name}', but '{half.Name}' is a fluid, falls, needs support or stacks; a half must stand with its partner.");
        return id;
    }

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
        var hardenPending = new List<(byte Id, string? To, string? Touching, string Path)>();
        var multiblockPending = new List<(byte Id, string? Part, string? Source, string Path)>();
        var wallPending = new List<(byte Id, string Wall, string Path)>();
        var upperPending = new List<(byte Id, string Upper, string Path)>();
        var pickPending = new List<(byte Id, string Target, string Path)>();

        foreach (var (name, path) in files)
        {
            var definition = ContentFiles.ReadJson<BlockDefinition>(path);
            var id = ContentRegistry.BlockIds[name];

            var renderer = definition.Renderer switch
            {
                null or "model" => BlockRenderer.Model,
                "tree_wood" => BlockRenderer.TreeWood,
                "pebbles" => BlockRenderer.Pebbles,
                "branch_pile" => BlockRenderer.BranchPile,
                "snow_layers" => BlockRenderer.SnowLayers,
                "fluid" => BlockRenderer.Fluid,
                "quern" => BlockRenderer.Quern,
                "multiblock" => BlockRenderer.Multiblock,
                "multiblock_part" => BlockRenderer.MultiblockPart,
                "pane" => BlockRenderer.Pane,
                _ => throw new InvalidOperationException(
                    $"Block '{path}' has unknown renderer '{definition.Renderer}'; expected model, tree_wood, pebbles, branch_pile, snow_layers, fluid, quern, multiblock, multiblock_part or pane."),
            };
            var pane = renderer == BlockRenderer.Pane;
            if (pane)
                BlockSchemaParser.ValidatePane(definition, path);
            else if (definition.Pane != null)
                throw new InvalidOperationException(
                    $"Block '{path}' declares a pane object without renderer \"pane\"; pane.* only shapes a pane.");
            var paneProfile = pane ? BlockSchemaParser.ParsePaneProfile(definition.Pane, path) : null;

            // A multiblock is a structure built out of eight cells: the anchor names the part
            // that fills the other seven and the plain block it is built from and reverts to,
            // and only an anchor can be a kiln. Both halves are ordinary solid full cells to
            // everything but the mesher, so the anchor's surface really does cover its
            // neighbors - which cutout, translucent and fluid would all break.
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
                        $"Block '{path}' uses a multiblock renderer with cutout, translucent or fluid; a multiblock cell must be an opaque solid so the structure occludes its neighbors.");
            }

            var onBreak = definition.OnBreak switch
            {
                null or "drop" => BlockBreakAction.Drop,
                "prime_tnt" => BlockBreakAction.PrimeTnt,
                _ => throw new InvalidOperationException(
                    $"Block '{path}' has unknown onBreak '{definition.OnBreak}'; expected drop or prime_tnt."),
            };

            // Tree wood carries its thickness and parent direction in the state byte; its wood
            // object tunes how hardness and loot follow that thickness, so it means nothing on
            // any other renderer
            var treeWood = renderer == BlockRenderer.TreeWood;
            if (!treeWood && definition.Wood != null)
                throw new InvalidOperationException(
                    $"Block '{path}' declares a wood object without renderer \"tree_wood\"; wood.* only tunes tree wood.");
            var (tipHardness, woodLootBands) = treeWood
                ? BlockSchemaParser.ParseTreeWood(definition, path)
                : (0.25f, (WoodLootBand[]?)null);

            var emission = definition.Light?.Emission ?? 0;
            if (emission > 15)
                throw new InvalidOperationException($"Block '{path}' has light.emission {emission}; expected 0..15.");
            var lightColorSlot = ResolveLightColor(definition.Light?.Color, emission, path);
            var flamePoints = BlockSchemaParser.ParseFlame(definition, renderer, path);

            var flow = definition.Flow;
            if (flow != null && !(definition.Fluid ?? false))
                throw new InvalidOperationException(
                    $"Block '{path}' sets flow.* without fluid: true; flow tuning only applies to fluids.");
            var tickDivisor = flow?.TickDivisor ?? 1;
            if (tickDivisor is < 1 or > 8)
                throw new InvalidOperationException(
                    $"Block '{path}' has flow.tickDivisor {tickDivisor}; expected 1..8.");
            var range = flow?.Range ?? 7;
            if (range is < 1 or > 7)
                throw new InvalidOperationException(
                    $"Block '{path}' has flow.range {range}; expected 1..7 (the state byte holds levels 1..7).");

            // A fluid's behavior follows from being a fluid, so the registry forces it rather
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
            var flowResistant = definition.FlowResistant ?? false;
            if (flowResistant && !needsSupport)
                throw new InvalidOperationException(
                    $"Block '{path}' sets flowResistant without needsSupport: only a fragile block has a wash-away rule to opt out of.");
            var (horizontalFacing, supportAttached) =
                BlockSchemaParser.ParseFacingAndSupport(definition, renderer, fluid, falls, stackMax, needsSupport, path, out var supportAbove);
            var facingFromLook = BlockSchemaParser.ParseFacingFromLook(definition, horizontalFacing, supportAttached, path);
            var openHitboxes = BlockSchemaParser.ParseOpen(definition, horizontalFacing, path);
            var solid = !fluid && (definition.Solid ?? true);
            if (stackSolidAt > 0 && solid)
                throw new InvalidOperationException(
                    $"Block '{path}' sets stackSolidAt on a solid block; state-dependent solidity needs solid: false.");

            // A placement only takes over a non-solid cell, so an always-solid block could never
            // honor the flag: the author's intent cannot work, so it is a content error rather
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

            // Nor is it ground a footfall scuffs: a swimmer kicks up no dust
            if (fluid && definition.FootstepDust == true)
                throw new InvalidOperationException(
                    $"Block '{path}' sets footstepDust on a fluid: nobody runs on a fluid, so it can never puff.");
            var footstepDust = definition.FootstepDust ?? false;
            var bounce = definition.Bounce ?? false;
            var blocksSight = definition.BlocksSight ?? true;
            var contactDamage = definition.ContactDamage ?? 0f;
            if (!float.IsFinite(contactDamage) || contactDamage < 0f || contactDamage > 100f)
                throw new InvalidOperationException(
                    $"Block '{path}' has contactDamage {definition.ContactDamage}; expected 0..100.");

            var material = BlockSchemaParser.ParseMaterial(definition.Material, path);

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

            var hitboxes = BlockSchemaParser.ParseHitboxes(definition.Hitbox, path);
            var collidesByHitbox = BlockSchemaParser.ResolveCollidesByHitbox(definition, hitboxes, solid, fluid, stackMax, path);
            // A pane's boxes follow its arms (PaneShape); the registered set is the lone post, and
            // a solid pane always collides by that live shape rather than the whole cell
            if (pane)
            {
                hitboxes = paneProfile!.Boxes(0);
                collidesByHitbox = solid;
            }

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
                FootstepDust = footstepDust,
                Bounces = bounce,
                BlocksSight = blocksSight,
                ContactDamage = contactDamage,
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
                Kiln = definition.Kiln ?? false,
                StackMax = (byte)stackMax,
                TipHardness = tipHardness,
                WoodLootBands = woodLootBands ?? [new WoodLootBand(0, null, true)],
                HasItem = !fluid && (definition.HasItem ?? true),
                NeedsSupport = needsSupport,
                FlowResistant = flowResistant,
                SupportAttached = supportAttached,
                SupportAbove = supportAbove,
                HasHorizontalFacing = horizontalFacing,
                FacingFromLook = facingFromLook,
                Openable = openHitboxes != null,
                ReplaceableByPlacement = replaceableByPlacement,
                Falls = falls,
                Fluid = fluid,
                FluidTickDivisor = (byte)tickDivisor,
                FluidRange = (byte)range,
                FluidInfinite = flow?.Infinite ?? true,
                OnBreak = onBreak,
                LootTable = fluid ? LootTable.None : ResolveLoot(name, definition.Loot, path),
                Hitboxes = hitboxes ?? [BlockHitbox.FullCube],
                CollidesByHitbox = collidesByHitbox,
                HardensToId = 0,
                HardensTouchingId = 0
            };
            if ((flow?.HardensTo == null) != (flow?.HardensTouching == null))
                throw new InvalidOperationException(
                    $"Block '{path}' sets only one of flow.hardensTo / flow.hardensTouching; hardening needs both.");
            hardenPending.Add((id, flow?.HardensTo, flow?.HardensTouching, path));
            if (definition.Multiblock != null)
                multiblockPending.Add((id, definition.Multiblock.Part, definition.Multiblock.Source, path));
            // A facing block's selection boxes turn with it, exactly like its model will in BakeModels
            if (horizontalFacing)
                type.RotatedHitboxes = HorizontalFacing.RotateBoxes(type.Hitboxes);
            // ...and an openable block's open boxes follow the closed ones (DoorState.ShapeIndex)
            if (openHitboxes != null)
            {
                type.RotatedHitboxes = [.. type.RotatedHitboxes!, .. HorizontalFacing.RotateBoxes(openHitboxes)];
                ContentRegistry.OpenModels[id] = ResolveModel(definition.Open!.Model, path);
            }
            if (definition.WallBlock != null)
                wallPending.Add((id, definition.WallBlock, path));
            if (definition.UpperHalf != null)
                upperPending.Add((id, definition.UpperHalf, path));
            if (definition.PickAs != null)
                pickPending.Add((id, definition.PickAs, path));
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
            BlockType.BlocksSightById[id] = type.BlocksSight;
            BlockType.ContactDamageById[id] = type.ContactDamage;
            BlockType.CastsAoById[id] = type.CastsAmbientOcclusion;
            BlockType.TreeWoodById[id] = treeWood;
            BlockType.PaneById[id] = pane;
            BlockType.PaneProfileById[id] = paneProfile;
            BlockType.FluidById[id] = type.Fluid;
            BlockType.FluidTickDivisorById[id] = type.FluidTickDivisor;
            BlockType.FluidRangeById[id] = type.FluidRange;
            BlockType.FluidInfiniteById[id] = type.FluidInfinite;
            BlockType.HardensToById[id] = type.HardensToId;
            BlockType.HardensTouchingById[id] = type.HardensTouchingId;
            BlockType.FallsById[id] = type.Falls;
            // Fragile blocks are washed away by a flow instead of stopping it; a flow-resistant
            // one (a cave formation) is rock and stays standing
            BlockType.ReplaceableById[id] = type.NeedsSupport && !type.FlowResistant;
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

        // Same reason again: a torch's wall block (wall_torch) loads after the torch itself.
        foreach (var (pendingId, wall, pendingPath) in wallPending)
        {
            var wallId = ResolveWallBlock(wall, pendingPath, pendingId);
            types[pendingId].WallBlockId = wallId;
            types[wallId].ItemBlockId = pendingId;
        }

        // And a door's upper half may load before or after its lower half
        foreach (var (pendingId, upper, pendingPath) in upperPending)
        {
            var upperId = ResolveUpperHalf(upper, pendingPath, pendingId);
            types[pendingId].UpperHalfId = upperId;
            types[upperId].LowerHalfId = pendingId;
            types[upperId].ItemBlockId = pendingId;
        }

        // A pickAs target may load after its owner too; run last, so an owner the loops above
        // already made a wall block or an upper half is refused.
        foreach (var (pendingId, target, pendingPath) in pickPending)
            types[pendingId].ItemBlockId = ResolvePickAs(target, pendingPath, pendingId);

        BlockType.Types = types;
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
