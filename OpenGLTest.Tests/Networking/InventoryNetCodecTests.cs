using System.Linq;
using VoxelEngine.Content.Spells;
using VoxelEngine.Items;
using VoxelEngine.Networking;
using VoxelEngine.UI.InventoryPanel;
using Xunit;

namespace VoxelEngine.Tests.Networking;

/// <summary>
/// The inventory wire codec: the mirror a client imports must match the server's inventory, since
/// the server owns it.
/// </summary>
public sealed class InventoryNetCodecTests
{
    public InventoryNetCodecTests() => TestContent.EnsureLoaded();

    [Fact]
    public void AnInventoryRoundTripsThroughTheCodec()
    {
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var source = new Inventory();
        source.Add(stone, 5);
        source.Select(2);
        var data = source.Export();

        var decoded = InventoryNetCodec.Decode(InventoryNetCodec.Encode(data));

        Assert.Equal(data.SelectedIndex, decoded.SelectedIndex);
        Assert.NotNull(decoded.Blocks);
        Assert.Equal(data.Blocks!.Count, decoded.Blocks!.Count);
        for (var i = 0; i < data.Blocks.Count; i++)
        {
            Assert.Equal(data.Blocks[i].Name, decoded.Blocks[i].Name);
            Assert.Equal(data.Blocks[i].Count, decoded.Blocks[i].Count);
        }
    }

    [Fact]
    public void AStaffsBoundSpellsSurviveTheServerSnapshot()
    {
        var staff = ItemRegistry.All.OfType<StaffItem>().First();
        SpellPart[] recipe =
        [
            SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Element && p.IsAllowedBy(staff.AllowedTags)),
            SpellPartRegistry.All.First(p => p.Kind == SpellPartKind.Shape && p.IsAllowedBy(staff.AllowedTags)),
        ];
        var source = Inventory.Empty();
        Assert.True(source.Add(staff.Id));
        var instance = source.ToolEntries().Single(t => t.ItemId == staff.Id).InstanceId;
        Assert.True(StaffBinding.TryBind(staff, default, staff.Slots - 1, recipe, out var spells, out _));
        spells = StaffBinding.Cycle(staff, spells, -1);
        source.GetToolStack(instance)!.Set(spells);

        var mirror = Inventory.Empty();
        mirror.Import(InventoryNetCodec.Decode(InventoryNetCodec.Encode(source.Export())));

        Assert.Equal(spells, mirror.GetToolStack(instance)!.Get<StaffComponent>());
    }
}
