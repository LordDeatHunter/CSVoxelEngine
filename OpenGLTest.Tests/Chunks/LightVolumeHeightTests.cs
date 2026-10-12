using System;
using System.Collections.Generic;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.Chunks;

/// <summary>
/// ComputeLight is private and has no direct coverage: the mesh-level sun bytes asserted by
/// MeshingTests/GreedyMeshingTests only prove the greedy and per-voxel paths agree with each
/// other, not that the lighting input is right. These pin the one input PERF_TODO C2 changed -
/// the per-column terrain height the sun seeds compare against. The nine precomputed chunk
/// columns must return the same integer the deleted per-column ColumnHeight probe did, for
/// every cell of the light volume; the oracle below is re-implemented here rather than called
/// from engine code, or it would only prove the new path agrees with itself.
/// </summary>
public sealed class LightVolumeHeightTests
{
    public LightVolumeHeightTests() => TestContent.EnsureLoaded();

    [Fact]
    public void PrecomputedLightColumnsMatchThePerColumnProbe()
    {
        // Position-independent: built once, outside the loop (stackalloc is not scoped to a
        // loop body, so building it per position would grow the frame on every iteration).
        Span<int> mapChunk = stackalloc int[Chunk.LightSize];
        Span<int> mapLocal = stackalloc int[Chunk.LightSize];
        ChunkLighting.BuildLightAxisMap(mapChunk, mapLocal);

        var columns = new ColumnTerrain[9];

        // Chunk columns chosen to cover both FloorDiv branches and columns straddling x/z = 0.
        foreach (var (cx, cz) in new[] { (0, 0), (1, 2), (-1, -1), (0, -1), (-3, 5), (7, -4) })
        {
            Assert.True(ChunkLighting.FillColumns(WorldgenColumnSource.Instance, cx, cz, columns));

            var worldX0 = cx * Chunk.Size - Chunk.LightBorder;
            var worldZ0 = cz * Chunk.Size - Chunk.LightBorder;

            for (var lx = 0; lx < Chunk.LightSize; lx++)
            for (var lz = 0; lz < Chunk.LightSize; lz++)
            {
                var fast = columns[mapChunk[lx] * 3 + mapChunk[lz]]
                    .Heights[(mapLocal[lx] << Chunk.SizeShift) | mapLocal[lz]];
                Assert.Equal(ProbeHeight(worldX0 + lx, worldZ0 + lz), fast);
            }
        }
    }

    /// <summary>
    /// The structural precondition the whole precompute rests on: the volume reaches at most one
    /// chunk into each neighbor, so nine columns cover it. Raising
    /// WorldGenerationConfig.LightPropagationRadius to Size or more silently breaks that, and
    /// this is what says so.
    /// </summary>
    [Fact]
    public void LightVolumeSpansExactlyThreeChunkColumnsPerAxis()
    {
        Assert.True(Chunk.LightBorder < Chunk.Size,
            $"LightBorder {Chunk.LightBorder} must stay below Size {Chunk.Size}");

        Span<int> mapChunk = stackalloc int[Chunk.LightSize];
        Span<int> mapLocal = stackalloc int[Chunk.LightSize];
        ChunkLighting.BuildLightAxisMap(mapChunk, mapLocal);

        Assert.Equal(0, mapChunk[0]);
        Assert.Equal(2, mapChunk[Chunk.LightSize - 1]);

        for (var l = 0; l < Chunk.LightSize; l++)
        {
            Assert.InRange(mapChunk[l], 0, 2);
            Assert.InRange(mapLocal[l], 0, Chunk.Size - 1);
            if (l > 0) Assert.True(mapChunk[l] >= mapChunk[l - 1], $"mapChunk fell back at {l}");
        }
    }

    // What Chunk.ColumnHeight did before the C2 precompute: an independent oracle.
    private static int ProbeHeight(int worldX, int worldZ)
    {
        var cx = FloorDiv(worldX, Chunk.Size);
        var cz = FloorDiv(worldZ, Chunk.Size);
        return ColumnCache.GetHeights(cx, cz)
            [((worldX - cx * Chunk.Size) << Chunk.SizeShift) | (worldZ - cz * Chunk.Size)];
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>
    /// A build's column fill takes every column from its source and nowhere else: a source that
    /// lacks any of the nine (the centre's neighbors included, the far diagonal too) makes the
    /// fill fail, so the build is abandoned rather than lit, watered and tinted from a substitute,
    /// and a source that has all nine lands each one in its dx * 3 + dz slot. The source is only
    /// ever asked about the nine columns around the chunk.
    /// </summary>
    [Fact]
    public void AMissingStreamedColumnAbandonsTheFillInsteadOfSubstituting()
    {
        const int cx = 4, cz = -2;
        var nine = new HashSet<(int, int)>();
        for (var dx = -1; dx <= 1; dx++)
        for (var dz = -1; dz <= 1; dz++)
            nine.Add((cx + dx, cz + dz));

        // Only the centre column: the fill fails, and nothing outside the nine was asked for
        var centreOnly = new FakeColumnSource();
        centreOnly.Add(cx, cz, Terrain(123));
        Assert.False(ChunkLighting.FillColumns(centreOnly, cx, cz, new ColumnTerrain[9]));
        Assert.Subset(nine, centreOnly.Asked);

        // Eight of the nine, missing a far diagonal: still refused
        var missingDiagonal = new FakeColumnSource();
        foreach (var (x, z) in nine)
            if ((x, z) != (cx + 1, cz + 1))
                missingDiagonal.Add(x, z, Terrain(x * 31 + z));
        Assert.False(ChunkLighting.FillColumns(missingDiagonal, cx, cz, new ColumnTerrain[9]));
        Assert.Subset(nine, missingDiagonal.Asked);

        // All nine: each column lands in its slot, and exactly the nine were asked for
        var all = new FakeColumnSource();
        foreach (var (x, z) in nine)
            all.Add(x, z, Terrain(x * 31 + z));
        var columns = new ColumnTerrain[9];
        Assert.True(ChunkLighting.FillColumns(all, cx, cz, columns));
        Assert.True(nine.SetEquals(all.Asked), "the fill asked for other columns than the nine");
        for (var dx = 0; dx < 3; dx++)
        for (var dz = 0; dz < 3; dz++)
        {
            Assert.True(all.TryGet(cx - 1 + dx, cz - 1 + dz, out var expected));
            Assert.Same(expected.Heights, columns[dx * 3 + dz].Heights);
            Assert.Same(expected.WaterLevel, columns[dx * 3 + dz].WaterLevel);
        }
    }

    private static ColumnTerrain Terrain(int height)
    {
        var heights = new int[Chunk.Size * Chunk.Size];
        Array.Fill(heights, height);
        var water = new int[Chunk.Size * Chunk.Size];
        Array.Fill(water, TerrainGenerator.NoWater);
        return new ColumnTerrain(heights, water, 0.5f, 0.5f);
    }

    /// <summary>A dictionary-backed column source that records every column it is asked about.</summary>
    private sealed class FakeColumnSource : IColumnSource
    {
        private readonly Dictionary<(int, int), ColumnTerrain> _columns = new();

        public HashSet<(int, int)> Asked { get; } = new();

        public void Add(int chunkX, int chunkZ, ColumnTerrain terrain) => _columns[(chunkX, chunkZ)] = terrain;

        public bool IsReady(int chunkX, int chunkZ)
        {
            Asked.Add((chunkX, chunkZ));
            return _columns.ContainsKey((chunkX, chunkZ));
        }

        public bool TryGet(int chunkX, int chunkZ, out ColumnTerrain terrain)
        {
            Asked.Add((chunkX, chunkZ));
            return _columns.TryGetValue((chunkX, chunkZ), out terrain);
        }
    }
}
