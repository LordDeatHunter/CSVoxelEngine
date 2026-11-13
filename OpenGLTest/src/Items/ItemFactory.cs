using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Data;

namespace VoxelEngine.Items;

/// <summary>
/// Builds the <see cref="Item"/> singletons from parsed JSON. The <c>use.action</c> string picks
/// the behavior family; a single item goes further by registering its own code class in
/// <see cref="CustomCreators"/> under its file name (see the firestarter default), which wins
/// over the action mapping. A new <c>use.action</c> value needs a new <see cref="Item"/>
/// subclass plus one arm here — that is deliberate: behavior is code, data stays in JSON.
/// </summary>
public static class ItemFactory
{
    /// <summary>Parsed <c>assets/data/items/&lt;name&gt;.json</c>: everything a subclass needs.</summary>
    public sealed record ItemSpec(
        ushort Id,
        string Name,
        string TexturePath,
        ItemUseAction Action,
        ushort ResultId,
        ushort FluidId,
        ushort BlockId,
        float Chance,
        int Durability,
        float UseDelay,
        int SpriteCols,
        int SpriteRows,
        int FrameCount,
        ItemAnimationMode Animation,
        float AnimationFps,
        Vector3 Rotation,
        Vector3 Translation,
        float Scale,
        bool IsTool,
        float ToolPower,
        float? HitDelayMs,
        AttackPattern Attack,
        float BlockDamage,
        float EntityDamage,
        float? ThrowDamage,
        string SourceName,
        // Appended with a default so the positional construction in the tests keeps compiling
        float FuelSeconds = 0f,
        int CrucibleCapacityMl = 0,
        float UseSeconds = 0f,
        string? TreeName = null,
        string? EntityName = null,
        Rgba? TrailColor = null,
        bool ThrowAligned = false,
        ushort ThrowBlockId = 0,
        ushort IngotBlockId = 0,
        UsePose UsePose = UsePose.Raise,
        int StaffSlots = 0,
        int StaffTier = 0,
        IReadOnlySet<string>? StaffTags = null,
        IReadOnlyList<string>? Teaches = null,
        float RestoresMana = 0f,
        int Heals = 0,
        Rarity Rarity = Rarity.Common,
        GearInfo? Equip = null,
        ShrapnelProfile? Shrapnel = null,
        GearPassive? OnHit = null);

    /// <summary>
    /// Per-item code classes by file name. The factory consults these before the action mapping,
    /// so e.g. "firestarter" always builds a <see cref="FirestarterItem"/> whatever its action
    /// string says. Add one line here plus the class to give an item its own behavior.
    /// </summary>
    public static readonly Dictionary<string, Func<ItemSpec, Item>> CustomCreators =
        new(StringComparer.Ordinal)
        {
            ["firestarter"] = spec => new FirestarterItem(spec),
        };

    /// <summary>Registers (or replaces) a per-item code class for <paramref name="name"/>.</summary>
    public static void RegisterCustom(string name, Func<ItemSpec, Item> creator) =>
        CustomCreators[name] = creator;

    internal static Item Create(string fileName, ItemSpec spec)
    {
        if (CustomCreators.TryGetValue(fileName, out var custom))
            return custom(spec);
        // A vessel is a family of its own: its contents live on the stack, so it needs the
        // Tools pool whatever use action (usually none) its JSON declares.
        if (spec.CrucibleCapacityMl > 0)
            return new CrucibleItem(spec);
        // A staff's bound spells live on its stack, so like a vessel it needs the Tools pool
        if (spec.StaffSlots > 0)
            return new StaffItem(spec);
        // Wearable gear has a pool of its own: its rolled modifiers ride on each instance's stack
        if (spec.Equip != null)
            return new GearItem(spec);
        return spec.Action switch
        {
            ItemUseAction.PickupFluid => new BucketPickupItem(spec),
            ItemUseAction.PlaceFluid => new BucketPlaceItem(spec),
            ItemUseAction.PlaceStack => new PlaceStackItem(spec),
            ItemUseAction.PlaceIngot => new PlaceIngotItem(spec),
            ItemUseAction.IgniteCampfire => new IgniteItem(spec),
            ItemUseAction.Heal => new HealItem(spec),
            ItemUseAction.GrowTree => new GrowTreeItem(spec),
            ItemUseAction.PlantTree => new PlantTreeItem(spec),
            ItemUseAction.LearnParts => new LearnPartsItem(spec),
            ItemUseAction.RestoreMana => new RestoreManaItem(spec),
            ItemUseAction.Summon => new SummonItem(spec),
            ItemUseAction.MarkTower => new TowerMapItem(spec),
            _ => new PlainItem(spec),
        };
    }
}
