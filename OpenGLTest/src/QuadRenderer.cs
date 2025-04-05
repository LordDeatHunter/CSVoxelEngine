using Silk.NET.OpenGL;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

public class QuadRenderer : IDisposable
{
    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;
    private readonly Shader _shader;
    private readonly int _mvpLocation;
    private bool _disposed;
    private readonly uint _indicesCount;

    public QuadRenderer(GL gl)
    {
        _gl = gl;

        Span<Vertex> vertexData =
        [
            new(new Vector3(-0.5f, -0.5f, -0.5f), 255, 0, 0, 255),    // 0
            new(new Vector3( 0.5f, -0.5f, -0.5f), 0, 255, 0, 255),    // 1
            new(new Vector3( 0.5f,  0.5f, -0.5f), 0, 0, 255, 255),    // 2
            new(new Vector3(-0.5f,  0.5f, -0.5f), 255, 255, 0, 255),  // 3
            new(new Vector3(-0.5f, -0.5f,  0.5f), 0, 255, 255, 255),  // 4
            new(new Vector3( 0.5f, -0.5f,  0.5f), 255, 0, 255, 255),  // 5
            new(new Vector3( 0.5f,  0.5f,  0.5f), 255, 255, 255, 255),// 6
            new(new Vector3(-0.5f,  0.5f,  0.5f), 128, 128, 128, 255) // 7
        ];
        var vertexDataSize = (nuint)(vertexData.Length * Vertex.Size);

        Span<ushort> indices =
        [
            // Front face
            0, 1, 2, 2, 3, 0,
            // Right face
            1, 5, 6, 6, 2, 1,
            // Back face
            5, 4, 7, 7, 6, 5,
            // Left face
            4, 0, 3, 3, 7, 4,
            // Top face
            3, 2, 6, 6, 7, 3,
            // Bottom face
            4, 5, 1, 1, 0, 4
        ];
        _indicesCount = (uint)indices.Length;
        var indicesSize = (nuint)(_indicesCount * sizeof(ushort));

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, vertexDataSize, vertexData, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BufferData<ushort>(BufferTargetARB.ElementArrayBuffer, indicesSize, indices, BufferUsageARB.StaticDraw);

        var vs = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fs = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        _shader = new Shader(_gl, vs, fs);
        _shader.Use();

        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)12);
            _gl.EnableVertexAttribArray(1);
        }

        _mvpLocation = _gl.GetUniformLocation(_shader.Handle, "uMVP");

        _gl.BindVertexArray(0);
    }

    public void Render(Matrix4x4 mvp)
    {
        _shader.Use();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);

        unsafe
        {
            fixed (float* ptr = &MemoryMarshal.GetReference(MemoryMarshal.CreateSpan(ref mvp.M11, 16)))
            {
                _gl.UniformMatrix4(_mvpLocation, 1, false, ptr);
            }
            _gl.DrawElements(PrimitiveType.Triangles, _indicesCount, DrawElementsType.UnsignedShort, null);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing) _shader.Dispose();

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);

        _disposed = true;
    }
}
