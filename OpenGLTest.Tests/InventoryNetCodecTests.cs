using Xunit;

namespace silkgltriangle.Tests;

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
}
