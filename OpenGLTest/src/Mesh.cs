using Silk.NET.OpenGL;
using System;
using System.Numerics;

namespace silkgltriangle;

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
        }

        _gl.BindVertexArray(0);
    }

    public void Render()
    {
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        unsafe { _gl.DrawElements(PrimitiveType.Triangles, _indicesCount, DrawElementsType.UnsignedShort, null); }
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);

        GC.SuppressFinalize(this);
    }

    public static Mesh CreateCube(GL gl)
    {
        Span<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, -0.5f), 255, 0, 0, 255),
            new(new Vector3( 0.5f, -0.5f, -0.5f), 0, 255, 0, 255),
            new(new Vector3( 0.5f,  0.5f, -0.5f), 0, 0, 255, 255),
            new(new Vector3(-0.5f,  0.5f, -0.5f), 255, 255, 0, 255),
            new(new Vector3(-0.5f, -0.5f,  0.5f), 0, 255, 255, 255),
            new(new Vector3( 0.5f, -0.5f,  0.5f), 255, 0, 255, 255),
            new(new Vector3( 0.5f,  0.5f,  0.5f), 255, 255, 255, 255),
            new(new Vector3(-0.5f,  0.5f,  0.5f), 128, 128, 128, 255)
        ];

        Span<ushort> indices =
        [
            0, 1, 2, 2, 3, 0,
            1, 5, 6, 6, 2, 1,
            5, 4, 7, 7, 6, 5,
            4, 0, 3, 3, 7, 4,
            3, 2, 6, 6, 7, 3,
            4, 5, 1, 1, 0, 4
        ];

        return new Mesh(gl, vertices, indices);
    }

    public static Mesh CreateQuad(GL gl)
    {
        Span<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0), 255, 0, 0, 255),
            new(new Vector3(-0.5f,  0.5f, 0), 0, 255, 0, 255),
            new(new Vector3( 0.5f,  0.5f, 0), 0, 0, 255, 255),
            new(new Vector3( 0.5f, -0.5f, 0), 255, 255, 0, 255)
        ];

        Span<ushort> indices =
        [
            0, 1, 2,
            0, 2, 3
        ];

        return new Mesh(gl, vertices, indices);
    }
}
