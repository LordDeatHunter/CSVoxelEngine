using VoxelEngine.Effects.Water;
using Xunit;

namespace VoxelEngine.Tests.Effects.Water;

/// <summary>
/// The rules of the view from under water: the fog charges only the wet part of a view ray
/// (<see cref="UnderwaterPath"/>, test-fragment.glsl's <c>WetShare</c>) and only open water takes
/// the ocean fog (<see cref="OpenWater"/>, test-fragment.glsl's <c>WetFogColor</c>).
/// </summary>
public sealed class UnderwaterViewTests
{
    [Fact]
    public void ARayThatStaysUnderTheSurfaceIsAllWater()
    {
        foreach (var pointY in new[] { -40f, -3f, 0f, 9.99f, 10f })
            Assert.Equal(1f, UnderwaterPath.WetShare(eyeY: 4f, surfaceY: 10f, pointY));
    }

    [Fact]
    public void TheWaterUnderTheSurfaceIsAllARayToTheWorldAbovePaysFor()
    {
        // Similar triangles: however high the point stands, the wet part of the ray climbs exactly
        // the eye's depth, so a far hilltop is fogged like the surface right above the eye
        const float eyeY = 4f;
        const float surfaceY = 10f;
        var previous = 1f;
        for (var pointY = surfaceY + 0.5f; pointY <= 300f; pointY += 0.5f)
        {
            var share = UnderwaterPath.WetShare(eyeY, surfaceY, pointY);
            Assert.True(share < previous, $"the wet share did not fall at {pointY}");
            Assert.Equal(surfaceY - eyeY, share * (pointY - eyeY), 3);
            previous = share;
        }
    }

    [Fact]
    public void AnEyeAboveTheSurfaceLooksUpThroughNoWater()
    {
        Assert.Equal(0f, UnderwaterPath.WetShare(eyeY: 12f, surfaceY: 10f, pointY: 30f));
    }

    [Fact]
    public void OnlyWaterMostOfTheWayAroundIsOpen()
    {
        Assert.Equal(0f, OpenWater.Amount(0f));
        Assert.Equal(0f, OpenWater.Amount(WaterRenderConfig.OpenWaterShareNear));
        Assert.Equal(1f, OpenWater.Amount(WaterRenderConfig.OpenWaterShareFar));
        Assert.Equal(1f, OpenWater.Amount(1f));

        var previous = 0f;
        for (var share = 0f; share <= 1f; share += 0.01f)
        {
            var amount = OpenWater.Amount(share);
            Assert.True(amount >= previous, $"the open-water weight fell at a share of {share}");
            previous = amount;
        }
    }

    [Fact]
    public void ASmallBodyKeepsThePlainFogInEveryDirection()
    {
        for (var dirY = -1f; dirY <= 1f; dirY += 0.05f)
            Assert.Equal(1f, OpenWater.FogShade(dirY, openWater: 0f));
    }

    [Fact]
    public void TheOceanFogIsDarkerTowardTheDeepAndBrighterTowardTheSurface()
    {
        var previous = OpenWater.FogShade(-1f, openWater: 1f);
        Assert.True(previous < 1f, "looking down into the sea the fog is not darker than the plain fog");
        for (var dirY = -0.95f; dirY <= 1f; dirY += 0.05f)
        {
            var shade = OpenWater.FogShade(dirY, openWater: 1f);
            Assert.True(shade >= previous, $"the shade fell looking up at {dirY}");
            previous = shade;
        }

        Assert.True(previous > 1f, "looking up at the surface the fog is not brighter than the plain fog");
        // Half-open water is shaded less than the open sea, in both directions
        Assert.InRange(OpenWater.FogShade(-1f, 0.5f), OpenWater.FogShade(-1f, 1f), 1f);
        Assert.InRange(OpenWater.FogShade(1f, 0.5f), 1f, OpenWater.FogShade(1f, 1f));
    }
}
