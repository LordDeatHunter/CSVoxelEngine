using System;
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
    private static readonly Vector3[] NeighborOffsets =
    [
        new Vector3(1, 0, 0),
        new Vector3(-1, 0, 0),
        new Vector3(0, 1, 0),
        new Vector3(0, -1, 0),
        new Vector3(0, 0, 1),
        new Vector3(0, 0, -1)
    ];

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
                    SetBlock(x, yi, z, 2);
                }

                continue;
            }

            if (y < 0) y = (int)(Size - MathF.Abs(y) % Size);

            for (var yi = 0; yi <= y; yi++)
            {
                SetBlock(x, yi, z, (byte)(yi == y ? 1 : 2));
            }
        }
    }

    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    public void SetBlock(int x, int y, int z, byte id)
    {
        if (InBounds(x, y, z)) _blocks[x, y, z] = id;
    }

    public byte GetBlock(int x, int y, int z)
    {
        if (InBounds(x, y, z))
            return _blocks[x, y, z];

        var globalX = (int)(Position.X * Size) + x;
        var globalY = (int)(Position.Y * Size) + y;
        var globalZ = (int)(Position.Z * Size) + z;

        var chunkX = MathF.Floor(globalX / (float)Size);
        var chunkY = MathF.Floor(globalY / (float)Size);
        var chunkZ = MathF.Floor(globalZ / (float)Size);

        var localX = ((globalX % Size) + Size) % Size;
        var localY = ((globalY % Size) + Size) % Size;
        var localZ = ((globalZ % Size) + Size) % Size;

        var neighborPos = new Vector3(chunkX, chunkY, chunkZ);

        return Program.Chunks.TryGetValue(neighborPos, out var neighbor)
            ? neighbor.GetBlock(localX, localY, localZ)
            : (byte)0;
    }

    public void RemoveBlockAt(Vector3 worldPos)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return;
        _blocks[localX, localY, localZ] = 0;

        HashSet<Vector3> chunksForUpdate = [Position];
        foreach (var neighborOffset in NeighborOffsets)
        {
            // check the chunk of the neighboring blocks of the removed block
            var neighborPos = worldPos + neighborOffset;
            var chunkPos = new Vector3(
                MathF.Floor(neighborPos.X / Size),
                MathF.Floor(neighborPos.Y / Size),
                MathF.Floor(neighborPos.Z / Size)
            );
            if (Program.Chunks.TryGetValue(chunkPos, out var neighbor))
            {
                chunksForUpdate.Add(neighbor.Position);
            }
        }
        
        foreach (var chunkPos in chunksForUpdate)
        {
            if (Program.Chunks.TryGetValue(chunkPos, out var chunk))
            {
                chunk.GenerateMesh();
            }
        }
    }

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

            var blockId = _blocks[x, y, z];
            var type = BlockType.Types[blockId];
            
            foreach (var (normal, face) in CubeFaces)
            {
                var nx = x + (int)normal.X;
                var ny = y + (int)normal.Y;
                var nz = z + (int)normal.Z;

                if (GetBlock(nx, ny, nz) != 0) continue;

                var tile = type.GetTile(normal);
                var basePos = new Vector3(x, y, z);
                var uv = FaceUV(tile.X, tile.Y);

                var brightness = normal switch
                {
                    _ when normal == Vector3.UnitY => 1.0f,
                    _ when normal == Vector3.UnitZ => 0.9f,
                    _ when normal == Vector3.UnitX => 0.85f,
                    _ when normal == -Vector3.UnitZ => 0.8f,
                    _ when normal == -Vector3.UnitX => 0.75f,
                    _ when normal == -Vector3.UnitY => 0.6f,
                    _ => 1.0f
                };

                var shade = (byte)(brightness * 255);

                foreach (var (pos, uvCoord) in face.Zip(uv))
                    vertices.Add(new Vertex(basePos + pos, shade, shade, shade, 255, uvCoord));

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

    private static readonly (Vector3 normal, Vector3[] face)[] CubeFaces =
    [
        // Back
        (new Vector3(0, 0, -1),
        [
            new Vector3(0, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(1, 1, 0),
            new Vector3(1, 0, 0)
        ]),

        // Front
        (new Vector3(0, 0, 1),
        [
            new Vector3(1, 0, 1),
            new Vector3(1, 1, 1),
            new Vector3(0, 1, 1),
            new Vector3(0, 0, 1)
        ]),

        // Left
        (new Vector3(-1, 0, 0),
        [
            new Vector3(0, 0, 1),
            new Vector3(0, 1, 1),
            new Vector3(0, 1, 0),
            new Vector3(0, 0, 0)
        ]),

        // Right
        (new Vector3(1, 0, 0),
        [
            new Vector3(1, 0, 0),
            new Vector3(1, 1, 0),
            new Vector3(1, 1, 1),
            new Vector3(1, 0, 1)
        ]),

        // Top
        (new Vector3(0, 1, 0),
        [
            new Vector3(0, 1, 0),
            new Vector3(0, 1, 1),
            new Vector3(1, 1, 1),
            new Vector3(1, 1, 0)
        ]),

        // Bottom
        (new Vector3(0, -1, 0),
        [
            new Vector3(0, 0, 1),
            new Vector3(0, 0, 0),
            new Vector3(1, 0, 0),
            new Vector3(1, 0, 1)
        ])
    ];

    private record struct Point(int X, int Y);
}
