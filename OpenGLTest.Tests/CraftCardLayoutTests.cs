using System;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class CraftCardLayoutTests
{
    private static readonly Vector2[] Screens =
    [
        new(1920f, 1080f), new(2560f, 1080f), new(3840f, 1080f), new(1920f, 1440f),
    ];

    private static readonly Vector2 Reference = new(1920f, 1080f);

    private const string ShortStatus = "Ready";

    private static float Mono(ReadOnlySpan<char> s) => s.Length * 10f;

    private static CraftCardLayout Build(Vector2 screen, CraftingRecipe recipe, string title, string[] labels,
        string status, TextMeasure measure) =>
        CraftCardLayout.Build(screen, recipe, title, "Makes 1", labels, status, 44f, measure, measure);

    private static CraftingRecipe Recipe(params CraftingRecipe.Ingredient[] ingredients) =>
        new("test", 1, 1, ingredients);

    private static CraftingRecipe.Ingredient Tag(int members) =>
        new(0, 2, "log", Enumerable.Range(10, members).Select(i => (byte)i).ToArray());

    private static CraftingRecipe ThreeIngredients(int middleMembers) =>
        Recipe(new CraftingRecipe.Ingredient(2, 1), Tag(middleMembers), new CraftingRecipe.Ingredient(3, 4));

    private static CraftingRecipe Huge() =>
        Recipe(Enumerable.Range(0, 8).Select(_ => Tag(25)).ToArray());

    private static string[] Labels(int n, string label = "x") => Enumerable.Repeat(label, n).ToArray();

    /// <summary>A text of <paramref name="words"/> short words that each fit alone.</summary>
    private static string Words(int words) => string.Join(" ", Enumerable.Repeat("word", words));

    [Fact]
    public void CardStaysInsideTheFreeBand()
    {
        var recipe = Recipe(new CraftingRecipe.Ingredient(2, 1));
        foreach (var screen in Screens)
        {
            var band = CraftCardLayout.BandRight(screen) - CraftCardLayout.BandLeft(screen);
            Assert.True(CraftCardLayout.LeftFor(screen) >= CraftCardLayout.BandLeft(screen));
            Assert.True(CraftCardLayout.LeftFor(screen) + CraftCardLayout.WidthFor(screen)
                        <= CraftCardLayout.BandRight(screen) + 1e-3f);
            Assert.True(CraftCardLayout.WidthFor(screen) <= CraftCardLayout.WidthMax);
            if (band >= CraftCardLayout.WidthMin)
                Assert.Equal(MathF.Min(CraftCardLayout.WidthMax, band), CraftCardLayout.WidthFor(screen));

            var layout = Build(screen, recipe, "T", Labels(1), ShortStatus, Mono);
            Assert.Equal(CraftCardLayout.LeftFor(screen), layout.Left);
            Assert.Equal(CraftCardLayout.WidthFor(screen), layout.Width);
        }

        var narrow = Reference;
        while (CraftCardLayout.BandRight(narrow) - CraftCardLayout.BandLeft(narrow) >= CraftCardLayout.WidthMin)
            narrow.X -= 10f;
        Assert.Equal(CraftCardLayout.WidthMin, CraftCardLayout.WidthFor(narrow));
        Assert.Equal(CraftCardLayout.BandLeft(narrow), CraftCardLayout.LeftFor(narrow));
    }

    [Fact]
    public void CardNeverReachesTheHotbar()
    {
        var small = Recipe(new CraftingRecipe.Ingredient(2, 1));
        var huge = Huge();
        var longLabel = new string('a', 30) + " " + new string('b', 29);
        foreach (var screen in Screens)
        {
            var s = Build(screen, small, "T", Labels(1), ShortStatus, Mono);
            var h = Build(screen, huge, "Title", Labels(8, longLabel), ShortStatus, Mono);
            foreach (var layout in new[] { s, h })
            {
                Assert.True(CraftCardLayout.Top + layout.ViewHeight <= HotbarLayout.Top(screen));
                Assert.True(layout.ViewHeight <= CraftCardLayout.MaxHeight(screen));
            }

            Assert.True(h.Overflows);
            Assert.Equal(CraftCardLayout.MaxHeight(screen), h.ViewHeight);
            Assert.True(h.ContentHeight > h.ViewHeight);

            Assert.False(s.Overflows);
            Assert.Equal(s.ContentHeight, s.ViewHeight);
        }
    }

    [Fact]
    public void LongerTitlePushesEverythingBelowDownByWholeLines()
    {
        var screen = Reference;
        var recipe = ThreeIngredients(3);
        var labels = Labels(3);
        var one = Build(screen, recipe, "Title", labels, ShortStatus, Mono);
        var many = Build(screen, recipe, Words(40), labels, ShortStatus, Mono);

        Assert.Single(one.TitleLines);
        var n = many.TitleLines.Length;
        Assert.True(n > 1);
        var delta = (n - 1) * CraftCardLayout.TitleLineH;

        Assert.Equal(one.TitleY, many.TitleY);
        Assert.Equal(delta, many.MakesY - one.MakesY, 3);
        Assert.Equal(delta, many.BatchTextY - one.BatchTextY, 3);
        Assert.Equal(delta, many.HeaderHeight - one.HeaderHeight, 3);
        for (var i = 0; i < recipe.Ingredients.Length; i++)
            Assert.Equal(delta, many.Ingredients[i].Top - one.Ingredients[i].Top, 3);
        Assert.Equal(delta, many.StatusY - one.StatusY, 3);
        Assert.Equal(delta, many.ButtonY - one.ButtonY, 3);
        Assert.Equal(delta, many.ContentHeight - one.ContentHeight, 3);
        Assert.Equal(delta, many.BatchMinusCentre(0f).Y - one.BatchMinusCentre(0f).Y, 3);
    }

    [Fact]
    public void LongerIngredientLabelPushesOnlyLaterItemsDown()
    {
        var screen = Reference;
        var recipe = ThreeIngredients(3);
        var baseline = Build(screen, recipe, "Title", Labels(3), ShortStatus, Mono);
        var grown = Build(screen, recipe, "Title", ["x", Words(40), "x"], ShortStatus, Mono);

        var m = grown.IngredientTitleLines[1].Length;
        Assert.True(m > 1);
        Assert.Single(grown.IngredientTitleLines[0]);
        Assert.Single(grown.IngredientTitleLines[2]);
        var delta = (m - 1) * CraftCardLayout.IngTitleLineH;

        Assert.Equal(baseline.Ingredients[0], grown.Ingredients[0]);
        Assert.Equal(delta, grown.Ingredients[1].Height - baseline.Ingredients[1].Height, 3);
        Assert.Equal(delta, grown.Ingredients[2].Top - baseline.Ingredients[2].Top, 3);
        Assert.Equal(delta, grown.StatusY - baseline.StatusY, 3);
        Assert.Equal(delta, grown.ButtonY - baseline.ButtonY, 3);
        Assert.Equal(delta, grown.ContentHeight - baseline.ContentHeight, 3);

        var wrappedStatus = Build(screen, recipe, "Title", Labels(3), Words(60), Mono);
        var k = wrappedStatus.StatusLines.Length;
        Assert.True(k > 1);
        var statusDelta = (k - 1) * CraftCardLayout.StatusLineH;
        for (var i = 0; i < recipe.Ingredients.Length; i++)
            Assert.Equal(baseline.Ingredients[i], wrappedStatus.Ingredients[i]);
        Assert.Equal(baseline.StatusY, wrappedStatus.StatusY);
        Assert.Equal(statusDelta, wrappedStatus.ButtonY - baseline.ButtonY, 3);
        Assert.Equal(statusDelta, wrappedStatus.ContentHeight - baseline.ContentHeight, 3);
    }

    [Fact]
    public void ControlCentresHitTheirControls()
    {
        foreach (var screen in Screens)
        {
            var recipe = ThreeIngredients(CraftCardLayout.ChipsPerRowFor(screen) + 1);
            var layout = Build(screen, recipe, "Title", Labels(3), ShortStatus, Mono);
            AssertEveryControlHits(layout, 0f, _ => true);

            // The chip that wrapped onto the second row sits below the +/- boxes.
            var second = layout.ChipCentre(1, layout.ChipsPerRow, 0f);
            Assert.True(second.Y - CraftCardLayout.ChipSize / 2f
                        >= layout.IngPlusCentre(1, 0f).Y + CraftCardLayout.IngStepBox / 2f);

            var mid = layout.CardCentre;
            Assert.Equal(CraftHitKind.None, layout.HitAt(new Vector2(layout.Left - 1f, mid.Y), 0f).Kind);
            Assert.Equal(CraftHitKind.None, layout.HitAt(new Vector2(mid.X, CraftCardLayout.Top - 1f), 0f).Kind);
            Assert.Equal(CraftHitKind.None,
                layout.HitAt(new Vector2(mid.X, CraftCardLayout.Top + layout.ViewHeight + 1f), 0f).Kind);
        }
    }

    [Fact]
    public void ScrolledControlsHitWhereTheyAreDrawn()
    {
        var screen = Reference;
        var longLabel = new string('a', 30) + " " + new string('b', 29);
        var layout = Build(screen, Huge(), "Title", Labels(8, longLabel), ShortStatus, Mono);
        Assert.True(layout.Overflows);

        Assert.Equal(0f, layout.ClampScroll(-5f));
        var scroll = layout.ClampScroll(float.MaxValue);
        Assert.Equal(layout.ContentHeight - layout.ViewHeight, scroll, 3);

        bool Visible(Vector2 c) => c.Y >= CraftCardLayout.Top && c.Y <= CraftCardLayout.Top + layout.ViewHeight;
        AssertEveryControlHits(layout, scroll, Visible);

        var batch = layout.BatchMinusCentre(scroll);
        Assert.True(batch.Y < CraftCardLayout.Top);
        Assert.Equal(CraftHitKind.None, layout.HitAt(batch, scroll).Kind);
    }

    /// <summary>Every control whose centre passes <paramref name="visible"/> is hit there as itself.</summary>
    private static void AssertEveryControlHits(CraftCardLayout layout, float scroll, Func<Vector2, bool> visible)
    {
        void Expect(Vector2 centre, CraftHit expected)
        {
            if (visible(centre)) Assert.Equal(expected, layout.HitAt(centre, scroll));
        }

        Expect(layout.ButtonCentre(scroll), new CraftHit(CraftHitKind.CraftButton));
        Expect(layout.BatchMinusCentre(scroll), new CraftHit(CraftHitKind.BatchMinus));
        Expect(layout.BatchPlusCentre(scroll), new CraftHit(CraftHitKind.BatchPlus));
        for (var i = 0; i < layout.Ingredients.Length; i++)
        {
            Expect(layout.IngMinusCentre(i, scroll), new CraftHit(CraftHitKind.IngMinus, i));
            Expect(layout.IngPlusCentre(i, scroll), new CraftHit(CraftHitKind.IngPlus, i));
            var members = layout.Recipe.Ingredients[i].AcceptedIds;
            for (var c = 0; c < members.Length; c++)
                Expect(layout.ChipCentre(i, c, scroll), new CraftHit(CraftHitKind.MemberChip, i, members[c]));
        }
    }
}
