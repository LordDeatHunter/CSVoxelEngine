using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Data;
using VoxelEngine.Interaction;
using VoxelEngine.Items;

namespace VoxelEngine.Content;

/// <summary>Loads <c>data/items</c> into <see cref="VoxelEngine.Items.ItemRegistry"/> (use action, tool, attack, fuel, crucible, spritesheet, held display, optional model), and once every block exists checks each declared use against the finished tables.</summary>
internal static class ItemLoader
{
    internal static void LoadItems(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<byte, Item>();
        foreach (var (name, path) in files)
        {
            var definition = ContentFiles.ReadJson<ItemDefinition>(path);
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
                "heal" => ItemUseAction.Heal,
                "grow_tree" => ItemUseAction.GrowTree,
                "plant_tree" => ItemUseAction.PlantTree,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.action '{use.Action}'; expected pickup_fluid, place_fluid, place_stack, ignite_campfire, convert_multiblock, heal, grow_tree or plant_tree."),
            };

            var durability = definition.Durability ?? 0;
            if (durability < 0 || durability > 1000)
                throw new InvalidOperationException(
                    $"Item '{path}' has durability {durability}; expected 0..1000.");
            var useDelay = definition.UseDelay ?? 0f;
            if (!float.IsFinite(useDelay) || useDelay < 0f || useDelay > 60f)
                throw new InvalidOperationException(
                    $"Item '{path}' has useDelay {definition.UseDelay}; expected 0..60 seconds.");
            var useSeconds = use?.Seconds ?? 0f;
            if (!float.IsFinite(useSeconds) || useSeconds < 0f || useSeconds > 10f)
                throw new InvalidOperationException(
                    $"Item '{path}' has use.seconds {use?.Seconds}; expected 0..10 seconds.");
            var usePose = use?.Pose?.Trim().ToLowerInvariant() switch
            {
                null or "" or "raise" => UsePose.Raise,
                "bow" => UsePose.Bow,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.pose '{use!.Pose}'; expected raise or bow."),
            };
            if (usePose == UsePose.Bow && useSeconds <= 0f)
                throw new InvalidOperationException($"Item '{path}' has use.pose bow but no use.seconds to draw over.");
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

            // A vessel whose contents ride on its stack; the capacity is what the melt gate reads
            var crucibleCapacity = 0;
            if (definition.Crucible is { } crucible)
            {
                crucibleCapacity = crucible.CapacityMl ?? 0;
                if (crucibleCapacity < 100 || crucibleCapacity > 10000)
                    throw new InvalidOperationException(
                        $"Item '{path}' has crucible.capacityMl {crucible.CapacityMl}; expected 100..10000 milliliters.");
            }

            var staff = ParseStaff(definition, path);

            var id = ContentRegistry.ItemIds[name];

            // A 3D model for station display; the sprite above stays the inventory icon
            if (!string.IsNullOrWhiteSpace(definition.Model))
            {
                try
                {
                    ContentRegistry.ItemModels[id] = ContentRegistry.ModelFiles!.Resolve(definition.Model);
                }
                catch (Exception e) when (e is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"Item '{path}': {e.Message}", e);
                }
            }

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
                null or "" or "punch" => AttackPattern.Punch,
                "slice" => AttackPattern.Slice,
                "sword" => AttackPattern.Sword,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown attack '{definition.Attack}'; expected punch, slice or sword."),
            };
            var trailColor = ParseTrail(definition.Trail, attack, path);

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

            if (definition.ThrowAligned && throwDamage == null)
                throw new InvalidOperationException($"Item '{path}' sets throwAligned but has no throwDamage.");

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
                FuelSeconds: fuelSeconds,
                CrucibleCapacityMl: crucibleCapacity,
                UseSeconds: useSeconds,
                TreeName: use?.Tree,
                TrailColor: trailColor,
                ThrowAligned: definition.ThrowAligned,
                UsePose: usePose,
                StaffSlots: staff?.Slots ?? 0,
                StaffTier: staff?.Tier ?? 0,
                StaffTags: staff?.Tags));
        }

        ItemRegistry.Publish(types, new Dictionary<string, byte>(ContentRegistry.ItemIds, StringComparer.Ordinal));
    }

    /// <summary>
    /// The <c>trail</c> object's color, opaque, or null when the item names none. Only a blade
    /// (a slice or sword attack) leaves a trail, so a trail on a punch item is refused.
    /// </summary>
    internal static Rgba? ParseTrail(ItemDefinition.TrailDefinition? trail, AttackPattern attack, string path)
    {
        if (trail == null) return null;
        if (attack == AttackPattern.Punch)
            throw new InvalidOperationException(
                $"Item '{path}' has a trail, but only slice and sword items leave one.");
        if (trail.Color is not { Length: 3 } color || color.Any(c => c is < 0 or > 255))
            throw new InvalidOperationException(
                $"Item '{path}' has a trail.color that is not three integers in 0..255.");
        return new Rgba((byte)color[0], (byte)color[1], (byte)color[2]);
    }

    /// <summary>
    /// The <c>staff</c> object's slots, tier and allowed tags, or null when the item is not a staff.
    /// Checks the shape only: spell parts load after items, so the tags are not matched against
    /// any part here. A staff is neither a crucible nor a <c>use</c> item (its right-click is its cast).
    /// </summary>
    internal static (int Slots, int Tier, IReadOnlySet<string> Tags)? ParseStaff(ItemDefinition definition,
        string path)
    {
        if (definition.Staff is not { } staff) return null;
        if (definition.Crucible != null)
            throw new InvalidOperationException($"Item '{path}' is both a staff and a crucible.");
        if (definition.Use != null)
            throw new InvalidOperationException(
                $"Item '{path}' is a staff but declares a use; a staff's right-click is its cast.");
        if (staff.Slots is not { } slots || slots < 1 || slots > SpellConfig.StaffMaxSlots)
            throw new InvalidOperationException(
                $"Item '{path}' has staff.slots {staff.Slots}; expected 1..{SpellConfig.StaffMaxSlots}.");
        if (staff.Tier is not { } tier || tier < 1)
            throw new InvalidOperationException($"Item '{path}' has staff.tier {staff.Tier}; expected 1 or more.");
        if (staff.Tags is not { Length: > 0 } tags)
            throw new InvalidOperationException($"Item '{path}' has a staff with no tags; it would allow no spell part.");

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            if (string.IsNullOrWhiteSpace(tag))
                throw new InvalidOperationException($"Item '{path}' has a blank staff tag.");
            if (!set.Add(tag))
                throw new InvalidOperationException($"Item '{path}' lists staff tag '{tag}' twice.");
        }
        return (slots, tier, set);
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

        var scale = display.Scale ?? 1f;
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new InvalidOperationException(
                $"Item '{path}' has display.scale {display.Scale}; expected a positive number.");

        return new ParsedDisplay(ReadTriple(display.Rotation, "rotation"),
            ReadTriple(display.Translation, "translation"), scale);

        Vector3 ReadTriple(float[]? values, string field)
        {
            if (values == null) return field == "rotation" ? new Vector3(-30f, 10f, 0f) : Vector3.Zero;
            if (values.Length != 3 || values.Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException(
                    $"Item '{path}' has display.{field} with {values?.Length ?? 0} entries; expected 3 finite numbers.");
            return new Vector3(values[0], values[1], values[2]);
        }
    }

    // Items are parsed before blocks, so only the id maps exist here; what those ids turn out
    // to be is checked in ValidateItemUses once the runtime tables are filled.
    private static byte ResolveUseTarget(string? name, string field, string path)
    {
        if (name == null) return 0;

        if (ContentFiles.TryResolve(name, out var id)) return id;
        throw new InvalidOperationException(
            $"Item '{path}' names '{name}' in {field}, which is not a registered block or item.");
    }

    /// <summary>
    /// Checks each item's declared use against the finished tables: a fluid action needs a real
    /// fluid block to work on and an item to turn into (the hotbar swaps one for the other),
    /// while a stack action needs a real stackable block and no result (it spends from its
    /// own stack instead of morphing, so it stays a plain pickup).
    /// Runs after <see cref="BlockLoader.LoadBlocks"/>, which is when the fluid and stack flags first exist.
    /// </summary>
    internal static void ValidateItemUses(List<(string Name, string Path)> files)
    {
        foreach (var (name, path) in files)
        {
            var item = ItemRegistry.ById[ContentRegistry.ItemIds[name]];
            switch (item.UseAction)
            {
                case ItemUseAction.None:
                    break;
                case ItemUseAction.PlaceStack:
                    ValidatePlaceStack(item, path);
                    break;
                case ItemUseAction.IgniteCampfire:
                    ValidateIgniteCampfire(item, path);
                    break;
                case ItemUseAction.Heal:
                    ValidateHeal(item, path);
                    break;
                case ItemUseAction.GrowTree:
                    ValidateGrowTree(item, path);
                    break;
                case ItemUseAction.PlantTree:
                    ValidatePlantTree(item, path);
                    break;
                case ItemUseAction.ConvertMultiblock:
                    ValidateConvertMultiblock(item, path);
                    break;
                default:
                    ValidateFluidAction(item, path);
                    break;
            }
        }
    }

    private static void ValidatePlaceStack(Item item, string path)
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
    }

    private static void ValidateIgniteCampfire(Item item, string path)
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
    }

    private static void ValidateHeal(Item item, string path)
    {
        if (item.UseResultId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'heal' with a use.result: a heal spends from its stack, it never morphs.");
        if (item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'heal' with a use.fluid or use.block: a heal targets nobody.");
    }

    private static void ValidateGrowTree(Item item, string path)
    {
        if (item.UseResultId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'grow_tree' with a use.result: a growth spends from its stack, it never morphs.");
        if (item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'grow_tree' with a use.fluid or use.block: the tree is read from the aimed cell.");
    }

    private static void ValidatePlantTree(Item item, string path)
    {
        if (string.IsNullOrWhiteSpace(item.UseTreeName))
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'plant_tree' without use.tree: name the species file under assets/data/trees/.");
        if (item.UseResultId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'plant_tree' with a use.result: planting spends from its stack, it never morphs.");
        if (item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'plant_tree' with a use.fluid or use.block: the ground is read from the aimed cell.");
    }

    private static void ValidateConvertMultiblock(Item item, string path)
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
    }

    // The bucket actions: pour or scoop a fluid block, morphing into the use.result item.
    private static void ValidateFluidAction(Item item, string path)
    {
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
