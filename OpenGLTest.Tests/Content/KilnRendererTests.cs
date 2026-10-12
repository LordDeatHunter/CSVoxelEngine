using System;
using System.Numerics;
using silkgltriangle.Content;
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
    /// The kiln opening's footprint in world axes once the volume is turned onto
    /// <paramref name="facing"/>: the four corners of the local opening box rotated the way the
    /// render pass rotates every prop it puts inside it.
    /// </summary>
    private static (float MinX, float MaxX, float MinZ, float MaxZ) OpeningFootprint(int facing)
    {
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

        return (minX, maxX, minZ, maxZ);
    }

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

        // The whole drawn fuel row - every unit's full width, not just the row's centre - has to
        // fit between the piers and clear the hearth's back wall, and no two units may share a
        // plane
        Assert.True(KilnRenderer.FuelSpacing >= KilnRenderer.FuelSize);
        var halfRow = (KilnManager.MaxFuel - 1) * 0.5f * KilnRenderer.FuelSpacing
                    + KilnRenderer.FuelSize * 0.5f;
        Assert.True(KilnRenderer.FuelLocal.X - halfRow >= KilnRenderer.OpeningMinX);
        Assert.True(KilnRenderer.FuelLocal.X + halfRow <= KilnRenderer.OpeningMaxX);
        Assert.True(KilnRenderer.FuelLocal.Z + KilnRenderer.FuelSize * 0.5f <= KilnRenderer.OpeningDepthZ);
        Assert.True(KilnRenderer.FuelLocal.Z - KilnRenderer.FuelSize * 0.5f >= 0f);

        for (var facing = 0; facing < 4; facing++)
        {
            // The opening box, rotated onto this facing: an axis-aligned footprint either way
            var (minX, maxX, minZ, maxZ) = OpeningFootprint(facing);

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
    /// The pose a lump of fuel (or any item sprite resting on a station) is drawn in: an
    /// upright sprite turned face-up, so its picture is read from above rather than seen
    /// edge-on like a card standing on the hearth, and its top pointing into the hearth away
    /// from whoever is looking in through the opening. The lift that goes with it has to grow
    /// with the sprite's own extrusion, or a wider sprite sinks through the floor it rests on.
    /// </summary>
    [Fact]
    public void LyingFaceUpTurnsTheSpriteFrontUpAndItsTopIntoTheHearth()
    {
        for (var facing = 0; facing < 4; facing++)
        {
            var yaw = KilnRenderer.OpeningYaw(facing);
            var front = MultiblockShape.FrontNormal(facing);

            // The upright convention the fuel used to rely on: local +Z leaves by the opening
            var standing = Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateRotationY(yaw));
            Assert.Equal(front.X, standing.X, precision: 4);
            Assert.Equal(front.Y, standing.Y, precision: 4);
            Assert.Equal(front.Z, standing.Z, precision: 4);

            var pose = SpritePose.LyingFaceUp(yaw);
            var face = Vector3.Transform(Vector3.UnitZ, pose);
            var top = Vector3.Transform(Vector3.UnitY, pose);

            Assert.Equal(1f, face.Y, precision: 4);      // the picture looks up, whatever the facing
            Assert.Equal(0f, top.Y, precision: 4);       // and the sprite no longer stands
            Assert.Equal(-1f, Vector3.Dot(top, front), precision: 4); // its top points into the hearth
        }

        // A face-up sprite's back face clears the surface it rests on, and a thicker (wider)
        // sprite is lifted further - a fixed lift would bury one of them
        Assert.True(SpritePose.RestingLift(KilnRenderer.FuelSize)
                  > SpritePose.HalfThickness(KilnRenderer.FuelSize, SpritePose.ItemSpriteFrame));
        Assert.True(SpritePose.RestingLift(2f * KilnRenderer.FuelSize)
                  > SpritePose.RestingLift(KilnRenderer.FuelSize));

        // A lump lying on the hearth floor still sits below the flame burning over it
        Assert.True(SpritePose.RestingLift(KilnRenderer.FuelSize)
                  < KilnRenderer.FireLocal.Y - KilnRenderer.OpeningFloorY);
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
    /// What the cooled cast is drawn at: the metal that filled the pocket solidified in place,
    /// so it spans the pocket it dissolves out of instead of shrinking to the icon a dropped
    /// item is drawn at - while still staying clear of the pocket's walls, its floor and the
    /// slab's rim, and under the molten sheet fading out over it.
    /// </summary>
    [Fact]
    public void CastResultFillsThePocketWithoutTouchingItsWallsOrRim()
    {
        // It fills the pocket rather than floating in it, and is nothing like a ground drop
        Assert.True(CastingRenderer.ResultSize < CastingRenderer.PocketWidth);
        Assert.True(CastingRenderer.ResultSize > ItemDrop.Size * 1.5f);

        // Centred in the cell, its edges stand exactly the clearance inside each pocket wall
        Assert.Equal(CastingRenderer.ResultClearance,
            0.5f - CastingRenderer.ResultSize / 2f - CastingRenderer.PocketInset, precision: 4);
        Assert.Equal(CastingRenderer.ResultClearance,
            1f - CastingRenderer.PocketInset - (0.5f + CastingRenderer.ResultSize / 2f), precision: 4);

        // Lying face-up: its back face clears the pocket floor and its top stays under both the
        // brim-full molten sheet drawn over it and the slab's own top
        var half = SpritePose.HalfThickness(CastingRenderer.ResultSize, SpritePose.ItemSpriteFrame);
        Assert.True(CastingRenderer.ResultY - half > CastingRenderer.PocketFloorY);
        Assert.True(CastingRenderer.ResultY + half < CastingRenderer.LiquidHeight(1f));
        Assert.True(CastingRenderer.ResultY + half < CastingRenderer.PocketTopY);

        // A block result is a cube fitted to the pocket instead: resting on its floor, never
        // standing through the slab, never wider than the pocket
        Assert.True(CastingRenderer.ResultCubeY - CastingRenderer.ResultCubeSize / 2f
                  >= CastingRenderer.PocketFloorY - Epsilon);
        Assert.True(CastingRenderer.ResultCubeY + CastingRenderer.ResultCubeSize / 2f
                  <= CastingRenderer.PocketTopY + Epsilon);
        Assert.True(CastingRenderer.ResultCubeSize <= CastingRenderer.PocketWidth);
    }

    /// <summary>
    /// The solid charge waiting to melt: it rests across the bowl's rim inside the opening, it is
    /// a lump the bowl could not simply swallow, and it only ever shrinks as its last unit turns
    /// to liquid - full size while a whole unit is still solid, gone exactly when the charge is.
    /// </summary>
    [Fact]
    public void PendingLumpHoversOverTheBowlInsideTheOpeningAndShrinksOnlyAsItsLastUnitMelts()
    {
        // It sits over the bowl's rim, and even at full size it clears the lintel
        Assert.True(KilnRenderer.PendingLocal.Y >= KilnRenderer.OpeningFloorY + KilnRenderer.CrucibleTopY);
        Assert.True(KilnRenderer.PendingLocal.Y + KilnRenderer.PendingSize <= KilnRenderer.OpeningCeilingY);

        // Wider than the bowl's mouth, so it reads as resting across it rather than fallen in,
        // and no wider than the bowl itself
        Assert.True(KilnRenderer.PendingSize > KilnRenderer.CrucibleInnerMax - KilnRenderer.CrucibleInnerMin);
        Assert.True(KilnRenderer.PendingSize <= 2f * KilnRenderer.CrucibleOuterHalf);

        for (var facing = 0; facing < 4; facing++)
        {
            var (minX, maxX, minZ, maxZ) = OpeningFootprint(facing);
            var lump = KilnRenderer.ToWorld(Vector3.Zero, facing, KilnRenderer.PendingLocal);
            var half = KilnRenderer.PendingSize * 0.5f;
            Assert.InRange(lump.X - half, minX - Epsilon, maxX + Epsilon);
            Assert.InRange(lump.X + half, minX - Epsilon, maxX + Epsilon);
            Assert.InRange(lump.Z - half, minZ - Epsilon, maxZ + Epsilon);
            Assert.InRange(lump.Z + half, minZ - Epsilon, maxZ + Epsilon);
        }

        // The size curve: nothing left means nothing drawn, a whole unit (or several) is a whole
        // lump, and in between it shrinks strictly with what is still solid
        const int unit = 100;
        Assert.Equal(0f, KilnRenderer.PendingScale01(0f, unit), precision: 5);
        Assert.Equal(1f, KilnRenderer.PendingScale01(unit, unit), precision: 5);
        Assert.Equal(1f, KilnRenderer.PendingScale01(3 * unit, unit), precision: 5);

        var previous = KilnRenderer.PendingScale01(0f, unit);
        for (var step = 1; step <= 10; step++)
        {
            var scale = KilnRenderer.PendingScale01(unit * (step / 10f), unit);
            Assert.True(scale > previous);
            Assert.InRange(scale, 0f, 1f);
            previous = scale;
        }

        // A charge with no recorded unit size still draws a finite lump rather than dividing by
        // zero, and still disappears once nothing is pending
        Assert.InRange(KilnRenderer.PendingScale01(50f, 0), 0f, 1f);
        Assert.True(KilnRenderer.PendingScale01(50f, 0) > 0f);
        Assert.Equal(0f, KilnRenderer.PendingScale01(0f, 0), precision: 5);
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
