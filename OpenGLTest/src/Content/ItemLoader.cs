using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using VoxelEngine.Content.StatusEffects;
using VoxelEngine.Data;
using VoxelEngine.Entities;
using VoxelEngine.Interaction;
using VoxelEngine.Items;

namespace VoxelEngine.Content;

/// <summary>Loads <c>data/items</c> into <see cref="VoxelEngine.Items.ItemRegistry"/> (use action, tool, attack, fuel, crucible, spritesheet, held display, optional model), and once every block exists checks each declared use against the finished tables.</summary>
internal static class ItemLoader
{
    internal static void LoadItems(List<(string Name, string Path)> files)
    {
        var types = new Dictionary<ushort, Item>();
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
                "learn_parts" => ItemUseAction.LearnParts,
                "restore_mana" => ItemUseAction.RestoreMana,
                "summon" => ItemUseAction.Summon,
                _ => throw new InvalidOperationException(
                    $"Item '{path}' has unknown use.action '{use.Action}'; expected pickup_fluid, place_fluid, place_stack, ignite_campfire, convert_multiblock, heal, grow_tree, plant_tree, learn_parts, restore_mana or summon."),
            };
            var teaches = ParseTeaches(definition, action, path);
            var restoresMana = ParseRestoresMana(definition, action, path);

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
            var equip = ParseEquip(definition, staff != null, path);
            var rarity = ParseRarity(definition.Rarity, path);

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
            var shrapnel = ParseShrapnel(definition.Shrapnel, attack, path);

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

            if (definition.ThrowBlock != null && throwDamage == null)
                throw new InvalidOperationException($"Item '{path}' sets throwBlock but has no throwDamage.");

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
                EntityName: use?.Entity,
                TrailColor: trailColor,
                ThrowAligned: definition.ThrowAligned,
                ThrowBlockId: ResolveUseTarget(definition.ThrowBlock, "throwBlock", path),
                UsePose: usePose,
                StaffSlots: staff?.Slots ?? 0,
                StaffTier: staff?.Tier ?? 0,
                StaffTags: staff?.Tags,
                Teaches: teaches,
                RestoresMana: restoresMana,
                Rarity: rarity,
                Equip: equip,
                Shrapnel: shrapnel));
        }

        ItemRegistry.Publish(types, new Dictionary<string, ushort>(ContentRegistry.ItemIds, StringComparer.Ordinal));
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
    /// The <c>shrapnel</c> object's profile, or null when the item names none. Only a blade (a slice or
    /// sword attack) sprays bone fragments, so a shrapnel object on a punch item is refused.
    /// </summary>
    internal static ShrapnelProfile? ParseShrapnel(ItemDefinition.ShrapnelDefinition? shrapnel,
        AttackPattern attack, string path)
    {
        if (shrapnel == null) return null;
        if (attack == AttackPattern.Punch)
            throw new InvalidOperationException(
                $"Item '{path}' has a shrapnel object, but only slice and sword items spray bone fragments.");
        if (shrapnel.Count is not { } count || count is < 1 or > 32)
            throw new InvalidOperationException(
                $"Item '{path}' has shrapnel.count {shrapnel.Count}; expected 1..32 fragments.");
        if (shrapnel.Damage is not { } damage || damage is < 1 or > 100)
            throw new InvalidOperationException(
                $"Item '{path}' has shrapnel.damage {shrapnel.Damage}; expected 1..100.");
        return new ShrapnelProfile(count, damage);
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

    /// <summary>The item's rarity, common when it names none; an unknown name is refused naming the file.</summary>
    internal static Rarity ParseRarity(string? text, string path)
    {
        if (string.IsNullOrWhiteSpace(text)) return Rarity.Common;
        if (Rarities.TryParse(text, out var rarity)) return rarity;
        throw new InvalidOperationException(
            $"Item '{path}' has unknown rarity '{text}'; expected common, uncommon, rare, epic or legendary.");
    }

    /// <summary>
    /// The <c>equip</c> object compiled, or null when the item is not wearable. Gear is neither a staff, a
    /// crucible nor a <c>use</c> item; its slot is required, its modifiers must name known stats with finite
    /// values, its passives known triggers and its abilities known names. Status effect names are matched in <see cref="ValidateGear"/>,
    /// once the effects have loaded.
    /// </summary>
    internal static GearInfo? ParseEquip(ItemDefinition definition, bool isStaff, string path)
    {
        if (definition.Equip is not { } equip) return null;
        if (isStaff || definition.Crucible != null || definition.Use != null)
            throw new InvalidOperationException(
                $"Item '{path}' is wearable gear but also declares a use, staff or crucible.");
        if (!GearSlots.TryParse(equip.Slot, out var slot))
            throw new InvalidOperationException(
                $"Item '{path}' has equip.slot '{equip.Slot}'; expected head, chest, legs, feet, gloves or accessory.");

        var modifiers = new List<GearModifier>();
        if (equip.Armor is { } armor)
        {
            if (armor < 0 || armor > GearConfig.ArmorCap)
                throw new InvalidOperationException(
                    $"Item '{path}' has equip.armor {armor}; expected 0..{GearConfig.ArmorCap}.");
            if (armor > 0) modifiers.Add(new GearModifier(GearStat.Armor, armor));
        }
        if (equip.Modifiers != null)
            foreach (var (statName, value) in equip.Modifiers)
            {
                if (!GearStats.TryParse(statName, out var stat))
                    throw new InvalidOperationException(
                        $"Item '{path}' has equip.modifiers stat '{statName}'; expected armor, maxHealth, maxMana, "
                        + "manaRegen, moveSpeed, miningSpeed, attackSpeed, attackDamage or undeadDamage.");
                if (!float.IsFinite(value) || MathF.Abs(value) > 1000f)
                    throw new InvalidOperationException(
                        $"Item '{path}' has equip.modifiers.{statName} {value}; expected a finite number up to 1000.");
                if (equip.Armor != null && stat == GearStat.Armor)
                    throw new InvalidOperationException(
                        $"Item '{path}' sets armor both as equip.armor and in equip.modifiers.");
                modifiers.Add(new GearModifier(stat, value));
            }

        var immune = new HashSet<string>(StringComparer.Ordinal);
        if (equip.Immune != null)
            foreach (var effect in equip.Immune)
            {
                if (string.IsNullOrWhiteSpace(effect))
                    throw new InvalidOperationException($"Item '{path}' has a blank equip.immune entry.");
                immune.Add(effect.Trim());
            }

        var passives = new List<GearPassive>();
        if (equip.Passives != null)
            foreach (var passive in equip.Passives)
                passives.Add(ParsePassive(passive, path));

        if (string.IsNullOrWhiteSpace(equip.Set) && equip.Set != null)
            throw new InvalidOperationException($"Item '{path}' has a blank equip.set.");
        var abilities = GearAbility.None;
        foreach (var name in equip.Abilities ?? [])
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException($"Item '{path}' has a blank equip.abilities entry.");
        if (!GearAbilities.TryParse(name, out var ability))
            throw new InvalidOperationException(
                $"Item '{path}' has equip.abilities '{name}'; expected air_jump, no_fall_damage, extra_air, magnet or bone_shrapnel.");
            abilities |= ability;
        }
        return new GearInfo(slot, modifiers, equip.Set?.Trim(), equip.Visible ?? false, immune, passives, abilities);
    }

    private static GearPassive ParsePassive(ItemDefinition.PassiveDefinition? passive, string path)
    {
        if (passive == null)
            throw new InvalidOperationException($"Item '{path}' has an empty equip.passives entry.");
        var trigger = passive.Trigger?.Trim().ToLowerInvariant() switch
        {
            "on_hit" => PassiveTrigger.OnHit,
            "on_damaged" => PassiveTrigger.OnDamaged,
            "low_health" => PassiveTrigger.LowHealth,
            _ => throw new InvalidOperationException(
                $"Item '{path}' has passive trigger '{passive.Trigger}'; expected on_hit, on_damaged or low_health."),
        };
        if (string.IsNullOrWhiteSpace(passive.Effect))
            throw new InvalidOperationException($"Item '{path}' has a passive with no effect.");
        var chance = passive.Chance ?? 1f;
        if (!float.IsFinite(chance) || chance <= 0f || chance > 1f)
            throw new InvalidOperationException(
                $"Item '{path}' has passive chance {passive.Chance}; expected 0..1 exclusive of 0.");
        var scale = passive.Scale ?? 1f;
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new InvalidOperationException($"Item '{path}' has passive scale {passive.Scale}; expected above 0.");
        var below = passive.Below ?? 0.3f;
        if (!float.IsFinite(below) || below <= 0f || below > 1f)
            throw new InvalidOperationException(
                $"Item '{path}' has passive below {passive.Below}; expected 0..1 exclusive of 0.");
        return new GearPassive(trigger, passive.Effect.Trim(), chance, scale, below);
    }

    /// <summary>
    /// Matches every gear item's immunities and passive effects against the loaded status effects, so a typo
    /// fails content load naming the item.
    /// </summary>
    internal static void ValidateGear()
    {
        foreach (var item in ItemRegistry.All)
        {
            if (item is not GearItem gear) continue;
            foreach (var effect in gear.Info.Immune)
                if (!StatusEffectRegistry.TryGet(effect, out _))
                    throw new InvalidOperationException(
                        $"Item '{item.SourceName}' is immune to unknown status effect '{effect}'.");
            foreach (var passive in gear.Info.Passives)
                if (!StatusEffectRegistry.TryGet(passive.Effect, out _))
                    throw new InvalidOperationException(
                        $"Item '{item.SourceName}' has a passive applying unknown status effect '{passive.Effect}'.");
        }
    }

    /// <summary>
    /// The <c>teaches</c> list, trimmed, or null when the item has none. Checks the shape only: spell
    /// parts load after items, so each key is matched against them in <see cref="ValidateLearnParts"/>.
    /// The list belongs to, and is required by, <c>use.action: "learn_parts"</c>.
    /// </summary>
    internal static string[]? ParseTeaches(ItemDefinition definition, ItemUseAction action, string path)
    {
        var teaches = definition.Teaches;
        if (action != ItemUseAction.LearnParts)
        {
            if (teaches != null)
                throw new InvalidOperationException($"Item '{path}' has teaches but its use.action is not learn_parts.");
            return null;
        }
        if (teaches is not { Length: > 0 })
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'learn_parts' without teaches: list the spell part keys (<kind>/<name>) it teaches.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var keys = new string[teaches.Length];
        for (var i = 0; i < teaches.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(teaches[i]))
                throw new InvalidOperationException($"Item '{path}' has a blank entry in teaches.");
            keys[i] = teaches[i].Trim();
            if (!seen.Add(keys[i]))
                throw new InvalidOperationException($"Item '{path}' teaches '{keys[i]}' twice.");
        }
        return keys;
    }

    /// <summary>
    /// The <c>restoresMana</c> share, 0 when the item has none. It belongs to, and is required by,
    /// <c>use.action: "restore_mana"</c>, and must lie in (0, 1].
    /// </summary>
    internal static float ParseRestoresMana(ItemDefinition definition, ItemUseAction action, string path)
    {
        var share = definition.RestoresMana;
        if (action != ItemUseAction.RestoreMana)
        {
            if (share != null)
                throw new InvalidOperationException($"Item '{path}' has restoresMana but its use.action is not restore_mana.");
            return 0f;
        }
        if (share is not { } value || !float.IsFinite(value) || value <= 0f || value > 1f)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'restore_mana' without a restoresMana share in (0, 1].");
        return value;
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

    internal readonly record struct ParsedDisplay(Vector3 Rotation, Vector3 Translation, float Scale)
    {
        /// <summary>The transform these values compose to (<see cref="Item.ComposeDisplay"/>).</summary>
        public Matrix4x4 Matrix => Item.ComposeDisplay(Rotation, Translation, Scale);
    }

    /// <summary>
    /// Reads a held-in-hand transform (an item's <c>display</c>, or an entity's <c>held.display</c>),
    /// defaulting to the upright side-facing tool pose when the file says nothing.
    /// </summary>
    internal static ParsedDisplay ParseDisplay(ItemDefinition.DisplayDefinition? display, string path)
    {
        if (display == null)
            return new ParsedDisplay(new Vector3(-30f, 10f, 0f), Vector3.Zero, 1f);

        var scale = display.Scale ?? 1f;
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new InvalidOperationException(
                $"'{path}' has display.scale {display.Scale}; expected a positive number.");

        return new ParsedDisplay(ReadTriple(display.Rotation, "rotation"),
            ReadTriple(display.Translation, "translation"), scale);

        Vector3 ReadTriple(float[]? values, string field)
        {
            if (values == null) return field == "rotation" ? new Vector3(-30f, 10f, 0f) : Vector3.Zero;
            if (values.Length != 3 || values.Any(v => !float.IsFinite(v)))
                throw new InvalidOperationException(
                    $"'{path}' has display.{field} with {values?.Length ?? 0} entries; expected 3 finite numbers.");
            return new Vector3(values[0], values[1], values[2]);
        }
    }

    // Items are parsed before blocks, so only the id maps exist here; what those ids turn out
    // to be is checked in ValidateItemUses once the runtime tables are filled.
    private static ushort ResolveUseTarget(string? name, string field, string path)
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
                    ValidateHeal(item, path, "heal");
                    break;
                case ItemUseAction.RestoreMana:
                    ValidateHeal(item, path, "restore_mana");
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
                case ItemUseAction.LearnParts:
                    ValidateLearnParts(item, path);
                    break;
                case ItemUseAction.Summon:
                    ValidateSummon(item, path);
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

    // A self-restoring use (heal, restore_mana) spends from its stack and targets nobody
    private static void ValidateHeal(Item item, string path, string action)
    {
        if (item.UseResultId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action '{action}' with a use.result: it spends from its stack, it never morphs.");
        if (item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action '{action}' with a use.fluid or use.block: it targets nobody.");
    }

    /// <summary>
    /// A scroll spends from its stack and targets nobody, and every key it teaches must name a loaded
    /// spell part (they load after items and blocks, before this check).
    /// </summary>
    internal static void ValidateLearnParts(Item item, string path)
    {
        if (item.UseResultId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'learn_parts' with a use.result: a scroll spends from its stack, it never morphs.");
        if (item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'learn_parts' with a use.fluid or use.block: a scroll targets nobody.");
        if (item is not LearnPartsItem scroll)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'learn_parts' but is not built as a scroll (a staff or a crucible cannot teach).");
        foreach (var key in scroll.Teaches)
            if (SpellPartRegistry.TryGetByKey(key) == null)
                throw new InvalidOperationException(
                    $"Item '{path}' teaches '{key}', which is not a spell part key (<kind>/<name>).");
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

    private static void ValidateSummon(Item item, string path)
    {
        if (string.IsNullOrWhiteSpace(item.UseEntityName))
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'summon' without use.entity: name the entity file under assets/data/entities/.");
        if (EntityRegistry.TryGet(item.UseEntityName) == null)
            throw new InvalidOperationException(
                $"Item '{path}' summons entity '{item.UseEntityName}', which is not under assets/data/entities/.");
        if (item.UseResultId != 0 || item.UseFluidId != 0 || item.UseBlockId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'summon' with a use.result, use.fluid or use.block: summoning spends from its stack, it never morphs.");
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

    // A converter names no structure: it presses whichever multiblock the clicked cube is the
    // source of (each anchor's own multiblock.source), so it carries none of the use fields
    private static void ValidateConvertMultiblock(Item item, string path)
    {
        if (item.UseBlockId != 0 || item.UseResultId != 0 || item.UseFluidId != 0)
            throw new InvalidOperationException(
                $"Item '{path}' has use.action 'convert_multiblock' with a use.block, use.result or use.fluid: a converter presses any multiblock built from the clicked cube, which each anchor's multiblock.source names.");
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
