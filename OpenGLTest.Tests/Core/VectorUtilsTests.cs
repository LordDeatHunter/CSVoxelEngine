using System;
using System.Numerics;
using VoxelEngine.Data;
using Xunit;

namespace VoxelEngine.Tests.Core;

public sealed class VectorUtilsTests
{
    public VectorUtilsTests() => TestContent.EnsureLoaded();

    [Fact]
    public void NormalizeSafeOrFallsBackOnDegenerateInput()
    {
        Assert.Equal(Vector3.UnitX, Vector3.Zero.NormalizeSafeOr(Vector3.UnitX));
        Assert.Equal(-Vector3.UnitZ, new Vector3(1e-9f, 0f, 0f).NormalizeSafeOr(-Vector3.UnitZ));
        var n = new Vector3(3f, 0f, 0f).NormalizeSafeOr(Vector3.Zero);
        Assert.Equal(Vector3.UnitX, n);
    }

    [Fact]
    public void NormalizeSafeRespectsExplicitThreshold()
    {
        var v = new Vector3(1e-4f, 0f, 0f);
        Assert.Equal(Vector3.Zero, v.NormalizeSafe(1e-6f));
        Assert.Equal(Vector3.UnitX, v.NormalizeSafe(1e-9f));
    }

    [Fact]
    public void FromYawPitchMatchesCameraConvention()
    {
        Assert.Equal(new Vector3(1f, 0f, 0f), Vector3Utils.FromYawPitch(0f, 0f));
        var up = Vector3Utils.FromYawPitch(0f, 90f);
        Assert.Equal(0f, up.X, precision: 4);
        Assert.Equal(1f, up.Y, precision: 4);
    }

    [Fact]
    public void YawRoundTripsThroughFromYaw()
    {
        foreach (var yaw in new[] { -90f, 0f, 45f, 135f, 179f })
            Assert.Equal(yaw, Vector3Utils.FromYaw(yaw).YawDegrees(), precision: 3);
        // +-180 are the same facing; accept either sign.
        Assert.Equal(180f, Math.Abs(Vector3Utils.FromYaw(180f).YawDegrees()), precision: 3);
    }

    [Fact]
    public void BillboardBasisMatchesInlinedFallbacks()
    {
        Vector3.Zero.BillboardBasis(out var right, out var up);
        Assert.Equal(Vector3.UnitX, right);
        Assert.Equal(Vector3.Cross(Vector3.UnitX, -Vector3.UnitZ), up);
    }

    [Fact]
    public void FlattenYAndWithYBehave()
    {
        Assert.Equal(new Vector3(1f, 0f, 3f), new Vector3(1f, 2f, 3f).FlattenY());
        Assert.Equal(new Vector3(1f, 9f, 3f), new Vector3(1f, 2f, 3f).WithY(9f));
    }

    [Fact]
    public void VectorLerpClampsWhileUnclampedOvershoots()
    {
        var a = Vector3.Zero;
        var b = new Vector3(10f, 0f, 0f);
        Assert.Equal(new Vector3(10f, 0f, 0f), a.Lerp(b, 2f));
        Assert.Equal(new Vector3(20f, 0f, 0f), a.LerpUnclamped(b, 2f));
    }

    [Fact]
    public void BlockCellFloorsNegatives()
    {
        Assert.Equal((-1, -1, -1), new Vector3(-0.1f, -0.1f, -0.1f).BlockCell());
        Assert.Equal((16, 64, -17), new Vector3(16.7f, 64f, -16.2f).BlockCell());
    }

    [Fact]
    public void Vector2LerpAndDampMirrorVector3()
    {
        Assert.Equal(new Vector2(10f, 10f), Vector2.Zero.Lerp(new Vector2(10f, 10f), 2f));
        var p = Vector2.Zero.Damp(new Vector2(10f, 0f), 1000f, 1f);
        Assert.Equal(10f, p.X, precision: 3);
    }

    [Fact]
    public void RgbaPackRoundTripsThroughVertexLayout()
    {
        var color = new Rgba(10, 20, 30, 40);
        Assert.Equal(color, Rgba.FromPacked(color.Pack()));
    }

    [Fact]
    public void FromCubeFaceMatchesChunkNormals()
    {
        Assert.Equal(new Vector3(0, 0, -1), Vector3Directions.FromCubeFace(0));
        Assert.Equal(new Vector3(0, 0, 1), Vector3Directions.FromCubeFace(1));
        Assert.Equal(new Vector3(-1, 0, 0), Vector3Directions.FromCubeFace(2));
        Assert.Equal(new Vector3(1, 0, 0), Vector3Directions.FromCubeFace(3));
        Assert.Equal(new Vector3(0, 1, 0), Vector3Directions.FromCubeFace(4));
        Assert.Equal(new Vector3(0, -1, 0), Vector3Directions.FromCubeFace(5));
    }
}
