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

        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)12);
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Vertex.Size, (void*)16);
            _gl.EnableVertexAttribArray(2);

            _gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, Vertex.Size, (void*)24);
            _gl.EnableVertexAttribArray(3);

            _gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, Vertex.Size, (void*)28);
            _gl.EnableVertexAttribArray(4);

            _gl.VertexAttribPointer(5, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)32);
            _gl.EnableVertexAttribArray(5);

            _gl.VertexAttribPointer(6, 1, VertexAttribPointerType.Float, false, Vertex.Size, (void*)44);
            _gl.EnableVertexAttribArray(6);
        }

        _gl.BindVertexArray(0);
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
