using System;
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
        var payload = LodTreeGpuPayload.Pack(trees, originX, originZ);
        var units = checked((payload.Bytes.Length + UnitBytes - 1) / UnitBytes);
        if (!_allocator.TryAllocate(units, out var unit))
        {
            Grow(Math.Max(checked(_allocator.Capacity * 2), checked(_allocator.Capacity + units)));
            if (!_allocator.TryAllocate(units, out unit))
                throw new InvalidOperationException("Tree payload pool failed after growth.");
        }
        try { _gl.NamedBufferSubData(_buffer, (nint)((long)unit * UnitBytes), payload.Bytes.AsSpan()); }
        catch { _allocator.Free(unit, units); throw; }
        return new Slice(unit, units, trees.Count, payload.MaskOffset, payload.MaskBytes);
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
