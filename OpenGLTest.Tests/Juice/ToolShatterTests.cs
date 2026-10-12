using System.Numerics;
using VoxelEngine.Juice;
using Xunit;

namespace VoxelEngine.Tests.Juice;

/// <summary>
/// The tool break shatter's color rule (<see cref="ToolShatter"/>): the chips wear the mean color
/// of the item's sprite, read once and cached per item.
/// </summary>
public sealed class ToolShatterTests
{
    public ToolShatterTests() => TestContent.EnsureLoaded();

    [Fact]
    public void TheShatterColourIsTheSpritesMeanOpaqueColour()
    {
        var firestarter = ContentRegistry.RequireItem("firestarter");
        var fallback = new Vector3(0.75f, 0.7f, 0.6f);
        var color = ToolShatter.ColourOf(firestarter, fallback);

        Assert.True(color != fallback, "the sprite's mean color was not read");
        Assert.InRange(color.X, 0f, 1f);
        Assert.InRange(color.Y, 0f, 1f);
        Assert.InRange(color.Z, 0f, 1f);

        // Cached: the fallback of a later call never wins once a color was read
        Assert.Equal(color, ToolShatter.ColourOf(firestarter, Vector3.Zero));
    }
}
