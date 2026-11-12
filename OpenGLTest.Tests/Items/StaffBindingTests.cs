using System;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Content.Definitions;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Tests.Content;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Items;

/// <summary>The staff slot rules: binding only valid recipes the staff's tags allow, clearing, cycling.</summary>
public class StaffBindingTests
{
    public StaffBindingTests() => TestContent.EnsureLoaded();

    private static StaffItem Staff(int slots, params string[] tags) =>
        (StaffItem)ItemFactory.Create("test_staff",
            StaffContentTests.Spec(slots, 1, new HashSet<string>(tags, StringComparer.Ordinal)));

    private static SpellPart Part(SpellPartKind kind, string name, params string[] tags) =>
        SpellPartLoader.Compile(kind, name, new SpellPartDefinition
        {
            CostShare = 0.1f, Complexity = 1f, Tags = tags.Length > 0 ? tags : ["basic"]
        }, "test/" + name + ".json");

    private static SpellPart Element(string name = "fire") => Part(SpellPartKind.Element, name);
    private static SpellPart Shape(string name = "bolt") => Part(SpellPartKind.Shape, name);

    [Fact]
    public void ABoundValidRecipeLandsInItsSlotOnly()
    {
        var staff = Staff(3, "basic");
        var current = default(StaffComponent);
        SpellPart[] parts = [Element(), Shape()];

        Assert.True(StaffBinding.TryBind(staff, current, 1, parts, out var result, out var verdict));
        Assert.Equal(SpellVerdict.Valid, verdict);
        Assert.Equal(parts.Select(p => p.Key), result.PartsAt(1));
        for (var i = 0; i < staff.Slots; i++)
            if (i != 1) Assert.False(result.IsBound(i));
        Assert.Equal(default, current);
        Assert.Equal(0, current.BoundCount);
    }

    [Fact]
    public void AnInvalidRecipeIsRefusedAndChangesNothing()
    {
        var staff = Staff(2, "basic");
        StaffBinding.TryBind(staff, default, 0, [Element(), Shape()], out var current, out _);

        Assert.False(StaffBinding.TryBind(staff, current, 1, [Element()], out var result, out var verdict));
        Assert.Equal(SpellVerdict.MissingShape, verdict);
        Assert.Equal(current, result);
    }

    [Fact]
    public void ASlotPastTheStaffsCountIsRefused()
    {
        var staff = Staff(2, "basic");
        StaffBinding.TryBind(staff, default, 0, [Element(), Shape()], out var current, out _);

        foreach (var slot in new[] { staff.Slots, -1 })
        {
            Assert.False(StaffBinding.TryBind(staff, current, slot, [Element(), Shape()], out var result, out _));
            Assert.Equal(current, result);
        }
    }

    [Fact]
    public void APartOutsideTheStaffsTagsIsRefusedUntilATierAllowsIt()
    {
        SpellPart[] parts = [Element(), Shape(), Part(SpellPartKind.Modifier, "odd", "advanced")];

        Assert.False(StaffBinding.TryBind(Staff(2, "basic"), default, 0, parts, out _, out var verdict));
        Assert.Equal(SpellVerdict.DisallowedPart, verdict);

        Assert.True(StaffBinding.TryBind(Staff(4, "basic", "advanced"), default, 0, parts, out var bound, out _));
        Assert.True(bound.IsBound(0));
    }

    [Fact]
    public void ClearEmptiesOnlyThatSlot()
    {
        var staff = Staff(3, "basic");
        StaffBinding.TryBind(staff, default, 0, [Element("fire"), Shape()], out var current, out _);
        StaffBinding.TryBind(staff, current, 2, [Element("frost"), Shape()], out current, out _);

        var cleared = StaffBinding.Clear(staff, current, 0);
        Assert.False(cleared.IsBound(0));
        Assert.Equal(current.PartsAt(2), cleared.PartsAt(2));
        Assert.True(current.IsBound(0));
        Assert.Equal(current, StaffBinding.Clear(staff, current, staff.Slots));
    }

    [Fact]
    public void CyclingWrapsBothWays()
    {
        var staff = Staff(3, "basic");
        StaffBinding.TryBind(staff, default, 1, [Element(), Shape()], out var start, out _);

        var current = start;
        var seen = new HashSet<int>();
        for (var i = 0; i < staff.Slots; i++)
        {
            current = StaffBinding.Cycle(staff, current, 1);
            seen.Add(current.Selected);
        }
        Assert.Equal(start, current);
        Assert.Equal(staff.Slots, seen.Count);

        Assert.Equal(staff.Slots - 1, StaffBinding.Cycle(staff, start, -1).Selected);
        Assert.Equal(start.PartsAt(1), StaffBinding.Cycle(staff, start, -1).PartsAt(1));
    }

    [Fact]
    public void EqualComponentsCompareEqualWhateverTheirTrailingEmptySlots()
    {
        var a = new StaffComponent([["element/fire", "shape/bolt"], null], 0);
        var b = new StaffComponent([["element/fire", "shape/bolt"]], 0);
        var c = new StaffComponent([[], null], 0);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(default, c);
        Assert.Equal(default(StaffComponent).GetHashCode(), c.GetHashCode());
        Assert.NotEqual(a, new StaffComponent([["element/fire", "shape/bolt"]], 1));
        Assert.NotEqual(a, new StaffComponent([null, ["element/fire", "shape/bolt"]], 0));
    }
}
