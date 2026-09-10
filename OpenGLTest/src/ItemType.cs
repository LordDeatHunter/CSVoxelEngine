using System;
using System.Collections.Generic;
using System.Numerics;

namespace silkgltriangle;

/// <summary>How a spritesheet item picks its frame each draw.</summary>
public enum ItemAnimationMode
{
    /// <summary>No sheet: the whole texture is the sprite.</summary>
    None,
    /// <summary>Frame follows the player's compass heading, so frame 0 faces north.</summary>
    Heading,
    /// <summary>Frames slide by at <see cref="ItemType.AnimationFps"/>.</summary>
    Loop,
}

/// <summary>
/// Pure items: pickups that are not blocks, loaded from <c>assets/data/items/&lt;name&gt;.json</c>
/// by <see cref="ContentRegistry"/>. They have no <see cref="BlockType"/> entry, so they can
/// never be placed or meshed into chunks — they only exist as <see cref="ItemDrop"/> entities
/// (rendered from their 2D sprite, see <see cref="Primitives.CreateExtrudedSprite"/>) and as
/// <see cref="Inventory"/> slots. Ids live in the same byte space as block ids and are handed
/// out after them, so the two never collide.
/// </summary>
public sealed class ItemType
{
    /// <summary>Every registered item by id.</summary>
    public static IReadOnlyDictionary<byte, ItemType> Types { get; internal set; } =
        new Dictionary<byte, ItemType>();

    internal ItemType(byte id, string name, string texturePath)
    {
        Id = id;
        Name = name;
        TexturePath = texturePath;
    }

    /// <summary>Byte id assigned at load.</summary>
    public byte Id { get; }

    /// <summary>Display name for future tooltips/crafting UI.</summary>
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

    /// <summary>
    /// Held-item transform in hand space: scaled, rotated (yaw, then pitch, then roll),
    /// then translated. The renderer composes it under the hand matrix, so the item rides
    /// the arm swing and the mine pump.
    /// </summary>
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

    /// <summary>Whether an id is a pure item rather than a block (or air/unknown).</summary>
    public static bool IsItem(byte id) => Types.ContainsKey(id);

    /// <summary>
    /// Whether holding this id should make the block selection raycast see fluids. Only an item
    /// that scoops one needs to aim at water; for everything else the water is aimed through,
    /// so a pond never blocks placing a block on its floor.
    /// </summary>
    public static bool TargetsFluids(byte id) =>
        Types.TryGetValue(id, out var type) && type.UseAction == ItemUseAction.PickupFluid;
}
