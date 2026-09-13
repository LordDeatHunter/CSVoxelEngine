using System;
using System.Numerics;

namespace silkgltriangle;

/// <summary>How a spritesheet item picks its frame each draw.</summary>
public enum ItemAnimationMode
{
    /// <summary>No sheet: the whole texture is the sprite.</summary>
    None,
    /// <summary>Frame follows the player's compass heading, so frame 0 faces north.</summary>
    Heading,
    /// <summary>Frames slide by at <see cref="Item.AnimationFps"/>.</summary>
    Loop,
}

/// <summary>
/// Source-of-truth data for one item: a static singleton built from
/// <c>assets/data/items/&lt;name&gt;.json</c> by <see cref="ItemFactory"/> and stored in
/// <see cref="ItemRegistry"/>. Instances never change after <see cref="ContentRegistry.Load"/> —
/// per-world state (count, durability, cooldown, rename, enchantments) lives on
/// <see cref="ItemStack"/> instead.
///
/// Behaviour families subclass this (bucket pickup/place, stack placing, igniting) and a single
/// item can go further with its own code class registered in
/// <see cref="ItemFactory.CustomCreators"/> (see <see cref="FirestarterItem"/>). Gameplay asks
/// "what does this item do" through the virtuals below instead of branching on ids.
/// </summary>
public abstract class Item
{
    /// <summary>Byte id assigned at load; shares the block id space (air = 0).</summary>
    public byte Id { get; }

    /// <summary>Display name for inventory rows and preview headings.</summary>
    public string Name { get; }

    /// <summary>Sprite source, relative to the working directory like every other asset.</summary>
    public string TexturePath { get; }

    /// <summary>What right-clicking with this item does; <see cref="ItemUseAction.None"/> for a
    /// plain pickup.</summary>
    public ItemUseAction UseAction { get; internal init; }

    /// <summary>Item id this one becomes after a successful use (a bucket becomes a water
    /// bucket); 0 when the item declares no use.</summary>
    public byte UseResultId { get; internal init; }

    /// <summary>Fluid block id the use picks up or places; 0 when the item declares no use.</summary>
    public byte UseFluidId { get; internal init; }

    /// <summary>
    /// Stackable block id an ignite/place_stack use works on (firepit block for
    /// <see cref="ItemUseAction.IgniteCampfire"/>); 0 for every other action.
    /// </summary>
    public byte UseBlockId { get; internal init; }

    /// <summary>Per-use success chance 0..1 for ignite uses; 1 for everything else.</summary>
    public float UseChance { get; internal init; } = 1f;

    /// <summary>Uses before this item breaks (10 for a firestarter); 0 means endless.</summary>
    public int MaxDurability { get; internal init; }

    /// <summary>Whether the item declares a <c>tool</c> block and therefore mines as a tool
    /// rather than at the bare-hand baseline. Membership in tool tags comes from the tag
    /// files, not from this flag.</summary>
    public bool IsTool { get; internal init; }

    /// <summary>Damage one hit deals to block hardness; 1 is the bare-hand baseline.</summary>
    public float ToolPower { get; internal init; } = 1f;

    /// <summary>Milliseconds between hits while mining; see <see cref="Mining"/>.</summary>
    public float HitDelayMs { get; internal init; } = Mining.DefaultHitGapMs;

    /// <summary>Seconds between uses of this item; 0 means the global click rate applies.</summary>
    public float UseDelaySeconds { get; internal init; }

    /// <summary>Cells across the spritesheet texture; 1 for a plain sprite.</summary>
    public int SpriteCols { get; internal init; } = 1;

    /// <summary>Cells down the spritesheet texture; 1 for a plain sprite.</summary>
    public int SpriteRows { get; internal init; } = 1;

    /// <summary>Frames used from the top-left, row-major; 1 for a plain sprite.</summary>
    public int FrameCount { get; internal init; } = 1;

    /// <summary>How the frame is picked each draw; <see cref="ItemAnimationMode.None"/> for a plain sprite.</summary>
    public ItemAnimationMode Animation { get; internal init; } = ItemAnimationMode.None;

    /// <summary>Frames per second for <see cref="ItemAnimationMode.Loop"/>; ignored otherwise.</summary>
    public float AnimationFps { get; internal init; } = 8f;

    /// <summary>Held-item rotation as [pitch, yaw, roll] degrees; the default [-30, 10, 0]
    /// stands the sprite nearly upright over the hand with a slight sideways cant, like a
    /// pickaxe, without ever going edge-on.</summary>
    public Vector3 DisplayRotation { get; internal init; } = new(-30f, 10f, 0f);

    /// <summary>Held-item offset in blocks of hand space, applied after the rotation.</summary>
    public Vector3 DisplayTranslation { get; internal init; } = Vector3.Zero;

    /// <summary>Held-item uniform scale multiplier.</summary>
    public float DisplayScale { get; internal init; } = 1f;

    /// <summary>Which inventory pool this item stores into. Replaces the old
    /// <c>UseResultId != 0 || MaxDurability > 0</c> probe in <c>Inventory.CategoryOf</c>.</summary>
    public virtual InventoryCategory StorageCategory =>
        UseResultId != 0 || MaxDurability > 0 ? InventoryCategory.Tools : InventoryCategory.Misc;

    /// <summary>
    /// Whether holding this item should make the block selection raycast see fluids. Only an
    /// item that scoops one needs to aim at water; for everything else the water is aimed
    /// through, so a pond never blocks placing a block on its floor.
    /// </summary>
    public virtual bool TargetsFluids => false;

    /// <summary>
    /// Right-click behaviour while holding this item, against whatever the player is aiming
    /// at. Pure decision + effect logic through <paramref name="ctx"/>; the caller
    /// (<c>World.TryUseSelected</c>) applies the returned morph and punch. The base answers
    /// "nothing happens" so plain pickups need no override. <paramref name="stack"/> is the
    /// selected tool instance, or null for stackable (misc) items like flint.
    /// </summary>
    public virtual ItemUseResult OnUse(ItemUseContext ctx, ItemStack? stack) => ItemUseResult.Ignored;

    /// <summary>Held-item transform in hand space: scaled, rotated (yaw, then pitch, then roll),
    /// then translated. The renderer composes it under the hand matrix, so the item rides
    /// the arm swing and the mine pump.</summary>
    public Matrix4x4 DisplayMatrix()
    {
        var radians = DisplayRotation * (MathF.PI / 180f);
        return Matrix4x4.CreateScale(DisplayScale)
            * Matrix4x4.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z)
            * Matrix4x4.CreateTranslation(DisplayTranslation);
    }

    /// <summary>Frame to draw for this item at this moment.</summary>
    /// <param name="timeSeconds">Scene clock, driving <see cref="ItemAnimationMode.Loop"/>.</param>
    /// <param name="headingDegrees">Compass heading (0 = north), driving <see cref="ItemAnimationMode.Heading"/>.</param>
    public int FrameAt(float timeSeconds, float headingDegrees) => Animation switch
    {
        ItemAnimationMode.Heading => Compass.HeadingFrame(headingDegrees, FrameCount),
        ItemAnimationMode.Loop => LoopFrame(timeSeconds, AnimationFps, FrameCount),
        _ => 0,
    };

    /// <summary>Loop frame at a moment: frame 0 at t = 0, advancing at <paramref name="fps"/>.</summary>
    public static int LoopFrame(float timeSeconds, float fps, int frameCount)
    {
        if (frameCount <= 0 || fps <= 0f || !float.IsFinite(timeSeconds)) return 0;
        return ((int)MathF.Floor(timeSeconds * fps) % frameCount + frameCount) % frameCount;
    }

    /// <summary>Minimal member for synthetic items (see <see cref="BlockItem"/>).</summary>
    protected Item(byte id, string name)
    {
        Id = id;
        Name = name;
        TexturePath = "";
    }

    /// <summary>Data-driven member: every JSON-backed item is built from one of these by
    /// <see cref="ItemFactory"/>.</summary>
    protected Item(ItemFactory.ItemSpec spec)
    {
        Id = spec.Id;
        Name = spec.Name;
        TexturePath = spec.TexturePath;
        UseAction = spec.Action;
        UseResultId = spec.ResultId;
        UseFluidId = spec.FluidId;
        UseBlockId = spec.BlockId;
        UseChance = spec.Chance;
        MaxDurability = spec.Durability;
        UseDelaySeconds = spec.UseDelay;
        SpriteCols = spec.SpriteCols;
        SpriteRows = spec.SpriteRows;
        FrameCount = spec.FrameCount;
        Animation = spec.Animation;
        AnimationFps = spec.AnimationFps;
        DisplayRotation = spec.Rotation;
        DisplayTranslation = spec.Translation;
        DisplayScale = spec.Scale;
        IsTool = spec.IsTool;
        ToolPower = spec.ToolPower;
        HitDelayMs = spec.HitDelayMs;
    }
}
