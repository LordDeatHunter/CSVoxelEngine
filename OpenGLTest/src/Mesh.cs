using Silk.NET.OpenGL;
using System;

namespace silkgltriangle;

/// <summary>
/// An indexed triangle mesh in the shared <see cref="Vertex"/> layout. See
/// <see cref="Primitives"/> for building simple shapes.
/// </summary>
public class Mesh : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;
    private readonly uint _indicesCount;

    public Mesh(GL gl, Span<Vertex> vertices, Span<ushort> indices)
    {
        _gl = gl;
        _indicesCount = (uint)indices.Length;
        var vertexDataSize = (nuint)(vertices.Length * Vertex.Size);
        var indexDataSize = (nuint)(_indicesCount * sizeof(ushort));

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, vertexDataSize, vertices, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, indexDataSize, indices, BufferUsageARB.StaticDraw);

        ConfigureVertexAttribs(_gl);

        _gl.BindVertexArray(0);
    }

    /// <summary>
    /// Points the currently bound vertex array at the currently bound array buffer using the
    /// shared <see cref="Vertex"/> layout. Both the VAO and its VBO must already be bound.
    /// <c>ChunkGeometryPool</c> configures its pooled VAO through this same helper, so
    /// the pool layout can never drift from the one meshes use.
    /// </summary>
    internal static unsafe void ConfigureVertexAttribs(GL gl)
    {
        // Offsets follow the Vertex field order: position 0, colour 12, uv 16, sun 24,
        // block 25, normal bits + tint 26-27 (one ushort), layer 28,
        // water surface 30 (see Vertex for the semantics)
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)0);
        gl.EnableVertexAttribArray(0);

        gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)12);
        gl.EnableVertexAttribArray(1);

        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Vertex.Size, (void*)16);
        gl.EnableVertexAttribArray(2);

        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)24);
        gl.EnableVertexAttribArray(3);

        gl.VertexAttribPointer(4, 1, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)25);
        gl.EnableVertexAttribArray(4);

        // Vertex.Normal and Vertex.LightTint read together as one integer ushort: face 0-2,
        // submerged 3, biome tint 4, sway 5, block-light tint code 6-15
        gl.VertexAttribIPointer(5, 1, VertexAttribIType.UnsignedShort, Vertex.Size, (void*)26);
        gl.EnableVertexAttribArray(5);

        // The array-texture layer is an integer attribute: no float conversion, read as uint
        gl.VertexAttribIPointer(7, 1, VertexAttribIType.UnsignedShort, Vertex.Size, (void*)28);
        gl.EnableVertexAttribArray(7);

        // The baked water surface of a submerged face (Vertex.WaterSurface), likewise an
        // integer attribute; programs that do not declare location 8 simply ignore it
        gl.VertexAttribIPointer(8, 1, VertexAttribIType.UnsignedShort, Vertex.Size, (void*)30);
        gl.EnableVertexAttribArray(8);
    }

    public void Render()
    {
        // The VAO already captures the element buffer binding; no need to rebind the EBO
        _gl.BindVertexArray(_vao);
        unsafe { _gl.DrawElements(PrimitiveType.Triangles, _indicesCount, DrawElementsType.UnsignedShort, null); }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);

        GC.SuppressFinalize(this);
    }
}
