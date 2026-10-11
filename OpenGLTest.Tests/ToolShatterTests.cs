using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The tool break shatter's colour rule (<see cref="ToolShatter"/>): the chips wear the mean colour
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
        var colour = ToolShatter.ColourOf(firestarter, fallback);

        Assert.True(colour != fallback, "the sprite's mean colour was not read");
        Assert.InRange(colour.X, 0f, 1f);
        Assert.InRange(colour.Y, 0f, 1f);
        Assert.InRange(colour.Z, 0f, 1f);

        // Cached: the fallback of a later call never wins once a colour was read
        Assert.Equal(colour, ToolShatter.ColourOf(firestarter, Vector3.Zero));
    }
}
