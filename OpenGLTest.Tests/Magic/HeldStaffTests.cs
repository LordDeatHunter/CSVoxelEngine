using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Magic;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Magic;

/// <summary>The held staff lookup and its slot selection: only a staff in hand selects, only slots it has, and
/// cycling wraps without touching a binding.</summary>
public sealed class HeldStaffTests
{
    public HeldStaffTests() => TestContent.EnsureLoaded();

    [Fact]
    public void ABareHandHasNoStaffToSelectOrCycle()
    {
        var inventory = Inventory.Empty();

        Assert.False(HeldStaff.TryGet(inventory, out _, out _));
        Assert.Equal(-1, HeldStaff.SelectedSlot(inventory));
        Assert.False(HeldStaff.TrySelect(inventory, 0));
        Assert.False(HeldStaff.TryCycle(inventory, 1, out var selected));
        Assert.Equal(-1, selected);
    }

    [Fact]
    public void AnotherToolInHandIsNoStaff()
    {
        var inventory = Inventory.Empty();
        var tool = ItemRegistry.All.First(i => i.StorageCategory == InventoryCategory.Tools && i is not StaffItem);
        Assert.True(inventory.Add(tool.Id));
        var instance = inventory.Tools.Last().InstanceId;
        inventory.Select(Enumerable.Range(0, Inventory.SlotCount).First(i => inventory.ToolInstanceAt(i) == instance));
        Assert.Same(tool, inventory.HeldItem);

        Assert.False(HeldStaff.TryGet(inventory, out _, out _));
        Assert.False(HeldStaff.TrySelect(inventory, 0));
        Assert.False(HeldStaff.TryCycle(inventory, 1, out _));
        Assert.False(inventory.GetToolStack(instance)!.Has<StaffComponent>());
    }

    [Fact]
    public void SelectRefusesASlotTheStaffLacksAndKeepsTheSelection()
    {
        var inventory = StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        Assert.True(HeldStaff.TrySelect(inventory, staff.Slots - 1));
        Assert.Equal(staff.Slots - 1, HeldStaff.SelectedSlot(inventory));

        foreach (var slot in new[] { -1, staff.Slots, SpellConfig.StaffMaxSlots })
        {
            Assert.False(HeldStaff.TrySelect(inventory, slot));
            Assert.Equal(staff.Slots - 1, stack.Get<StaffComponent>().Selected);
        }
    }

    [Fact]
    public void CyclingWrapsBothWaysAndKeepsEveryBinding()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out _);
        StaffFixture.Bind(staff, stack, staff.Slots - 1, out _);
        var before = stack.Get<StaffComponent>();

        Assert.True(HeldStaff.TryCycle(inventory, -1, out var selected));
        Assert.Equal(staff.Slots - 1, selected);
        Assert.Equal(selected, HeldStaff.SelectedSlot(inventory));
        Assert.True(HeldStaff.TryCycle(inventory, 1, out selected));
        Assert.Equal(before.Selected, selected);

        for (var i = 0; i < staff.Slots; i++)
            Assert.True(HeldStaff.TryCycle(inventory, 1, out _));
        Assert.Equal(before, stack.Get<StaffComponent>());

        Assert.False(HeldStaff.TryCycle(inventory, 0, out selected));
        Assert.Equal(-1, selected);

        HeldStaff.TryCycle(inventory, 1, out _);
        var after = stack.Get<StaffComponent>();
        for (var slot = 0; slot < staff.Slots; slot++)
            Assert.Equal(before.PartsAt(slot), after.PartsAt(slot));
    }

    private static string[] Keys(SpellPart[] parts) => parts.Select(p => p.Key).ToArray();

    [Fact]
    public void AKnownValidRecipeBindsIntoItsSlotOnly()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        StaffFixture.KnowAll(inventory);
        StaffFixture.Bind(staff, stack, 0, out _);
        var before = stack.Get<StaffComponent>();
        var keys = Keys(StaffFixture.ValidRecipe(staff));
        var slot = staff.Slots - 1;

        Assert.True(HeldStaff.TryBind(inventory, slot, keys, creative: false));

        var after = stack.Get<StaffComponent>();
        Assert.Equal(keys, after.PartsAt(slot));
        for (var i = 0; i < staff.Slots; i++)
            if (i != slot) Assert.Equal(before.PartsAt(i), after.PartsAt(i));
        Assert.Equal(before.Selected, after.Selected);
    }

    [Fact]
    public void AnUnknownPartIsRefusedInSurvivalButCreativeKnowsEveryPart()
    {
        var inventory = StaffFixture.Holding("wooden_staff", out var staff, out var stack);
        var keys = Keys(StaffFixture.ValidRecipe(staff));
        inventory.ReplaceKnownParts(SpellPartRegistry.All.Select(p => p.Key).Where(k => k != keys[0]));
        var before = stack.Get<StaffComponent>();

        Assert.False(HeldStaff.TryBind(inventory, 0, keys, creative: false));
        Assert.Equal(before, stack.Get<StaffComponent>());

        Assert.True(HeldStaff.TryBind(inventory, 0, keys, creative: true));
        Assert.Equal(keys, stack.Get<StaffComponent>().PartsAt(0));
    }

    [Fact]
    public void AnInvalidRecipeOrAnUnknownKeyIsRefusedAndChangesNothing()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        var before = stack.Get<StaffComponent>();
        var elements = SpellPartRegistry.All.Where(p => p.Kind == SpellPartKind.Element).Take(2).ToArray();
        var shape = SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Shape);
        var valid = Keys(StaffFixture.ValidRecipe(staff));

        foreach (var keys in new string[][]
                 {
                     Keys([elements[0], elements[1], shape]),
                     Keys([shape]),
                     [],
                     [valid[0], "shape/no_such_part"],
                 })
        {
            Assert.False(HeldStaff.TryBind(inventory, 0, keys, creative: true));
            Assert.Equal(before, stack.Get<StaffComponent>());
        }
    }

    [Fact]
    public void BindAndClearNeedAStaffInHand()
    {
        var inventory = Inventory.Empty();
        var keys = Keys(StaffFixture.ValidRecipe(StaffFixture.Staff("wooden_staff")));

        Assert.False(HeldStaff.TryBind(inventory, 0, keys, creative: true));
        Assert.False(HeldStaff.TryClear(inventory, 0));
    }

    [Fact]
    public void ClearUnbindsABoundSlotAndRefusesAnUnboundOne()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out _);

        Assert.False(HeldStaff.TryClear(inventory, staff.Slots - 1));
        Assert.False(HeldStaff.TryClear(inventory, -1));
        Assert.False(HeldStaff.TryClear(inventory, staff.Slots));
        Assert.True(stack.Get<StaffComponent>().IsBound(0));

        Assert.True(HeldStaff.TryClear(inventory, 0));
        Assert.False(stack.Get<StaffComponent>().IsBound(0));
        Assert.False(HeldStaff.TryClear(inventory, 0));
    }

    [Fact]
    public void AReplacedSetIsKnownExactly()
    {
        var inventory = Inventory.Empty();
        var kept = SpellPartRegistry.All.First();
        var version = inventory.KnownPartsVersion;
        inventory.ReplaceKnownParts([kept.Key]);
        Assert.NotEqual(version, inventory.KnownPartsVersion);

        foreach (var part in SpellPartRegistry.All)
        {
            Assert.Equal(part == kept, KnownSpellParts.Knows(inventory, part, creative: false));
            Assert.True(KnownSpellParts.Knows(inventory, part, creative: true));
        }
    }

    [Fact]
    public void ReplacingWithTheSameSetDoesNotMoveTheVersion()
    {
        var inventory = Inventory.Empty();
        var keys = SpellPartRegistry.All.Take(2).Select(p => p.Key).ToArray();
        inventory.ReplaceKnownParts(keys);
        var version = inventory.KnownPartsVersion;

        inventory.ReplaceKnownParts([keys[1], keys[0]]);
        Assert.Equal(version, inventory.KnownPartsVersion);

        inventory.ReplaceKnownParts(keys.Take(1));
        Assert.NotEqual(version, inventory.KnownPartsVersion);
    }
}
