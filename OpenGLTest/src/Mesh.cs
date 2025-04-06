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

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, Vertex.Size, (void*)16);
            _gl.EnableVertexAttribArray(2);
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
        static Vector2[] FaceUV(int tileX, int tileY, int tileSize = 4)
        {
            var u = tileX / (float)tileSize;
            var v = tileY / (float)tileSize;
            var du = 1f / tileSize;
            var dv = 1f / tileSize;
            return
            [
                new Vector2(u,     v + dv),
                new Vector2(u,     v),
                new Vector2(u + du, v),
                new Vector2(u + du, v + dv)
            ];
        }

        var uvBack  = FaceUV(1, 0);
        var uvRight = FaceUV(1, 0);
        var uvFront = FaceUV(1, 0);
        var uvLeft  = FaceUV(1, 0);
        var uvTop   = FaceUV(0, 0);
        var uvBottom= FaceUV(2, 0);

        Span<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvBack[0]),
            new(new Vector3(-0.5f,  0.5f, -0.5f), 255, 255, 255, 255, uvBack[1]),
            new(new Vector3( 0.5f,  0.5f, -0.5f), 255, 255, 255, 255, uvBack[2]),
            new(new Vector3( 0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvBack[3]),

            new(new Vector3(0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvRight[0]),
            new(new Vector3(0.5f,  0.5f, -0.5f), 255, 255, 255, 255, uvRight[1]),
            new(new Vector3(0.5f,  0.5f,  0.5f), 255, 255, 255, 255, uvRight[2]),
            new(new Vector3(0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvRight[3]),

            new(new Vector3( 0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvFront[0]),
            new(new Vector3( 0.5f,  0.5f,  0.5f), 255, 255, 255, 255, uvFront[1]),
            new(new Vector3(-0.5f,  0.5f,  0.5f), 255, 255, 255, 255, uvFront[2]),
            new(new Vector3(-0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvFront[3]),

            new(new Vector3(-0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvLeft[0]),
            new(new Vector3(-0.5f,  0.5f,  0.5f), 255, 255, 255, 255, uvLeft[1]),
            new(new Vector3(-0.5f,  0.5f, -0.5f), 255, 255, 255, 255, uvLeft[2]),
            new(new Vector3(-0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvLeft[3]),

            new(new Vector3(-0.5f, 0.5f, -0.5f), 255, 255, 255, 255, uvTop[0]),
            new(new Vector3(-0.5f, 0.5f,  0.5f), 255, 255, 255, 255, uvTop[1]),
            new(new Vector3( 0.5f, 0.5f,  0.5f), 255, 255, 255, 255, uvTop[2]),
            new(new Vector3( 0.5f, 0.5f, -0.5f), 255, 255, 255, 255, uvTop[3]),

            new(new Vector3(-0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvBottom[0]),
            new(new Vector3(-0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvBottom[1]),
            new(new Vector3( 0.5f, -0.5f, -0.5f), 255, 255, 255, 255, uvBottom[2]),
            new(new Vector3( 0.5f, -0.5f,  0.5f), 255, 255, 255, 255, uvBottom[3])
        ];

        Span<ushort> indices = new ushort[36];
        for (ushort i = 0; i < 6; i++)
        {
            var offset = (ushort)(i * 4);
            indices[i * 6 + 0] = (ushort)(offset + 0);
            indices[i * 6 + 1] = (ushort)(offset + 1);
            indices[i * 6 + 2] = (ushort)(offset + 2);
            indices[i * 6 + 3] = (ushort)(offset + 0);
            indices[i * 6 + 4] = (ushort)(offset + 2);
            indices[i * 6 + 5] = (ushort)(offset + 3);
        }

        return new Mesh(gl, vertices, indices);
    }

    public static Mesh CreateQuad(GL gl)
    {
        Span<Vertex> vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0), 255, 255, 255, 255, new Vector2(0, 1)),
            new(new Vector3(-0.5f,  0.5f, 0), 255, 255, 255, 255, new Vector2(0, 0)),
            new(new Vector3( 0.5f,  0.5f, 0), 255, 255, 255, 255, new Vector2(1, 0)),
            new(new Vector3( 0.5f, -0.5f, 0), 255, 255, 255, 255, new Vector2(1, 1))
        ];

        Span<ushort> indices = [0, 1, 2, 0, 2, 3];
        return new Mesh(gl, vertices, indices);
    }

    public static Mesh CreateUIQuad(GL gl, float pixelSize = 16)
    {
        var half = pixelSize / 2f;
        var positions = new[]
        {
            new Vector3(-half, -half, 0),
            new Vector3(-half,  half, 0),
            new Vector3( half,  half, 0),
            new Vector3( half, -half, 0)
        };

        var uvs = new[]
        {
            new Vector2(0, 1),
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1)
        };

        var vertices = new Vertex[4];
        for (var i = 0; i < 4; i++)
        {
            vertices[i] = new Vertex(positions[i], 255, 255, 255, 255, uvs[i]);
        }

        var indices = new ushort[] { 0, 1, 2, 0, 2, 3 };
        return new Mesh(gl, vertices, indices);
    }
}
