using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// Item D2's output-preservation fact, and the cross-tree hash that is its authority.
///
/// <para><b>The gate rule, stated once and applied identically to both trees.</b> <em>D2 is
/// output-preserving by construction - a cache is a cache, so the same column must generate the
/// same blocks whether it was served as a hit, a miss or an eviction. The gate looks for ZERO
/// difference: the hash must be bit-identical in-run on each tree AND identical between the
/// pristine baseline and the work tree. Any difference is a bug to diagnose, not noise to
/// accept.</em></para>
///
/// <para><b>This hash, not the pixel gate, is the authority for output preservation.</b> The
/// scripted visual scenarios are a smoke check on top: they run a real renderer, so they carry
/// streaming-depth and fade-phase residue that a cache-policy change moves by construction (see
/// PERF_TODO's gate traps). The bytes folded here carry none of that - they are the generator's
/// own output, compared exactly, with no tolerance anywhere.</para>
///
/// <para><b>How the same file runs on both trees.</b> This file must compile and run UNCHANGED
/// against a pristine <c>git archive HEAD</c> export, so it touches only API that exists on both:
/// <see cref="Chunk.GetColumnData"/>, <see cref="Chunk.GetColumnHeights"/>, a generated
/// <see cref="Chunk"/> read through <see cref="Chunk.GetLocalBlock"/>,
/// <see cref="LodRegion.BuildMeshData"/> and <c>TestContent</c>. It never names
/// <c>GenerationalCache</c> and never reads a rotation counter, because the baseline has neither.
/// Anti-vacuity is proved instead by REFERENCE IDENTITY: a <see cref="ColumnData"/> captured
/// before the sweep and re-fetched after must be a DIFFERENT instance. That is a stronger check
/// than a global counter - it proves that <em>this specific column</em> was evicted, not merely
/// that some eviction happened somewhere - and it holds on the baseline (wholesale
/// <c>Clear()</c>) and on the work tree (rotated out of the oldest generation) alike.</para>
///
/// <para><b>A CellCache eviction test is deliberately absent.</b> <c>WormCarver.CellCache</c>'s
/// cap is 4096 cells of 64 blocks each; forcing it would mean carving across 256x256 chunk
/// columns, roughly 65k column generations - an order of magnitude beyond what this suite should
/// spend, and on a cache whose cap essentially never fires in play (the whole chunk load sphere
/// spans on the order of 150 cells). Its policy coverage is <c>GenerationalCacheTests</c>; its
/// output coverage is the two generated chunks folded into the hash below, which run
/// <c>WormCarver.Carve</c> for real. The next round should not re-derive this.</para>
///
/// <para><b>Why the coordinates sit far from the origin.</b> <see cref="LodRegion"/>'s
/// <c>ColumnCaps</c> is process-global, reset only by <c>WorldSeed.Apply</c> (which the parallel
/// suite never calls), and xUnit runs test classes in parallel,
/// so a class that mines or drops a block near the origin can flip a region's build output
/// mid-suite (<c>LodColumnCapTests</c> and <c>LodHeightSamplingTests</c> already dodge this the
/// same way). The probe and sweep areas below sit in the same far-but-sane band those classes use,
/// and are disjoint from each other and from every other test's coordinates, so no cap this suite
/// writes can reach them and the pre- and post-hash see the same cap state.</para>
/// </summary>
public sealed class ColumnCacheEvictionTests
{
    private readonly ITestOutputHelper _out;

    public ColumnCacheEvictionTests(ITestOutputHelper output)
    {
        _out = output;
        TestContent.EnsureLoaded();
    }

    /// <summary>
    /// Distinct chunk columns the sweep generates, FIXED rather than read from production - the
    /// <c>LodBuildPoolTests.StressWorkers</c> precedent: a gate constant sized off the very
    /// constant it guards defuses itself silently when that constant moves. 32768 is 2x
    /// max(work-tree <c>WorldGenerationConfig.ColumnCacheCapacity</c>, baseline
    /// <c>Chunk.ColumnCacheLimit</c>), both 16384 as of 2026-09-19. Twice the larger capacity
    /// guarantees the probe entry ages out on the work tree whatever <c>ColumnCacheGenerations</c>
    /// ships at (a generational cache needs capacity + capacity/G distinct inserts to drop an
    /// untouched entry, and 2x capacity covers every G &gt;= 2) and trips the baseline's wholesale
    /// <c>Clear()</c> at least twice. LOWERING THIS BELOW 2 * 16384 MAKES THE GATE VACUOUS on one
    /// tree or the other: the probe survives, the ReferenceEquals check passes, and the fact goes
    /// green while proving nothing about eviction at all.
    /// </summary>
    private const int SweepColumns = 32768;

    /// <summary>
    /// Sweep width, also fixed. It matches
    /// <c>WorldGenerationConfig.MaxConcurrentBlockDataGenerations</c> (4 as of 2026-09-19) so
    /// eviction is exercised under the concurrency production actually has, but it is NOT read
    /// from it - see <see cref="SweepColumns"/> for why.
    /// </summary>
    private const int SweepThreads = 4;

    /// <summary>Width of the sweep rectangle in chunk columns. Columns are taken row-major from
    /// it by linear index, so the swept set is exactly <see cref="SweepColumns"/> distinct columns
    /// however the rectangle is shaped.</summary>
    private const int SweepWidth = 182;

    private const int SweepBaseX = 3600;
    private const int SweepBaseZ = 3600;

    // The probe area: disjoint from the sweep rectangle, from its level-2 LOD prefetch border, and
    // from every other test class's coordinates. The lattice is 8x8 columns on a stride of 3 (64
    // columns), anchored on the probe column itself.
    private const int ProbeX = 3100;
    private const int ProbeZ = -3100;
    private const int LatticeSide = 8;
    private const int LatticeStride = 3;

    [Fact]
    public void WorldgenAndLodSurviveForcedCacheEviction()
    {
        // 1. ANTI-VACUITY ON THE PROBE ITSELF, before anything else. A legitimately flat or
        //    uniform probe would compare equal trivially and this fact would pass while proving
        //    nothing about the generator.
        var probeBefore = Chunk.GetColumnData(ProbeX, ProbeZ);
        Assert.True(probeBefore.MinHeight != probeBefore.MaxHeight,
            $"probe column ({ProbeX}, {ProbeZ}) is flat (MinHeight == MaxHeight == " +
            $"{probeBefore.MinHeight}); pick another column - a flat probe cannot show a " +
            "generation difference");

        var probeChunk = SurfaceChunk(ProbeX * Chunk.Size + 8, ProbeZ * Chunk.Size + 8);
        var distinct = new HashSet<byte>();
        for (var x = 0; x < Chunk.Size; x++)
        for (var y = 0; y < Chunk.Size; y++)
        for (var z = 0; z < Chunk.Size; z++)
            distinct.Add(probeChunk.GetLocalBlock(x, y, z));
        Assert.True(distinct.Count >= 2,
            $"probe chunk holds only {distinct.Count} distinct block id(s); a uniform chunk " +
            "cannot show a generation difference");

        // 2. BEFORE: one fold over the whole composed worldgen path plus all three LOD levels.
        var preHash = FoldEverything();

        // 3. SWEEP: drive the cache well past its cap on four threads, so the work tree's rotation
        //    and the baseline's wholesale clear both happen under real concurrency.
        var elapsed = RunSweep();

        // 4. AFTER: the eviction really happened - on either tree...
        var probeAfter = Chunk.GetColumnData(ProbeX, ProbeZ);
        Assert.False(ReferenceEquals(probeBefore, probeAfter),
            $"the probe column survived a {SweepColumns}-column sweep, so nothing was evicted and " +
            "this gate proved nothing - see SweepColumns' doc comment");

        // ...and the regenerated column is element-wise identical, so a failure names the array
        // that moved instead of only reporting that a 64-bit fold moved.
        AssertColumnsIdentical(probeBefore, probeAfter);

        var postHash = FoldEverything();
        Assert.Equal(preHash, postHash);

        // 5. The grep-able cross-tree fact a human compares between the pristine baseline and the
        //    work tree.
        _out.WriteLine($"[hash] pre=0x{preHash:x16} post=0x{postHash:x16} sweep={SweepColumns} " +
                       $"threads={SweepThreads} elapsed={elapsed.TotalMilliseconds:F0}ms");
    }

    /// <summary>
    /// Folds every generated byte this gate covers into one FNV-1a 64: the full
    /// <see cref="ColumnData"/> of a fixed lattice of probe columns, the 4096 block bytes of each
    /// of two generated chunks at the probe surface (which runs the composed path - the carvers,
    /// <c>WormCarver.Carve</c>, the water fill and the trees), and the three
    /// <see cref="LodRegion.BuildMeshData"/> levels over the probe area.
    /// </summary>
    private static ulong FoldEverything()
    {
        var h = Fnv.Init;

        for (var i = 0; i < LatticeSide; i++)
        for (var j = 0; j < LatticeSide; j++)
            h = FoldColumn(h, Chunk.GetColumnData(ProbeX + i * LatticeStride, ProbeZ + j * LatticeStride));

        foreach (var (cx, cz) in new[] { (ProbeX, ProbeZ), (ProbeX + 5, ProbeZ + 7) })
        {
            var chunk = SurfaceChunk(cx * Chunk.Size + 8, cz * Chunk.Size + 8);
            for (var x = 0; x < Chunk.Size; x++)
            for (var y = 0; y < Chunk.Size; y++)
            for (var z = 0; z < Chunk.Size; z++)
                h = Fnv.Byte(h, chunk.GetLocalBlock(x, y, z));
        }

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            var span = LodRegion.Levels[level].RegionBlocks / Chunk.Size;
            var data = LodRegion.BuildMeshData(level, FloorDiv(ProbeX, span), FloorDiv(ProbeZ, span));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.Vertices)));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.Indices)));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.WaterVertices)));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(data.WaterIndices)));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(data.ColumnCyMin)));
            h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(data.ColumnCyMax)));
            var bounds = new[] { data.YMin, data.YMax };
            h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(bounds)));
        }

        return h;
    }

    private static ulong FoldColumn(ulong h, ColumnData c)
    {
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(c.Heights)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<byte>(c.DirtDepth)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(c.EntranceWeight)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<float>(c.CaveIntensity)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<Biome>(c.Biomes)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(c.Bed)));
        h = Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(c.WaterLevel)));
        var scalars = new[] { c.MinHeight, c.MaxHeight, c.MaxWaterLevel };
        return Fnv.Add(h, MemoryMarshal.AsBytes(new ReadOnlySpan<int>(scalars)));
    }

    /// <summary>Exact, tolerance-free element-wise comparison of the probe column, so a
    /// regression reports WHICH array and WHICH index moved.</summary>
    private static void AssertColumnsIdentical(ColumnData before, ColumnData after)
    {
        AssertSame("Heights", before.Heights, after.Heights);
        AssertSame("DirtDepth", before.DirtDepth, after.DirtDepth);
        AssertSame("EntranceWeight", before.EntranceWeight, after.EntranceWeight);
        AssertSame("CaveIntensity", before.CaveIntensity, after.CaveIntensity);
        AssertSame("Biomes", before.Biomes, after.Biomes);
        AssertSame("Bed", before.Bed, after.Bed);
        AssertSame("WaterLevel", before.WaterLevel, after.WaterLevel);
        Assert.True(before.MinHeight == after.MinHeight,
            $"probe MinHeight changed across eviction: {before.MinHeight} -> {after.MinHeight}");
        Assert.True(before.MaxHeight == after.MaxHeight,
            $"probe MaxHeight changed across eviction: {before.MaxHeight} -> {after.MaxHeight}");
        Assert.True(before.MaxWaterLevel == after.MaxWaterLevel,
            $"probe MaxWaterLevel changed across eviction: {before.MaxWaterLevel} -> " +
            $"{after.MaxWaterLevel}");
    }

    private static void AssertSame<T>(string name, T[] before, T[] after) where T : struct
    {
        Assert.True(before.Length == after.Length,
            $"probe {name} length changed across eviction: {before.Length} -> {after.Length}");
        for (var i = 0; i < before.Length; i++)
            Assert.True(before[i].Equals(after[i]),
                $"probe {name}[{i}] changed across eviction: {before[i]} -> {after[i]}");
    }

    /// <summary>
    /// Generates <see cref="SweepColumns"/> distinct chunk columns on <see cref="SweepThreads"/>
    /// dedicated threads and returns the wall clock it took. Conventions from
    /// <c>LodBuildPoolTests</c>: a fixed <see cref="Thread"/> array rather than the ThreadPool
    /// (which would silently serialise the stripes or inject threads of its own), background and
    /// named, disjoint contiguous stripes, failures funnelled into a <see cref="ConcurrentBag{T}"/>
    /// because an xUnit assertion thrown on a worker thread is swallowed and would let this fact
    /// pass silently, and <c>Join()</c> on every thread before anything is asserted.
    /// </summary>
    private TimeSpan RunSweep()
    {
        var failures = new ConcurrentBag<string>();
        var threads = new Thread[SweepThreads];
        var per = (SweepColumns + SweepThreads - 1) / SweepThreads;
        var sw = Stopwatch.StartNew();

        for (var t = 0; t < SweepThreads; t++)
        {
            var from = t * per;
            var to = Math.Min(SweepColumns, from + per);
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = from; i < to; i++)
                    {
                        var heights = Chunk.GetColumnHeights(
                            SweepBaseX + i % SweepWidth, SweepBaseZ + i / SweepWidth);
                        if (heights.Length != Chunk.Size * Chunk.Size)
                            failures.Add($"swept column {i} returned {heights.Length} heights");
                    }
                }
                catch (Exception e)
                {
                    failures.Add($"sweep stripe [{from}, {to}) threw: {e}");
                }
            })
            {
                IsBackground = true,
                Name = $"ColumnCacheSweep-{t}",
            };
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        sw.Stop();
        Assert.Empty(failures);
        _out.WriteLine($"[sweep] {SweepColumns} columns on {SweepThreads} threads in " +
                       $"{sw.Elapsed.TotalMilliseconds:F0}ms");
        return sw.Elapsed;
    }

    /// <summary>The surface chunk of a world column - the <c>BiomeTests</c> idiom, copied rather
    /// than shared (that helper is a private static on that class and there is no shared test
    /// helper type beyond <c>TestContent</c>).</summary>
    private static Chunk SurfaceChunk(int x, int z)
    {
        var height = Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
        var cy = height >> Chunk.SizeShift;
        return new Chunk(null!, new Vector3(x >> Chunk.SizeShift, cy, z >> Chunk.SizeShift),
            generateBlockData: true);
    }

    /// <summary>Floor division, so a negative chunk column maps to the LOD region that contains
    /// it rather than the one above it (C#'s <c>/</c> truncates toward zero).</summary>
    private static int FloorDiv(int a, int b) => (a >= 0 ? a : a - (b - 1)) / b;

    private static class Fnv
    {
        internal const ulong Init = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        internal static ulong Byte(ulong h, byte b) => unchecked((h ^ b) * Prime);

        internal static ulong Add(ulong h, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes) h = unchecked((h ^ b) * Prime);
            return h;
        }
    }
}
