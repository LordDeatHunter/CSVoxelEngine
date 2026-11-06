using System.Linq;
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
}
