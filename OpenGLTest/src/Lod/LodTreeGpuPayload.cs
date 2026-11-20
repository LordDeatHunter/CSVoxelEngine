using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VoxelEngine.Lod;

/// <summary>Pure std430 payload shared by the live GPU uploader and headless ABI tests.</summary>
internal readonly record struct LodTreeGpuPayload(byte[] Bytes, int MaskOffset, int MaskBytes)
{
    internal static LodTreeGpuPayload Pack(LodTreeBuild trees, int originX, int originZ)
    {
        var tint = trees.Tint ?? throw new InvalidOperationException("Tree instances need a tint lattice.");
        var instanceBytes = checked(trees.Count * LodTreeInstance.ByteSize);
        var materialBytes = checked(trees.Materials.Length * LodTreeMaterial.ByteSize);
        var tintBytes = tint.ByteSize;
        var maskBytes = checked(trees.GeneratedTiles.Length * sizeof(ulong));
        var total = checked(64 + instanceBytes + materialBytes + tintBytes + maskBytes);
        if (total > 12 * 1024 * 1024)
            throw new InvalidOperationException("One logical tree region exceeds the 12 MiB ceiling.");
        var bytes = new byte[total];
        var instanceOffset = 64;
        var materialOffset = checked(instanceOffset + instanceBytes);
        var tintOffset = checked(materialOffset + materialBytes);
        var maskOffset = checked(tintOffset + tintBytes);
        WriteWord(bytes, 0, instanceOffset / 4);
        WriteWord(bytes, 1, materialOffset / 4);
        WriteWord(bytes, 2, tintOffset / 4);
        WriteWord(bytes, 3, maskOffset / 4);
        WriteWord(bytes, 4, tint.LatticeSide);
        WriteWord(bytes, 5, trees.Materials.Length);
        WriteWord(bytes, 6, trees.Count);
        WriteWord(bytes, 7, trees.ProxyYMin.Length == 0 ? 0 : (int)MathF.Sqrt(trees.ProxyYMin.Length));
        for (var i = 0; i < trees.Count; i++)
            trees.Instances[i].WriteRegionLittleEndian(originX, originZ,
                bytes.AsSpan(instanceOffset + i * LodTreeInstance.ByteSize, LodTreeInstance.ByteSize));
        for (var i = 0; i < trees.Materials.Length; i++)
            trees.Materials[i].WriteLittleEndian(
                bytes.AsSpan(materialOffset + i * LodTreeMaterial.ByteSize, LodTreeMaterial.ByteSize));
        MemoryMarshal.AsBytes(tint.Grass).CopyTo(bytes.AsSpan(tintOffset));
        MemoryMarshal.AsBytes(tint.Foliage).CopyTo(bytes.AsSpan(tintOffset + tintBytes / 2));
        for (var i = 0; i < trees.GeneratedTiles.Length; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(maskOffset + i * sizeof(ulong)),
                trees.GeneratedTiles[i]);
        return new LodTreeGpuPayload(bytes, maskOffset, maskBytes);
    }

    private static void WriteWord(Span<byte> bytes, int word, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(word * 4, 4), value);
}
