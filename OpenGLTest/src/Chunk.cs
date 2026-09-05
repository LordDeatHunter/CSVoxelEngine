using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Silk.NET.OpenGL;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace silkgltriangle;

/// <summary>
/// Represents a 16x16x16 chunk of blocks in the world.
/// </summary>
public class Chunk : IDisposable
{
    public const int Size = 16;
    public const int SizeShift = 4; // log2(Size), for shift/mask coordinate math
    public const int SizeMask = Size - 1;
    private const int BlockCount = Size * Size * Size;

    // Meshing reads a snapshot of this chunk plus a 1-block border from neighbors
    private const int Padded = Size + 2;
    private const int PaddedSq = Padded * Padded;
    private const int PaddedCount = Padded * PaddedSq;

    internal const int AtlasTilesPerSide = 4;
    internal const float TileUVSize = 1f / AtlasTilesPerSide;

    // Flat block storage; index = (x << 8) | (y << 4) | z
    private readonly byte[] _blocks = new byte[BlockCount];
    private int _solidCount;

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

    /// <summary>
    /// Whether this chunk has an uploaded, non-empty mesh worth drawing.
    /// </summary>
    public bool HasMesh => _mesh != null;

    /// <summary>
    /// Whether a mesh has been applied at least once, even an empty one (all air, or solid with
    /// no exposed face). Main-thread only. The LOD uses it to tell "chunk is drawn" apart from
    /// "chunk has nothing to draw", which <see cref="HasMesh"/> cannot.
    /// </summary>
    public bool IsMeshed { get; private set; }

    // Column data only depends on (x, z), so vertical chunk stacks share one entry.
    // The outermost LOD ring touches more columns than this (they're computed once per region
    // build and rarely revisited), so occasional Clear + recompute is cheaper than a bigger
    // cache. 16k entries is ~40 MB.
    private static readonly ConcurrentDictionary<(int X, int Z), ColumnData> ColumnCache = new();
    private const int ColumnCacheLimit = 16384;

    // Meshing lookup tables, flattened from CubeFaces/AONeighbors into padded-snapshot index deltas
    private static readonly int[] FaceNeighborDelta = new int[6];
    private static readonly int[][] AODeltas = new int[6][]; // 12 per face: (side1, side2, corner) x 4 corners
    internal static readonly float[] FaceBrightness = [0.85f, 0.95f, 0.8f, 0.9f, 1.0f, 0.75f]; // matches CubeFaces order
    internal static readonly byte[] ShadeLut = new byte[6 * 4]; // [face * 4 + ao]

    static Chunk()
    {
        for (var i = 0; i < 6; i++)
        {
            FaceNeighborDelta[i] = PaddedDelta(CubeFaces[i].normal);

            var deltas = new int[12];
            for (var j = 0; j < 4; j++)
            {
                deltas[j * 3 + 0] = PaddedDelta(AONeighbors[i][j][0]);
                deltas[j * 3 + 1] = PaddedDelta(AONeighbors[i][j][1]);
                deltas[j * 3 + 2] = PaddedDelta(AONeighbors[i][j][2]);
            }
            AODeltas[i] = deltas;

            for (var ao = 0; ao <= 3; ao++)
                ShadeLut[(i << 2) | ao] = (byte)(FaceBrightness[i] * ao * MathUtils.ONE_THIRD * 255);
        }
    }

    private static int PaddedDelta(Vector3 offset) =>
        (int)offset.X * PaddedSq + (int)offset.Y * Padded + (int)offset.Z;

    /// <summary>Flat index of a chunk-local block; shared with the WorldGen carvers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Idx(int x, int y, int z) => (x << (2 * SizeShift)) | (y << SizeShift) | z;

    public Chunk(GL gl, Vector3 position, bool generateBlockData = true)
    {
        _gl = gl;
        Position = position;

        if (generateBlockData)
        {
            GenerateBlockData();
        }
    }

    /// <summary>
    /// Fills the terrain columns (grass / dirt / stone by depth), then lets the cave carvers
    /// clear their air out of the result. Runs on a generation worker thread.
    /// </summary>
    public void GenerateBlockData()
    {
        var column = GetColumnData((int)Position.X, (int)Position.Z);
        var lowerBoundY = LowerBoundY;

        // Entirely above the terrain: nothing to fill, nothing to carve
        if (lowerBoundY > column.MaxHeight)
        {
            IsBlockDataReady = true;
            return;
        }

        for (var x = 0; x < Size; x++)
        {
            for (var z = 0; z < Size; z++)
            {
                var i = (x << SizeShift) | z;
                GenerateColumn(x, z, column.Heights[i], column.DirtDepth[i], lowerBoundY);
            }
        }

        var worldX0 = (int)Position.X * Size;
        var worldZ0 = (int)Position.Z * Size;

        if (WorldGenerationConfig.NoiseCavesEnabled)
            NoiseCaves.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        if (WorldGenerationConfig.WormCavesEnabled)
            WormCarver.Carve(_blocks, column, worldX0, lowerBoundY, worldZ0);

        // Carving removed an unknown number of blocks; a recount is cheaper than bookkeeping
        var solid = 0;
        foreach (var block in _blocks)
            if (block != BlockType.Air) solid++;
        _solidCount = solid;

        IsBlockDataReady = true;
    }

    /// <summary>
    /// Returns the 2D generation inputs for a chunk column. Nothing generated is ever solid
    /// above Heights[x, z]; the block at that height is solid unless a cave or entrance carved
    /// it. Cached, deterministic, and safe to call from any thread.
    /// </summary>
    public static ColumnData GetColumnData(int chunkX, int chunkZ)
    {
        var key = (chunkX, chunkZ);
        if (ColumnCache.TryGetValue(key, out var cached))
            return cached;

        var column = TerrainGenerator.Compute(chunkX, chunkZ);

        // Crude growth cap; entries are deterministic so a full reset is harmless
        if (ColumnCache.Count >= ColumnCacheLimit)
            ColumnCache.Clear();

        ColumnCache[key] = column;
        return column;
    }

    /// <summary>
    /// The terrain heightmap for a chunk column, indexed by (x &lt;&lt; SizeShift) | z. Ravines
    /// are already cut in; caves are not (they are 3D). See <see cref="GetColumnData"/>.
    /// </summary>
    public static int[] GetColumnHeights(int chunkX, int chunkZ) => GetColumnData(chunkX, chunkZ).Heights;

    // Solid from the chunk floor up to the terrain height: grass on top, dirt for dirtDepth
    // blocks under it, stone below that. dirtDepth == 0 marks a bare stone column
    // (see ColumnData.DirtDepth) - deep ravine walls, where the top block is stone as well
    private void GenerateColumn(int x, int z, int terrainHeight, int dirtDepth, int lowerBoundY)
    {
        for (var y = 0; y < Size; y++)
        {
            var depth = terrainHeight - (lowerBoundY + y);
            if (depth < 0) break;

            _blocks[Idx(x, y, z)] = dirtDepth == 0 ? BlockType.Stone
                : depth == 0 ? BlockType.Grass
                : depth <= dirtDepth ? BlockType.Dirt
                : BlockType.Stone;
        }
    }

    public int LowerBoundY => (int)Position.Y * Size;
    public int UpperBoundY => (int)Position.Y * Size + Size;

    /// <summary>
    /// Reads a block using chunk-local coordinates. Coordinates must be in [0, Size).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetLocalBlock(int x, int y, int z) => _blocks[Idx(x, y, z)];

    /// <summary>Clears a block. Returns false if the cell is outside this chunk or already air.</summary>
    public bool RemoveBlockAt(Vector3 worldPos, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        if (_blocks[Idx(localX, localY, localZ)] == 0) return false;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;

        chunkManager.ImmediateRemesh(Position);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Fills a block. Returns false if the cell is outside this chunk or already occupied, so
    /// callers can avoid spending an item on a placement that never happened.
    /// </summary>
    public bool PlaceBlockAt(Vector3 worldPos, byte blockId, ChunkManager chunkManager)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return false;
        if (_blocks[Idx(localX, localY, localZ)] != 0) return false;

        _blocks[Idx(localX, localY, localZ)] = blockId;
        _solidCount++;

        chunkManager.ImmediateRemesh(Position);
        MarkNeighborsDirty(worldPos, chunkManager);
        return true;
    }

    /// <summary>
    /// Clears a block without remeshing and returns the removed id (0 if the cell was outside
    /// this chunk or already air). The caller must remesh every touched chunk afterwards —
    /// explosions clear hundreds of blocks, where per-block remeshing would hitch.
    /// Main thread only, like the other block edits.
    /// </summary>
    public byte ClearBlockAt(Vector3 worldPos)
    {
        var localX = (int)(worldPos.X - Position.X * Size);
        var localY = (int)(worldPos.Y - Position.Y * Size);
        var localZ = (int)(worldPos.Z - Position.Z * Size);

        if (!InBounds(localX, localY, localZ)) return 0;
        var removed = _blocks[Idx(localX, localY, localZ)];
        if (removed == 0) return 0;
        _blocks[Idx(localX, localY, localZ)] = 0;
        _solidCount--;
        return removed;
    }

    /// <summary>
    /// Remeshes this chunk right away and schedules its 6 neighbours for remeshing, so a batch
    /// edit that already cleared the blocks (see <see cref="ClearBlockAt"/>) becomes visible.
    /// </summary>
    public void RemeshAfterBatchEdit(ChunkManager chunkManager)
    {
        chunkManager.ImmediateRemesh(Position);
        foreach (var offset in NeighborOffsets)
        {
            var neighborPos = Position + offset;
            if (chunkManager.Chunks.ContainsKey(neighborPos))
                chunkManager.MarkDirty(neighborPos);
        }
    }

    private void MarkNeighborsDirty(Vector3 worldPos, ChunkManager chunkManager)
    {
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
        if (_solidCount == 0)
            return new ChunkMeshData([], []);

        var padded = ArrayPool<byte>.Shared.Rent(PaddedCount);
        try
        {
            FillPaddedSnapshot(padded, chunkManager);
            return GenerateMeshDataFromSnapshot(padded);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(padded);
        }
    }

    /// <summary>
    /// Copies this chunk plus a 1-block border from the 26 surrounding chunks into a flat
    /// snapshot, so meshing does zero dictionary lookups. Missing neighbors read as air.
    /// </summary>
    private void FillPaddedSnapshot(byte[] padded, ChunkManager chunkManager)
    {
        var neighbors = new Chunk?[27];
        for (var cx = 0; cx < 3; cx++)
        for (var cy = 0; cy < 3; cy++)
        for (var cz = 0; cz < 3; cz++)
        {
            var ni = (cx * 3 + cy) * 3 + cz;
            if (ni == 13)
            {
                neighbors[ni] = this;
                continue;
            }

            var pos = Position + new Vector3(cx - 1, cy - 1, cz - 1);
            if (chunkManager.Chunks.TryGetValue(pos, out var neighbor))
                neighbors[ni] = neighbor;
        }

        for (var px = 0; px < Padded; px++)
        {
            var cx = px == 0 ? 0 : px == Padded - 1 ? 2 : 1;
            var lx = (px - 1) & SizeMask;

            for (var py = 0; py < Padded; py++)
            {
                var cy = py == 0 ? 0 : py == Padded - 1 ? 2 : 1;
                var ly = (py - 1) & SizeMask;
                var rowBase = px * PaddedSq + py * Padded;
                var neighborRow = (cx * 3 + cy) * 3;

                for (var pz = 0; pz < Padded; pz++)
                {
                    var cz = pz == 0 ? 0 : pz == Padded - 1 ? 2 : 1;
                    var neighbor = neighbors[neighborRow + cz];
                    padded[rowBase + pz] = neighbor?._blocks[Idx(lx, ly, (pz - 1) & SizeMask)] ?? 0;
                }
            }
        }
    }

    private ChunkMeshData GenerateMeshDataFromSnapshot(byte[] padded)
    {
        var vertices = new List<Vertex>(4096);
        var indices = new List<ushort>(6144);
        ushort indexOffset = 0;
        Span<Vector2> uv = stackalloc Vector2[4];

        for (var x = 0; x < Size; x++)
        for (var y = 0; y < Size; y++)
        for (var z = 0; z < Size; z++)
        {
            var blockId = _blocks[Idx(x, y, z)];
            if (blockId == 0) continue;

            var type = BlockType.ById[blockId];
            if (type == null) continue;

            var basePos = new Vector3(x, y, z);
            var pIdx = (x + 1) * PaddedSq + (y + 1) * Padded + (z + 1);

            for (var i = 0; i < 6; i++)
            {
                if (padded[pIdx + FaceNeighborDelta[i]] != 0) continue;

                var tile = type.FaceTiles[i];
                var u0 = tile.X * TileUVSize;
                var v0 = tile.Y * TileUVSize;
                uv[0] = new Vector2(u0, v0 + TileUVSize);
                uv[1] = new Vector2(u0, v0);
                uv[2] = new Vector2(u0 + TileUVSize, v0);
                uv[3] = new Vector2(u0 + TileUVSize, v0 + TileUVSize);

                var face = CubeFaces[i].face;
                var aoDeltas = AODeltas[i];

                for (var j = 0; j < 4; j++)
                {
                    var side1 = padded[pIdx + aoDeltas[j * 3 + 0]] != 0;
                    var side2 = padded[pIdx + aoDeltas[j * 3 + 1]] != 0;
                    var corner = padded[pIdx + aoDeltas[j * 3 + 2]] != 0;

                    var ao = VertexAO(side1, side2, corner);
                    var shade = ShadeLut[(i << 2) | ao];

                    vertices.Add(new Vertex(basePos + face[j], shade, shade, shade, 255, uv[j]));
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
        IsMeshed = true;
        _mesh?.Dispose();
        _mesh = meshData.Vertices.Count == 0
            ? null
            : new Mesh(_gl, CollectionsMarshal.AsSpan(meshData.Vertices), CollectionsMarshal.AsSpan(meshData.Indices));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int VertexAO(bool side1, bool side2, bool corner)
    {
        if (side1 && side2) return 0;
        return 3 - ((side1 ? 1 : 0) + (side2 ? 1 : 0) + (corner ? 1 : 0));
    }

    public void Render()
    {
        _mesh?.Render();
    }

    public void Dispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        GC.SuppressFinalize(this);
    }

    internal static readonly (Vector3 normal, Vector3[] face)[] CubeFaces =
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
