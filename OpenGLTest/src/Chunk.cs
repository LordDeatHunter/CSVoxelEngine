using System.Collections.Generic;
using System.Linq;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

public class Chunk
{
    public const int Size = 16;
    private readonly byte[,,] _blocks = new byte[Size, Size, Size];
    private static readonly FastNoiseLite Noise;

    public Vector3 Position { get; }
    private Mesh? _mesh;
    private readonly GL _gl;

    static Chunk()
    {
        Noise = new FastNoiseLite();
        Noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        Noise.SetFrequency(0.01f);
        Noise.SetSeed(1337);
        Noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        Noise.SetFractalOctaves(6);
    }

    public Chunk(GL gl, Vector3 position)
    {
        _gl = gl;
        Position = position;

        for (var x = 0; x < Size; x++)
        for (var z = 0; z < Size; z++)
        {
            var y = (int)(Noise.GetNoise(x + position.X * Size, z + position.Z * Size) * 16F);

            if (y < LowerBoundY) continue;

            if (y >= UpperBoundY)
            {
                for (var yi = 0; yi < Size; yi++)
                {
                    SetBlock(x, yi, z, 1);
                }

                continue;
            }

            if (y < 0) y = Size + y;

            for (var yi = 0; yi < y; yi++)
            {
                SetBlock(x, yi, z, 1);
            }
        }
    }
    
    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    public void SetBlock(int x, int y, int z, byte id)
    {
        if (InBounds(x, y, z)) _blocks[x, y, z] = id;
    }

    public byte GetBlock(int x, int y, int z) => InBounds(x, y, z) ? _blocks[x, y, z] : (byte)0;

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;

    public void GenerateMesh()
    {
        List<Vertex> vertices = [];
        List<ushort> indices = [];
        ushort indexOffset = 0;

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            if (_blocks[x, y, z] == 0) continue;

            foreach (var (normal, face, tile) in CubeFaces)
            {
                var nx = x + (int)normal.X;
                var ny = y + (int)normal.Y;
                var nz = z + (int)normal.Z;

                if (GetBlock(nx, ny, nz) != 0) continue;

                var basePos = new Vector3(x, y, z);
                var uv = FaceUV(tile.X, tile.Y);

                foreach (var (pos, uvCoord) in face.Zip(uv))
                    vertices.Add(new Vertex(basePos + pos, 255, 255, 255, 255, uvCoord));

                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 1));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 3));

                indexOffset += 4;
            }
        }

        _mesh?.Dispose();
        _mesh = new Mesh(_gl, CollectionsMarshal.AsSpan(vertices), CollectionsMarshal.AsSpan(indices));
    }

    public void Render()
    {
        _mesh?.Render();
    }

    private static Vector2[] FaceUV(int tileX, int tileY, int tileSize = 4)
    {
        var u = tileX / (float)tileSize;
        var v = tileY / (float)tileSize;
        var du = 1f / tileSize;
        var dv = 1f / tileSize;
        return
        [
            new Vector2(u, v + dv),
            new Vector2(u, v),
            new Vector2(u + du, v),
            new Vector2(u + du, v + dv)
        ];
    }

    private static readonly (Vector3 normal, Vector3[] face, Point tile)[] CubeFaces =
    [
        // Back
        (new Vector3(0, 0, -1),
        [
            new Vector3(0, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(1, 1, 0),
            new Vector3(1, 0, 0)
        ], new Point(1, 0)),

        // Front
        (new Vector3(0, 0, 1),
        [
            new Vector3(1, 0, 1),
            new Vector3(1, 1, 1),
            new Vector3(0, 1, 1),
            new Vector3(0, 0, 1)
        ], new Point(1, 0)),

        // Left
        (new Vector3(-1, 0, 0),
        [
            new Vector3(0, 0, 1),
            new Vector3(0, 1, 1),
            new Vector3(0, 1, 0),
            new Vector3(0, 0, 0)
        ], new Point(1, 0)),

        // Right
        (new Vector3(1, 0, 0),
        [
            new Vector3(1, 0, 0),
            new Vector3(1, 1, 0),
            new Vector3(1, 1, 1),
            new Vector3(1, 0, 1)
        ], new Point(1, 0)),

        // Top
        (new Vector3(0, 1, 0),
        [
            new Vector3(0, 1, 0),
            new Vector3(0, 1, 1),
            new Vector3(1, 1, 1),
            new Vector3(1, 1, 0)
        ], new Point(0, 0)),

        // Bottom
        (new Vector3(0, -1, 0),
        [
            new Vector3(0, 0, 1),
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(1, 0, 1)
        ], new Point(2, 0))
    ];

    private record struct Point(int X, int Y);
}
