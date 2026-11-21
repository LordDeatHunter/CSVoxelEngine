using System;
using System.Buffers.Binary;
using System.Numerics;
using VoxelEngine.Effects.Climate;
using VoxelEngine.Lod;
using VoxelEngine.WorldGen.Trees.Shape;
using Xunit;

namespace VoxelEngine.Tests.Lod;

/// <summary>Unrun CPU controls for the proposed render-only regional template mode.</summary>
public sealed class LodTreeSingleLayerProposalTests
{
    private const ushort Missing = LodTreeMaterial.NoTile;

    [Fact]
    public void SingleLayerKeepsOriginalPartFaceCodesAndQuadTopology()
    {
        var (oldVertices, oldIndices) = LodTreeTemplate.Build();
        var (single, indices) = LodTreeTemplate.Build(singleLayer: true);
        Assert.Equal(144, oldVertices.Length);
        Assert.Equal(216, oldIndices.Length);
        Assert.Equal(72, single.Length);
        Assert.Equal(108, indices.Length);
        for (var part = 0; part < 3; part++)
        for (var face = 0; face < 6; face++)
        {
            var group = part * 6 + face;
            for (var corner = 0; corner < 4; corner++)
            {
                var candidate = single[group * 4 + corner];
                var original = oldVertices[group * 8 + corner];
                Assert.Equal(original.UnitPosition, candidate.UnitPosition);
                Assert.Equal(original.Part, candidate.Part);
                Assert.Equal(original.Face, candidate.Face);
                Assert.Equal((byte)0, candidate.Layer);
                var code = LodTreeTemplate.LayerCode(candidate);
                Assert.Equal(part, code / 12);
                Assert.Equal(face, code / 2 % 6);
                Assert.Equal(0, code % 2);
            }
            for (var j = 0; j < 6; j++)
                Assert.Equal(oldIndices[group * 12 + j] - group * 4,
                    indices[group * 6 + j]);
        }
    }

    [Fact]
    public void MixedOneLayerFacesUseOneModeAndKeepLogicalTilesAndTintPalette()
    {
        ushort[] bases = [11, Missing, 12, Missing, 13, Missing];
        ushort[] tinted = [Missing, 21, Missing, 22, Missing, Missing];
        Assert.True(LodTreeMaterial.TryCreate(bases, tinted, BiomeTintPalette.Foliage, out var mixed));
        Assert.False(mixed.HasOverlayAt(0));
        Assert.False(mixed.HasOverlayAt(1));
        Assert.Equal(BiomeTintPalette.Foliage, mixed.Palette);
        var build = Trees(mixed, mixed);
        var payload = LodTreeGpuPayload.Pack(build, 0, 0);
        Assert.True(payload.SingleLayer);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(payload.Bytes.AsSpan(32, 4)));
        Assert.Equal(64 + LodTreeInstance.ByteSize + 2 * LodTreeMaterial.ByteSize +
            build.Tint!.ByteSize, payload.MaskOffset);
        for (var face = 0; face < 6; face++)
        {
            Assert.Equal(bases[face], mixed.BaseAt(face));
            Assert.Equal(tinted[face], mixed.TintedAt(face));
        }
        // The last face has no tile in either layer and stays absent.
        Assert.Equal(Missing, mixed.BaseAt(5));
        Assert.Equal(Missing, mixed.TintedAt(5));
        Span<byte> expected = stackalloc byte[LodTreeMaterial.ByteSize];
        mixed.WriteLittleEndian(expected);
        var materialOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.Bytes.AsSpan(4, 4)) * 4;
        Assert.True(payload.Bytes.AsSpan(materialOffset, expected.Length).SequenceEqual(expected));
    }

    [Fact]
    public void UnusedDualLayerFaceForcesWholeRegionGenericFallback()
    {
        ushort[] baseOnly = [7, 7, 7, 7, 7, 7];
        ushort[] absent = [Missing, Missing, Missing, Missing, Missing, Missing];
        Assert.True(LodTreeMaterial.TryCreate(baseOnly, absent, null, out var ordinary));
        ushort[] tinted = [Missing, Missing, Missing, Missing, 9, Missing];
        Assert.True(LodTreeMaterial.TryCreate(baseOnly, tinted, BiomeTintPalette.Grass, out var dual));
        var build = Trees(ordinary, ordinary, dual); // index 2 is intentionally unused by the instance
        var payload = LodTreeGpuPayload.Pack(build, 0, 0);
        Assert.True(dual.HasOverlayAt(4));
        Assert.False(payload.SingleLayer);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(payload.Bytes.AsSpan(32, 4)));
        var (generic, indices) = LodTreeTemplate.Build();
        Assert.Equal(144, generic.Length);
        Assert.Equal(216, indices.Length);
        Assert.Equal((byte)0, generic[(4 * 2) * 4].Layer);
        Assert.Equal((byte)1, generic[(4 * 2 + 1) * 4].Layer);
        var materialOffset = BinaryPrimitives.ReadInt32LittleEndian(payload.Bytes.AsSpan(4, 4)) * 4;
        Span<byte> expected = stackalloc byte[LodTreeMaterial.ByteSize];
        dual.WriteLittleEndian(expected);
        Assert.True(payload.Bytes.AsSpan(materialOffset + 2 * expected.Length, expected.Length)
            .SequenceEqual(expected));
    }

    private static LodTreeBuild Trees(params LodTreeMaterial[] materials)
    {
        var proxy = new LodTreeProxy(2, 3, 32, 40, 52, 0, 8, 0, 8, 1, 11, 12,
            LodCrownProfile.Dome);
        Assert.True(LodTreeInstance.TryCreate(proxy, 0, 1, materials.Length, false,
            out var instance));
        var min = new int[16];
        var max = new int[16];
        Array.Fill(min, int.MaxValue);
        Array.Fill(max, int.MinValue);
        min[0] = 32;
        max[0] = 52;
        var corners = new Vector3[25];
        Array.Fill(corners, Vector3.One);
        return new LodTreeBuild([instance], materials,
            new LodTreeTintLattice(4, corners, corners), [1UL], min, max);
    }
}
