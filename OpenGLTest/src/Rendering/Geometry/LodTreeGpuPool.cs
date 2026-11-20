using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using VoxelEngine.Chunks;
using VoxelEngine.Chunks.Meshing;
using VoxelEngine.Data;
using VoxelEngine.Lod;

namespace VoxelEngine.Rendering.Geometry;

/// <summary>One coalescing SSBO allocation for all resident compact LOD tree regions.</summary>
public sealed unsafe class LodTreeGpuPool : IDisposable
{
    internal const uint PayloadBinding = 3;
    private const int UnitBytes = 16;
    private const int HeaderBytes = 64;
    private readonly GL _gl;
    private readonly GeometryAllocator _allocator = new(4096);
    private readonly ChunkGeometryPool _template;
    private readonly GeometrySlice _templateSlice;
    private uint _buffer;
    private int _drawSetChanges;

    internal readonly record struct Slice(int Unit, int Units, int Count, int MaskOffset, int MaskBytes)
    {
        internal uint BaseInstance => (uint)Unit;
    }

    internal uint Vao => _template.Vao;
    internal GeometrySlice Template => _templateSlice;
    internal int ContentVersion => unchecked(_allocator.ContentVersion + _drawSetChanges);
    internal void NoteDrawSetChange() => _drawSetChanges = unchecked(_drawSetChanges + 1);
    internal int UsedBytes => _allocator.UsedUnits * UnitBytes;
    internal int CapacityBytes => _allocator.Capacity * UnitBytes;

    internal LodTreeGpuPool(GL gl)
    {
        _gl = gl;
        _template = new ChunkGeometryPool(gl, 4, "lod-tree-template");
        var (shape, indices) = LodTreeTemplate.Build();
        var vertices = new Vertex[shape.Length];
        for (var i = 0; i < shape.Length; i++)
        {
            var source = shape[i];
            var normal = Chunk.CubeFaces[source.Face].normal;
            var code = (ushort)((source.Part * LodTreeTemplate.Faces + source.Face) *
                LodTreeTemplate.Layers + source.Layer);
            vertices[i] = new Vertex(source.UnitPosition, 255, 255, 255, 255,
                ChunkFaceTables.CropUv(source.Face, source.UnitPosition),
                code, 1f, 0f, normal);
        }
        _templateSlice = _template.Upload(vertices, indices);
        _buffer = NewBuffer(_allocator.Capacity);
    }

    internal Slice Upload(LodTreeBuild trees, int originX, int originZ)
    {
        if (trees.Count == 0) return default;
        var tint = trees.Tint ?? throw new InvalidOperationException("Tree instances need a tint lattice.");
        var instanceBytes = checked(trees.Count * LodTreeInstance.ByteSize);
        var materialBytes = checked(trees.Materials.Length * LodTreeMaterial.ByteSize);
        var tintBytes = tint.ByteSize;
        var maskBytes = checked(trees.GeneratedTiles.Length * sizeof(ulong));
        var total = checked(HeaderBytes + instanceBytes + materialBytes + tintBytes + maskBytes);
        if (total > 12 * 1024 * 1024)
            throw new InvalidOperationException("One logical tree region exceeds the 12 MiB ceiling.");
        var bytes = new byte[total];
        var instanceOffset = HeaderBytes;
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
        var units = checked((total + UnitBytes - 1) / UnitBytes);
        if (!_allocator.TryAllocate(units, out var unit))
        {
            Grow(Math.Max(checked(_allocator.Capacity * 2), checked(_allocator.Capacity + units)));
            if (!_allocator.TryAllocate(units, out unit))
                throw new InvalidOperationException("Tree payload pool failed after growth.");
        }
        try { _gl.NamedBufferSubData(_buffer, (nint)((long)unit * UnitBytes), bytes.AsSpan()); }
        catch { _allocator.Free(unit, units); throw; }
        return new Slice(unit, units, trees.Count, maskOffset, maskBytes);
    }

    internal void SetCoverage(in Slice slice, ReadOnlySpan<ulong> mask)
    {
        if (slice.Count == 0) return;
        if (mask.Length * sizeof(ulong) != slice.MaskBytes)
            throw new ArgumentException("Coverage must match the source-tile mask.", nameof(mask));
        _gl.NamedBufferSubData(_buffer,
            (nint)((long)slice.Unit * UnitBytes + slice.MaskOffset),
            MemoryMarshal.AsBytes(mask));
    }

    internal void Bind() =>
        _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PayloadBinding, _buffer);

    internal void Free(in Slice slice)
    {
        if (slice.Units > 0) _allocator.Free(slice.Unit, slice.Units);
    }

    private static void WriteWord(Span<byte> bytes, int word, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(word * 4, 4), value);

    private uint NewBuffer(int units)
    {
        var buffer = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, buffer);
        _gl.BufferStorage(BufferStorageTarget.CopyWriteBuffer, (nuint)((long)units * UnitBytes),
            null, BufferStorageMask.DynamicStorageBit);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
        return buffer;
    }

    private void Grow(int units)
    {
        var next = NewBuffer(units);
        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, _buffer);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, next);
        _gl.CopyBufferSubData(CopyBufferSubDataTarget.CopyReadBuffer,
            CopyBufferSubDataTarget.CopyWriteBuffer, 0, 0,
            (nuint)((long)_allocator.Capacity * UnitBytes));
        _gl.BindBuffer(BufferTargetARB.CopyReadBuffer, 0);
        _gl.BindBuffer(BufferTargetARB.CopyWriteBuffer, 0);
        _gl.DeleteBuffer(_buffer);
        _buffer = next;
        _allocator.Grow(units);
    }

    public void Dispose()
    {
        _template.Dispose();
        _gl.DeleteBuffer(_buffer);
        GC.SuppressFinalize(this);
    }
}
