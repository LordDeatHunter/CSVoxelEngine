using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using VoxelEngine.Content.Structures;
using VoxelEngine.Lod;
using VoxelEngine.Simulation;
using VoxelEngine.Simulation.Fluids;
using VoxelEngine.Simulation.LeafDecay;
using VoxelEngine.Simulation.Tower;
using VoxelEngine.WorldGen.Structures;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Simulation.Tower;

/// <summary>
/// A MEASUREMENT HARNESS for the battle tower's collapse (BT-8, measured by BT-9), not a regression guard. It stamps the
/// largest tower (the widest radius, the most floors) into loaded chunks with no terrain, builds the real writer and the
/// site's <see cref="TowerCollapse"/>, and runs it frame by frame under the per-frame scan budget and the writer's cap,
/// printing the frames it takes and the main-thread cost of each frame's step (mean, p90, max) next to the cells it changed.
///
/// Nothing here asserts a TIMING (a shared dev machine with no CI would make that a flake generator); the numbers are read by
/// a human and recorded in plans/BATTLE_TOWER.md. The only assertions are anti-vacuity: the collapse finishes and changes
/// cells. The remeshing the writes queue is not in the step's cost; it is bounded by the mesh scheduler's per-frame budgets.
/// A synthetic site keeps it off the world seed; the writer lowers far-LOD column caps, hence the collection.
/// </summary>
[Collection("LOD column caps")]
public sealed class TowerCollapseCostProbe(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private const int MaxFrames = 100_000;

    [Fact]
    public void CollapseStepCostPerFrame()
    {
        TestContent.EnsureLoaded();
        var site = TowerSiteFixture.SiteOf(WorldGenerationConfig.TowerMaxRadius, WorldGenerationConfig.TowerMaxFloors, 0);

        var chunks = new ChunkManager(null!);
        var stamped = new List<Chunk>();
        for (var cx = site.MinChunkX; cx <= site.MaxChunkX; cx++)
        for (var cy = site.MinChunkY; cy <= site.MaxChunkY; cy++)
        for (var cz = site.MinChunkZ; cz <= site.MaxChunkZ; cz++)
        {
            var position = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, position, generateBlockData: false);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
            {
                var cell = TowerGenerator.CellAt(site, cx * Chunk.Size + x, cy * Chunk.Size + y, cz * Chunk.Size + z);
                if (cell > 0) chunk.SetBlockRaw(x, y, z, StructureCell.BlockOf(cell), StructureCell.StateOf(cell));
            }
            chunk.IsBlockDataReady = true;
            chunks.Chunks[position] = chunk;
            stamped.Add(chunk);
        }
        var before = stamped.Select(c => c.Storage.ToDense()).ToArray();

        var writer = new TowerBlockWriter(chunks, new FluidManager(), new LeafDecayManager(), new FallingBlockManager(),
            new LodManager(null!), (_, _, _) => { }, null);
        var collapse = TowerCollapse.ForSite(site, 0f);

        var frameMs = new List<double>();
        var sw = new Stopwatch();
        for (var f = 0; f < MaxFrames && !collapse.Done; f++)
        {
            writer.BeginFrame();
            var budget = MobConfig.TowerCollapseScanCellsPerFrame;
            sw.Restart();
            collapse.Step(Dt, writer, chunks, ref budget, null);
            sw.Stop();
            frameMs.Add(sw.Elapsed.TotalMilliseconds);
        }

        Assert.True(collapse.Done);
        var changed = 0;
        for (var i = 0; i < stamped.Count; i++)
        {
            var after = stamped[i].Storage.ToDense();
            for (var j = 0; j < after.Length; j++)
                if (after[j] != before[i][j]) changed++;
        }
        Assert.True(changed > 0);

        var sorted = frameMs.OrderBy(t => t).ToArray();
        var worst = frameMs.IndexOf(sorted[^1]);
        output.WriteLine($"tower radius {site.Radius}, {site.Floors} floors, {stamped.Count} chunks");
        output.WriteLine($"collapse: {frameMs.Count} frames to done, {changed} cells changed, "
                         + $"step mean {frameMs.Average():F3} ms, p90 {sorted[(int)(sorted.Length * 0.9)]:F3} ms, "
                         + $"p99 {sorted[(int)(sorted.Length * 0.99)]:F3} ms, max {sorted[^1]:F3} ms (frame {worst}), "
                         + $"{frameMs.Count(t => t > 1.0)} frames over 1 ms; "
                         + $"after the first (JIT) frame max {frameMs.Skip(1).Max():F3} ms");
    }
}
