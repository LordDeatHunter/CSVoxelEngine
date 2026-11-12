using System;
using VoxelEngine.Content;
using VoxelEngine.Content.Blocks;
using VoxelEngine.Items;
using VoxelEngine.WorldGen.Trees;

namespace VoxelEngine.Interaction;

/// <summary>
/// GL-free mining rules shared by the break loop and the tests. A block's <c>tools</c> dict
/// (tag name to minimum tier) gates it when <see cref="BlockType.RequiresTool"/> is set: the
/// held item must belong to one of those tags at or above the required tier, or the hit deals
/// nothing. A tool's <c>power</c> is that tier and nothing else — the damage a hit removes is
/// the item's <see cref="Item.BlockDamage"/>, and its pace its own <see cref="Item.HitDelayMs"/>.
/// One punch's damage to an enemy is <see cref="Item.EntityDamage"/>; see <see cref="Combat"/>.
/// </summary>
public static class Mining
{
    /// <summary>Gap between punches for a bare hand or any item that declares no tool pace.</summary>
    public const float DefaultHitGapMs = 600f;

    /// <summary>Gap between block hits for a bare hand or an item that declares no tool pace. Mining is
    /// quicker than punching: a punch stays on <see cref="DefaultHitGapMs"/>.</summary>
    public const float DefaultBlockHitGapMs = 300f;

    /// <summary>Mining tier a bare hand counts as when a block's <c>tools</c> gate is checked.</summary>
    public const float DefaultPower = 0.5f;

    /// <summary>Damage per hit a bare hand or an item that declares no <c>blockDamage</c> deals
    /// against a block's hardness.</summary>
    public const float DefaultBlockDamage = 0.5f;

    /// <summary>Damage per punch a bare hand or an item that declares no <c>entityDamage</c>
    /// deals to an enemy.</summary>
    public const float DefaultEntityDamage = 1f;

    /// <summary>Whether the held item may damage this block under its requiresTool gate. Never
    /// for a fluid or an unbreakable block, which nothing mines.</summary>
    public static bool CanMine(ushort blockId, Item? held)
    {
        var block = BlockType.ById[blockId];
        return block is { Fluid: false, Unbreakable: false } && !Refused(block, held);
    }

    /// <summary>Whether <paramref name="held"/> is an axe: a member of the <c>axe</c> tag, which swings an
    /// overhead chop at what it mines instead of a punch.</summary>
    public static bool IsAxe(Item? held) => held != null && ContentRegistry.IsInTag("axe", held.Id);

    /// <summary>Whether <paramref name="held"/> is a smithing hammer: a member of the <c>hammer</c> tag, which works
    /// a bloom on an anvil (<see cref="Simulation.Smithing.AnvilManager"/>) instead of mining it.</summary>
    public static bool IsHammer(Item? held) => held != null && ContentRegistry.IsInTag("hammer", held.Id);

    /// <summary>Damage one hit deals, or 0 when the block's gate refuses the held item. An
    /// unbreakable block takes no damage from anything.</summary>
    public static float HitDamage(ushort blockId, Item? held)
    {
        var block = BlockType.ById[blockId];
        if (block == null || block.Fluid || block.Unbreakable) return 0f;
        return Refused(block, held) ? 0f : BlockDamage(held);
    }

    /// <summary>
    /// Hardness of the block in a cell with state byte <paramref name="state"/>: the block's own
    /// <see cref="BlockType.Hardness"/>, except that tree wood scales it by thickness, from
    /// <see cref="BlockType.TipHardness"/> of it at the thinnest up to all of it at a full cube
    /// (so placed, detached wood is as hard as a trunk). 0 for an unknown id.
    /// </summary>
    public static float HardnessOf(ushort blockId, byte state)
    {
        var type = BlockType.ById[blockId];
        if (type == null) return 0f;
        if (type.Renderer != BlockRenderer.TreeWood) return type.Hardness;

        var share = WoodState.Thickness(state) / (float)WoodState.FullCube;
        return type.Hardness * (type.TipHardness + (1f - type.TipHardness) * share);
    }

    /// <summary>Damage per hit the held item removes from a block; a bare hand deals the
    /// baseline. This is the item's <c>blockDamage</c>, not its mining tier (<see cref="Power"/>).</summary>
    public static float BlockDamage(Item? held) => held?.BlockDamage ?? DefaultBlockDamage;

    /// <summary>Damage one punch deals to an enemy; a bare hand deals the baseline.</summary>
    public static float EntityDamage(Item? held) => held?.EntityDamage ?? DefaultEntityDamage;

    /// <summary>Mining tier of the held item: what a block's <c>tools</c> gate compares against.
    /// Deliberately not a damage number — see <see cref="BlockDamage"/>.</summary>
    public static float Power(Item? held) => held?.ToolPower ?? DefaultPower;

    /// <summary>Whether a held power meets a required power (float tiers: a 0 requirement
    /// admits any member of the tag).</summary>
    public static bool MeetsRequired(float heldPower, float requiredPower) => heldPower >= requiredPower;

    /// <summary>Seconds between punches for the held item; a bare hand uses the baseline.</summary>
    public static float HitGapSeconds(Item? held) => HitGapMs(held) / 1000f;

    /// <summary>Seconds between block hits for the held item (mining); a bare hand uses its own baseline.</summary>
    public static float BlockHitGapSeconds(Item? held) => BlockHitGapMs(held) / 1000f;

    /// <summary>The effect-slowed mining pace: <see cref="BlockHitGapSeconds(Item?)"/> divided by
    /// <paramref name="attackFactor"/>, as <see cref="HitGapSeconds(Item?, float)"/> does for punches.</summary>
    public static float BlockHitGapSeconds(Item? held, float attackFactor) =>
        BlockHitGapSeconds(held) / MathF.Max(attackFactor, 0.05f);

    /// <summary>The effect-slowed pace: seconds between hits for the held item while a status
    /// effect scales the attacker's pace by <paramref name="attackFactor"/> (1 = unslowed, lower =
    /// slower). Only the local player's own cadence reads it; the server's
    /// <c>AttackPacing</c> and <c>BreakProgress</c> keep the unslowed overload, since they check
    /// a minimum gap and a slower client is therefore always legal.</summary>
    public static float HitGapSeconds(Item? held, float attackFactor) =>
        HitGapSeconds(held) / MathF.Max(attackFactor, 0.05f);

    /// <summary>Milliseconds between punches for the held item; a bare hand or an item with no declared
    /// <c>hitDelayMs</c> uses <see cref="DefaultHitGapMs"/>.</summary>
    public static float HitGapMs(Item? held) => held?.HitDelayMs ?? DefaultHitGapMs;

    /// <summary>Milliseconds between block hits for the held item; a bare hand or an item with no declared
    /// <c>hitDelayMs</c> uses <see cref="DefaultBlockHitGapMs"/>.</summary>
    public static float BlockHitGapMs(Item? held) => held?.HitDelayMs ?? DefaultBlockHitGapMs;

    private static bool Refused(BlockType block, Item? held)
    {
        if (!block.RequiresTool || block.Tools is not { Count: > 0 } tools) return false;
        var id = held?.Id ?? BlockType.Air;
        if (id == BlockType.Air) return true;
        foreach (var (tag, required) in tools)
            if (MeetsRequired(Power(held), required) && ContentRegistry.IsInTag(tag, id))
                return false;
        return true;
    }
}
