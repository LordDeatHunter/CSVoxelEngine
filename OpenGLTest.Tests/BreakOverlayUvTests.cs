using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class BreakOverlayUvTests
{
    [Fact]
    public void FullCubeFacesCoverTheWholeFrame()
    {
        for (var face = 0; face < 6; face++)
        {
            var (offset, scale) = BreakOverlayUv.ForFace(face, BlockHitbox.FullCube);
            Assert.Equal(Vector2.Zero, offset);
            Assert.Equal(Vector2.One, scale);
        }
    }

    [Fact]
    public void FloorSlabSidesKeepFullWidthButShallowHeight()
    {
        var box = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        for (var face = 0; face < 4; face++)
        {
            var (offset, scale) = BreakOverlayUv.ForFace(face, box);
            Assert.Equal(0f, offset.Y);
            Assert.Equal(1f, scale.X);
            Assert.Equal(0.25f, scale.Y);
        }
    }

    [Fact]
    public void FloorSlabTopStillCoversTheWholeFrame()
    {
        var box = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        for (var face = 4; face < 6; face++)
        {
            var (offset, scale) = BreakOverlayUv.ForFace(face, box);
            Assert.Equal(Vector2.Zero, offset);
            Assert.Equal(Vector2.One, scale);
        }
    }

    [Fact]
    public void RaisedSlabAnchorsItsStripAtItsFloor()
    {
        var box = new BlockHitbox(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 0.75f, 1f));
        for (var face = 0; face < 4; face++)
        {
            var (offset, scale) = BreakOverlayUv.ForFace(face, box);
            Assert.Equal(0.5f, offset.Y);
            Assert.Equal(0.25f, scale.Y);
        }
    }

    [Fact]
    public void CenteredColumnSamplesItsMiddleBandOnEveryFace()
    {
        var box = new BlockHitbox(new Vector3(0.25f, 0.25f, 0.25f), new Vector3(0.75f, 0.75f, 0.75f));
        for (var face = 0; face < 6; face++)
        {
            var (offset, scale) = BreakOverlayUv.ForFace(face, box);
            Assert.Equal(new Vector2(0.25f, 0.25f), offset);
            Assert.Equal(new Vector2(0.5f, 0.5f), scale);
        }
    }

    [Fact]
    public void NarrowSideArmKeepsFullHeightButCropsAcross()
    {
        var box = new BlockHitbox(new Vector3(0.25f, 0.25f, 0f), new Vector3(0.75f, 0.75f, 1f));
        var (zOffset, zScale) = BreakOverlayUv.ForFace(0, box);
        Assert.Equal(new Vector2(0.25f, 0.25f), zOffset);
        Assert.Equal(new Vector2(0.5f, 0.5f), zScale);

        var (xOffset, xScale) = BreakOverlayUv.ForFace(2, box);
        Assert.Equal(new Vector2(0f, 0.25f), xOffset);
        Assert.Equal(new Vector2(1f, 0.5f), xScale);
    }

    [Fact]
    public void SampledAreaMatchesFaceArea()
    {
        var box = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 0.5f));
        var size = box.Size;
        var expected = new[]
        {
            size.X * size.Y, size.X * size.Y,
            size.Z * size.Y, size.Z * size.Y,
            size.X * size.Z, size.X * size.Z,
        };
        for (var face = 0; face < 6; face++)
        {
            var (_, scale) = BreakOverlayUv.ForFace(face, box);
            Assert.Equal(expected[face], scale.X * scale.Y, 5);
        }
    }

    [Fact]
    public void CropNeverLeavesTheFrame()
    {
        var boxes = new[]
        {
            BlockHitbox.FullCube,
            new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.125f, 1f)),
            new BlockHitbox(new Vector3(0.3f, 0f, 0.3f), new Vector3(0.7f, 0.6f, 0.7f)),
        };
        foreach (var box in boxes)
            for (var face = 0; face < 6; face++)
            {
                var (offset, scale) = BreakOverlayUv.ForFace(face, box);
                Assert.True(offset.X >= 0f && offset.Y >= 0f);
                Assert.True(offset.X + scale.X <= 1f + 1e-6f);
                Assert.True(offset.Y + scale.Y <= 1f + 1e-6f);
            }
    }
}
