using System;
using Xunit;

namespace silkgltriangle.Tests;

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
        Chunk.BuildLightAxisMap(mapChunk, mapLocal);

        int[]?[] columns = new int[9][];

        // Chunk columns chosen to cover both FloorDiv branches and columns straddling x/z = 0.
        foreach (var (cx, cz) in new[] { (0, 0), (1, 2), (-1, -1), (0, -1), (-3, 5), (7, -4) })
        {
            Chunk.FillLightColumnHeights(cx, cz, columns);

            var worldX0 = cx * Chunk.Size - Chunk.LightBorder;
            var worldZ0 = cz * Chunk.Size - Chunk.LightBorder;

            for (var lx = 0; lx < Chunk.LightSize; lx++)
            for (var lz = 0; lz < Chunk.LightSize; lz++)
            {
                var fast = columns[mapChunk[lx] * 3 + mapChunk[lz]]!
                    [(mapLocal[lx] << Chunk.SizeShift) | mapLocal[lz]];
                Assert.Equal(ProbeHeight(worldX0 + lx, worldZ0 + lz), fast);
            }
        }
    }

    /// <summary>
    /// The structural precondition the whole precompute rests on: the volume reaches at most one
    /// chunk into each neighbour, so nine columns cover it. Raising
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
        Chunk.BuildLightAxisMap(mapChunk, mapLocal);

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
        return Chunk.GetColumnHeights(cx, cz)
            [((worldX - cx * Chunk.Size) << Chunk.SizeShift) | (worldZ - cz * Chunk.Size)];
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
