using System;
using System.Linq;
using System.Numerics;
using VoxelEngine.WorldGen;
using Xunit;

namespace VoxelEngine.Tests.WorldGen;

/// <summary>
/// The <see cref="WorldgenStageTimer"/> seam in <c>ChunkTerrainFill.GenerateBlockData</c> must be
/// pure instrumentation: a chunk generated with a timer is byte-identical to one generated without,
/// and the marks cover the whole call (every stage once on a filled chunk, only the column stage on
/// an early-out chunk), so the probe's stage sum accounts for the whole fill.
/// </summary>
public sealed class WorldgenStageTimerTests
{
    public WorldgenStageTimerTests() => TestContent.EnsureLoaded();

    private static Vector3 OriginSurfaceChunk() =>
        new(0, ColumnCache.Get(0, 0).MaxHeight >> Chunk.SizeShift, 0);

    private static Vector3 SeaFloorChunk()
    {
        // Gate on the cheap biome test first, as WorldgenTests.FindSubmergedChunkColumn does.
        for (var cx = -160; cx <= 160; cx++)
        for (var cz = -160; cz <= 160; cz++)
        {
            if (TerrainGenerator.BiomeAt(cx * Chunk.Size + 8, cz * Chunk.Size + 8) != Biome.Ocean)
                continue;
            var column = ColumnCache.Get(cx, cz);
            if (column.MaxHeight < WorldGenerationConfig.SeaLevel)
                return new Vector3(cx, column.MaxHeight >> Chunk.SizeShift, cz);
        }

        Assert.Fail("no fully submerged ocean chunk column within 160 chunks");
        return default;
    }

    /// <summary>The surface, deep-stone, sea-floor and just-above-surface chunks, in that order.</summary>
    private static Vector3[] FixedChunks()
    {
        var surface = OriginSurfaceChunk();
        return
        [
            surface,
            surface with { Y = surface.Y - 3 },
            SeaFloorChunk(),
            surface with { Y = surface.Y + 1 },
        ];
    }

    private static Chunk Generate(Vector3 pos, WorldgenStageTimer? timer)
    {
        var chunk = new Chunk(null!, pos, generateBlockData: false);
        ChunkTerrainFill.GenerateBlockData(chunk, timer);
        return chunk;
    }

    private static bool SameOrBothNull<T>(T[]? a, T[]? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    [Fact]
    public void TheStageTimerNeverChangesTheGeneratedChunk()
    {
        var positions = FixedChunks();
        var water = ContentRegistry.RequireBlock("water").Id;
        var ice = ContentRegistry.RequireBlock("ice").Id;
        var anySolid = false;
        var seaFloorFlooded = false;

        for (var i = 0; i < positions.Length; i++)
        {
            var plain = Generate(positions[i], null);
            var timed = Generate(positions[i], new WorldgenStageTimer());

            Assert.True(plain.Storage.ToDense().SequenceEqual(timed.Storage.ToDense()), $"blocks differ at {positions[i]}");
            Assert.True(SameOrBothNull(plain.StateArray, timed.StateArray), $"state differs at {positions[i]}");
            Assert.Equal(plain.SolidCount, timed.SolidCount);
            Assert.Equal(plain.SurfaceMinY, timed.SurfaceMinY);
            Assert.Equal(plain.IsBlockDataReady, timed.IsBlockDataReady);
            Assert.True(SameOrBothNull(plain.FluidFrontier, timed.FluidFrontier),
                $"fluid frontier differs at {positions[i]}");

            anySolid |= plain.SolidCount > 0;
            if (i == 2)
                seaFloorFlooded = plain.Storage.ToDense().Any(b => b == water || b == ice);
        }

        Assert.True(anySolid, "no fixed chunk generated any solid block");
        Assert.True(seaFloorFlooded, "the sea-floor chunk holds no water, so the water path never ran");
    }

    [Fact]
    public void EveryStageIsMarkedOnceOnAFilledChunkAndOnlyTheColumnsOnASkyChunk()
    {
        var timer = new WorldgenStageTimer();
        var stages = Enum.GetValues<WorldgenStageTimer.Stage>();

        Generate(OriginSurfaceChunk(), timer);
        foreach (var s in stages)
        {
            var expected = s == WorldgenStageTimer.Stage.Water && !WorldGenerationConfig.WaterEnabled ? 0 : 1;
            Assert.True(timer.MarksOf(s) == expected, $"{s} marked {timer.MarksOf(s)} times on a filled chunk");
        }

        timer.Clear();
        Generate(new Vector3(0, 64, 0), timer);
        foreach (var s in stages)
        {
            var expected = s == WorldgenStageTimer.Stage.Columns ? 1 : 0;
            Assert.True(timer.MarksOf(s) == expected, $"{s} marked {timer.MarksOf(s)} times on a sky chunk");
        }
    }
}
