namespace silkgltriangle;

/// <summary>Outcome of <see cref="Item.OnUse"/>: did the use take effect, what the tool morphs
/// into, and whether the player punches. The caller applies the morph; durability, cooldown
/// and stack spending already happened inside the override through <see cref="ItemUseContext"/>.
/// </summary>
public readonly record struct ItemUseResult(bool Applied, byte? MorphToId, bool Punch)
{
    /// <summary>The aim missed or refused: no morph, no punch.</summary>
    public static ItemUseResult Ignored => new(false, null, false);

    /// <summary>The use took effect; <paramref name="morphToId"/> swaps the tool instance, or
    /// null to keep it (durability/stack paths).</summary>
    public static ItemUseResult Succeed(byte? morphToId, bool punch = true) =>
        new(true, morphToId, punch);
}
