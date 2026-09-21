using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>
/// Builds the <see cref="Item"/> singletons from parsed JSON. The <c>use.action</c> string picks
/// the behaviour family; a single item goes further by registering its own code class in
/// <see cref="CustomCreators"/> under its file name (see the firestarter default), which wins
/// over the action mapping. A new <c>use.action</c> value needs a new <see cref="Item"/>
/// subclass plus one arm here — that is deliberate: behaviour is code, data stays in JSON.
/// </summary>
public static class ItemFactory
{
    /// <summary>Parsed <c>assets/data/items/&lt;name&gt;.json</c>: everything a subclass needs.</summary>
    public sealed record ItemSpec(
        byte Id,
        string Name,
        string TexturePath,
        ItemUseAction Action,
        byte ResultId,
        byte FluidId,
        byte BlockId,
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
        float HitDelayMs,
        AttackPattern Attack,
        float BlockDamage,
        float EntityDamage,
        float? ThrowDamage,
        string SourceName,
        // Appended with a default so the positional construction in the tests keeps compiling
        float FuelSeconds = 0f);

    /// <summary>
    /// Per-item code classes by file name. The factory consults these before the action mapping,
    /// so e.g. "firestarter" always builds a <see cref="FirestarterItem"/> whatever its action
    /// string says. Add one line here plus the class to give an item its own behaviour.
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
        return spec.Action switch
        {
            ItemUseAction.PickupFluid => new BucketPickupItem(spec),
            ItemUseAction.PlaceFluid => new BucketPlaceItem(spec),
            ItemUseAction.PlaceStack => new PlaceStackItem(spec),
            ItemUseAction.IgniteCampfire => new IgniteItem(spec),
            ItemUseAction.ConvertMultiblock => new ConvertMultiblockItem(spec),
            _ => new PlainItem(spec),
        };
    }
}
