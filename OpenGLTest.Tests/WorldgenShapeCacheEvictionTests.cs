using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// Item D6's output-preservation fact, and the cross-tree hash that is its authority. The direct
/// analogue of <c>ColumnCacheEvictionTests</c>, one round later and for the three worldgen caches
/// D6 moved off <c>if (Count &gt;= limit) Clear()</c>: tree shapes, canopy tops and cactus stems.
///
/// <para><b>The gate rule, stated once and applied identically to both trees.</b> <em>D6 is
/// output-preserving by construction - all three caches hold values that are a pure function of
/// their key, so the same trunk, the same stem and the same canopy column must come back whether
/// they were served as a hit, a miss or an eviction. The gate looks for ZERO difference: the hash
/// must be bit-identical in-run on each tree AND identical between the pristine baseline and the
/// work tree. Any difference is a bug to diagnose, not noise to accept.</em></para>
///
/// <para><b>This hash, not the pixel gate, is the authority for output preservation.</b> The
/// scripted visual scenarios run a real renderer, so they carry streaming-depth and fade-phase
/// residue that a cache-policy change moves by construction. The bytes folded here carry none of
/// that - they are the generator's own output, compared exactly, with no tolerance anywhere.</para>
///
/// <para><b>How the same file runs on both trees.</b> This file must compile and run UNCHANGED
/// against a pristine <c>git archive HEAD</c> export, so it touches only API that exists on both:
/// <see cref="TreeGenerator.TryGetTrunk"/>, <see cref="TreeGenerator.GetCanopyTops"/>,
/// <c>TreeGenerator.GetCanopyColumns</c>, <see cref="CactusGenerator.TryGetCactus"/>,
/// <see cref="Chunk.GetColumnHeights"/>, <see cref="TerrainGenerator.IsForest"/> and
/// <c>TestContent</c>. It never names <c>GenerationalCache</c> and never reads a rotation counter,
/// because the baseline has neither. Anti-vacuity is proved instead by REFERENCE IDENTITY: the
/// <c>List</c> instances inside a cached shape, and the <c>CanopyColumns</c> instance itself,
/// captured before the sweeps and re-fetched after, must be DIFFERENT instances. That is stronger
/// than a global counter - it proves that <em>these specific entries</em> were evicted, not merely
/// that some eviction happened somewhere - and it holds on the baseline (wholesale <c>Clear()</c>)
/// and on the work tree (rotated out of the oldest generation) alike. It is also fail-closed: a
/// sweep that turns out not to evict the probe fails loudly here instead of passing vacuously.
/// </para>
///
/// <para><b>Why there are THREE sweeps and not two, with the arithmetic that decided it.</b> The
/// round's plan expected the canopy sweep alone to evict <c>TreeGenerator.ShapeCache</c> as a side
/// effect: <c>TreeReachBlocks</c> is 8, so <c>BuildCanopyTops</c> scans (16 + 2*8)^2 = 1024 block
/// columns per chunk column, and 8192 canopy columns therefore drive roughly 8.4M
/// <see cref="TreeGenerator.TryGetTrunk"/> calls - far past the 16384 shape capacity. That
/// arithmetic counts CALLS, and the shape cache stores only ACCEPTED trunks. Measured on this
/// tree: the 8192-column canopy grid below spans 1472 x 1472 = 2166784 DISTINCT block columns and
/// accepts 9786 trunks - UNDER the 16384 capacity, so it cannot age the tree probe out and the
/// ReferenceEquals check would (correctly, fail-closed) have failed. The separate trunk sweep is
/// therefore kept rather than dropped, sized off measured acceptance: 3008 x 3008 = 9048064
/// distinct block columns from (60000, -20000) accept 65213 trunks, comfortably past
/// <see cref="EvictionInserts"/>. Do not re-derive this from call counts.</para>
///
/// <para><b>Why the pinned constants are pinned.</b> Same argument as
/// <c>ColumnCacheEvictionTests.SweepColumns</c>, which is the precedent: a gate constant sized off
/// the very constant it guards defuses itself silently when that constant moves. Every number
/// below is a literal, NOT read from <c>WorldGenerationConfig</c>. See
/// <see cref="EvictionInserts"/> and <see cref="CanopySweepColumns"/>.</para>
///
/// <para><b>Why the coordinates sit far from the origin, and disjoint from each other.</b>
/// <see cref="LodRegion"/>'s <c>ColumnCaps</c> is process-global, reset only by
/// <c>WorldSeed.Apply</c> (which the parallel suite never calls), and xUnit runs test classes in
/// parallel, so a class that generates or edits near another's probe can flip a
/// region's build output mid-suite (<c>LodColumnCapTests</c>, <c>LodHeightSamplingTests</c> and
/// <c>ColumnCacheEvictionTests</c> all dodge this the same way). Every area below is disjoint from
/// <c>ColumnCacheEvictionTests</c>' probe (3100, -3100) and sweep (3600, 3600 x 182), from the Lod
/// classes, and - crucially - from each other: no probe lies inside any sweep rectangle, so no
/// sweep can PROMOTE the entry it is meant to evict (an oldest-generation hit is promoted back into
/// the newest generation, which would defuse the gate silently).</para>
///
/// <para><b>xUnit parallelism.</b> There is no <c>xunit.runner.json</c> and no collection anywhere
/// in this project, so classes run concurrently by default. This class continuously rotates
/// <c>TreeGenerator.ShapeCache</c>, while <c>WorldgenTests.TrunkShapeIsCachedAcrossChunks</c>
/// asserts record-struct equality across two ADJACENT <c>TryGetTrunk</c> calls - which falls
/// through to <c>List&lt;T&gt;</c> reference equality, i.e. instance identity. That is the one
/// assertion in the suite a concurrent eviction sweep can falsify, and it would look like a D6 bug
/// rather than a scheduling accident. <see cref="WorldgenShapeCacheCollection"/> puts this class
/// and <c>WorldgenTests</c> in one xUnit collection so they never overlap. If a future flake
/// appears there, this is the interaction to check first.</para>
///
/// <para><b>What this class costs the rest of the suite, measured rather than guessed.</b> The
/// three sweeps saturate four threads for about 4 s, on top of the 4-thread sweep
/// <c>ColumnCacheEvictionTests</c> already runs and xUnit's own class-level parallelism. That
/// oversubscription raises the flake rate of the suite's THROUGHPUT-SENSITIVE concurrency facts -
/// <c>GenerationalCacheTests.EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads</c> (which requires
/// &gt; 100000 lookups inside a 1500 ms deadline) and
/// <c>LodBuildPoolTests.TheInFlightCapHoldsUnderThreeProducers</c>. Both were observed failing in
/// whole-suite runs on 2026-09-19 and both are load artefacts, not interactions: run together with
/// this class ALONE they pass repeatedly (3/3), and neither reads a cache this round converted. Do
/// not chase them as D6 regressions, and do not "fix" them by weakening the sweeps - the sweep
/// sizes are what stop this gate going vacuous (see <see cref="EvictionInserts"/>). If they become
/// a nuisance, the fix is the same instrument used for <c>WorldgenTests</c> below: give the
/// throughput-sensitive classes a shared xUnit collection so they never overlap a sweep.</para>
///
/// <para><b>Wall cost.</b> About 4 s on this machine (measured 2026-09-19), split canopy 993 ms /
/// trunk 1748 ms / cactus 1244 ms across the three sweeps. The 29M-column cactus sweep is not the
/// dominant one despite being by far the largest, because most of its columns are rejected by the
/// density roll before any column data is touched. The three <c>[sweep]</c> lines print the split
/// on every run.</para>
/// </summary>
[Collection("Worldgen shape cache")]
public sealed class WorldgenShapeCacheEvictionTests
{
    private readonly ITestOutputHelper _out;

    public WorldgenShapeCacheEvictionTests(ITestOutputHelper output)
    {
        _out = output;
        TestContent.EnsureLoaded();
    }

    /// <summary>
    /// Distinct inserts a sweep must make to age an untouched entry out of a 16384-entry cache,
    /// FIXED rather than read from production - the <c>LodBuildPoolTests.StressWorkers</c> and
    /// <c>ColumnCacheEvictionTests.SweepColumns</c> precedent: a gate constant sized off the very
    /// constant it guards defuses itself silently when that constant moves. 32768 is 2x
    /// max(work-tree <c>WorldGenerationConfig.TreeShapeCacheCapacity</c> /
    /// <c>CactusShapeCacheCapacity</c>, baseline <c>ShapeCacheLimit</c>), all 16384 as of
    /// 2026-09-19. Twice the capacity guarantees the probe ages out on the work tree whatever
    /// generation count those caches ship at (a generational cache needs capacity + capacity/G
    /// distinct inserts to drop an untouched entry, and 2x capacity covers every G &gt;= 2) and
    /// trips the baseline's wholesale <c>Clear()</c> at least twice. LOWERING THIS BELOW 2 * 16384
    /// MAKES THE GATE VACUOUS on one tree or the other: the probe survives, the ReferenceEquals
    /// check passes, and the fact goes green while proving nothing about eviction at all.
    /// </summary>
    private const int EvictionInserts = 32768;

    /// <summary>
    /// Distinct chunk columns the canopy sweep builds, also fixed and for the same reason. 8192 is
    /// 2x max(work-tree <c>WorldGenerationConfig.TreeCanopyCacheCapacity</c>, baseline
    /// <c>CanopyCacheLimit</c>), both 4096 as of 2026-09-19. Unlike the two shape caches, every
    /// swept column is an insert here - <c>GetCanopyTops</c> caches unconditionally - so the count
    /// is asserted directly rather than via an acceptance tally.
    /// </summary>
    private const int CanopySweepColumns = 8192;

    /// <summary>
    /// Sweep width in threads, also fixed. It matches
    /// <c>WorldGenerationConfig.MaxConcurrentBlockDataGenerations</c> (4 as of 2026-09-19) so
    /// eviction is exercised under the concurrency production actually has, but it is NOT read
    /// from it - see <see cref="EvictionInserts"/> for why.
    /// </summary>
    private const int SweepThreads = 4;

    // --- Probe areas. Each is resolved by SEARCHING, not by assuming a fixed cell qualifies:
    // biomes put desert, ocean and bare plains wherever they like, and a hard-coded "there is a
    // tree here" would rot into a vacuous pass the first time the noise changed.
    private const int TreeProbeBaseX = -52000;
    private const int TreeProbeBaseZ = 52000;
    private const int TreeProbeChunkSpan = 64;

    private const int CactusProbeBaseX = 40480;
    private const int CactusProbeBaseZ = 32288;
    private const int CactusProbeWidth = 512;
    private const int CactusProbeScan = CactusProbeWidth * 4096;

    // --- Sweep areas, all disjoint from every probe and from each other.
    private const int CanopySweepChunkX = 3000;
    private const int CanopySweepChunkZ = 3000;
    private const int CanopySweepWidth = 91; // 91 x 91 = 8281 >= CanopySweepColumns

    private const int TrunkSweepBaseX = 60000;
    private const int TrunkSweepBaseZ = -20000;
    private const int TrunkSweepWidth = 3008;
    private const int TrunkSweepColumns = TrunkSweepWidth * TrunkSweepWidth; // 9048064

    private const int CactusSweepBaseX = 12288;
    private const int CactusSweepBaseZ = 38912;
    private const int CactusSweepWidth = 4096;
    private const int CactusSweepColumns = 29_000_000;

    [Fact]
    public void TreeCactusAndCanopyShapesSurviveForcedCacheEviction()
    {
        // 1. RESOLVE THE PROBES BY SEARCHING.
        var tree = FindTrunk();
        var cactus = FindCactus();
        var canopyChunkX = tree.X >> Chunk.SizeShift;
        var canopyChunkZ = tree.Z >> Chunk.SizeShift;
        var canopyBefore = TreeGenerator.GetCanopyColumns(canopyChunkX, canopyChunkZ);

        // 2. ANTI-VACUITY ON THE PROBES THEMSELVES, before anything else. A degenerate probe - a
        //    trunk with no wood, a stem with no cells, a canopy column with nothing over it -
        //    compares equal trivially and would let this fact pass while proving nothing.
        Assert.True(tree.Shape.Wood.Count > 0,
            $"tree probe ({tree.X}, {tree.Z}) has no wood cells; an empty shape compares equal " +
            "trivially - pick another probe");
        Assert.True(tree.Shape.LeafBlobs.Count > 0,
            $"tree probe ({tree.X}, {tree.Z}) has no leaf blobs; see above");
        Assert.True(cactus.Shape.Wood.Count > 0,
            $"cactus probe ({cactus.X}, {cactus.Z}) has no wood cells; see above");
        Assert.Contains(canopyBefore.Tops, t => t != 0);

        // 3. PIN THE baseY IDENTITY THE NEW STATIC FACTORIES MUST PRESERVE. D6's GetOrAdd takes a
        //    key-only factory, so both shape caches now RECOMPUTE baseY from the key on a miss
        //    instead of capturing the caller's local. That is a behaviour-preserving claim, and
        //    this is where it is pinned rather than asserted in prose - cold, on a cache hit, and
        //    again after a forced eviction (step 8 below).
        AssertBaseYIdentity("tree", tree.X, tree.Z, tree.BaseY, tree.Shape.TrunkBaseY);
        AssertBaseYIdentity("cactus", cactus.X, cactus.Z, cactus.BaseY, cactus.Shape.TrunkBaseY);

        // 4. BEFORE: one fold over all three probe values.
        var preHash = FoldProbes(tree, cactus, canopyBefore);

        // 5. CAPTURE REFERENCE IDENTITY of the three cached instances. These are what prove the
        //    entries were really evicted, on either tree.
        var treeWoodBefore = tree.Shape.Wood;
        var cactusWoodBefore = cactus.Shape.Wood;

        // 6. SWEEPS: drive each cache well past its cap on four threads, so the work tree's
        //    rotation and the baseline's wholesale clear both happen under real concurrency.
        //    Canopy first (it also warms a slice of the shape cache), then trunks, then cacti.
        var canopySwept = 0;
        var canopyElapsed = RunSweep("canopy", CanopySweepColumns, (i, failures) =>
        {
            var tops = TreeGenerator.GetCanopyTops(
                CanopySweepChunkX + i % CanopySweepWidth, CanopySweepChunkZ + i / CanopySweepWidth);
            if (tops.Length != Chunk.Size * Chunk.Size)
                failures.Add($"swept canopy column {i} returned {tops.Length} tops");
            Interlocked.Increment(ref canopySwept);
        });

        var trunksAccepted = 0;
        var trunkElapsed = RunSweep("trunk", TrunkSweepColumns, (i, failures) =>
        {
            if (TreeGenerator.TryGetTrunk(
                    TrunkSweepBaseX + i % TrunkSweepWidth, TrunkSweepBaseZ + i / TrunkSweepWidth,
                    out _, out _))
                Interlocked.Increment(ref trunksAccepted);
        });

        var stemsAccepted = 0;
        var cactusElapsed = RunSweep("cactus", CactusSweepColumns, (i, failures) =>
        {
            if (CactusGenerator.TryGetCactus(
                    CactusSweepBaseX + i % CactusSweepWidth, CactusSweepBaseZ + i / CactusSweepWidth,
                    out _, out _))
                Interlocked.Increment(ref stemsAccepted);
        });

        // 7. ANTI-SELF-DEFUSING: each sweep really made enough DISTINCT inserts to age its probe
        //    out. Counts are compared against the pinned literals above, never against the
        //    capacities they guard.
        Assert.True(canopySwept == CanopySweepColumns,
            $"canopy sweep built {canopySwept} columns, expected {CanopySweepColumns}");
        Assert.True(trunksAccepted >= EvictionInserts,
            $"the trunk sweep accepted only {trunksAccepted} trunks, under the {EvictionInserts} " +
            "distinct inserts needed to age an untouched entry out of a 16384-entry cache - the " +
            "gate would prove nothing; widen the sweep, do not lower the bar");
        Assert.True(stemsAccepted >= EvictionInserts,
            $"the cactus sweep accepted only {stemsAccepted} stems, under the {EvictionInserts} " +
            "distinct inserts needed; see above");

        // 8. AFTER: the eviction really happened - on either tree - and the regenerated values are
        //    byte-identical.
        var treeAfter = TreeGenerator.TryGetTrunk(tree.X, tree.Z, out var treeBaseYAfter, out var treeShapeAfter);
        Assert.True(treeAfter, $"tree probe ({tree.X}, {tree.Z}) stopped growing across eviction");
        var cactusAfter = CactusGenerator.TryGetCactus(cactus.X, cactus.Z, out var cactusBaseYAfter, out var cactusShapeAfter);
        Assert.True(cactusAfter, $"cactus probe ({cactus.X}, {cactus.Z}) stopped growing across eviction");
        var canopyAfter = TreeGenerator.GetCanopyColumns(canopyChunkX, canopyChunkZ);

        Assert.False(ReferenceEquals(treeWoodBefore, treeShapeAfter.Wood),
            $"the tree probe survived a {trunksAccepted}-trunk sweep, so nothing was evicted and " +
            "this gate proved nothing - see EvictionInserts' doc comment");
        Assert.False(ReferenceEquals(cactusWoodBefore, cactusShapeAfter.Wood),
            $"the cactus probe survived a {stemsAccepted}-stem sweep, so nothing was evicted and " +
            "this gate proved nothing - see EvictionInserts' doc comment");
        Assert.False(ReferenceEquals(canopyBefore, canopyAfter),
            $"the canopy probe survived a {canopySwept}-column sweep, so nothing was evicted and " +
            "this gate proved nothing - see CanopySweepColumns' doc comment");

        AssertBaseYIdentity("tree", tree.X, tree.Z, treeBaseYAfter, treeShapeAfter.TrunkBaseY);
        AssertBaseYIdentity("cactus", cactus.X, cactus.Z, cactusBaseYAfter, cactusShapeAfter.TrunkBaseY);

        // Element-wise first, so a regression names the cell that moved instead of only reporting
        // that a 64-bit fold moved.
        AssertShapesIdentical(tree, new TreeProbe(tree.X, tree.Z, treeBaseYAfter, treeShapeAfter));
        AssertStemsIdentical(cactus, new CactusProbe(cactus.X, cactus.Z, cactusBaseYAfter, cactusShapeAfter));
        AssertCanopyIdentical(canopyBefore, canopyAfter);

        var postHash = FoldProbes(new TreeProbe(tree.X, tree.Z, treeBaseYAfter, treeShapeAfter),
            new CactusProbe(cactus.X, cactus.Z, cactusBaseYAfter, cactusShapeAfter), canopyAfter);
        Assert.Equal(preHash, postHash);

        // 9. The grep-able cross-tree fact a human compares between the pristine baseline and the
        //    work tree. Timings stay on the [sweep] lines so this one is byte-comparable.
        _out.WriteLine($"[hash] pre=0x{preHash:x16} post=0x{postHash:x16} canopy={canopySwept} " +
                       $"cactusAccepted={stemsAccepted} trunkAccepted={trunksAccepted} " +
                       $"tree=({tree.X},{tree.Z}) cactus=({cactus.X},{cactus.Z})");
        _out.WriteLine($"[elapsed] canopy={canopyElapsed.TotalMilliseconds:F0}ms " +
                       $"trunk={trunkElapsed.TotalMilliseconds:F0}ms " +
                       $"cactus={cactusElapsed.TotalMilliseconds:F0}ms");
    }

    private readonly record struct TreeProbe(int X, int Z, int BaseY, TreeGenerator.TreeShape Shape);

    private readonly record struct CactusProbe(int X, int Z, int BaseY, CactusGenerator.CactusShape Shape);

    /// <summary>
    /// The first accepted trunk found walking chunk columns row-major from
    /// (<see cref="TreeProbeBaseX"/>, <see cref="TreeProbeBaseZ"/>), with
    /// <see cref="TerrainGenerator.IsForest"/> as the outer filter so the scan skips desert, ocean
    /// and plains whole chunks at a time (the <c>WorldgenTests</c> idiom).
    /// </summary>
    private static TreeProbe FindTrunk()
    {
        var cx0 = TreeProbeBaseX >> Chunk.SizeShift;
        var cz0 = TreeProbeBaseZ >> Chunk.SizeShift;

        for (var i = 0; i < TreeProbeChunkSpan * TreeProbeChunkSpan; i++)
        {
            var cx = cx0 + i % TreeProbeChunkSpan;
            var cz = cz0 + i / TreeProbeChunkSpan;
            if (!TerrainGenerator.IsForest(cx * Chunk.Size + Chunk.Size / 2, cz * Chunk.Size + Chunk.Size / 2))
                continue;

            for (var x = cx * Chunk.Size; x < cx * Chunk.Size + Chunk.Size; x++)
            for (var z = cz * Chunk.Size; z < cz * Chunk.Size + Chunk.Size; z++)
                if (TreeGenerator.TryGetTrunk(x, z, out var baseY, out var shape))
                    return new TreeProbe(x, z, baseY, shape);
        }

        Assert.Fail($"no trunk in the {TreeProbeChunkSpan}x{TreeProbeChunkSpan} chunk search from " +
                    $"({TreeProbeBaseX}, {TreeProbeBaseZ}); the probe area must contain one");
        return default;
    }

    /// <summary>The first accepted cactus scanning row-major from
    /// (<see cref="CactusProbeBaseX"/>, <see cref="CactusProbeBaseZ"/>).</summary>
    private static CactusProbe FindCactus()
    {
        for (var i = 0; i < CactusProbeScan; i++)
        {
            var x = CactusProbeBaseX + i % CactusProbeWidth;
            var z = CactusProbeBaseZ + i / CactusProbeWidth;
            if (CactusGenerator.TryGetCactus(x, z, out var baseY, out var shape))
                return new CactusProbe(x, z, baseY, shape);
        }

        Assert.Fail($"no cactus in the {CactusProbeScan}-column search from " +
                    $"({CactusProbeBaseX}, {CactusProbeBaseZ}); the probe area must contain one");
        return default;
    }

    /// <summary>
    /// The identity D6's key-only factories must preserve: a shape's base is the column's own
    /// ground height plus one, and the shape records that same base. Both trees compute the height
    /// from the same deterministic column data, so this is exact, not approximate.
    /// </summary>
    private static void AssertBaseYIdentity(string what, int x, int z, int baseY, int trunkBaseY)
    {
        var height = Chunk.GetColumnHeights(x >> Chunk.SizeShift, z >> Chunk.SizeShift)
            [((x & Chunk.SizeMask) << Chunk.SizeShift) | (z & Chunk.SizeMask)];
        Assert.True(baseY == height + 1,
            $"{what} probe ({x}, {z}): baseY {baseY} != column height {height} + 1 - the cache " +
            "factory no longer derives the base from the key the way the caller did");
        Assert.True(trunkBaseY == baseY,
            $"{what} probe ({x}, {z}): shape.TrunkBaseY {trunkBaseY} != baseY {baseY}");
    }

    /// <summary>Folds every generated byte this gate covers into one FNV-1a 64.</summary>
    private static ulong FoldProbes(TreeProbe tree, CactusProbe cactus, TreeGenerator.CanopyColumns canopy)
    {
        var h = Fnv.Init;

        h = Fnv.Int(h, tree.BaseY);
        h = Fnv.Int(h, tree.Shape.TrunkX);
        h = Fnv.Int(h, tree.Shape.TrunkBaseY);
        h = Fnv.Int(h, tree.Shape.TrunkZ);
        foreach (var (x, y, z, id) in tree.Shape.Wood)
        {
            h = Fnv.Int(h, x);
            h = Fnv.Int(h, y);
            h = Fnv.Int(h, z);
            h = Fnv.Byte(h, id);
        }

        foreach (var (x, y, z, r) in tree.Shape.LeafBlobs)
        {
            h = Fnv.Int(h, x);
            h = Fnv.Int(h, y);
            h = Fnv.Int(h, z);
            h = Fnv.Int(h, r);
        }

        h = Fnv.Byte(h, tree.Shape.LeafBlock);

        h = Fnv.Int(h, cactus.BaseY);
        h = Fnv.Int(h, cactus.Shape.TrunkX);
        h = Fnv.Int(h, cactus.Shape.TrunkBaseY);
        h = Fnv.Int(h, cactus.Shape.TrunkZ);
        foreach (var (x, y, z, id) in cactus.Shape.Wood)
        {
            h = Fnv.Int(h, x);
            h = Fnv.Int(h, y);
            h = Fnv.Int(h, z);
            h = Fnv.Byte(h, id);
        }

        foreach (var top in canopy.Tops) h = Fnv.Int(h, top);
        foreach (var leaf in canopy.LeafBlocks) h = Fnv.Byte(h, leaf);

        return h;
    }

    private static void AssertShapesIdentical(TreeProbe before, TreeProbe after)
    {
        Assert.True(before.BaseY == after.BaseY,
            $"tree probe baseY changed across eviction: {before.BaseY} -> {after.BaseY}");
        AssertWoodIdentical("tree", before.Shape.Wood, after.Shape.Wood);
        Assert.True(before.Shape.LeafBlobs.Count == after.Shape.LeafBlobs.Count,
            $"tree probe leaf blob count changed across eviction: {before.Shape.LeafBlobs.Count} " +
            $"-> {after.Shape.LeafBlobs.Count}");
        for (var i = 0; i < before.Shape.LeafBlobs.Count; i++)
            Assert.True(before.Shape.LeafBlobs[i] == after.Shape.LeafBlobs[i],
                $"tree probe LeafBlobs[{i}] changed across eviction: {before.Shape.LeafBlobs[i]} " +
                $"-> {after.Shape.LeafBlobs[i]}");
        Assert.True(before.Shape.LeafBlock == after.Shape.LeafBlock,
            $"tree probe LeafBlock changed across eviction: {before.Shape.LeafBlock} -> " +
            $"{after.Shape.LeafBlock}");
        AssertTrunkOriginIdentical("tree", before.Shape.TrunkX, before.Shape.TrunkZ,
            after.Shape.TrunkX, after.Shape.TrunkZ);
    }

    private static void AssertStemsIdentical(CactusProbe before, CactusProbe after)
    {
        Assert.True(before.BaseY == after.BaseY,
            $"cactus probe baseY changed across eviction: {before.BaseY} -> {after.BaseY}");
        AssertWoodIdentical("cactus", before.Shape.Wood, after.Shape.Wood);
        AssertTrunkOriginIdentical("cactus", before.Shape.TrunkX, before.Shape.TrunkZ,
            after.Shape.TrunkX, after.Shape.TrunkZ);
    }

    private static void AssertWoodIdentical(string what, List<(int X, int Y, int Z, byte Id)> before,
        List<(int X, int Y, int Z, byte Id)> after)
    {
        Assert.True(before.Count == after.Count,
            $"{what} probe wood cell count changed across eviction: {before.Count} -> {after.Count}");
        for (var i = 0; i < before.Count; i++)
            Assert.True(before[i] == after[i],
                $"{what} probe Wood[{i}] changed across eviction: {before[i]} -> {after[i]}");
    }

    private static void AssertTrunkOriginIdentical(string what, int bx, int bz, int ax, int az)
    {
        Assert.True(bx == ax, $"{what} probe TrunkX changed across eviction: {bx} -> {ax}");
        Assert.True(bz == az, $"{what} probe TrunkZ changed across eviction: {bz} -> {az}");
    }

    private static void AssertCanopyIdentical(TreeGenerator.CanopyColumns before, TreeGenerator.CanopyColumns after)
    {
        Assert.True(before.Tops.Length == after.Tops.Length,
            $"canopy Tops length changed across eviction: {before.Tops.Length} -> {after.Tops.Length}");
        for (var i = 0; i < before.Tops.Length; i++)
            Assert.True(before.Tops[i] == after.Tops[i],
                $"canopy Tops[{i}] changed across eviction: {before.Tops[i]} -> {after.Tops[i]}");
        Assert.True(before.LeafBlocks.Length == after.LeafBlocks.Length,
            $"canopy LeafBlocks length changed across eviction: {before.LeafBlocks.Length} -> " +
            $"{after.LeafBlocks.Length}");
        for (var i = 0; i < before.LeafBlocks.Length; i++)
            Assert.True(before.LeafBlocks[i] == after.LeafBlocks[i],
                $"canopy LeafBlocks[{i}] changed across eviction: {before.LeafBlocks[i]} -> " +
                $"{after.LeafBlocks[i]}");
    }

    /// <summary>
    /// Walks <paramref name="count"/> linear indices on <see cref="SweepThreads"/> dedicated
    /// threads and returns the wall clock it took. Conventions from <c>LodBuildPoolTests</c> and
    /// <c>ColumnCacheEvictionTests.RunSweep</c>: a fixed <see cref="Thread"/> array rather than the
    /// ThreadPool (which would silently serialise the stripes or inject threads of its own),
    /// background and named, disjoint contiguous stripes, failures funnelled into a
    /// <see cref="ConcurrentBag{T}"/> because an xUnit assertion thrown on a worker thread is
    /// swallowed and would let this fact pass silently, and <c>Join()</c> on every thread before
    /// anything is asserted.
    /// </summary>
    private TimeSpan RunSweep(string name, int count, Action<int, ConcurrentBag<string>> body)
    {
        var failures = new ConcurrentBag<string>();
        var threads = new Thread[SweepThreads];
        var per = (count + SweepThreads - 1) / SweepThreads;
        var sw = Stopwatch.StartNew();

        for (var t = 0; t < SweepThreads; t++)
        {
            var from = t * per;
            var to = Math.Min(count, from + per);
            threads[t] = new Thread(() =>
            {
                try
                {
                    for (var i = from; i < to; i++)
                        body(i, failures);
                }
                catch (Exception e)
                {
                    failures.Add($"{name} stripe [{from}, {to}) threw: {e}");
                }
            })
            {
                IsBackground = true,
                Name = $"WorldgenShapeSweep-{name}-{t}",
            };
            threads[t].Start();
        }

        foreach (var thread in threads) thread.Join();
        sw.Stop();
        Assert.Empty(failures);
        _out.WriteLine($"[sweep] {name} {count} columns on {SweepThreads} threads in " +
                       $"{sw.Elapsed.TotalMilliseconds:F0}ms");
        return sw.Elapsed;
    }

    private static class Fnv
    {
        internal const ulong Init = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        internal static ulong Byte(ulong h, byte b) => unchecked((h ^ b) * Prime);

        internal static ulong Int(ulong h, int v)
        {
            for (var i = 0; i < 4; i++)
                h = Byte(h, (byte)(v >> (i * 8)));
            return h;
        }
    }
}

/// <summary>
/// Serialises <see cref="WorldgenShapeCacheEvictionTests"/> against <c>WorldgenTests</c>. The
/// eviction gate rotates <c>TreeGenerator.ShapeCache</c> for seconds at a time, and
/// <c>WorldgenTests.TrunkShapeIsCachedAcrossChunks</c> asserts record-struct equality across two
/// adjacent <c>TryGetTrunk</c> calls - which compares the wood/blob lists BY REFERENCE, so an
/// eviction landing between those two calls falsifies it. xUnit runs test classes in parallel by
/// default (there is no <c>xunit.runner.json</c> here), so without this collection the two would
/// overlap and produce a rare, confusing flake that looks like a D6 bug. Nothing else in the suite
/// depends on shape-cache instance identity.
/// </summary>
[CollectionDefinition("Worldgen shape cache")]
public sealed class WorldgenShapeCacheCollection;
