using System;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// A MEASUREMENT HARNESS for item C5, not a regression guard. It exists to answer one
/// go/no-go question before any threading code is written: how long does ONE builder take
/// to build one LOD region at each level?
///
/// C5's maximum possible win is bounded above by the MAIN THREAD's drain, not by the
/// builder. <see cref="LodManager.ApplyCompletedMeshes"/> uploads at most
/// <see cref="WorldGenerationConfig.MaxLodUploadsPerFrame"/> regions per frame, so region
/// arrivals can never exceed MaxLodUploadsPerFrame * fps however many builders exist. If a
/// single builder already saturates that ceiling, three builders buy nothing and C5 would
/// be ~200 lines of concurrent code for zero throughput.
///
/// Nothing here asserts a TIMING. This is a shared dev machine with no CI, so a timing
/// assertion would be a flake generator; the numbers are printed through
/// <see cref="ITestOutputHelper"/> and read by a human into the round's capture record.
/// The only assertion is an anti-vacuity one: every build must have emitted geometry, so a
/// silently-empty build cannot be mistaken for a fast one.
///
/// The numbers are biased in BOTH directions and neither half may be quoted alone. They
/// OVERSTATE one builder because the probe is single-threaded, with no four chunk-gen
/// threads competing for cores or clearing <c>Chunk.ColumnCache</c> underneath it. They
/// also UNDERSTATE one builder, and by more: most of a cold build's cost is not meshing at
/// all but the heightmap prefetch generating chunk columns that missed
/// <c>Chunk.ColumnCache</c> — every level meshes the same 32x32 cell grid, and the per-level
/// spread tracks the prefetch's 36 / 100 / 324 columns exactly — whereas in the game the
/// level-0 ring largely overlaps the full-detail chunk radius whose columns chunk-gen has
/// already cached. That is what the near-zero rows in the raw output are: cache hits.
///
/// So this is an ORDER-OF-MAGNITUDE reading for a go/no-go, not a rate to quote. The
/// measurement that counts is the in-game regions/s capture against the measured drain
/// ceiling; this probe only decides whether that capture is worth running.
/// </summary>
public sealed class LodBuildCostProbe
{
    private readonly ITestOutputHelper _output;

    public LodBuildCostProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    private const int WarmBuilds = 3;
    private const int TimedBuilds = 12;

    /// <summary>
    /// A distinct region coordinate per index: a 5x3 lattice around the origin, so the
    /// warm builds and the timed builds never share a region and no result is served by a
    /// cache populated by the measurement itself.
    /// </summary>
    private static (int X, int Z) RegionAt(int i) => (i % 5 - 2, i / 5 - 1);

    [Fact]
    public void RegionBuildCostPerLevel()
    {
        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            // The headless build idiom from LodBufferPoolTests: a LodManager with no GL, used
            // only for its LOD-sized MeshBufferPool, with every build's four lists handed back
            // so the timed builds measure the production rent-and-recycle path rather than a
            // cold allocation on every iteration.
            var pool = new LodManager(null!).MeshBuffers;

            for (var i = 0; i < WarmBuilds; i++)
            {
                var (x, z) = RegionAt(i);
                var warm = LodRegion.BuildMeshData(level, x, z, pool);
                Assert.NotEmpty(warm.Vertices);
                pool.Return(warm.Vertices, warm.Indices, warm.WaterVertices, warm.WaterIndices);
            }

            var ms = new double[TimedBuilds];
            for (var i = 0; i < TimedBuilds; i++)
            {
                var (x, z) = RegionAt(WarmBuilds + i);
                var sw = Stopwatch.StartNew();
                var mesh = LodRegion.BuildMeshData(level, x, z, pool);
                sw.Stop();
                ms[i] = sw.Elapsed.TotalMilliseconds;
                Assert.NotEmpty(mesh.Vertices);
                pool.Return(mesh.Vertices, mesh.Indices, mesh.WaterVertices, mesh.WaterIndices);
            }

            var sorted = (double[])ms.Clone();
            Array.Sort(sorted);
            var min = sorted[0];
            var median = (sorted[TimedBuilds / 2 - 1] + sorted[TimedBuilds / 2]) / 2.0;
            var mean = 0.0;
            foreach (var v in ms) mean += v;
            mean /= TimedBuilds;

            var spec = LodRegion.Levels[level];
            _output.WriteLine(
                $"level {level} (cell {spec.CellSize}, region {spec.RegionBlocks} blocks): " +
                $"min {min:F2} ms  median {median:F2} ms  mean {mean:F2} ms  " +
                $"=> one builder at the median = {1000.0 / median:F0} regions/s");
            _output.WriteLine($"  raw ms: {string.Join(", ", Array.ConvertAll(ms, v => v.ToString("F2")))}");
        }

        _output.WriteLine(
            $"drain ceiling = MaxLodUploadsPerFrame ({WorldGenerationConfig.MaxLodUploadsPerFrame}) * fps; " +
            "at a burst fps of 30-50 that is 120-200 regions/s.");
    }
}
