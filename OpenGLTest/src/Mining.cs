namespace silkgltriangle;

/// <summary>
/// GL-free mining rules shared by the break loop and the tests. A block's <c>tools</c> dict
/// (tag name to minimum power) gates it when <see cref="BlockType.RequiresTool"/> is set: the
/// held item must belong to one of those tags at or above the required power, or the hit deals
/// nothing. There are deliberately no per-tool speed bonuses — an item's pace is its own
/// <see cref="Item.HitDelayMs"/> and its damage is <see cref="Item.ToolPower"/>.
/// </summary>
public static class Mining
{
    /// <summary>Gap between hits for a bare hand or any item that declares no tool pace.</summary>
    public const float DefaultHitGapMs = 300f;

    /// <summary>Damage per hit a bare hand or an item with no tool power deals against a
    /// block's hardness.</summary>
    public const float DefaultPower = 0.5f;

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
        return Refused(block, held) ? 0f : Power(held);
    }

    /// <summary>Damage per hit of the held item; a bare hand deals the baseline.</summary>
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
