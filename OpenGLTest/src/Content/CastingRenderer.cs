namespace silkgltriangle;

/// <summary>
/// Authoring contract of a cast mold's pocket: where the molten metal sits inside the slab and
/// how the pour and the cooling read on screen. Every level here mirrors
/// <c>tools/generate-mold-models.ps1</c>, which bakes the six mold models — <see cref="PocketTopY"/>
/// is its <c>$SlabHeight</c>, <see cref="PocketFloorY"/> is <c>$SlabHeight - $PocketDepth</c> and
/// <see cref="PocketInset"/> is its <c>$RimCells</c> — exactly the way <see cref="KilnRenderer"/>
/// mirrors the kiln model. The generator is the source of truth; these constants follow it, and
/// <c>CastingTests</c> pins the tie.
///
/// GL-free and pure: the render pass and the tests read the same numbers instead of re-deriving
/// the geometry.
/// </summary>
public static class CastingRenderer
{
    /// <summary>Floor of the carved pocket, where a pour starts (model y = 2).</summary>
    public const float PocketFloorY = 2f / 16f;

    /// <summary>Top of the slab, which the pocket is brim-full at (model y = 4).</summary>
    public const float PocketTopY = 4f / 16f;

    /// <summary>Width of the rim around the pocket, in cell space (model 2 of 16 per side).</summary>
    public const float PocketInset = 2f / 16f;

    /// <summary>
    /// Height at which the pour's molten surface is drawn inside the mold's cell, rising smoothly
    /// from just over the pocket floor to just under the rim as <paramref name="fill01"/> goes
    /// 0..1. Kept strictly inside the pocket at both ends so the sheet never z-fights the floor
    /// or pokes through the slab top.
    /// </summary>
    public static float LiquidHeight(float fill01) =>
        MathUtils.Lerp(PocketFloorY + 0.005f, PocketTopY - 0.01f, MathUtils.Clamp01(fill01));

    /// <summary>
    /// Opacity of the molten sheet while the cast cools. Complementary to
    /// <see cref="ResultAlpha"/> — the two always sum to 1 — so the metal dissolves into the
    /// finished item over the whole of the recipe's cool time instead of cutting between them.
    /// </summary>
    public static float LiquidAlpha(float cool01) => 1f - MathUtils.SmoothStep(cool01);

    /// <summary>
    /// Opacity of the result sprite while the cast cools: the Hermite counterpart of
    /// <see cref="LiquidAlpha"/>, fading in over the whole cool time.
    /// </summary>
    public static float ResultAlpha(float cool01) => MathUtils.SmoothStep(cool01);
}
