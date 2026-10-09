using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// <see cref="ChunkManager.GetFluidColumnTopGlobal"/>: the real surface of the fluid column a
/// cell belongs to, which the underwater rays march against instead of the sea plane.
/// </summary>
public class FluidSurfaceTests
{
    private const int X = 8;
    private const int Z = 8;

    private readonly byte _water;
    private readonly byte _stone;

    public FluidSurfaceTests()
    {
        TestContent.EnsureLoaded();
        _water = ContentRegistry.RequireBlock("water").Id;
        _stone = ContentRegistry.RequireBlock("stone").Id;
    }

    private static (int X, int Y, int Z) World(Chunk chunk, int x, int y, int z) =>
        ((int)chunk.Position.X * Chunk.Size + x, (int)chunk.Position.Y * Chunk.Size + y,
            (int)chunk.Position.Z * Chunk.Size + z);

    /// <summary>Water sources from centre y 12 up through the chunk above to its y 2, open air
    /// above, so the column crosses the centre/upper chunk border.</summary>
    private (ChunkManager Manager, Chunk Center, Chunk Upper) BorderColumn()
    {
        var (manager, center) = MeshTestWorld.WalledArea(MeshTestWorld.HighAbove);
        var upper = manager.Chunks[MeshTestWorld.HighAbove + Vector3.UnitY];
        for (var y = 12; y < Chunk.Size; y++) center.SetBlockRaw(X, y, Z, _water, FluidState.Source);
        for (var y = 0; y <= 2; y++) upper.SetBlockRaw(X, y, Z, _water, FluidState.Source);
        return (manager, center, upper);
    }

    [Fact]
    public void ColumnTopIsTheSameFromEveryCellOfTheColumn()
    {
        var (manager, center, upper) = BorderColumn();
        var bottom = World(center, X, 12, Z);
        var middle = World(center, X, 15, Z);
        var top = World(upper, X, 2, Z);

        var expected = manager.GetFluidSurfaceGlobal(top.X, top.Y, top.Z);
        Assert.True(float.IsFinite(expected));
        Assert.Equal(expected, manager.GetFluidColumnTopGlobal(bottom.X, bottom.Y, bottom.Z));
        Assert.Equal(expected, manager.GetFluidColumnTopGlobal(middle.X, middle.Y, middle.Z));
        Assert.Equal(expected, manager.GetFluidColumnTopGlobal(top.X, top.Y, top.Z));
    }

    [Fact]
    public void ColumnTopTracksAFlowingTopCell()
    {
        var (manager, center, upper) = BorderColumn();
        var bottom = World(center, X, 12, Z);
        var asSource = manager.GetFluidColumnTopGlobal(bottom.X, bottom.Y, bottom.Z);

        var flowing = FluidState.Flowing(3);
        upper.SetBlockRaw(X, 2, Z, _water, flowing);
        var asFlowing = manager.GetFluidColumnTopGlobal(bottom.X, bottom.Y, bottom.Z);

        var expectedDelta = FluidState.SurfaceHeight(flowing) - FluidState.SurfaceHeight(FluidState.Source);
        Assert.NotEqual(0f, expectedDelta);
        Assert.Equal(expectedDelta, asFlowing - asSource, 4);
    }

    [Fact]
    public void CeilingDefersToTheGeneratedLevel()
    {
        // A chunk column with generated water at (X, Z), and a chunk Y wholly below that level,
        // so the generated surface really is above the ceiling-bounded top.
        var (cx, cz, level) = FindWetColumn();
        var chunkY = (int)MathF.Floor(level / (float)Chunk.Size) - 3;
        var (manager, center) = MeshTestWorld.WalledArea(new Vector3(cx, chunkY, cz));
        for (var y = 4; y <= 8; y++) center.SetBlockRaw(X, y, Z, _water, FluidState.Source);
        center.SetBlockRaw(X, 9, Z, _stone, 0);

        var cell = World(center, X, 4, Z);
        var bounded = manager.GetFluidSurfaceGlobal(cell.X, cell.Y + 4, cell.Z);
        var column = ColumnCache.Get(cx, cz);
        var generated = column.WaterLevel[(X << Chunk.SizeShift) | Z];
        var expected = MathF.Max(bounded, generated + FluidState.SourceHeight);

        var top = manager.GetFluidColumnTopGlobal(cell.X, cell.Y, cell.Z);
        Assert.Equal(expected, top);
        Assert.True(top > bounded);

        // Without the ceiling the column is open to the air and reads its own top
        center.SetBlockRaw(X, 9, Z, 0, 0);
        Assert.Equal(bounded, manager.GetFluidColumnTopGlobal(cell.X, cell.Y, cell.Z));
    }

    [Fact]
    public void AStreamedWaterLevelIsWhatAClientSeesThroughACeiling()
    {
        // A capped column at negative X, so the column and in-column indices go through the
        // arithmetic shift and mask the way a client's camera can stand anywhere.
        var (manager, center) = MeshTestWorld.WalledArea(new Vector3(-2, 1, 3));
        for (var y = 4; y <= 8; y++) center.SetBlockRaw(X, y, Z, _water, FluidState.Source);
        center.SetBlockRaw(X, 9, Z, _stone, 0);

        var cell = World(center, X, 4, Z);
        var walked = manager.GetFluidSurfaceGlobal(cell.X, cell.Y + 4, cell.Z);
        var columnX = cell.X >> Chunk.SizeShift;
        var columnZ = cell.Z >> Chunk.SizeShift;
        var index = ((cell.X & Chunk.SizeMask) << Chunk.SizeShift) | (cell.Z & Chunk.SizeMask);

        // The source knows only this cell's column, and only this cell of it holds water, well
        // above the ceiling: the query must read exactly that entry.
        var level = cell.Y + 40;
        var source = new FakeColumnSource();
        source.Columns[(columnX, columnZ)] = DryColumn();
        source.Columns[(columnX, columnZ)].WaterLevel[index] = level;
        manager.ColumnSource = source;

        Assert.Equal(level + FluidState.SourceHeight, manager.GetFluidColumnTopGlobal(cell.X, cell.Y, cell.Z));

        // A column that has not arrived: only the walked surface is known
        source.Columns.Clear();
        Assert.Equal(walked, manager.GetFluidColumnTopGlobal(cell.X, cell.Y, cell.Z));

        // A column holding no water there: the walked surface again
        source.Columns[(columnX, columnZ)] = DryColumn();
        Assert.Equal(walked, manager.GetFluidColumnTopGlobal(cell.X, cell.Y, cell.Z));
    }

    private static ColumnTerrain DryColumn()
    {
        var water = new int[Chunk.Size * Chunk.Size];
        Array.Fill(water, TerrainGenerator.NoWater);
        return new ColumnTerrain(new int[Chunk.Size * Chunk.Size], water, 0.5f, 0.5f);
    }

    /// <summary>An <see cref="IColumnSource"/> that knows exactly the columns it was given.</summary>
    private sealed class FakeColumnSource : IColumnSource
    {
        public readonly Dictionary<(int X, int Z), ColumnTerrain> Columns = new();

        public bool IsReady(int chunkX, int chunkZ) => Columns.ContainsKey((chunkX, chunkZ));

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain) =>
            Columns.TryGetValue((chunkX, chunkZ), out terrain);
    }

    [Fact]
    public void DryCellHasNoColumnTop()
    {
        var (manager, center, _) = BorderColumn();
        var air = World(center, X, 4, Z);
        Assert.Equal(float.NegativeInfinity, manager.GetFluidColumnTopGlobal(air.X, air.Y, air.Z));

        Assert.Equal(float.NegativeInfinity,
            manager.GetFluidColumnTopGlobal(air.X + Chunk.Size * 10, air.Y, air.Z));
    }

    private static (int ChunkX, int ChunkZ, int Level) FindWetColumn()
    {
        for (var r = 0; r < 256; r += 4)
        foreach (var (cx, cz) in new[] { (r, 0), (-r, 0), (0, r), (0, -r), (r, r), (-r, -r) })
        {
            var level = ColumnCache.Get(cx, cz).WaterLevel[(X << Chunk.SizeShift) | Z];
            if (level != TerrainGenerator.NoWater) return (cx, cz, level);
        }
        throw new InvalidOperationException("no generated water found near the origin");
    }
}
