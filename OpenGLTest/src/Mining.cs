using silkgltriangle.Content;

namespace silkgltriangle;

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
    /// <summary>Gap between hits for a bare hand or any item that declares no tool pace.</summary>
    public const float DefaultHitGapMs = 300f;

    /// <summary>Mining tier a bare hand counts as when a block's <c>tools</c> gate is checked.</summary>
    public const float DefaultPower = 0.5f;

    /// <summary>Damage per hit a bare hand or an item that declares no <c>blockDamage</c> deals
    /// against a block's hardness.</summary>
    public const float DefaultBlockDamage = 0.5f;

    /// <summary>Damage per punch a bare hand or an item that declares no <c>entityDamage</c>
    /// deals to an enemy.</summary>
    public const float DefaultEntityDamage = 1f;

    /// <summary>Whether the held item may damage this block under its requiresTool gate.</summary>
    public static bool CanMine(byte blockId, Item? held)
    {
        var block = BlockType.ById[blockId];
        return block is { Fluid: false } && !Refused(block, held);
    }

    /// <summary>Damage one hit deals, or 0 when the block's gate refuses the held item.</summary>
    public static float HitDamage(byte blockId, Item? held)
    {
        var block = BlockType.ById[blockId];
        if (block == null || block.Fluid) return 0f;
        return Refused(block, held) ? 0f : BlockDamage(held);
    }

    /// <summary>
    /// Hardness of the block in a cell with state byte <paramref name="state"/>: the block's own
    /// <see cref="BlockType.Hardness"/>, except that tree wood scales it by thickness, from
    /// <see cref="BlockType.TipHardness"/> of it at the thinnest up to all of it at a full cube
    /// (so placed, detached wood is as hard as a trunk). 0 for an unknown id.
    /// </summary>
    public static float HardnessOf(byte blockId, byte state)
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

    /// <summary>Seconds between hits for the held item; a bare hand uses the baseline.</summary>
    public static float HitGapSeconds(Item? held) => HitGapMs(held) / 1000f;

    /// <summary>Milliseconds between hits for the held item; a bare hand uses the baseline.</summary>
    public static float HitGapMs(Item? held) =>
        held is { HitDelayMs: > 0f } ? held.HitDelayMs : DefaultHitGapMs;

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
