using System;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The placement and animation contracts the kiln and casting render passes read: where the
/// props they draw stand inside the kiln's opening, and the two curves that drive a molten
/// surface rising and a finished cast fading in. None of it touches GL — the pass only turns
/// these numbers into matrices, so pinning them here is what keeps a prop out of the brickwork
/// and the dissolve free of a hard cut.
/// </summary>
public sealed class KilnRendererTests
{
    public KilnRendererTests() => TestContent.EnsureLoaded();

    private const float Epsilon = 1e-4f;

    /// <summary>
    /// Contract between the renderer's placement constants and the kiln model's opening: a prop
    /// standing outside the opening would clip into the brickwork, and a flame in front of the
    /// crucible would burn through the bowl instead of under it.
    /// </summary>
    [Fact]
    public void CrucibleAndFuelSitInsideTheOpeningAndTheFlameStandsBehindTheBowl()
    {
        // The bowl's rim has to clear the lintel whatever the facing
        Assert.True(KilnRenderer.OpeningFloorY + KilnRenderer.CrucibleTopY < KilnRenderer.OpeningCeilingY);

        // The flame burns behind the bowl's back wall, and the fuel sits behind the flame
        Assert.True(KilnRenderer.FireLocal.Z >= KilnRenderer.CrucibleLocal.Z + KilnRenderer.CrucibleOuterHalf);
        Assert.True(KilnRenderer.FuelLocal.Z > KilnRenderer.FireLocal.Z);

        for (var facing = 0; facing < 4; facing++)
        {
            // The opening box, rotated onto this facing: an axis-aligned footprint either way
            var minX = float.MaxValue;
            var maxX = float.MinValue;
            var minZ = float.MaxValue;
            var maxZ = float.MinValue;
            foreach (var x in new[] { KilnRenderer.OpeningMinX, KilnRenderer.OpeningMaxX })
            foreach (var z in new[] { 0f, KilnRenderer.OpeningDepthZ })
            {
                var corner = MultiblockShape.RotatePoint(new Vector3(x, KilnRenderer.OpeningFloorY, z), facing);
                minX = MathF.Min(minX, corner.X);
                maxX = MathF.Max(maxX, corner.X);
                minZ = MathF.Min(minZ, corner.Z);
                maxZ = MathF.Max(maxZ, corner.Z);
            }

            var bowl = KilnRenderer.ToWorld(Vector3.Zero, facing, KilnRenderer.CrucibleLocal);
            Assert.Equal(KilnRenderer.OpeningFloorY, bowl.Y, precision: 4);
            Assert.InRange(bowl.X - KilnRenderer.CrucibleOuterHalf, minX - Epsilon, maxX + Epsilon);
            Assert.InRange(bowl.X + KilnRenderer.CrucibleOuterHalf, minX - Epsilon, maxX + Epsilon);
            Assert.InRange(bowl.Z - KilnRenderer.CrucibleOuterHalf, minZ - Epsilon, maxZ + Epsilon);
            Assert.InRange(bowl.Z + KilnRenderer.CrucibleOuterHalf, minZ - Epsilon, maxZ + Epsilon);

            var fuel = KilnRenderer.ToWorld(Vector3.Zero, facing, KilnRenderer.FuelLocal);
            Assert.Equal(KilnRenderer.OpeningFloorY, fuel.Y, precision: 4);
            Assert.InRange(fuel.X, minX - Epsilon, maxX + Epsilon);
            Assert.InRange(fuel.Z, minZ - Epsilon, maxZ + Epsilon);
        }
    }

    /// <summary>
    /// Both liquid surfaces rise strictly with what fills them and never leave their vessel, so
    /// a pool reads as filling rather than jumping, and never pokes through a bowl's floor or
    /// a mold's rim.
    /// </summary>
    [Fact]
    public void LiquidHeightsAreMonotoneAndStayInsideTheirVessels()
    {
        var capacity = ContentRegistry.RequireItem("crucible").CrucibleCapacityMl;
        Assert.True(capacity > 0);

        Assert.True(KilnRenderer.LiquidHeight(0f, capacity) > KilnRenderer.CrucibleFloorY);
        Assert.True(KilnRenderer.LiquidHeight(capacity, capacity) < KilnRenderer.CrucibleTopY);

        var previous = float.MinValue;
        for (var step = 0; step <= 8; step++)
        {
            var height = KilnRenderer.LiquidHeight(capacity * (step / 8f), capacity);
            Assert.True(height > previous);
            Assert.InRange(height, KilnRenderer.CrucibleFloorY, KilnRenderer.CrucibleTopY);
            previous = height;
        }

        // Past full it clamps rather than climbing out of the bowl, and a capacity of 0 (an item
        // that is not a crucible) is a finite empty reading rather than a division by zero
        Assert.Equal(KilnRenderer.LiquidHeight(capacity, capacity),
            KilnRenderer.LiquidHeight(capacity * 2, capacity), precision: 5);
        Assert.True(float.IsFinite(KilnRenderer.LiquidHeight(5f, 0)));

        previous = float.MinValue;
        for (var step = 0; step <= 8; step++)
        {
            var height = CastingRenderer.LiquidHeight(step / 8f);
            Assert.True(height > previous);
            Assert.InRange(height, CastingRenderer.PocketFloorY, CastingRenderer.PocketTopY);
            previous = height;
        }
    }

    /// <summary>
    /// The cooling cross-fade: the liquid and the result always sum to one opaque draw, each
    /// moves only one way, and no single step of the curve is large enough to read as a cut.
    /// </summary>
    [Fact]
    public void CoolingDissolveIsSmoothComplementaryAndMonotone()
    {
        Assert.Equal(1f, CastingRenderer.LiquidAlpha(0f), precision: 5);
        Assert.Equal(0f, CastingRenderer.LiquidAlpha(1f), precision: 5);
        Assert.Equal(0.5f, CastingRenderer.ResultAlpha(0.5f), precision: 5);

        var previousLiquid = CastingRenderer.LiquidAlpha(0f);
        var previousResult = CastingRenderer.ResultAlpha(0f);
        for (var step = 1; step <= 20; step++)
        {
            var cool = step / 20f;
            var liquid = CastingRenderer.LiquidAlpha(cool);
            var result = CastingRenderer.ResultAlpha(cool);

            Assert.Equal(1f, liquid + result, precision: 5);
            Assert.True(liquid <= previousLiquid);
            Assert.True(result >= previousResult);
            Assert.True(MathF.Abs(liquid - previousLiquid) < 0.1f);
            Assert.True(MathF.Abs(result - previousResult) < 0.1f);

            previousLiquid = liquid;
            previousResult = result;
        }
    }
}
