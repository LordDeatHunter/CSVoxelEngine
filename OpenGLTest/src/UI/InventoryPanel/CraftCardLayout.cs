using System;
using System.Collections.Generic;
using System.Numerics;
using VoxelEngine.Content.Recipes;

namespace VoxelEngine.UI.InventoryPanel;

/// <summary>What part of the crafting detail card a click landed on.</summary>
public enum CraftHitKind
{
    None,
    BatchMinus,
    BatchPlus,
    CraftButton,
    IngMinus,
    IngPlus,
    MemberChip,
}

/// <summary>One hit-test result inside the crafting detail card.</summary>
public readonly record struct CraftHit(CraftHitKind Kind, int IngredientIndex = -1, byte MemberId = 0);

/// <summary>
/// GL-free geometry of the [E] inventory's crafting detail card, in canvas units (see
/// <see cref="UiCanvas"/>). The static <c>*For</c> methods are the width/no-overlap policy: the
/// card lives in the free band between the inventory panel and the screen's right margin, is
/// right-anchored there and never crosses the panel. <see cref="Build"/> wraps the card's texts
/// (<see cref="TextWrap"/>) and lays every element out top-down, each wrapped element growing by
/// whole lines and pushing the ones below it down; the draw and <see cref="HitAt"/> read that one
/// built instance, so a click always lands on what was drawn. Y values stored on the instance are
/// content-relative (0 = card top, unscrolled); <see cref="ContentY"/> turns them into canvas Y.
/// </summary>
public sealed class CraftCardLayout
{
    public const float WidthMin = 300f;
    public const float WidthMax = 640f;
    public const float Pad = 14f;
    /// <summary>Width reserved left of the header text for the result icon.</summary>
    public const float IconColumn = 76f;
    public const float HeaderTextTop = 4f;
    public const float TitleLineH = 28f;
    public const float BodyLineH = 20f;
    public const float BatchRowH = 28f;
    public const float HeaderGap = 10f;
    public const float StepBox = 24f;
    public const float BatchCountW = 34f;
    public const float BatchGap = 8f;
    public const float IngPad = 10f;
    public const float IngTitleLineH = 20f;
    public const float IngBarH = 6f;
    public const float IngFedH = 40f;
    public const float IngStepBox = 26f;
    public const float ChipSize = 36f;
    public const float ChipGap = 6f;
    public const float IngGap = 8f;
    public const float StatusLineH = 22f;
    public const float StatusGap = 8f;
    public const float ButtonH = 46f;
    /// <summary>Height of the placeholder card shown while no recipe is open.</summary>
    public const float EmptyHeight = Pad * 2f + 90f;
    public const float ScrollbarWidth = 6f;
    public const float ThumbMinLength = 24f;
    /// <summary>Canvas pixels one mouse-wheel notch scrolls the card by.</summary>
    public const float WheelStep = 60f;
    /// <summary>Canvas Y of the card's top edge, level with the inventory panel.</summary>
    public const float Top = InventoryPanelLayout.PanelTop;

    /// <summary>One ingredient card's content-relative geometry; no arrays, so builds compare by value.</summary>
    public readonly record struct IngredientLayout(float Top, float Height, float TitleY, float BarY,
        float FedY, float ChipsY, int MemberCount);

    // ---- width / no-overlap policy ----

    /// <summary>Left edge of the free band: the panel's right edge plus the panel gap.</summary>
    public static float BandLeft(Vector2 screen) =>
        InventoryPanelLayout.PanelLeft + InventoryPanelLayout.PanelWidth(screen) + InventoryPanelLayout.PanelGap;

    /// <summary>Right edge of the free band: the screen's right margin.</summary>
    public static float BandRight(Vector2 screen) => screen.X - InventoryPanelLayout.PanelRightMargin;

    /// <summary>Card width: the free band, clamped to [<see cref="WidthMin"/>, <see cref="WidthMax"/>].</summary>
    public static float WidthFor(Vector2 screen) =>
        Math.Clamp(BandRight(screen) - BandLeft(screen), WidthMin, WidthMax);

    /// <summary>
    /// Card left edge: right-anchored in the free band, never left of it — a band narrower than
    /// <see cref="WidthMin"/> anchors to the panel and spills toward the screen edge instead.
    /// </summary>
    public static float LeftFor(Vector2 screen) =>
        MathF.Max(BandLeft(screen), BandRight(screen) - WidthFor(screen));

    /// <summary>Tallest viewport: the list panel's height, ending the same gap above the hotbar.</summary>
    public static float MaxHeight(Vector2 screen) => InventoryPanelLayout.PanelHeight(screen);

    public static float InnerWidthFor(Vector2 screen) => WidthFor(screen) - 2f * Pad;

    public static int ChipsPerRowFor(Vector2 screen) =>
        Math.Max(1, (int)((InnerWidthFor(screen) - IngPad * 2f + ChipGap) / (ChipSize + ChipGap)));

    /// <summary>Width the header's title and "makes" lines wrap against (right of the icon).</summary>
    public static float HeadAvail(Vector2 screen) => InnerWidthFor(screen) - IconColumn;

    public static float IngTitleAvail(Vector2 screen) => InnerWidthFor(screen) - IngPad * 2f;

    public static float StatusAvail(Vector2 screen) => InnerWidthFor(screen) - 8f;

    // ---- built instance ----

    public Vector2 Screen { get; }
    public CraftingRecipe Recipe { get; }
    public float Left { get; }
    public float Width { get; }
    public float InnerWidth { get; }
    public int ChipsPerRow { get; }
    public float BatchLabelWidth { get; }
    public string[] TitleLines { get; }
    public string[] MakesLines { get; }
    public string[] StatusLines { get; }
    public string[][] IngredientTitleLines { get; }
    public float TitleY { get; }
    public float MakesY { get; }
    public float BatchTextY { get; }
    public float BatchCentreY { get; }
    public float HeaderHeight { get; }
    public IngredientLayout[] Ingredients { get; }
    public float StatusY { get; }
    public float ButtonY { get; }
    /// <summary>Full height of the grown card's content.</summary>
    public float ContentHeight { get; }
    /// <summary>Height of the drawn card: the content, capped at <see cref="MaxHeight"/>.</summary>
    public float ViewHeight { get; }
    /// <summary>Whether the content is taller than the viewport, so the card scrolls and clips.</summary>
    public bool Overflows { get; }

    private CraftCardLayout(Vector2 screen, CraftingRecipe recipe, float batchLabelWidth,
        string[] titleLines, string[] makesLines, string[][] ingredientTitleLines, string[] statusLines)
    {
        Screen = screen;
        Recipe = recipe;
        Left = LeftFor(screen);
        Width = WidthFor(screen);
        InnerWidth = InnerWidthFor(screen);
        ChipsPerRow = ChipsPerRowFor(screen);
        BatchLabelWidth = batchLabelWidth;
        TitleLines = titleLines;
        MakesLines = makesLines;
        IngredientTitleLines = ingredientTitleLines;
        StatusLines = statusLines;

        // Header: title lines, "makes" lines, then the batch row. For one line of each this is
        // 4 + 28 + 20 + 4 + 28 = 84, so a single-line card matches the old fixed header exactly.
        var y = Pad;
        TitleY = y + HeaderTextTop;
        MakesY = TitleY + Math.Max(1, titleLines.Length) * TitleLineH;
        BatchTextY = MakesY + Math.Max(1, makesLines.Length) * BodyLineH + 4f;
        BatchCentreY = BatchTextY + 8f;
        HeaderHeight = BatchTextY + BatchRowH - y;
        y += HeaderHeight + HeaderGap;

        var ingredients = recipe.Ingredients;
        Ingredients = new IngredientLayout[ingredients.Length];
        for (var i = 0; i < ingredients.Length; i++)
        {
            var members = ingredients[i].AcceptedIds;
            var top = y;
            var titleY = top + IngPad;
            var barY = titleY + Math.Max(1, ingredientTitleLines[i].Length) * IngTitleLineH;
            var fedY = barY + IngBarH + 6f;
            var chipsY = fedY + IngFedH + 6f;
            var rows = Math.Max(1, (members.Length + ChipsPerRow - 1) / ChipsPerRow);
            var height = chipsY - top + rows * (ChipSize + ChipGap) - ChipGap + IngPad;
            Ingredients[i] = new IngredientLayout(top, height, titleY, barY, fedY, chipsY, members.Length);
            y += height + IngGap;
        }

        StatusY = y;
        y += Math.Max(1, statusLines.Length) * StatusLineH + StatusGap;
        ButtonY = y;
        ContentHeight = y + ButtonH + Pad;

        ViewHeight = MathF.Min(ContentHeight, MaxHeight(screen));
        Overflows = ScrollList.Overflows(ContentHeight, ViewHeight);
    }

    /// <summary>
    /// Wraps the card's texts for <paramref name="screen"/> and lays the grown card out.
    /// <paramref name="measureHead"/> measures at the title's draw scale, <paramref name="measureBody"/>
    /// at the body's; <paramref name="ingredientLabels"/> is one title per recipe ingredient.
    /// </summary>
    public static CraftCardLayout Build(Vector2 screen, CraftingRecipe recipe, string title, string makes,
        string[] ingredientLabels, string status, float batchLabelWidth, TextMeasure measureHead,
        TextMeasure measureBody)
    {
        if (ingredientLabels.Length != recipe.Ingredients.Length)
            throw new ArgumentException(
                $"Expected {recipe.Ingredients.Length} ingredient labels, got {ingredientLabels.Length}.",
                nameof(ingredientLabels));

        var scratch = new List<string>();
        var headAvail = HeadAvail(screen);
        var titleLines = WrapToArray(title, headAvail, measureHead, scratch);
        var makesLines = WrapToArray(makes, headAvail, measureBody, scratch);

        var ingAvail = IngTitleAvail(screen);
        var ingLines = new string[ingredientLabels.Length][];
        for (var i = 0; i < ingredientLabels.Length; i++)
            ingLines[i] = WrapToArray(ingredientLabels[i], ingAvail, measureBody, scratch);

        var statusLines = WrapToArray(status, StatusAvail(screen), measureBody, scratch);
        return new CraftCardLayout(screen, recipe, batchLabelWidth, titleLines, makesLines, ingLines, statusLines);
    }

    private static string[] WrapToArray(string text, float maxWidth, TextMeasure measure, List<string> scratch)
    {
        scratch.Clear();
        TextWrap.Wrap(text, maxWidth, measure, scratch);
        return scratch.ToArray();
    }

    // ---- geometry helpers (canvas units) ----

    public float ClampScroll(float offset) => ScrollList.Clamp(offset, ContentHeight, ViewHeight);

    /// <summary>Canvas Y of content-relative <paramref name="y"/> with the card scrolled by <paramref name="scroll"/>.</summary>
    public float ContentY(float y, float scroll) => Top + y - scroll;

    public Vector2 CardCentre => new(Left + Width * 0.5f, Top + ViewHeight * 0.5f);
    public Vector2 CardSize => new(Width, ViewHeight);
    /// <summary>Left edge of the header text, right of the icon column.</summary>
    public float TextX => Left + Pad + IconColumn;

    public Vector2 IconCentre(float scroll) => new(Left + Pad + 32f, ContentY(Pad, scroll) + 38f);

    public Vector2 BatchMinusCentre(float scroll) =>
        new(TextX + BatchLabelWidth + BatchGap + StepBox * 0.5f, ContentY(BatchCentreY, scroll));

    public Vector2 BatchCountCentre(float scroll)
    {
        var minus = BatchMinusCentre(scroll);
        return new Vector2(minus.X + StepBox * 0.5f + BatchGap + BatchCountW * 0.5f, minus.Y);
    }

    public Vector2 BatchPlusCentre(float scroll)
    {
        var count = BatchCountCentre(scroll);
        return new Vector2(count.X + BatchCountW * 0.5f + BatchGap + StepBox * 0.5f, count.Y);
    }

    /// <summary>Left edge of the "max N" label, right of the batch + box.</summary>
    public float BatchMaxX(float scroll) => BatchPlusCentre(scroll).X + StepBox * 0.5f + BatchGap;

    public Vector2 IngPlusCentre(int i, float scroll) =>
        new(Left + Width - Pad - IngPad - IngStepBox * 0.5f, ContentY(Ingredients[i].FedY, scroll) + IngFedH * 0.5f);

    public Vector2 IngMinusCentre(int i, float scroll)
    {
        var plus = IngPlusCentre(i, scroll);
        return new Vector2(plus.X - IngStepBox - 8f, plus.Y);
    }

    public Vector2 ChipCentre(int i, int pos, float scroll)
    {
        var col = pos % ChipsPerRow;
        var row = pos / ChipsPerRow;
        return new Vector2(
            Left + Pad + IngPad + ChipSize * 0.5f + col * (ChipSize + ChipGap),
            ContentY(Ingredients[i].ChipsY, scroll) + ChipSize * 0.5f + row * (ChipSize + ChipGap));
    }

    public Vector2 ButtonCentre(float scroll) => new(Left + Width * 0.5f, ContentY(ButtonY, scroll) + ButtonH * 0.5f);
    public Vector2 ButtonSize => new(InnerWidth, ButtonH);

    /// <summary>The scrollbar track down the card's right padding, inset by <see cref="Pad"/> top and bottom.</summary>
    public (Vector2 Centre, Vector2 Size) TrackRect()
    {
        var length = TrackLength;
        return (new Vector2(TrackX, Top + Pad + length * 0.5f), new Vector2(ScrollbarWidth, length));
    }

    public (Vector2 Centre, Vector2 Size) ThumbRect(float scroll)
    {
        var (start, length) = ScrollList.Thumb(TrackLength, ContentHeight, ViewHeight, scroll, ThumbMinLength);
        return (new Vector2(TrackX, Top + Pad + start + length * 0.5f), new Vector2(ScrollbarWidth, length));
    }

    private float TrackX => Left + Width - Pad * 0.5f;
    private float TrackLength => MathF.Max(0f, ViewHeight - Pad * 2f);

    /// <summary>Whether a canvas point lies on the drawn card (its viewport, not the scrolled content).</summary>
    public bool Contains(Vector2 point) =>
        point.X >= Left && point.X <= Left + Width && point.Y >= Top && point.Y <= Top + ViewHeight;

    /// <summary>
    /// The control under a canvas point with the card scrolled by <paramref name="scroll"/>; None
    /// outside the viewport, so content scrolled out of view never responds.
    /// </summary>
    public CraftHit HitAt(Vector2 point, float scroll)
    {
        if (!Contains(point)) return new CraftHit(CraftHitKind.None);
        if (InBox(point, ButtonCentre(scroll), ButtonSize))
            return new CraftHit(CraftHitKind.CraftButton);
        if (InBox(point, BatchMinusCentre(scroll), new Vector2(StepBox)))
            return new CraftHit(CraftHitKind.BatchMinus);
        if (InBox(point, BatchPlusCentre(scroll), new Vector2(StepBox)))
            return new CraftHit(CraftHitKind.BatchPlus);

        for (var i = 0; i < Ingredients.Length; i++)
        {
            if (InBox(point, IngMinusCentre(i, scroll), new Vector2(IngStepBox)))
                return new CraftHit(CraftHitKind.IngMinus, i);
            if (InBox(point, IngPlusCentre(i, scroll), new Vector2(IngStepBox)))
                return new CraftHit(CraftHitKind.IngPlus, i);
            for (var c = 0; c < Ingredients[i].MemberCount; c++)
                if (InBox(point, ChipCentre(i, c, scroll), new Vector2(ChipSize)))
                    return new CraftHit(CraftHitKind.MemberChip, i, Recipe.Ingredients[i].AcceptedIds[c]);
        }
        return new CraftHit(CraftHitKind.None);
    }

    internal static bool InBox(Vector2 point, Vector2 centre, Vector2 size) =>
        MathF.Abs(point.X - centre.X) <= size.X * 0.5f
        && MathF.Abs(point.Y - centre.Y) <= size.Y * 0.5f;
}
