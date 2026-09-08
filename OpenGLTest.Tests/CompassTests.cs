using Xunit;

namespace silkgltriangle.Tests;

public sealed class CompassTests
{
    public CompassTests() => TestContent.EnsureLoaded();

    [Theory]
    [InlineData(-90f, 0f)]   // default look faces -Z: North
    [InlineData(0f, 90f)]    // yaw 0 looks down +X: East
    [InlineData(90f, 180f)]  // +Z: South
    [InlineData(180f, 270f)] // -X: West
    [InlineData(-180f, 270f)]
    [InlineData(270f, 0f)]
    [InlineData(45f, 135f)]
    public void HeadingFromYawMapsYawToClockwiseHeading(float yaw, float expected)
    {
        Assert.Equal(expected, Compass.HeadingFromYaw(yaw), 2);
    }

    [Theory]
    [InlineData(0.5f, -10f, 180f)] // player north of spawn: spawn lies south
    [InlineData(10f, 0.5f, 270f)]  // player east of spawn: spawn lies west
    [InlineData(0.5f, 10f, 0f)]    // player south of spawn: spawn lies north
    [InlineData(-10f, 0.5f, 90f)]  // player west of spawn: spawn lies east
    public void BearingToSpawnUsesSameConvention(float x, float z, float expected)
    {
        Assert.Equal(expected, Compass.BearingTo(x, z, Compass.SpawnX, Compass.SpawnZ), 2);
    }

    [Theory]
    [InlineData(190f, -170f)]
    [InlineData(-190f, 170f)]
    [InlineData(360f, 0f)]
    [InlineData(-360f, 0f)]
    [InlineData(90f, 90f)]
    public void WrapDeltaKeepsTurnsShort(float delta, float expected)
    {
        Assert.Equal(expected, Compass.WrapDelta(delta), 2);
    }

    [Theory]
    [InlineData(0f, "N")]
    [InlineData(22f, "N")]
    [InlineData(23f, "NE")]
    [InlineData(90f, "E")]
    [InlineData(180f, "S")]
    [InlineData(270f, "W")]
    [InlineData(315f, "NW")]
    [InlineData(359f, "N")]
    public void CardinalNameSnapsToNearest45(float heading, string expected)
    {
        Assert.Equal(expected, Compass.CardinalName(heading));
    }

    [Fact]
    public void CompassItemIsPlainMiscPickup()
    {
        var compass = ContentRegistry.RequireItem("compass");
        Assert.Equal(ItemUseAction.None, compass.UseAction);
        Assert.Equal(InventoryCategory.Misc, Inventory.CategoryOf(compass.Id));
    }

    [Fact]
    public void StartingInventoryCarriesACompass()
    {
        var compass = ContentRegistry.RequireItem("compass").Id;
        Assert.Equal(Inventory.StartingCompasses, new Inventory().CountFor(compass));
    }

    [Fact]
    public void CompassShowsOnlyWhileEquippedOnHotbar()
    {
        var inventory = new Inventory();
        var compass = ContentRegistry.RequireItem("compass").Id;

        Assert.False(inventory.ContainsInHotbar(compass));
        Assert.False(inventory.ContainsInHotbar(0));

        inventory.EquipStackable(3, compass);
        Assert.True(inventory.ContainsInHotbar(compass));

        // Equipping the shown id again clears the slot (toggle off)
        inventory.EquipStackable(3, compass);
        Assert.False(inventory.ContainsInHotbar(compass));
    }
}
