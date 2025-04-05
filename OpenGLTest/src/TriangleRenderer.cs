using Silk.NET.OpenGL;
using System;
using System.Numerics;

namespace silkgltriangle;

public class TriangleRenderer : IDisposable
{
    private readonly GL _gl;
    private uint _vao;
    private uint _vbo;
    private Shader _shader;

    private bool _disposed;

    public TriangleRenderer(GL gl)
    {
        _gl = gl;

        Span<Vertex> vertexData = 
        [
            new(new Vector3(-0.5f, -0.5f, 0), 255, 0, 0, 255),
            new(new Vector3(0, 0.5f, 0), 0, 255, 0, 255),
            new(new Vector3(0.5f, -0.5f, 0), 0, 0, 255, 255)
        ];

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData<Vertex>(BufferTargetARB.ArrayBuffer, vertexData, BufferUsageARB.StaticDraw);

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        unsafe
        {
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vertex.Size, (void*)0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.UnsignedByte, true, Vertex.Size, (void*)12);
            _gl.EnableVertexAttribArray(1);
        }

        var vsSource = FileUtils.ReadFile("assets/shaders/test-vertex.glsl");
        var fsSource = FileUtils.ReadFile("assets/shaders/test-fragment.glsl");
        _shader = new Shader(_gl, vsSource, fsSource);
    }

    public void Render()
    {
        _gl.BindVertexArray(_vao);
        _shader.Use();
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this); // Recommended
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _shader.Dispose();
        }

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);

        _disposed = true;
    }
}