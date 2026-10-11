using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The hover tooltip's lines (<see cref="ItemTooltip"/>): the name comes first, a block shows its
/// hardness and light, and an item shows only the numbers it actually declares (a plain pickup
/// stays two lines).
/// </summary>
public sealed class ItemTooltipTests
{
    public ItemTooltipTests() => TestContent.EnsureLoaded();

    [Fact]
    public void APlainPickupShowsItsNameAndKind()
    {
        var cotton = ContentRegistry.RequireItem("cotton").Id;
        var lines = ItemTooltip.Lines(cotton, null);
        Assert.Equal("Cotton", lines[0]);
        Assert.Equal("Item", lines[1]);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void ABlockShowsItsHardnessAndLight()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var lines = ItemTooltip.Lines(stone, null);
        Assert.Equal("Stone", lines[0]);
        Assert.Contains("Block", lines);
        Assert.Contains(lines, l => l.StartsWith("Hardness"));

        var torch = ContentRegistry.RequireBlock("torch").Id;
        Assert.Contains(ItemTooltip.Lines(torch, null), l => l.StartsWith("Light"));
    }

    [Fact]
    public void AToolShowsItsPowerDamageDurabilityAndFuel()
    {
        var pickaxe = ContentRegistry.RequireItem("primitive_pickaxe").Id;
        var lines = ItemTooltip.Lines(pickaxe, null);
        Assert.Contains("Tool", lines);
        Assert.Contains(lines, l => l.StartsWith("Power"));

        // A weapon declares damage without being a tool
        var blade = ContentRegistry.RequireItem("obsidian_blade").Id;
        Assert.Contains(ItemTooltip.Lines(blade, null), l => l.StartsWith("Damage"));

        var firestarter = ContentRegistry.RequireItem("firestarter").Id;
        Assert.Contains(ItemTooltip.Lines(firestarter, null), l => l.StartsWith("Durability"));

        var coal = ContentRegistry.RequireItem("coal").Id;
        Assert.Contains(ItemTooltip.Lines(coal, null), l => l.StartsWith("Fuel"));

        var crucible = ContentRegistry.RequireItem("crucible").Id;
        Assert.Contains(ItemTooltip.Lines(crucible, null), l => l.StartsWith("Holds"));
    }

    [Fact]
    public void AHoveredStackShowsItsOwnDurabilityLeft()
    {
        var firestarter = ContentRegistry.RequireItem("firestarter");
        var stack = new ItemStack(1, firestarter.Id);
        stack.Damage(3);
        var lines = ItemTooltip.Lines(firestarter.Id, stack);
        var durability = lines.Single(l => l.StartsWith("Durability"));
        Assert.Contains($" {firestarter.MaxDurability - 3}/", durability);
    }
}
