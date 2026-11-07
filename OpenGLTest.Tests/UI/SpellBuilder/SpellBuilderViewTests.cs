using System;
using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Tests.Magic;
using VoxelEngine.UI.InventoryPanel;
using VoxelEngine.UI.SpellBuilder;
using Xunit;

namespace VoxelEngine.Tests.UI.SpellBuilder;

/// <summary>The spell builder's selection rules, its live stats and the known-part refresh.</summary>
public sealed class SpellBuilderViewTests
{
    public SpellBuilderViewTests() => TestContent.EnsureLoaded();

    private static SpellPart[] OfKind(SpellPartKind kind) =>
        SpellPartRegistry.All.Where(p => p.Kind == kind).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();

    private static void AssertWellFormed(SpellBuilderView view)
    {
        var parts = view.Parts();
        Assert.True(parts.Count(p => p.Kind == SpellPartKind.Element) <= 1);
        Assert.True(parts.Count(p => p.Kind == SpellPartKind.Shape) <= 1);
        Assert.True(parts.Count(p => p.Kind == SpellPartKind.Trigger) <= 1);
        var modifiers = parts.Where(p => p.Kind == SpellPartKind.Modifier).ToArray();
        Assert.Equal(modifiers.Length, modifiers.Distinct().Count());
        Assert.Equal(parts.Select(p => p.Key), view.PartKeys());
    }

    [Fact]
    public void TogglingKeepsAtMostOneElementShapeAndTriggerAndNoDuplicateModifier()
    {
        var view = new SpellBuilderView();
        var all = SpellPartRegistry.All.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        var random = new Random(1234);

        for (var i = 0; i < 200; i++)
        {
            view.Toggle(all[random.Next(all.Length)]);
            AssertWellFormed(view);
        }

        var element = OfKind(SpellPartKind.Element)[0];
        view.ClearSelection();
        view.Toggle(element);
        Assert.Same(element, view.Element);
        view.Toggle(element);
        Assert.Null(view.Element);

        var elements = OfKind(SpellPartKind.Element);
        if (elements.Length > 1)
        {
            view.Toggle(elements[0]);
            view.Toggle(elements[1]);
            Assert.Same(elements[1], view.Element);
        }

        var modifier = OfKind(SpellPartKind.Modifier)[0];
        view.Toggle(modifier);
        Assert.Contains(modifier, view.Modifiers);
        view.Toggle(modifier);
        Assert.DoesNotContain(modifier, view.Modifiers);
    }

    [Fact]
    public void LiveStatsMatchSpellRecipeCompileAgainstTheStaffsTags()
    {
        var staff = StaffFixture.Staff("wooden_staff");
        var view = new SpellBuilderView();
        var element = OfKind(SpellPartKind.Element)[0];
        var shape = OfKind(SpellPartKind.Shape)[0];
        var modifiers = OfKind(SpellPartKind.Modifier);

        void AssertMatches()
        {
            var expected = new SpellRecipe(view.Parts()).Compile(staff.AllowedTags);
            var actual = view.StatsFor(staff);
            Assert.Equal(expected.Verdict, actual.Verdict);
            Assert.Equal(expected.Cost, actual.Cost);
            Assert.Equal(expected.FizzleChance, actual.FizzleChance);
            Assert.Equal(expected.DamageFactor, actual.DamageFactor);
            Assert.Equal(expected.RangeFactor, actual.RangeFactor);
        }

        AssertMatches();
        view.Toggle(shape);
        AssertMatches();
        Assert.False(view.StatsFor(staff).IsValid);
        view.Toggle(element);
        AssertMatches();
        Assert.True(view.StatsFor(staff).IsValid);
        foreach (var modifier in modifiers)
        {
            view.Toggle(modifier);
            AssertMatches();
        }
    }

    [Fact]
    public void TheVersionMovesOnAToggleAndASlotChange()
    {
        var staff = StaffFixture.Staff("wooden_staff");
        var view = new SpellBuilderView();

        var before = view.Version;
        view.Toggle(OfKind(SpellPartKind.Element)[0]);
        Assert.NotEqual(before, view.Version);

        before = view.Version;
        view.SelectSlot(1, staff, default);
        Assert.NotEqual(before, view.Version);
    }

    [Fact]
    public void SelectingABoundSlotLoadsItsRecipe()
    {
        StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out var recipe);
        var component = stack.Get<StaffComponent>();
        var view = new SpellBuilderView();

        view.SelectSlot(0, staff, component);
        Assert.Equal(recipe.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal),
            view.PartKeys().OrderBy(k => k, StringComparer.Ordinal));

        // An unbound slot keeps what is selected.
        var keys = view.PartKeys();
        view.SelectSlot(1, staff, component);
        Assert.Equal(1, view.Slot);
        Assert.Equal(keys, view.PartKeys());

        // A slot past the staff's count is clamped onto its last slot.
        view.SelectSlot(staff.Slots + 5, staff, component);
        Assert.Equal(staff.Slots - 1, view.Slot);
        view.SelectSlot(-3, staff, component);
        Assert.Equal(0, view.Slot);
    }

    [Fact]
    public void ForgettingAPartDropsItFromTheColumnAndTheSelection()
    {
        var inventory = Inventory.Empty();
        var view = new SpellBuilderView();
        view.Refresh(inventory, creative: false);

        var element = view.Column(SpellPartKind.Element)[0];
        var shape = view.Column(SpellPartKind.Shape)[0];
        view.Toggle(element);
        view.Toggle(shape);

        inventory.RestrictKnownParts(SpellPartRegistry.All.Where(p => p != element).Select(p => p.Key));
        var before = view.Version;
        view.Refresh(inventory, creative: false);

        Assert.Null(view.Element);
        Assert.Same(shape, view.Shape);
        Assert.DoesNotContain(element, view.Column(SpellPartKind.Element));
        Assert.NotEqual(before, view.Version);
    }

    [Fact]
    public void CreativeListsEveryPart()
    {
        var inventory = Inventory.Empty();
        inventory.RestrictKnownParts([]);
        var view = new SpellBuilderView();

        view.Refresh(inventory, creative: false);
        foreach (var kind in SpellBuilderCatalog.Columns)
            Assert.Empty(view.Column(kind));

        view.Refresh(inventory, creative: true);
        foreach (var kind in SpellBuilderCatalog.Columns)
            Assert.Equal(OfKind(kind), view.Column(kind));
    }

    [Fact]
    public void ASteadyRefreshChangesNothing()
    {
        var inventory = Inventory.Empty();
        var view = new SpellBuilderView();
        view.Refresh(inventory, creative: false);
        view.Toggle(view.Column(SpellPartKind.Element)[0]);

        var version = view.Version;
        var column = view.Column(SpellPartKind.Element);
        view.Refresh(inventory, creative: false);

        Assert.Equal(version, view.Version);
        Assert.Same(column, view.Column(SpellPartKind.Element));
    }

    [Fact]
    public void ResetEmptiesTheSelectionAndClosesTheView()
    {
        var staff = StaffFixture.Staff("wooden_staff");
        var view = new SpellBuilderView { IsOpen = true };
        view.Toggle(OfKind(SpellPartKind.Element)[0]);
        view.Toggle(OfKind(SpellPartKind.Modifier)[0]);
        view.SelectSlot(1, staff, default);

        view.Reset();

        Assert.False(view.IsOpen);
        Assert.Empty(view.Parts());
        Assert.Equal(0, view.Slot);
    }
}
