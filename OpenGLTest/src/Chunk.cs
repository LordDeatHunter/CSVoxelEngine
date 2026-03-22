using System;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Represents a 16x16x16 chunk of blocks in the world.
/// </summary>
public class Chunk : IDisposable
{
    public const int Size = 16;
    private readonly byte[,,] _blocks = new byte[Size, Size, Size];
    private static readonly FastNoiseLite Noise;
    private static readonly Vector3[] NeighborOffsets = Vector3Directions.CardinalDirections;
    public double lifetime = 0.0f;
    public const float MAX_LIFETIME = 0.5f;

    public Vector3 Position { get; }
    private Mesh? _mesh;
    private readonly GL _gl;
    
    /// <summary>
    /// Whether block data has been fully generated and is ready for meshing.
    /// </summary>
    public volatile bool IsBlockDataReady;

    static Chunk()
    {
        Noise = new FastNoiseLite();
        Noise.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
        Noise.SetFrequency(0.001f);
        Noise.SetSeed(1337);
        Noise.SetFractalType(FastNoiseLite.FractalType.FBm);
        Noise.SetFractalOctaves(6);
    }

    public Chunk(GL gl, Vector3 position, bool generateBlockData = true)
    {
        _gl = gl;
        Position = position;

        if (generateBlockData)
        {
            GenerateBlockData();
        }
    }

    public void GenerateBlockData()
    {
        for (int x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                GenerateColumn(x, z);
            }
        }
        IsBlockDataReady = true;
    }

    private void GenerateColumn(int x, int z)
    {
        var worldX = x + Position.X * Size;
        var worldZ = z + Position.Z * Size;
        
        var heightNoise = Noise.GetNoise(worldX, worldZ) * Noise.GetNoise(worldZ, worldX);
        var terrainHeight = (int)(heightNoise * 256f);

        if (terrainHeight < LowerBoundY) return;

        if (terrainHeight >= UpperBoundY)
        {
            for (var y = 0; y < Size; y++)
                SetBlock(x, y, z, 2);
            return;
        }

        if (terrainHeight < 0) 
            terrainHeight = Size - (int)MathF.Abs(terrainHeight) % Size;
        
        var localHeight = terrainHeight % Size;

        for (var y = 0; y <= localHeight; y++)
        {
            var blockType = (byte)(y == localHeight ? 1 : 2);
            SetBlock(x, y, z, blockType);
        }
    }

    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    public void SetBlock(int x, int y, int z, byte id)
    {
        if (InBounds(x, y, z)) _blocks[x, y, z] = id;
    }

    public byte GetBlock(Vector3 pos, ChunkManager chunkManager) => GetBlock((int)pos.X, (int)pos.Y, (int)pos.Z, chunkManager);

    public byte GetBlock(int x, int y, int z, ChunkManager chunkManager)
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

        return chunkManager.Chunks.TryGetValue(neighborPos, out var neighbor)
            ? neighbor.GetBlock(localX, localY, localZ, chunkManager)
            : (byte)0;
    }

    public void RemoveBlockAt(Vector3 worldPos, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return;
        _blocks[localX, localY, localZ] = 0;

        chunkManager.ImmediateRemesh(Position);

        HashSet<Vector3> chunksForUpdate = [];
        foreach (var neighborOffset in NeighborOffsets)
        {
            var neighborPos = worldPos + neighborOffset;
            var chunkPos = new Vector3(
                MathF.Floor(neighborPos.X / Size),
                MathF.Floor(neighborPos.Y / Size),
                MathF.Floor(neighborPos.Z / Size)
            );

            if (chunkManager.Chunks.TryGetValue(chunkPos, out var neighbor))
            {
                chunksForUpdate.Add(neighbor.Position);
            }
        }

        foreach (var chunkPos in chunksForUpdate)
        {
            chunkManager.MarkDirty(chunkPos);
        }
    }

    public void PlaceBlockAt(Vector3 worldPos, byte blockId, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return;
        if (_blocks[localX, localY, localZ] != 0) return;

        _blocks[localX, localY, localZ] = blockId;

        chunkManager.ImmediateRemesh(Position);

        HashSet<Vector3> chunksForUpdate = [];
        foreach (var neighborOffset in NeighborOffsets)
        {
            var neighborPos = worldPos + neighborOffset;
            var chunkPos = new Vector3(
                MathF.Floor(neighborPos.X / Size),
                MathF.Floor(neighborPos.Y / Size),
                MathF.Floor(neighborPos.Z / Size)
            );

            if (chunkManager.Chunks.TryGetValue(chunkPos, out var neighbor))
            {
                chunksForUpdate.Add(neighbor.Position);
            }
        }

        foreach (var chunkPos in chunksForUpdate)
        {
            chunkManager.MarkDirty(chunkPos);
        }
    }

    private static bool InBounds(int x, int y, int z)
        => x >= 0 && y >= 0 && z >= 0 && x < Size && y < Size && z < Size;

    public ChunkMeshData GenerateMeshData(ChunkManager chunkManager)
    {
        var vertices = new List<Vertex>();
        var indices = new List<ushort>();
        ushort indexOffset = 0;

        for (int x = 0; x < Size; x++)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        {
            if (_blocks[x, y, z] == 0) continue;

            var blockId = _blocks[x, y, z];
            var type = BlockType.Types[blockId];
            var basePos = new Vector3(x, y, z);

            for (var i = 0; i < CubeFaces.Length; i++)
            {
                var (normal, face) = CubeFaces[i];
                var nx = x + (int)normal.X;
                var ny = y + (int)normal.Y;
                var nz = z + (int)normal.Z;

                if (GetBlock(nx, ny, nz, chunkManager) != 0) continue;

                var tile = type.GetTile(normal);
                var uv = FaceUV(tile.X, tile.Y);

                for (var j = 0; j < 4; j++)
                {
                    var faceCorner = face[j];
                    var aoNeighbor = AONeighbors[i][j];
                    var pos = basePos + faceCorner;
                    var uvCoord = uv[j];

                    var side1AoNeighbor = aoNeighbor[0];
                    var side2AoNeighbor = aoNeighbor[1];
                    var cornerAoNeighbor = aoNeighbor[2];

                    var side1 = GetBlock(basePos + side1AoNeighbor, chunkManager) != 0;
                    var side2 = GetBlock(basePos + side2AoNeighbor, chunkManager) != 0;
                    var corner = GetBlock(basePos + cornerAoNeighbor, chunkManager) != 0;

                    var ao = VertexAO(side1, side2, corner);

                    var baseBrightness = normal switch
                    {
                        _ when normal == Vector3.UnitY => 1.0f,
                        _ when normal == Vector3.UnitZ => 0.95f,
                        _ when normal == Vector3.UnitX => 0.9f,
                        _ when normal == -Vector3.UnitZ => 0.85f,
                        _ when normal == -Vector3.UnitX => 0.8f,
                        _ when normal == -Vector3.UnitY => 0.75f,
                        _ => 1.0f
                    };

                    var brightness = baseBrightness * ao * MathUtils.ONE_THIRD;
                    var shade = (byte)(brightness * 255);
                    vertices.Add(new Vertex(pos, shade, shade, shade, 255, uvCoord));
                }

                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 1));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 0));
                indices.Add((ushort)(indexOffset + 2));
                indices.Add((ushort)(indexOffset + 3));

                indexOffset += 4;
            }
        }

        return new ChunkMeshData(vertices, indices);
    }

    public void UpdateMesh(ChunkMeshData meshData)
    {
        _mesh?.Dispose();
        _mesh = new Mesh(_gl, CollectionsMarshal.AsSpan(meshData.Vertices), CollectionsMarshal.AsSpan(meshData.Indices));
    }

    public int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2)
        {
            return 0;
        }

        return 3 - (Convert.ToInt16(side1) + Convert.ToInt16(side2) + Convert.ToInt16(corner));
    }

    public bool IsSolidBlock(int x, int y, int z)
    {
        return InBounds(x, y, z) && _blocks[x, y, z] != 0;
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

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        GC.SuppressFinalize(this);
    }

    private static readonly (Vector3 normal, Vector3[] face)[] CubeFaces =
    [
       (new Vector3(0,  0, -1), [CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackRightTop, CubeVertexPositions.BackRightBottom]),
       (new Vector3(0,  0,  1), [CubeVertexPositions.FrontRightBottom, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontLeftBottom]),
       (new Vector3(-1, 0,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.BackLeftTop, CubeVertexPositions.BackLeftBottom]),
       (new Vector3(1,  0,  0), [CubeVertexPositions.BackRightBottom, CubeVertexPositions.BackRightTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.FrontRightBottom]),
       (new Vector3(0,  1,  0), [CubeVertexPositions.BackLeftTop, CubeVertexPositions.FrontLeftTop, CubeVertexPositions.FrontRightTop, CubeVertexPositions.BackRightTop]),
       (new Vector3(0, -1,  0), [CubeVertexPositions.FrontLeftBottom, CubeVertexPositions.BackLeftBottom, CubeVertexPositions.BackRightBottom, CubeVertexPositions.FrontRightBottom])
    ];

    private static readonly Vector3[][][] AONeighbors =
    [
        [
            [Vector3Directions.LeftBackward, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.LeftBackward, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.RightBackward, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.RightBackward, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
        ],
        [
            [Vector3Directions.RightForward, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
            [Vector3Directions.RightForward, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.LeftForward, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.LeftForward, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.LeftForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpLeft, Vector3Directions.LeftBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.DownLeft, Vector3Directions.LeftBackward, Vector3Directions.DownLeftBackward],
        ],
        [
            [Vector3Directions.DownRight, Vector3Directions.RightBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightBackward, Vector3Directions.UpRightBackward],
            [Vector3Directions.UpRight, Vector3Directions.RightForward, Vector3Directions.UpRightForward],
            [Vector3Directions.DownRight, Vector3Directions.RightForward, Vector3Directions.DownRightForward],
        ],
        [
            [Vector3Directions.UpLeft, Vector3Directions.UpBackward, Vector3Directions.UpLeftBackward],
            [Vector3Directions.UpLeft, Vector3Directions.UpForward, Vector3Directions.UpLeftForward],
            [Vector3Directions.UpRight, Vector3Directions.UpForward, Vector3Directions.UpRightForward],
            [Vector3Directions.UpRight, Vector3Directions.UpBackward, Vector3Directions.UpRightBackward],
        ],
        [
            [Vector3Directions.DownLeft, Vector3Directions.DownForward, Vector3Directions.DownLeftForward],
            [Vector3Directions.DownLeft, Vector3Directions.DownBackward, Vector3Directions.DownLeftBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownBackward, Vector3Directions.DownRightBackward],
            [Vector3Directions.DownRight, Vector3Directions.DownForward, Vector3Directions.DownRightForward],
        ]
    ];
}

public record ChunkMeshData(List<Vertex> Vertices, List<ushort> Indices);