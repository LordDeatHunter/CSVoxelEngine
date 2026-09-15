using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

public sealed class PopBoxEmitterTests
{
    private static readonly ushort[] Tiles = [10, 11, 12, 13, 14, 15];

    private static (List<Vertex> Verts, List<ushort> Indices) Emit(BlockHitbox grown, BlockHitbox crop)
    {
        var verts = new List<Vertex>();
        var indices = new List<ushort>();
        ushort offset = 0;
        PopBoxEmitter.EmitBox(verts, indices, ref offset, grown, crop, Vector3.Zero, Tiles);
        return (verts, indices);
    }

    [Fact]
    public void OneBoxEmitsSixQuadsWithChunkWinding()
    {
        var (verts, indices) = Emit(BlockHitbox.FullCube, BlockHitbox.FullCube);
        Assert.Equal(24, verts.Count);
        Assert.Equal(36, indices.Count);
        for (var q = 0; q < 6; q++)
        {
            var b = (ushort)(q * 4);
            var o = q * 6;
            Assert.Equal(new ushort[] { b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3) },
                indices.GetRange(o, 6));
        }
    }

    [Fact]
    public void FullCubeBoxSamplesWholeTiles()
    {
        var (verts, _) = Emit(BlockHitbox.FullCube, BlockHitbox.FullCube);
        for (var face = 0; face < 6; face++)
            for (var c = 0; c < 4; c++)
            {
                var v = verts[face * 4 + c];
                Assert.Equal(Tiles[face], v.Layer);
                Assert.Equal(face, v.Normal & 7);
            }
        foreach (var v in verts)
        {
            Assert.True(v.Uv.X is >= 0f and <= 1f);
            Assert.True(v.Uv.Y is >= 0f and <= 1f);
        }
        var xs = new List<float>();
        var ys = new List<float>();
        foreach (var v in verts)
        {
            xs.Add(v.Uv.X);
            ys.Add(v.Uv.Y);
        }
        Assert.Equal(0f, xs.Min(), 5);
        Assert.Equal(1f, xs.Max(), 5);
        Assert.Equal(0f, ys.Min(), 5);
        Assert.Equal(1f, ys.Max(), 5);
    }

    [Fact]
    public void SlabSidesCropToTheirHeightAtFullWidth()
    {
        var slab = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        var (verts, _) = Emit(slab, slab);
        for (var face = 0; face < 4; face++)
        {
            var u0 = float.MaxValue;
            var u1 = float.MinValue;
            var v0 = float.MaxValue;
            var v1 = float.MinValue;
            for (var c = 0; c < 4; c++)
            {
                var uv = verts[face * 4 + c].Uv;
                u0 = MathF.Min(u0, uv.X);
                u1 = MathF.Max(u1, uv.X);
                v0 = MathF.Min(v0, uv.Y);
                v1 = MathF.Max(v1, uv.Y);
            }
            Assert.Equal(0f, u0, 5);
            Assert.Equal(1f, u1, 5);
            Assert.Equal(0.25f, v1 - v0, 5);
        }
    }

    [Fact]
    public void RaisedSlabAnchorsItsStripAtItsHeight()
    {
        var slab = new BlockHitbox(new Vector3(0f, 0.5f, 0f), new Vector3(1f, 0.75f, 1f));
        var (verts, _) = Emit(slab, slab);
        var v0 = float.MaxValue;
        var v1 = float.MinValue;
        for (var c = 0; c < 4; c++)
        {
            var uv = verts[c].Uv;
            v0 = MathF.Min(v0, uv.Y);
            v1 = MathF.Max(v1, uv.Y);
        }
        Assert.Equal(0.25f, v0, 5);
        Assert.Equal(0.5f, v1, 5);
    }

    [Fact]
    public void SlabTopKeepsItsFullFootprint()
    {
        var slab = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        var (verts, _) = Emit(slab, slab);
        var u0 = float.MaxValue;
        var u1 = float.MinValue;
        var v0 = float.MaxValue;
        var v1 = float.MinValue;
        for (var c = 0; c < 4; c++)
        {
            var uv = verts[4 * 4 + c].Uv;
            u0 = MathF.Min(u0, uv.X);
            u1 = MathF.Max(u1, uv.X);
            v0 = MathF.Min(v0, uv.Y);
            v1 = MathF.Max(v1, uv.Y);
        }
        Assert.Equal(0f, u0, 5);
        Assert.Equal(1f, u1, 5);
        Assert.Equal(0f, v0, 5);
        Assert.Equal(1f, v1, 5);
    }

    [Fact]
    public void PositionsFollowTheGrownBoxAtTheBlockOrigin()
    {
        var crop = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        var grown = new BlockHitbox(new Vector3(-0.05f, -0.05f, -0.05f), new Vector3(1.05f, 0.3f, 1.05f));
        var origin = new Vector3(3f, 41f, -7f);
        var verts = new List<Vertex>();
        var indices = new List<ushort>();
        ushort offset = 0;
        PopBoxEmitter.EmitBox(verts, indices, ref offset, grown, crop, origin, Tiles);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        foreach (var v in verts)
        {
            lo = Vector3.Min(lo, v.Position);
            hi = Vector3.Max(hi, v.Position);
        }
        Assert.Equal(origin + grown.Min, lo);
        Assert.Equal(origin + grown.Max, hi);
    }

    [Fact]
    public void PopVertsStayFullBrightAndUnswayedLikeDrops()
    {
        var (verts, _) = Emit(BlockHitbox.FullCube, BlockHitbox.FullCube);
        foreach (var v in verts)
        {
            Assert.Equal(255, v.Sun);
            Assert.Equal(0, v.Block);
            Assert.Equal(0, v.Sway);
            var r = (byte)(v.PackedColor & 0xFFu);
            var g = (byte)((v.PackedColor >> 8) & 0xFFu);
            var b = (byte)((v.PackedColor >> 16) & 0xFFu);
            var a = (byte)((v.PackedColor >> 24) & 0xFFu);
            Assert.Equal(r, g);
            Assert.Equal(r, b);
            Assert.Equal(255, a);
        }
    }

    [Fact]
    public void FacesKeepDirectionalShading()
    {
        var (verts, _) = Emit(BlockHitbox.FullCube, BlockHitbox.FullCube);
        byte TopShade(int c) => (byte)(verts[4 * 4 + c].PackedColor & 0xFFu);
        byte SideShade(int c) => (byte)(verts[c].PackedColor & 0xFFu);
        for (var c = 0; c < 4; c++)
        {
            Assert.Equal(TopShade(0), TopShade(c));
            Assert.Equal(SideShade(0), SideShade(c));
            Assert.True(TopShade(c) > SideShade(c));
        }
    }

    [Fact]
    public void SecondBoxContinuesTheIndexRun()
    {
        var verts = new List<Vertex>();
        var indices = new List<ushort>();
        ushort offset = 0;
        var slab = new BlockHitbox(new Vector3(0f, 0f, 0f), new Vector3(1f, 0.25f, 1f));
        PopBoxEmitter.EmitBox(verts, indices, ref offset, slab, slab, Vector3.Zero, Tiles);
        PopBoxEmitter.EmitBox(verts, indices, ref offset, slab, slab, Vector3.One, Tiles);
        Assert.Equal(48, verts.Count);
        Assert.Equal(72, indices.Count);
        Assert.Equal((ushort)24, indices[36]);
        Assert.Equal(verts[0].Position + Vector3.One, verts[24].Position);
    }
}
