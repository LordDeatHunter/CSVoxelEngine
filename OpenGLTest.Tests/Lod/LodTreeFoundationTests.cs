using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Pure future-instance foundations; no content, world, renderer or codec load.</summary>
public sealed class LodTreeFoundationTests
{
    [Fact]
    public void CanonicalInstanceRetainsSignedBoundsAndRejectsTruncationOrMissingMaterial()
    {
        var tree = new LodTreeProxy(int.MaxValue - 30, -1, -40, 20, 52,
            int.MaxValue - 40, int.MaxValue - 10, -12, 13, 3, 7, 8, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(tree, 0, 1, 2, added: false, out var instance));
        Assert.Equal(64, Marshal.SizeOf<LodTreeInstance>());
        Span<byte> bytes = stackalloc byte[LodTreeInstance.ByteSize];
        instance.WriteLittleEndian(bytes);
        Assert.Equal(tree.RootX, BinaryPrimitives.ReadInt32LittleEndian(bytes));
        Assert.Equal(tree.RootZ, BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]));
        Assert.True(LodTreeInstance.TryReadLittleEndian(bytes, 2, out var decoded));
        Assert.Equal(tree.CrownBottom, decoded.CrownBottom);
        Assert.Equal(tree.MaxZ, decoded.MaxZ);
        Assert.True(decoded.TryRegionProxy(int.MaxValue - 50, -16, out var local));
        Assert.Equal(20, local.RootX);
        Assert.False(LodTreeInstance.TryCreate(tree, 0, 2, 2, added: false, out _));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[60..], 1);
        Assert.False(LodTreeInstance.TryReadLittleEndian(bytes, 2, out _));

        var added = tree with { CrownBottom = 49, CrownTop = 51, Profile = LodCrownProfile.Column };
        Assert.True(LodTreeInstance.TryCreate(added, LodTreeInstance.NoMaterial, 1, 2,
            added: true, out var cap));
        Assert.False(LodTreeTemplate.TryPart(cap, 0, int.MaxValue - 50, -16, out _, out _));
        Assert.True(LodTreeTemplate.TryPart(cap, 1, int.MaxValue - 50, -16, out var box, out _));
        Assert.Equal(49f, box.MinY);
        Assert.Equal(51f, box.MaxY);
        Assert.False(LodTreeTemplate.TryPart(cap, 2, int.MaxValue - 50, -16, out _, out _));
    }

    [Fact]
    public void BaseTintOverlayAndSentinelSurviveMaterialRemap()
    {
        ushort[] bases = [1, 2, 3, 4, 5, 6];
        ushort[] tinted = [LodTreeMaterial.NoTile, LodTreeMaterial.NoTile,
            LodTreeMaterial.NoTile, LodTreeMaterial.NoTile, 7, LodTreeMaterial.NoTile];
        Assert.True(LodTreeMaterial.TryCreate(bases, tinted, BiomeTintPalette.Foliage, out var material));
        Assert.True(material.HasOverlayAt(4));
        Assert.Equal(BiomeTintPalette.Foliage, material.Palette);
        Assert.Equal(32, Marshal.SizeOf<LodTreeMaterial>());
        var calls = 0;
        Assert.True(material.TryRemap(tile => { calls++; return (ushort)(tile + 10); }, out var mapped));
        Assert.Equal(7, calls); // six bases and the one real overlay, never NoTile
        Assert.Equal((ushort)15, mapped.BaseAt(4));
        Assert.Equal((ushort)17, mapped.TintedAt(4));
        Assert.Equal(LodTreeMaterial.NoTile, mapped.TintedAt(0));
        Span<byte> bytes = stackalloc byte[LodTreeMaterial.ByteSize];
        mapped.WriteLittleEndian(bytes);
        Assert.True(LodTreeMaterial.TryReadLittleEndian(bytes, out var restored));
        Assert.Equal(mapped.BaseAt(4), restored.BaseAt(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[28..], 1);
        Assert.False(LodTreeMaterial.TryReadLittleEndian(bytes, out _));
        Assert.False(material.TryRemap(_ => LodTreeMaterial.NoTile, out _));
        var table = new LodTreeMaterialTable();
        Assert.Equal(table.Add(17, material), table.Add(17, material));
        Assert.Equal(1, table.Count);
        Assert.Equal(1, table.Add(18, mapped));
        Assert.Throws<InvalidOperationException>(() => table.Add(17, mapped));
    }

    [Fact]
    public void SharedTemplateHasFiniteValidTopologyAndIndependentPartBounds()
    {
        var (vertices, indices) = LodTreeTemplate.Build();
        Assert.Equal(LodTreeTemplate.Vertices, vertices.Length);
        Assert.Equal(LodTreeTemplate.Indices, indices.Length);
        Assert.All(indices, i => Assert.InRange(i, 0, vertices.Length - 1));
        Assert.All(vertices, v =>
        {
            Assert.InRange(v.UnitPosition.X, 0f, 1f);
            Assert.InRange(v.UnitPosition.Y, 0f, 1f);
            Assert.InRange(v.UnitPosition.Z, 0f, 1f);
        });
        Assert.Equal(3, vertices.Select(v => v.Part).Distinct().Count());
        Assert.Equal(2, vertices.Select(v => v.Layer).Distinct().Count());

        var tree = new LodTreeProxy(63, -1, 8, 40, 56, 57, 70, -7, 7, 2,
            1, 2, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(tree, 0, 1, 2, added: false, out var instance));
        Assert.True(LodTreeTemplate.TryPart(instance, 0, 64, 0, out var trunk, out var wood));
        Assert.True(LodTreeTemplate.TryPart(instance, 1, 64, 0, out var crown, out var leaf));
        Assert.True(LodTreeTemplate.TryPart(instance, 2, 64, 0, out var cap, out _));
        Assert.Equal(0, wood);
        Assert.Equal(1, leaf);
        Assert.Equal(8f, trunk.MinY);
        Assert.Equal(40f, trunk.MaxY);
        Assert.Equal(40f, crown.MinY);
        Assert.Equal(56f, cap.MaxY);
        Assert.True(cap.MinX > crown.MinX && cap.MaxX < crown.MaxX);
        Assert.True(instance.TryRegionProxy(64, 0, out var local));
        Assert.Equal(-1, local.RootX);
    }

    [Fact]
    public void TileCoverWaitsForAllReplacementPiecesAndKeepsNegativeSeamsHalfOpen()
    {
        const int regionBlocks = 128;
        const int side = regionBlocks / LodTreeCoverage.TileBlocks;
        var supplied = new bool[side * side];
        var replacement = new bool[supplied.Length];
        var full = new bool[supplied.Length];
        LodTreeCoverage.MarkChildReady(regionBlocks, 0, 0, replacement);
        var one = LodTreeCoverage.Build(regionBlocks, 1, supplied, replacement, full);
        Assert.False(one.Visible(0, 0));
        Assert.True(one.Visible(7, 7));
        LodTreeCoverage.MarkChildReady(regionBlocks, 0, 1, replacement);
        LodTreeCoverage.MarkChildReady(regionBlocks, 1, 0, replacement);
        var three = LodTreeCoverage.Build(regionBlocks, 1, supplied, replacement, full);
        Assert.True(three.Visible(7, 7));
        LodTreeCoverage.MarkChildReady(regionBlocks, 1, 1, replacement); // valid empty child also completes
        var four = LodTreeCoverage.Build(regionBlocks, 1, supplied, replacement, full);
        Assert.All(four.Words.ToArray(), word => Assert.Equal(0UL, word));

        replacement[7 * side + 7] = false; // unload/rebuild makes the coarse tile visible again
        supplied[0] = true; // imported tile is never owned by generated proxy
        var reopened = LodTreeCoverage.Build(regionBlocks, 1, supplied, replacement, full);
        Assert.True(reopened.VisibleAt(-1, -1, -128, -128));
        Assert.False(reopened.VisibleAt(0, -1, -128, -128));
        Assert.False(reopened.VisibleAt(-128, -128, -128, -128));
        Assert.Equal(63, reopened.TileIndexAt(-1, -1, -128, -128));

        var level0 = LodTreeCoverage.Build(regionBlocks, 0, new bool[64], new bool[64], full);
        Assert.True(level0.Visible(1, 1)); // a missing vertical chunk leaves the proxy visible
        full[1 * side + 1] = true; // caller certifies the full proxy Y span, not just ground
        Assert.False(LodTreeCoverage.Build(regionBlocks, 0, new bool[64], new bool[64], full).Visible(1, 1));
    }

    [Fact]
    public void ExplicitTintCornersAgreeAtSharedRegionEdgeWithoutClimateReads()
    {
        Vector3 low = new(0.1f, 0.2f, 0.3f);
        Vector3 mid = new(0.4f, 0.5f, 0.6f);
        Vector3 high = new(0.7f, 0.8f, 0.9f);
        Vector3[] left = [low, low, mid, mid];
        Vector3[] right = [mid, mid, high, high];
        var a = new LodTreeTintLattice(1, left, left);
        var b = new LodTreeTintLattice(1, right, right);
        Assert.Equal(128, a.ByteSize); // two palettes × four vec4 corners × sixteen bytes
        Assert.Equal(new Vector4(mid, 0f), a.Foliage[2]);
        Assert.Equal(a.Sample(BiomeTintPalette.Foliage, 16, 8),
            b.Sample(BiomeTintPalette.Foliage, 0, 8));
        Assert.Equal(low, a.Sample(BiomeTintPalette.Grass, 0, 0));
    }

    [Fact]
    public void CanonicalInstanceRejectsAliasedProfilesAndUnsafePartArithmetic()
    {
        var tree = new LodTreeProxy(-1, 3, -20, 8, 30, -8, 9, -4, 11,
            2, 1, 2, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(tree, 0, 1, 2, added: false, out var instance));
        Span<byte> bytes = stackalloc byte[LodTreeInstance.ByteSize];
        instance.WriteLittleEndian(bytes);
        foreach (var raw in new[] { -256, -1, 256, 257, 258 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes[48..], raw);
            Assert.False(LodTreeInstance.TryReadLittleEndian(bytes, 2, out _));
        }
        instance.WriteLittleEndian(bytes);
        Assert.False(LodTreeInstance.TryReadLittleEndian(bytes[..^1], 2, out _));
        Span<byte> extra = stackalloc byte[LodTreeInstance.ByteSize + 1];
        bytes.CopyTo(extra);
        Assert.False(LodTreeInstance.TryReadLittleEndian(extra, 2, out _));
        BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], int.MaxValue);
        Assert.False(LodTreeInstance.TryReadLittleEndian(bytes, 2, out _));
        instance.WriteLittleEndian(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], int.MaxValue);
        Assert.False(LodTreeInstance.TryReadLittleEndian(bytes, 2, out _));
        Assert.False(instance.TryRegionProxy(int.MaxValue, 0, out _));
        Assert.False(LodTreeTemplate.TryPart(instance, 0, int.MaxValue, 0, out _, out _));
        var nearLimit = new LodTreeProxy(int.MinValue + 50, int.MaxValue - 50,
            -LodTreeInstance.MaxWorldY + 1, -LodTreeInstance.MaxWorldY + 2,
            -LodTreeInstance.MaxWorldY + 4, int.MinValue + 40, int.MinValue + 60,
            int.MaxValue - 60, int.MaxValue - 40, 2, 1, 2, LodCrownProfile.Cone);
        Assert.True(LodTreeInstance.TryCreate(nearLimit, 0, 1, 2, added: false, out var safe));
        Assert.True(LodTreeTemplate.TryPart(safe, 0, int.MinValue + 32, int.MaxValue - 64,
            out var safeTrunk, out _));
        Assert.True(safeTrunk.HasVolume && float.IsFinite(safeTrunk.MinX));
    }

    [Fact]
    public void NaturalWoodOnlyInstanceKeepsCompleteTrunk()
    {
        var tree = new LodTreeProxy(0, -1, -18, 41, 41, 0, 1, -1, 0,
            2, 12, 0, LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(tree, 0, LodTreeInstance.NoMaterial,
            1, added: false, out var instance));
        Span<byte> bytes = stackalloc byte[LodTreeInstance.ByteSize];
        instance.WriteLittleEndian(bytes);
        Assert.True(LodTreeInstance.TryReadLittleEndian(bytes, 1, out var restored));
        Assert.True(restored.HasTrunk);
        Assert.False(restored.HasCrown);
        Assert.True(LodTreeTemplate.TryPart(restored, 0, 0, 0, out var trunk, out var wood));
        Assert.Equal(0, wood);
        Assert.Equal(-18f, trunk.MinY);
        Assert.Equal(41f, trunk.MaxY);
        Assert.False(LodTreeTemplate.TryPart(restored, 1, 0, 0, out _, out _));
        Assert.False(LodTreeTemplate.TryPart(restored, 2, 0, 0, out _, out _));
    }

    [Fact]
    public void AbsentMaterialFacesRemainAbsentAfterRemap()
    {
        ushort n = LodTreeMaterial.NoTile;
        ushort[] bases = [n, n, 7, n, n, n];
        ushort[] tinted = [n, n, n, n, 8, n];
        Assert.True(LodTreeMaterial.TryCreate(bases, tinted, BiomeTintPalette.Grass, out var material));
        var mappedCount = 0;
        Assert.True(material.TryRemap(tile => { mappedCount++; return (ushort)(tile + 20); }, out var mapped));
        Assert.Equal(2, mappedCount);
        Assert.Equal(n, mapped.BaseAt(0));
        Assert.Equal(n, mapped.TintedAt(0));
        Assert.Equal((ushort)27, mapped.BaseAt(2));
        Assert.Equal((ushort)28, mapped.TintedAt(4));
        Span<byte> bytes = stackalloc byte[LodTreeMaterial.ByteSize];
        mapped.WriteLittleEndian(bytes);
        Assert.True(LodTreeMaterial.TryReadLittleEndian(bytes, out var restored));
        Assert.Equal(n, restored.BaseAt(0));
        ushort[] allAbsent = [n, n, n, n, n, n];
        Assert.True(LodTreeMaterial.TryCreate(allAbsent, allAbsent, null, out var invisible));
        Assert.True(invisible.TryRemap(_ => throw new InvalidOperationException(), out _));
    }

    [Fact]
    public void TintLatticeRejectsNonFiniteCoordinatesAndCorners()
    {
        Vector3[] corners = [Vector3.One, Vector3.One, Vector3.One, Vector3.One];
        var bad = (Vector3[])corners.Clone();
        bad[2] = new Vector3(float.NaN, 0f, 0f);
        Assert.Throws<ArgumentException>(() => new LodTreeTintLattice(1, bad, corners));
        var lattice = new LodTreeTintLattice(1, corners, corners);
        Assert.Throws<ArgumentOutOfRangeException>(() => lattice.Sample(BiomeTintPalette.Grass, float.NaN, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => lattice.Sample(BiomeTintPalette.Foliage, 0, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => lattice.Sample((BiomeTintPalette)255, 0, 0));
    }

    [Fact]
    public void FullDetailCoverageRequiresEveryProxyChunkY()
    {
        int[] ys = [-2, -1, 0];
        bool[] ready = [true, true, true];
        Assert.True(LodTreeCoverage.FullDetailYReady(-17, 16, ys, ready));
        ready[1] = false;
        Assert.False(LodTreeCoverage.FullDetailYReady(-17, 16, ys, ready));
        var source = new bool[16];
        var replacement = new bool[16];
        var full = new bool[16];
        full[0] = LodTreeCoverage.FullDetailYReady(-17, 16, ys, ready);
        Assert.True(LodTreeCoverage.Build(64, 0, source, replacement, full).Visible(0, 0));
        ready[1] = true;
        full[0] = LodTreeCoverage.FullDetailYReady(-17, 16, ys, ready);
        Assert.False(LodTreeCoverage.Build(64, 0, source, replacement, full).Visible(0, 0));
        Assert.True(LodTreeCoverage.FullDetailYReady(16, 16, [], []));
        Assert.False(LodTreeCoverage.FullDetailYReady(17, 16, ys, ready));
        Assert.False(LodTreeCoverage.FullDetailYReady(int.MinValue, int.MaxValue, ys, ready));
        Assert.False(LodTreeCoverage.FullDetailYReady(-17, 16, [-1, -2, 0], ready));
    }

    [Fact]
    public void SupportedProxyReachCannotExceedTheGrownBatchEnvelope()
    {
        Assert.True(TreeProxyReach.FitsGrownReach(0, 0));
        Assert.True(TreeProxyReach.FitsGrownReach(7, 21));
        Assert.True(TreeProxyReach.FitsGrownReach(32, 32));
        Assert.False(TreeProxyReach.FitsGrownReach(33, 32));
        Assert.False(TreeProxyReach.FitsGrownReach(-1, 32));
        Assert.False(TreeProxyReach.FitsGrownReach(1, -1));
    }
}
