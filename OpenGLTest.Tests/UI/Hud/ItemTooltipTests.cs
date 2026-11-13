using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Spells;
using VoxelEngine.Data;
using VoxelEngine.Items;
using VoxelEngine.UI;
using VoxelEngine.UI.Hud;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.UI.Hud;

/// <summary>
/// The hover tooltip's lines (<see cref="ItemTooltip"/>): the name comes first, a block shows its
/// hardness and light, and an item shows only the numbers it actually declares (a plain pickup
/// stays two lines), and a teaching scroll names every part it teaches (how the hint scroll shows its combo).
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
        var blade = ContentRegistry.RequireItem("obsidian_dagger").Id;
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

    [Fact]
    public void AHoveredCrucibleListsEveryMetalItHolds()
    {
        var crucible = ContentRegistry.RequireItem("crucible");
        var metals = ContentRegistry.MoltenMaterials.Take(2).ToArray();
        var stack = new ItemStack(1, crucible.Id);
        stack.Set(new CrucibleComponent([new CrucibleMetal(metals[0].Id, 100, 0), new CrucibleMetal(metals[1].Id, 0, 50)]));

        var lines = ItemTooltip.Lines(crucible.Id, stack);
        Assert.All(metals, m => Assert.Single(lines, l => l.StartsWith(m.DisplayName)));
        // An empty vessel lists no metal
        Assert.DoesNotContain(ItemTooltip.Lines(crucible.Id, new ItemStack(2, crucible.Id)),
            l => metals.Any(m => l.StartsWith(m.DisplayName)));
    }

    [Fact]
    public void AScrollNamesEveryPartItTeaches()
    {
        var scrolls = ItemRegistry.All.OfType<LearnPartsItem>().ToList();
        Assert.NotEmpty(scrolls);
        foreach (var scroll in scrolls)
        {
            var lines = ItemTooltip.Lines(scroll.Id, null);
            Assert.Contains(lines, l => scroll.Teaches.All(k => l.Contains(SpellPartRegistry.TryGetByKey(k)!.DisplayName)));
        }
    }

    [Fact]
    public void ATitleWearsItsItemsRarityAndANonGearItemNamesIt()
    {
        foreach (var item in ItemRegistry.All)
        {
            var lines = ItemTooltip.Lines(item.Id, null, null, out var colors);
            var rarity = Rarities.Of(item.Id, null);
            var style = RarityStyle.Of(rarity);
            Assert.Equal(style.Color, colors.GetValueOrDefault(0, Rgba.White));
            // Gear names its rarity with its slot; any other item above Common names it on its kind line
            if (item is not GearItem and not BlockItem && style.HasChrome)
                Assert.StartsWith(Rarities.Label(rarity), lines[1]);
        }
    }

    [Fact]
    public void TheHoverTooltipCarriesTheHoveredStacksRarity()
    {
        var inventory = Inventory.Empty();
        var cap = ItemRegistry.All.OfType<GearItem>().First(g => g.SourceName == "bronze_cap");
        var rolled = cap.Rarity == Rarity.Legendary ? Rarity.Common : cap.Rarity + 1;
        Assert.True(inventory.AddRolledGear(cap.Id, GearRoller.Roll(cap, rolled, 5)));
        var stack = inventory.GetToolStack(inventory.GearEntries()[0].InstanceId)!;
        var anchor = new Vector2(120f, 64f);

        var hover = ItemTooltip.Hover(anchor, cap.Id, stack, inventory.Equipment);

        Assert.Equal(rolled, hover.Rarity);
        Assert.Equal(RarityStyle.Of(rolled).Color, hover.Colors.GetValueOrDefault(0, Rgba.White));
        Assert.Equal(anchor, hover.Anchor);
        Assert.Equal(ItemTooltip.Lines(cap.Id, stack, inventory.Equipment, out _), hover.Lines);
    }
}
