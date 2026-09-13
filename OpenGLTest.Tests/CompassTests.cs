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
    public void StartingInventoryCarriesACompass()
    {
        var compass = ContentRegistry.RequireItem("compass").Id;
        Assert.Equal(Inventory.StartingCompasses, new Inventory().CountFor(compass));
    }

    [Fact]
    public void CompassShowsOnlyWhileEquippedOnHotbar()
    {        var inventory = new Inventory();
        var compass = ContentRegistry.RequireItem("compass").Id;

        Assert.False(inventory.ContainsInHotbar(compass));
        Assert.False(inventory.ContainsInHotbar(0));

        inventory.EquipStackable(3, compass);
        Assert.True(inventory.ContainsInHotbar(compass));

        // Equipping the shown id again clears the slot (toggle off)
        inventory.EquipStackable(3, compass);
        Assert.False(inventory.ContainsInHotbar(compass));
    }

    [Theory]
    // Facing north the needle points up; turning clockwise swings it counter-clockwise,
    // so facing east it points left (west, frame 12 of the clockwise sheet).
    [InlineData(0f, 0)]
    [InlineData(90f, 12)]
    [InlineData(180f, 8)]
    [InlineData(270f, 4)]
    [InlineData(-90f, 4)]
    [InlineData(45f, 14)]
    [InlineData(359f, 0)]
    [InlineData(11.25f, 0)]
    [InlineData(12f, 15)]
    public void HeadingFrameKeepsTheNeedleOnNorth(float heading, int expected)
    {
        Assert.Equal(expected, Compass.HeadingFrame(heading, 16));
    }

    [Fact]
    public void CompassFrameIgnoresTime()
    {
        var compass = ContentRegistry.RequireItem("compass");
        Assert.Equal(12, compass.FrameAt(999f, 90f));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.125f, 1)]
    [InlineData(0.249f, 1)]
    [InlineData(2f, 0)]
    [InlineData(-0.125f, 15)]
    public void LoopFrameSlidesOneFramePerTick(float time, int expected)
    {
        Assert.Equal(expected, Item.LoopFrame(time, 8f, 16));
    }

    [Theory]
    [InlineData(8f, 0)]
    [InlineData(0f, 16)]
    public void LoopFrameIgnoresDegenerateInput(float fps, int frames)
    {
        Assert.Equal(0, Item.LoopFrame(1f, fps, frames));
    }
}
