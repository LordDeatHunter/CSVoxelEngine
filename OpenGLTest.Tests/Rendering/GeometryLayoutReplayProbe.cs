using System;
using System.Collections.Generic;
using VoxelEngine.Lod;
using VoxelEngine.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace VoxelEngine.Tests.Rendering;

/// <summary>
/// THE CROSS-TREE OUTPUT-PRESERVATION GATE for item D5, which prices the geometry free list's
/// linear scan and may replace it (branch (a): an offset-ordered, max-augmented structure) or fuse
/// an LOD region's shell and sheet into one paired pool allocation (branch (b)) — or, on the
/// measurement, land nothing at all (branch (c)).
///
/// <para><b>Why a replay and not a transcription.</b> Every candidate change to
/// <see cref="GeometryAllocator"/> is output-preserving by construction: the same geometry reaches
/// the GPU, only its placement in the shared buffer may move. The honest way to show that is to
/// drive the REAL allocator through a REAL paired stream-in / churn / stream-out sequence on both
/// trees and compare the resulting layout to the bit, and to drive the REAL
/// <see cref="LodRegion.BuildMeshData"/> and compare the drawn geometry it produces. Nothing here
/// re-implements production, so nothing here can re-implement it wrongly: the two trees either
/// print the same hash line or they do not. This file is written and its hashes recorded BEFORE
/// the first production edit of the round, so it is a per-step tripwire for every later step
/// rather than a post-hoc gate.</para>
///
/// <para><b>It must compile and run UNCHANGED on the pristine HEAD export</b>, which is the whole
/// point, so it names only API that exists at HEAD: <see cref="GeometryAllocator"/>'s constructor,
/// <c>TryAllocate</c>, <c>Free</c>, <c>Grow</c>, <c>Capacity</c>, <c>UsedUnits</c>,
/// <c>FreeBlockCount</c> and <c>LargestFreeBlock</c>; <see cref="ChunkGeometryPool.UnitsFor"/> and
/// <see cref="ChunkGeometryPool.LodInitialUnits"/>; <see cref="GeometrySlice"/>;
/// <see cref="LodRegion.BuildMeshData"/>, <see cref="LodMeshData"/> and
/// <see cref="LodRegion.Levels"/>. It names NOTHING the D5 round introduces — not
/// <c>ScanSteps</c>, not <c>PeakFreeBlockCount</c>, not <c>AllocatorCalls</c>, not
/// <c>UploadPair</c>. Any later step that makes this file stop compiling on the export has broken
/// the gate, not fixed the probe.</para>
///
/// <para><b>What the gate can and cannot see, stated here as the pre-registered rule states it.</b>
/// These hashes gate branches (a) and (c). They CANNOT gate branch (b): branch (b) changes only
/// <c>ChunkGeometryPool.Upload</c>/<c>FreePair</c> and <see cref="LodRegion"/>'s constructor,
/// neither of which is on either hash's code path — <see cref="GeometryAllocator"/> is untouched by
/// (b) and <see cref="LodRegion.BuildMeshData"/> is a static pure builder — and the seam (b)
/// changes needs a GL context and cannot be replayed headlessly. Under (b) both lines below are
/// byte-identical BY CONSTRUCTION and a match is not evidence; (b)'s gate is instead the new
/// exact-equality facts over <see cref="GeometrySlice"/>'s derived draw parameters plus the
/// <c>entity-binds</c> pixel check.</para>
///
/// <para><b>What is folded, and what deliberately is not.</b> Fact 1 folds only POLICY-INDEPENDENT
/// state: per live client its <c>(clientId, unitCount)</c>, then <c>UsedUnits</c>, then
/// <c>Capacity</c>. Capacity is folded deliberately — a policy that grows the pool more often is an
/// observable regression even though it moves no pixel. Offsets, <c>FreeBlockCount</c> and
/// <c>LargestFreeBlock</c> are NOT folded into the gate: a different allocation policy may
/// legitimately move all three, and a gate that folded them would fail a correct change. They go on
/// the separate <c>[d5offsets]</c> DIAGNOSTIC line instead, which is reported and never asserted.
/// Fact 2 folds the drawn geometry itself, which no branch may move.</para>
///
/// <para><b>Determinism.</b> Both facts are exact and machine-independent. The workload sizes come
/// from a probe-local SplitMix64 seeded by fixed literals — not <c>System.Random</c>, whose
/// sequence is a runtime implementation detail, and not the production SplitMix64, because a probe
/// that seeds itself off production stops being a fixed workload the moment production retunes. No
/// wall clock is read anywhere in this file and no assertion anywhere in it is a timing.</para>
/// </summary>
public sealed class GeometryLayoutReplayProbe
{
    private readonly ITestOutputHelper _output;

    public GeometryLayoutReplayProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    // --- FNV-1a 64, the same fold ChunkRekeyReplayProbe / ColumnCacheEvictionTests use: order-free
    //     only if the input is sorted first, which is why every list below is sorted before it is
    //     folded.
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong FoldInt(ulong hash, int value)
    {
        var bits = (uint)value;
        for (var i = 0; i < 4; i++)
        {
            hash ^= (bits >> (i * 8)) & 0xFF;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// A probe-local SplitMix64, so the scripted workload is a fixed one rather than whatever a
    /// runtime or a production generator happens to produce today.
    /// </summary>
    private static ulong NextRandom(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // ---------------------------------------------------------------------------------------
    // FACT 1 — the allocator layout replay.
    // ---------------------------------------------------------------------------------------

    /// <summary>Seed. A fixed literal; never derived from anything.</summary>
    private const ulong WorkloadSeed = 0xD5D5A110C5EEDUL;

    /// <summary>The resident LOD ceiling: LodManager generates inside <c>RegionRadius</c> but only
    /// drops past <c>RegionRadius + 1</c>, so 253 offsets x 3 levels = 759 regions can be resident
    /// at once. The same literal <c>GeometryAllocatorTests.ResidentRegions</c> pins.</summary>
    private const int ResidentRegions = 759;

    /// <summary>Per-region combined shell-plus-sheet occupancy, the measured steady-state figure
    /// recorded at <c>ChunkGeometryPool.cs:72</c> ("176-181 units per resident region"). The TOTAL
    /// is drawn from this range and then SPLIT into two sides; drawing each side independently
    /// from it would be twice the measured occupancy and would stop being the workload the pool
    /// actually sees.</summary>
    private const int RegionUnitsMin = 176;

    private const int RegionUnitsMax = 181;

    /// <summary>Both sides floor at 64 units: a shell floors at 1024 top quads = 4096 vertices =
    /// 64 units and a full sea sheet is the same 64 units (ChunkGeometryPool.cs:91-95).</summary>
    private const int SideFloorUnits = 64;

    /// <summary>The clustered-departure pattern: whole contiguous runs of regions leave together,
    /// which is the geographic clustering R10 observed in a real fly — departures are not
    /// scattered, so the list coalesces as it drains.</summary>
    private const int DepartureRunLength = 17;

    private const int DepartureRunStride = 53;

    /// <summary>Every seventh survivor is rebuilt in place (free the pair, immediately re-allocate
    /// the same two sizes) — the free-then-upload order <c>LodManager.ApplyCompletedMeshes</c>
    /// uses.</summary>
    private const int RebuildEveryNth = 7;

    /// <summary>The drain permutation stride, the same shape
    /// <c>GeometryAllocatorTests.AFullStreamInAndOutCycleCoalescesBackToOneBlock</c> uses. 617 is
    /// prime, so it is coprime with any client count that is not a multiple of it — asserted
    /// rather than assumed.</summary>
    private const int DrainStride = 617;

    /// <summary>How many regions are streamed back in after the full drain, so the final live set
    /// and the <c>[d5offsets]</c> diagnostic are not empty: one level's worth of the resident
    /// ceiling.</summary>
    private const int RestreamRegions = 253;

    /// <summary>
    /// THE LAYOUT AUTHORITY. A real <see cref="GeometryAllocator"/> sized at exactly
    /// <see cref="ChunkGeometryPool.LodInitialUnits"/>, driven through the paired LOD workload:
    /// a full resident stream-in, a clustered departure phase, an in-place rebuild phase, a
    /// shuffled full drain and a re-stream — folded into one FNV-1a 64 hash after every phase.
    ///
    /// <para><b>Why the allocator is constructed at exactly <c>LodInitialUnits</c>.</b> The
    /// workload's own arithmetic (759 regions at 176-181 units) peaks around 135000 units, well
    /// inside 163840, so NO grow should occur — and <c>Capacity</c> is folded, so if a later
    /// policy does provoke one the hash moves and the round has to explain it. A grow here is
    /// deliberate and visible rather than absorbed.</para>
    ///
    /// <para><b>Allocations are PAIRED</b>, two clients per region (<c>2r</c> the shell,
    /// <c>2r + 1</c> the sheet), because that is how the LOD pool is actually driven: one
    /// <c>LodMeshData</c> is born with both sides and one <c>LodRegion.Dispose</c> frees both. It
    /// is also exactly the traffic branch (b) proposes to halve, so the workload has to contain it
    /// for the gate to mean anything.</para>
    ///
    /// <para><b>The non-vacuity assertions are the point.</b> A gate that compares two hashes
    /// passes just as happily when nothing was ever allocated or when nothing was ever freed —
    /// both are perfectly reproducible constants on both trees. So every phase asserts the live set
    /// really moved: the departure phase must leave strictly between zero and all of the regions
    /// live and must fragment the list past one block, the drain must return the pool to a single
    /// whole free block, and the re-stream must refill it.</para>
    /// </summary>
    [Fact]
    public void AllocatorLayoutSurvivesAPairedStreamInAndOutReplay()
    {
        var allocator = new GeometryAllocator(ChunkGeometryPool.LodInitialUnits);
        var live = new Dictionary<int, (int Offset, int Count)>();
        var state = WorkloadSeed;

        // Region r's two side sizes, drawn once per region from the fixed generator. Kept so the
        // rebuild phase can re-allocate the SAME two sizes a region's rebuild would.
        var shellUnits = new int[ResidentRegions];
        var sheetUnits = new int[ResidentRegions];
        for (var r = 0; r < ResidentRegions; r++)
        {
            var total = RegionUnitsMin + (int)(NextRandom(ref state) % (RegionUnitsMax - RegionUnitsMin + 1));
            // Split so both sides floor at the measured 64-unit floor and sum back to the measured
            // per-region total.
            sheetUnits[r] = SideFloorUnits;
            shellUnits[r] = total - SideFloorUnits;
            Assert.True(shellUnits[r] >= SideFloorUnits);
        }

        var hash = FnvOffsetBasis;

        // --- PHASE 1: the full resident stream-in ---------------------------------------------
        for (var r = 0; r < ResidentRegions; r++)
        {
            Allocate(allocator, live, r * 2, shellUnits[r]);
            Allocate(allocator, live, r * 2 + 1, sheetUnits[r]);
        }

        Assert.Equal(ResidentRegions * 2, live.Count);
        Assert.Equal(ChunkGeometryPool.LodInitialUnits, allocator.Capacity);
        AssertInvariants(allocator, live, "phase 1");
        hash = FoldState(hash, 1, allocator, live);

        // --- PHASE 2: clustered departure ------------------------------------------------------
        var departed = 0;
        for (var start = 0; start < ResidentRegions; start += DepartureRunStride)
        for (var r = start; r < Math.Min(start + DepartureRunLength, ResidentRegions); r++)
        {
            FreeRegion(allocator, live, r);
            departed++;
        }

        Assert.True(departed > 0 && departed < ResidentRegions,
            $"the departure phase moved {departed} of {ResidentRegions} regions — the replay is vacuous");
        Assert.True(allocator.FreeBlockCount > 1,
            "the departure phase left a single free block, so the fragmented scan is not exercised");
        Assert.True(allocator.UsedUnits > 0, "the departure phase emptied the pool");
        AssertInvariants(allocator, live, "phase 2");
        hash = FoldState(hash, 2, allocator, live);

        // --- PHASE 3: in-place rebuilds (free the pair, then immediately re-upload it) ----------
        var rebuilt = 0;
        for (var r = 0; r < ResidentRegions; r += RebuildEveryNth)
        {
            if (!live.ContainsKey(r * 2)) continue;
            FreeRegion(allocator, live, r);
            Allocate(allocator, live, r * 2, shellUnits[r]);
            Allocate(allocator, live, r * 2 + 1, sheetUnits[r]);
            rebuilt++;
        }

        Assert.True(rebuilt > 0, "no region was rebuilt, so the free-then-upload order is not covered");
        AssertInvariants(allocator, live, "phase 3");
        hash = FoldState(hash, 3, allocator, live);

        // --- PHASE 4: the shuffled full drain --------------------------------------------------
        var drainOrder = new List<int>(live.Keys);
        drainOrder.Sort();
        var n = drainOrder.Count;
        Assert.True(n % DrainStride != 0,
            $"{n} live clients is a multiple of the drain stride {DrainStride}, so the permutation " +
            "is not a permutation");

        for (var i = 0; i < n; i++)
        {
            // MID-DRAIN FOLD. A full drain ends with an empty live set, and an empty set folds to
            // the same constant whatever order it was emptied in — so a fold taken only after the
            // drain would be blind to the drain itself, and the negative control (perturbing the
            // permutation stride) would not move the hash. Halfway through, WHICH clients are still
            // live is determined by the permutation, and it is still policy-independent state.
            if (i == n / 2)
            {
                Assert.Equal(n - n / 2, live.Count);
                AssertInvariants(allocator, live, "phase 4 (mid-drain)");
                hash = FoldState(hash, 40, allocator, live);
            }

            var clientId = drainOrder[i * DrainStride % n];
            var allocation = live[clientId];
            allocator.Free(allocation.Offset, allocation.Count);
            live.Remove(clientId);
        }

        Assert.Empty(live);
        Assert.Equal(0, allocator.UsedUnits);
        // The coalescing result R10 proved: whatever order the slices come back in, the pool ends
        // as one whole free block. Asserted here too, because a gate over a pool that quietly
        // stopped coalescing would still compare equal on both trees.
        Assert.Equal(1, allocator.FreeBlockCount);
        Assert.Equal(allocator.Capacity, allocator.LargestFreeBlock);
        AssertInvariants(allocator, live, "phase 4");
        hash = FoldState(hash, 4, allocator, live);

        // --- PHASE 5: the re-stream, so the final live set (and the diagnostic) is not empty ----
        for (var r = 0; r < RestreamRegions; r++)
        {
            var total = RegionUnitsMin + (int)(NextRandom(ref state) % (RegionUnitsMax - RegionUnitsMin + 1));
            Allocate(allocator, live, r * 2, total - SideFloorUnits);
            Allocate(allocator, live, r * 2 + 1, SideFloorUnits);
        }

        Assert.Equal(RestreamRegions * 2, live.Count);
        Assert.True(allocator.UsedUnits > 0, "the re-stream allocated nothing");
        AssertInvariants(allocator, live, "phase 5");
        hash = FoldState(hash, 5, allocator, live);

        // One grep-able line. Every field on it is policy-independent, so the two trees' lines are
        // compared whole rather than field by field.
        _output.WriteLine(
            $"[d5hash-layout] hash=0x{hash:x16} regions={ResidentRegions} departed={departed} " +
            $"rebuilt={rebuilt} drained={n} restreamed={RestreamRegions} live={live.Count} " +
            $"used={allocator.UsedUnits} capacity={allocator.Capacity}");

        // --- the DIAGNOSTIC line: NOT A GATE ---------------------------------------------------
        // This folds PLACEMENT — where each live client landed, and the draw parameters
        // GeometrySlice derives from that placement — plus the two fragmentation gauges. Under
        // branch (a) first fit is preserved, so it should match and a difference is a defect; under
        // branch (b) placement legitimately moves and a difference is the expected signature.
        // It is REPORTED in the round's write-up, never asserted, because asserting it would fail a
        // correct change.
        var offsetHash = FnvOffsetBasis;
        var ids = new List<int>(live.Keys);
        ids.Sort();
        foreach (var clientId in ids)
        {
            var (offset, count) = live[clientId];
            var slice = new GeometrySlice(offset, count, 0, 0);
            offsetHash = FoldInt(offsetHash, clientId);
            offsetHash = FoldInt(offsetHash, offset);
            offsetHash = FoldInt(offsetHash, slice.VertexOffset);
            offsetHash = FoldInt(offsetHash, slice.IndexOffset);
        }

        offsetHash = FoldInt(offsetHash, allocator.FreeBlockCount);
        offsetHash = FoldInt(offsetHash, allocator.LargestFreeBlock);

        _output.WriteLine(
            $"[d5offsets] hash=0x{offsetHash:x16} freeBlocks={allocator.FreeBlockCount} " +
            $"largest={allocator.LargestFreeBlock}");
    }

    /// <summary>Allocates one client, growing the way <c>ChunkGeometryPool.Upload</c> does if the
    /// request does not fit. It should never fire on this workload; if it does, <c>Capacity</c> is
    /// folded, so the hash says so.</summary>
    private static void Allocate(GeometryAllocator allocator, Dictionary<int, (int Offset, int Count)> live,
        int clientId, int units)
    {
        if (!allocator.TryAllocate(units, out var offset))
        {
            allocator.Grow(Math.Max(allocator.Capacity * 2, allocator.Capacity + units));
            Assert.True(allocator.TryAllocate(units, out offset),
                $"client {clientId} did not fit {units} units even after a grow");
        }

        live.Add(clientId, (offset, units));
    }

    /// <summary>Frees a region's two sides together, which is what <c>LodRegion.Dispose</c> does —
    /// holes in the LOD pool are therefore always region-sized, never slice-sized.</summary>
    private static void FreeRegion(GeometryAllocator allocator, Dictionary<int, (int Offset, int Count)> live,
        int region)
    {
        foreach (var clientId in new[] { region * 2, region * 2 + 1 })
        {
            if (!live.TryGetValue(clientId, out var allocation)) continue;
            allocator.Free(allocation.Offset, allocation.Count);
            live.Remove(clientId);
        }
    }

    /// <summary>
    /// The POLICY-INDEPENDENT invariants, checked after every phase: every live range lies inside
    /// <c>[0, Capacity)</c>, no two live ranges overlap, and <c>UsedUnits</c> equals the summed
    /// live counts. These hold under every candidate policy; a policy that breaks one of them is
    /// broken, not different.
    /// </summary>
    private static void AssertInvariants(GeometryAllocator allocator,
        Dictionary<int, (int Offset, int Count)> live, string where)
    {
        var ranges = new List<(int Offset, int Count)>(live.Count);
        var used = 0L;
        foreach (var allocation in live.Values)
        {
            Assert.True(allocation.Offset >= 0 && allocation.Offset + allocation.Count <= allocator.Capacity,
                $"{where}: live range [{allocation.Offset}, {allocation.Offset + allocation.Count}) " +
                $"lies outside [0, {allocator.Capacity})");
            ranges.Add(allocation);
            used += allocation.Count;
        }

        ranges.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        for (var i = 1; i < ranges.Count; i++)
            Assert.True(ranges[i - 1].Offset + ranges[i - 1].Count <= ranges[i].Offset,
                $"{where}: live ranges [{ranges[i - 1].Offset}, " +
                $"{ranges[i - 1].Offset + ranges[i - 1].Count}) and [{ranges[i].Offset}, " +
                $"{ranges[i].Offset + ranges[i].Count}) overlap");

        Assert.Equal(used, allocator.UsedUnits);
    }

    /// <summary>
    /// Folds ONLY policy-independent state, in sorted client-id order: each live client's
    /// <c>(clientId, unitCount)</c>, then the live count, then <c>UsedUnits</c>, then
    /// <c>Capacity</c>. Offsets, <c>FreeBlockCount</c> and <c>LargestFreeBlock</c> are deliberately
    /// absent — see the <c>[d5offsets]</c> diagnostic.
    /// </summary>
    private static ulong FoldState(ulong hash, int phase, GeometryAllocator allocator,
        Dictionary<int, (int Offset, int Count)> live)
    {
        hash = FoldInt(hash, phase);

        var ids = new List<int>(live.Keys);
        ids.Sort();
        hash = FoldInt(hash, ids.Count);
        foreach (var clientId in ids)
        {
            hash = FoldInt(hash, clientId);
            hash = FoldInt(hash, live[clientId].Count);
        }

        hash = FoldInt(hash, allocator.UsedUnits);
        return FoldInt(hash, allocator.Capacity);
    }

    // ---------------------------------------------------------------------------------------
    // FACT 2 — the drawn geometry, which no branch may move.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The far region coordinates. <c>LodRegion.ColumnCaps</c> is process-global, reset only by
    /// <c>WorldSeed.Apply</c> (which the parallel suite never calls), and xUnit runs test classes in parallel, so a near-origin build is contaminated by
    /// whatever other class happened to mine a block — which is exactly why
    /// <c>LodBuildPoolTests.RegionBuildsAreIdenticalWhicheverThreadRunsThem</c> and
    /// <c>LodHeightSamplingTests.SeparableMinMatchesTheNestedWindowAtEveryLevel</c> are
    /// intermittent. So this fact uses the 777 scale <c>LodColumnCapTests</c> uses (its comment
    /// records that the rest of the suite stays near region index ~5) — but on a NEGATIVE Z,
    /// because <c>LodColumnCapTests</c> itself mines at <c>rz = 777</c> and scans <c>rx</c> upward
    /// from 777, and a gate must not share a chunk column with the one class that deliberately
    /// writes caps.
    /// </summary>
    private const int FarRegionX = 777;

    private const int FarRegionZ = -777;

    /// <summary>How many consecutive region offsets the selection scan may try per level before
    /// giving up on finding one of the two kinds.</summary>
    private const int SelectionScanWindow = 24;

    /// <summary>
    /// THE DRAWN-GEOMETRY AUTHORITY. Builds real LOD region meshes at all three
    /// <see cref="LodRegion.Levels"/> and folds what the draw call is derived from:
    /// <c>Vertices.Count</c>, <c>Indices.Count</c>, <c>WaterVertices.Count</c>,
    /// <c>WaterIndices.Count</c>, and the <see cref="ChunkGeometryPool.UnitsFor"/> result for each
    /// of the two sides. <c>firstIndex</c>, <c>baseVertex</c> and the element count all derive from
    /// these, so this is the quantity that must be invariant under EVERY branch of the round —
    /// where a slice OFFSET may legitimately move under a new placement policy, the geometry it
    /// points at may not.
    ///
    /// <para><b>The region set is SCANNED, not fixed.</b> Both kinds of region matter: one whose
    /// <c>WaterVertices</c> list is non-empty (so a paired shell-plus-sheet allocation is exercised)
    /// and one whose list is empty (so the one-sided landlocked case is too). Asserting that a
    /// fixed coordinate has water would be asserting a property of the current terrain generator
    /// blind, so the fact scans consecutive offsets for one of each instead and asserts what it
    /// found. If a level's scan window turns up no water-free region, only the water-bearing half
    /// is folded for that level and the <c>dry</c> field on the output line says so.</para>
    /// </summary>
    [Fact]
    public void LodRegionDrawnGeometryIsIndependentOfPoolPlacement()
    {
        // (level, rx, rz) -> the build, collected then sorted so the fold order is fixed.
        var selected = new List<(int Level, int Rx, int Rz, LodMeshData Data)>();
        var wetLevels = 0;
        var dryLevels = 0;

        for (var level = 0; level < LodRegion.Levels.Length; level++)
        {
            (int Rx, int Rz, LodMeshData Data)? wet = null;
            (int Rx, int Rz, LodMeshData Data)? dry = null;

            for (var d = 0; d < SelectionScanWindow && (wet == null || dry == null); d++)
            {
                var rx = FarRegionX + d;
                var data = LodRegion.BuildMeshData(level, rx, FarRegionZ);
                if (data.WaterVertices.Count > 0) wet ??= (rx, FarRegionZ, data);
                else dry ??= (rx, FarRegionZ, data);
            }

            Assert.True(wet != null,
                $"level {level} found no water-bearing region in {SelectionScanWindow} offsets from " +
                $"({FarRegionX}, {FarRegionZ}) — the paired-allocation case is unmeasured");

            selected.Add((level, wet.Value.Rx, wet.Value.Rz, wet.Value.Data));
            wetLevels++;
            if (dry != null)
            {
                selected.Add((level, dry.Value.Rx, dry.Value.Rz, dry.Value.Data));
                dryLevels++;
            }
        }

        selected.Sort(static (a, b) =>
        {
            var byLevel = a.Level.CompareTo(b.Level);
            if (byLevel != 0) return byLevel;
            var byX = a.Rx.CompareTo(b.Rx);
            return byX != 0 ? byX : a.Rz.CompareTo(b.Rz);
        });

        Assert.Equal(LodRegion.Levels.Length, wetLevels);

        var hash = FnvOffsetBasis;
        hash = FoldInt(hash, selected.Count);

        var totalVertices = 0;
        var totalWaterVertices = 0;
        foreach (var (level, rx, rz, data) in selected)
        {
            // Every region actually produced a shell — a level whose builds came back empty would
            // hash to a perfectly reproducible constant on both trees while measuring nothing.
            Assert.True(data.Vertices.Count > 0,
                $"level {level} region ({rx}, {rz}) built no terrain shell at all");

            hash = FoldInt(hash, level);
            hash = FoldInt(hash, rx);
            hash = FoldInt(hash, rz);
            hash = FoldInt(hash, data.Vertices.Count);
            hash = FoldInt(hash, data.Indices.Count);
            hash = FoldInt(hash, data.WaterVertices.Count);
            hash = FoldInt(hash, data.WaterIndices.Count);
            hash = FoldInt(hash, ChunkGeometryPool.UnitsFor(data.Vertices.Count, data.Indices.Count));
            hash = FoldInt(hash,
                ChunkGeometryPool.UnitsFor(data.WaterVertices.Count, data.WaterIndices.Count));

            totalVertices += data.Vertices.Count;
            totalWaterVertices += data.WaterVertices.Count;
        }

        Assert.True(totalWaterVertices > 0, "no selected region carried water — the fold is vacuous");

        _output.WriteLine(
            $"[d5hash-draw] hash=0x{hash:x16} regions={selected.Count} wetLevels={wetLevels} " +
            $"dryLevels={dryLevels} verts={totalVertices} waterVerts={totalWaterVertices}");
    }
}
