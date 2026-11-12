using VoxelEngine.Magic;
using VoxelEngine.Tests.Magic;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

public class StaffSlotIndicatorTests
{
    public StaffSlotIndicatorTests() => TestContent.EnsureLoaded();

    [Fact]
    public void NoStaffInHandShowsNoLabel()
    {
        var indicator = new StaffSlotIndicator();
        Assert.Null(indicator.Label(Inventory.Empty()));
    }

    [Fact]
    public void ABoundSlotNamesItsIndexAndEveryPart()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out var parts);

        var label = new StaffSlotIndicator().Label(inventory);

        Assert.NotNull(label);
        Assert.Contains($"1/{staff.Slots}", label);
        foreach (var part in parts)
            Assert.Contains(part.DisplayName, label);
    }

    [Fact]
    public void SelectingAnUnboundSlotDropsTheBoundPartNames()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        Assert.True(staff.Slots >= 2);
        StaffFixture.Bind(staff, stack, 0, out var parts);
        var indicator = new StaffSlotIndicator();
        var bound = indicator.Label(inventory);

        Assert.True(HeldStaff.TrySelect(inventory, 1));
        var unbound = indicator.Label(inventory);

        Assert.NotNull(unbound);
        Assert.NotEqual(bound, unbound);
        Assert.Contains($"2/{staff.Slots}", unbound);
        foreach (var part in parts)
            Assert.DoesNotContain(part.DisplayName, unbound);
    }

    [Fact]
    public void TheLabelIsRebuiltOnlyWhenTheSelectionChanges()
    {
        var inventory = StaffFixture.Holding("crystal_staff", out var staff, out var stack);
        StaffFixture.Bind(staff, stack, 0, out _);
        var indicator = new StaffSlotIndicator();

        var first = indicator.Label(inventory);
        Assert.Same(first, indicator.Label(inventory));

        Assert.True(HeldStaff.TrySelect(inventory, 1));
        var changed = indicator.Label(inventory);
        Assert.NotSame(first, changed);
        Assert.Same(changed, indicator.Label(inventory));
    }
}
