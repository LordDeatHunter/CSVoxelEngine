using System.Runtime.InteropServices;

namespace VoxelEngine.Rendering.Geometry;

/// <summary>
/// One entry of a <c>glMultiDrawElementsIndirect</c> command buffer, laid out exactly as the GL
/// 4.6 spec (§10.4, "DrawElementsIndirectCommand") requires: five 32-bit words in this order and
/// nothing else, since the driver reads the buffer memory directly.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct DrawElementsIndirectCommand
{
    /// <summary>
    /// Indices to draw: a chunk's <see cref="GeometrySlice.IndexCount"/>, or the length of one
    /// cloud tile run.
    /// </summary>
    public uint Count;

    /// <summary>
    /// Instances to draw: 1 per chunk (zeroed in place by the Hi-Z occlusion cull to skip it), and
    /// 1 per cloud tile run.
    /// </summary>
    public uint InstanceCount;

    /// <summary>
    /// First index into the bound element buffer: a chunk's <see cref="GeometrySlice.IndexOffset"/>,
    /// or where a cloud tile run starts in the field mesh.
    /// </summary>
    public uint FirstIndex;

    /// <summary>
    /// Added to every index: a chunk's <see cref="GeometrySlice.VertexOffset"/>, which is what lets
    /// a slice keep its indices slice-relative <c>ushort</c>s however far into the pool it sits;
    /// 0 for the clouds, whose indices are absolute.
    /// </summary>
    public int BaseVertex;

    /// <summary>
    /// Added to the instance index when fetching attributes with a non-zero divisor: 0 for chunks
    /// (nothing there is instanced), and the copy's slot in the instance-offset stream for a cloud
    /// tile run.
    /// </summary>
    public uint BaseInstance;
}
