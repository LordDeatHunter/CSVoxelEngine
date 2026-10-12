using System.Collections.Concurrent;
using System.Collections.Generic;
using VoxelEngine.Saves;
using VoxelEngine.WorldGen;

namespace VoxelEngine.Tests.Support;

/// <summary>
/// A test <see cref="IWorldSource"/> that supplies a rectangle of chunk columns itself and defers
/// every other column to a wrapped fallback (worldgen by default): inside the rectangle every
/// column is a flat plateau of one block up to <see cref="Top"/>, optionally under water up to a
/// level of its own and with single cells carved out, and its <see cref="ColumnData"/> says so.
/// Thread-safe and deterministic like a real source: the columns are built once per key and the
/// chunks are a pure function of their position.
/// </summary>
internal sealed class FlatWorldSource : IWorldSource
{
    private const int ColumnArea = Chunk.Size * Chunk.Size;

    private readonly int _cxMin;
    private readonly int _cxMax;
    private readonly int _czMin;
    private readonly int _czMax;
    private readonly byte _block;
    private readonly Biome _biome;
    private readonly float _warmth;
    private readonly float _wetness;
    private readonly int _waterLevel;
    private readonly IReadOnlySet<(int X, int Y, int Z)>? _carved;
    private readonly IWorldSource _fallback;
    private readonly byte _dirtDepth;
    private readonly ConcurrentDictionary<(int X, int Z), ColumnData> _columns = new();

    /// <param name="cxMin">Lowest supplied chunk column X, inclusive.</param>
    /// <param name="cxMax">Highest supplied chunk column X, inclusive.</param>
    /// <param name="czMin">Lowest supplied chunk column Z, inclusive.</param>
    /// <param name="czMax">Highest supplied chunk column Z, inclusive.</param>
    /// <param name="top">The world y of the plateau's top cell, the height of every column.</param>
    /// <param name="block">The block filling every cell at or below <paramref name="top"/>.</param>
    /// <param name="biome">The biome of every supplied column.</param>
    /// <param name="warmth">The corner climate warmth of every supplied column.</param>
    /// <param name="wetness">The corner climate wetness of every supplied column.</param>
    /// <param name="waterLevel">The water level of every supplied column; water (a source) fills
    /// the cells above <paramref name="top"/> up to and including it.
    /// <see cref="TerrainGenerator.NoWater"/> means dry.</param>
    /// <param name="carved">World cells cleared to air after the fill.</param>
    /// <param name="fallback">The source of every column outside the rectangle.</param>
    /// <param name="dirtDepth">The <see cref="ColumnData.DirtDepth"/> of every supplied column; 0
    /// marks bare rock. It only describes the column (the far LOD reads it); the fill is
    /// <paramref name="block"/> all the same.</param>
    public FlatWorldSource(int cxMin, int cxMax, int czMin, int czMax, int top, byte block, Biome biome,
        float warmth, float wetness, int waterLevel = TerrainGenerator.NoWater,
        IReadOnlySet<(int X, int Y, int Z)>? carved = null, IWorldSource? fallback = null,
        int dirtDepth = WorldGenerationConfig.NominalDirtDepth)
    {
        _cxMin = cxMin;
        _cxMax = cxMax;
        _czMin = czMin;
        _czMax = czMax;
        Top = top;
        _block = block;
        _biome = biome;
        _warmth = warmth;
        _wetness = wetness;
        _waterLevel = waterLevel;
        _carved = carved;
        _fallback = fallback ?? GeneratedWorldSource.Instance;
        _dirtDepth = (byte)dirtDepth;
    }

    /// <summary>The world y of the plateau's top cell.</summary>
    public int Top { get; }

    /// <inheritdoc/>
    public bool Supplies(int chunkX, int chunkZ) =>
        chunkX >= _cxMin && chunkX <= _cxMax && chunkZ >= _czMin && chunkZ <= _czMax;

    /// <inheritdoc/>
    public ColumnData GetColumn(int chunkX, int chunkZ) =>
        Supplies(chunkX, chunkZ)
            ? _columns.GetOrAdd((chunkX, chunkZ), static (_, self) => self.BuildColumn(), this)
            : _fallback.GetColumn(chunkX, chunkZ);

    /// <inheritdoc/>
    public void Fill(Chunk chunk)
    {
        var cx = (int)chunk.Position.X;
        var cz = (int)chunk.Position.Z;
        if (!Supplies(cx, cz))
        {
            _fallback.Fill(chunk);
            return;
        }

        var blocks = new byte[Chunk.BlockCount];
        var lowerY = chunk.LowerBoundY;
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
        {
            var worldY = lowerY + y;
            if (worldY <= Top)
                blocks[Chunk.Idx(x, y, z)] = _block;
            else if (_waterLevel != TerrainGenerator.NoWater && worldY <= _waterLevel)
                blocks[Chunk.Idx(x, y, z)] = TerrainGenerator.SeaBlock;
        }

        if (_carved != null)
        {
            var min = chunk.WorldMin;
            foreach (var (x, y, z) in _carved)
            {
                var lx = x - (int)min.X;
                var ly = y - (int)min.Y;
                var lz = z - (int)min.Z;
                if ((uint)lx < Chunk.Size && (uint)ly < Chunk.Size && (uint)lz < Chunk.Size)
                    blocks[Chunk.Idx(lx, ly, lz)] = BlockType.Air;
            }
        }

        chunk.RestoreFrom(new ChunkSnapshot(blocks, state: null, playerPlaced: null), GetColumn(cx, cz));
    }

    /// <inheritdoc/>
    public bool IsReady(int chunkX, int chunkZ) => true;

    /// <inheritdoc/>
    public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
    {
        var column = GetColumn(chunkX, chunkZ);
        terrain = new ColumnTerrain(column.Heights, column.WaterLevel, column.CornerWarmth, column.CornerWetness);
        return true;
    }

    private ColumnData BuildColumn()
    {
        var heights = new int[ColumnArea];
        var bed = new int[ColumnArea];
        var water = new int[ColumnArea];
        var biomes = new Biome[ColumnArea];
        var dirtDepth = new byte[ColumnArea];
        for (var i = 0; i < ColumnArea; i++)
        {
            heights[i] = Top;
            bed[i] = Top;
            water[i] = _waterLevel;
            biomes[i] = _biome;
            dirtDepth[i] = _dirtDepth;
        }

        return new ColumnData(heights, dirtDepth, new float[ColumnArea], new float[ColumnArea], biomes, bed, water,
            _warmth, _wetness);
    }
}
