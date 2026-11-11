using System;

namespace VoxelEngine.Content.Stations;

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

    /// <summary>Inner width of the pocket in cell space - the whole cell less a rim on each
    /// side - which is what the molten sheet is scaled to.</summary>
    public const float PocketWidth = 1f - 2f * PocketInset;

    /// <summary>How far the finished cast's edge stays inside the pocket wall on each side, so
    /// the sprite's extruded side walls never touch or z-fight that wall.</summary>
    public const float ResultClearance = 0.01f;

    /// <summary>Edge length an item result is drawn at: the metal that filled the pocket
    /// solidified in place, so it spans the pocket less <see cref="ResultClearance"/> a side
    /// rather than reading as a ground-drop icon dropped into the slab.</summary>
    public const float ResultSize = PocketWidth - 2f * ResultClearance;

    /// <summary>Height of that sprite's center: lying face-up on the pocket floor with its back
    /// face just clear of it (<see cref="SpritePose.RestingLift"/>), under the molten sheet that
    /// dissolves into it.</summary>
    public static readonly float ResultY = PocketFloorY + SpritePose.RestingLift(ResultSize);

    /// <summary>Edge length a <em>block</em> result is drawn at - none of the cast recipes
    /// yields one today, but the render pass handles the branch - fitted to the pocket's depth,
    /// since a pocket-wide cube would stand straight through the slab.</summary>
    public const float ResultCubeSize = PocketTopY - PocketFloorY;

    /// <summary>Height of that cube's center: resting on the pocket floor.</summary>
    public const float ResultCubeY = PocketFloorY + ResultCubeSize * 0.5f;

    /// <summary>
    /// Height at which the pour's molten surface is drawn inside the mold's cell, rising smoothly
    /// from just over the pocket floor to just under the rim as <paramref name="fill01"/> goes
    /// 0..1. Kept strictly inside the pocket at both ends so the sheet never z-fights the floor
    /// or pokes through the slab top.
    /// </summary>
    public static float LiquidHeight(float fill01) =>
        Mold.LiquidHeightAt(fill01);

    /// <summary>
    /// One pocket's levels in cell space: a slab or bowl whose molten sheet rises from
    /// <see cref="FloorY"/> to <see cref="TopY"/>, inside a rim <see cref="Inset"/> wide. The mold
    /// constants above are <see cref="Mold"/>; a casting basin's deeper bowl is <see cref="Basin"/>.
    /// </summary>
    public readonly record struct Pocket(float FloorY, float TopY, float Inset)
    {
        /// <summary>Inner width: the cell less a rim on each side.</summary>
        public float Width => 1f - 2f * Inset;

        /// <summary>Edge of a block result: as wide as the pocket allows without standing out of it,
        /// so a deep bowl shows a cube and a shallow slab a flatter, depth-fitted one.</summary>
        public float ResultCubeSize => MathF.Min(TopY - FloorY, Width);

        /// <summary>Height of a block result's center: resting on the pocket floor.</summary>
        public float ResultCubeY => FloorY + ResultCubeSize * 0.5f;

        /// <summary>Height of the molten surface for a pour <paramref name="fill01"/> full, kept
        /// strictly inside the pocket at both ends (see <see cref="CastingRenderer.LiquidHeight"/>).</summary>
        public float LiquidHeightAt(float fill01) =>
            MathUtils.Lerp(FloorY + 0.005f, TopY - 0.01f, MathUtils.Clamp01(fill01));
    }

    /// <summary>The pocket of every fired mold (the constants above).</summary>
    public static readonly Pocket Mold = new(PocketFloorY, PocketTopY, PocketInset);

    /// <summary>The casting basin's pocket: a 2 px floor under a 6 px deep bowl with a 2 px rim,
    /// mirroring <c>assets/models/block/casting_basin.json</c> (<c>CastingTests</c> pins the tie).</summary>
    public static readonly Pocket Basin = new(2f / 16f, 8f / 16f, 2f / 16f);

    /// <summary>
    /// The pocket a cast that yields <paramref name="resultIsBlock"/> sits in: block casts are
    /// poured in the basin, item casts in a mold slab.
    /// </summary>
    public static Pocket PocketFor(bool resultIsBlock) => resultIsBlock ? Basin : Mold;

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
