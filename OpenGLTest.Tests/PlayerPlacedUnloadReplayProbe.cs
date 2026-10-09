using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// THE CROSS-TREE GATE AND THE COUNTED MEASUREMENT for item C6, which replaced
/// <c>ChunkManager._playerPlaced</c> - one flat <c>HashSet</c> scanned wholesale by
/// <c>RemoveWhere</c> once per unloaded chunk - with one <c>HashSet</c> bucket per chunk position.
///
/// <para><b>Why there are two parts, and which one is the authority.</b> The obvious way to show
/// that C6 changed nothing observable is to replay the deleted predicate against the new structure
/// and check they agree. That check is worth having, but it CANNOT be the authority: the deleted
/// predicate has to be hand-transcribed into the probe, and if the transcription is subtly wrong
/// then the probe's two models agree with each other AND with the new production code while all
/// three differ from what HEAD actually did - and the gate passes in exactly the case it exists to
/// catch. So PART A drives the REAL <see cref="ChunkManager"/> through a REAL unload sequence and
/// folds the surviving placements into a hash, and that file is run on both trees: the work tree
/// and a pristine <c>git archive HEAD</c> export. Nothing is transcribed, so nothing can be
/// transcribed wrongly; the two trees either agree to the bit or they do not. PART B is the
/// measurement that sits on top, and it says so in its own summary.</para>
///
/// <para><b>Part A must compile and run UNCHANGED on the pristine export</b>, which is what makes
/// the comparison meaningful at all. It therefore touches only API that exists on BOTH trees:
/// <see cref="ChunkManager.MarkPlayerPlaced"/>, <see cref="ChunkManager.UnmarkPlayerPlaced"/>,
/// <see cref="ChunkManager.IsPlayerPlaced"/>, <c>ChunkManager.Chunks</c>,
/// <see cref="ChunkManager.Update"/>, <c>Camera.TestTeleport</c> and the headless
/// <c>new Chunk(null!, pos, generateBlockData: false)</c> harness the rest of
/// <c>ChunkManagerTests</c> already uses. In particular it does NOT reference
/// <c>ChunkManager.PlayerPlacedChunkCount</c>: that property is new in this round, so naming it
/// here would make the file uncompilable on the export and the gate would silently become a
/// one-tree gate. The bucket-count invariants it would pin live in
/// <c>ChunkManagerTests.UnmarkingTheLastCellDropsItsBucket</c> instead, which is the right home
/// for them.</para>
///
/// <para><b>Determinism.</b> Both parts are exact and machine-independent. The scripted placement
/// list comes from a probe-local SplitMix64 seeded by a fixed constant, not from
/// <c>System.Random</c> and not from the production <c>SplitMix64</c> - a probe that seeds itself
/// off production code stops being a fixed workload the moment production retunes, and the whole
/// value of the cross-tree hash is that the two runs marked the identical list. No wall clock is
/// read anywhere in this file and no assertion anywhere in it is a timing.</para>
/// </summary>
public sealed class PlayerPlacedUnloadReplayProbe
{
    private readonly ITestOutputHelper _output;

    public PlayerPlacedUnloadReplayProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    // --- FNV-1a 64, the same fold ColumnCacheEvictionTests / WorldgenShapeCacheEvictionTests use:
    //     order-free only if the input is sorted first, which is why every survivor list below is
    //     sorted before it is folded.
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private static ulong FoldCell(ulong hash, (int X, int Y, int Z) cell)
    {
        hash = FoldInt(hash, cell.X);
        hash = FoldInt(hash, cell.Y);
        return FoldInt(hash, cell.Z);
    }

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
    /// A probe-local SplitMix64, so the scripted placement list is a fixed workload rather than
    /// whatever the production generator happens to produce today. Deliberately NOT
    /// <c>System.Random</c>: its sequence is a runtime implementation detail, and the two trees in
    /// the cross-tree gate have to mark byte-identical lists.
    /// </summary>
    private static ulong NextRandom(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Sorts a cell list into a total order so a hash over it is order-independent.</summary>
    private static void SortCells(List<(int X, int Y, int Z)> cells) =>
        cells.Sort(static (a, b) =>
        {
            var byX = a.X.CompareTo(b.X);
            if (byX != 0) return byX;
            var byY = a.Y.CompareTo(b.Y);
            return byY != 0 ? byY : a.Z.CompareTo(b.Z);
        });

    // ---------------------------------------------------------------------------------------
    // PART A - the authority.
    // ---------------------------------------------------------------------------------------

    /// <summary>The seed for the scripted placement list. Fixed; never derived from anything.</summary>
    private const ulong PlacementSeed = 0xC6C6C6C6_5EEDUL;

    /// <summary>
    /// Chunk coordinates used on every axis of the registered chunk grid. They straddle the unload
    /// radius (<c>WorldGenerationConfig.ChunkRadius</c> = 16, tested as a squared distance) from
    /// both sides and on both signs: +-1 and 0 stay loaded throughout, +-8 survive the first pass
    /// and leave on the second, and +-24 are already out of range on the first. Pinned literals -
    /// a gate whose geometry is read live from the radius it exercises stops exercising it the
    /// moment the radius moves.
    /// </summary>
    private static readonly int[] GridAxis = [-24, -8, -1, 0, 1, 8, 24];

    /// <summary>The Y chunk coordinates of the grid: the unload test is a 3D distance, so a
    /// bucket key that ignores Y has to fail here rather than pass by never being asked.</summary>
    private static readonly int[] GridAxisY = [-1, 0];

    /// <summary>Every seventh candidate is unmarked again before the unload passes, so the gate
    /// covers the unmark path and not only mark-then-unload.</summary>
    private const int UnmarkEveryNth = 7;

    /// <summary>
    /// THE CROSS-TREE OUTPUT-PRESERVATION GATE. A real <see cref="ChunkManager"/>, a real chunk
    /// set spanning both signs and both sides of the unload radius, a scripted placement list, two
    /// real unload passes driven by <c>TestTeleport</c> + <see cref="ChunkManager.Update"/>, and
    /// then an FNV-1a 64 fold over exactly the cells that survived.
    ///
    /// <para><b>Why the survivors are probed rather than enumerated:</b> the placement index has
    /// no public enumerator on either tree (and giving it one to satisfy a test would be the test
    /// changing the thing it measures), so the gate re-asks
    /// <see cref="ChunkManager.IsPlayerPlaced"/> for every candidate it marked. That is also the
    /// stronger check - it goes through the same accessor the engine does.</para>
    ///
    /// <para><b>Why the candidate list has the shape it has.</b> It contains each registered
    /// chunk's two inclusive corners (an off-by-one in the bucket key drops or keeps exactly one
    /// of them), three interior cells drawn from the fixed seed, cells in chunks that were never
    /// registered at all (nothing can ever unload those, so they must survive both passes), and
    /// the every-seventh cells that are unmarked before the passes run.</para>
    ///
    /// <para><b>The non-vacuity assertions are the point.</b> A gate that compares two hashes
    /// passes just as happily when nothing survived or when everything did - both are
    /// order-independent constants that reproduce perfectly on both trees. So the survivor count
    /// is asserted to be strictly between zero and the candidate count, and the registered-chunk
    /// membership is asserted after each pass so the fact cannot pass without unloads having
    /// happened.</para>
    ///
    /// <para><b>The pass structure.</b> The first <c>Update</c> after construction always runs the
    /// scan because <c>_lastUnloadFoci</c> starts null; a second <c>Update</c> from the same
    /// camera chunk would NOT rescan until the camera crosses a chunk border or the 120-frame
    /// fallback fires, so pass two teleports rather than just calling <c>Update</c> again. Pass 2's
    /// camera chunk is (9,0,9) rather than (8,0,8) deliberately: from (8,0,8) the chunk at
    /// (-8,0,8) sits at squared distance exactly 256, i.e. exactly on the inclusive boundary, and a
    /// gate should not balance on a tie.</para>
    /// </summary>
    [Fact]
    public void UnloadPreservesExactlyTheSameSurvivingPlacements()
    {
        var manager = new ChunkManager(null!);

        var registered = new List<Vector3>();
        foreach (var cx in GridAxis)
        foreach (var cy in GridAxisY)
        foreach (var cz in GridAxis)
        {
            var pos = new Vector3(cx, cy, cz);
            var chunk = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };
            manager.Chunks[pos] = chunk;
            registered.Add(pos);
        }

        // Chunks the manager never hears about. Their placements can never be unloaded, so they
        // are the half of the survivor set that proves the purge is scoped rather than global.
        var neverRegistered = new[]
        {
            new Vector3(3, 5, 3), new Vector3(-40, 2, 17), new Vector3(100, 0, 100), new Vector3(-100, 0, -100),
        };

        var seen = new HashSet<(int X, int Y, int Z)>();
        var candidates = new List<(int X, int Y, int Z)>();
        var state = PlacementSeed;

        void AddChunkCells(Vector3 chunkPos)
        {
            var x0 = (int)chunkPos.X * Chunk.Size;
            var y0 = (int)chunkPos.Y * Chunk.Size;
            var z0 = (int)chunkPos.Z * Chunk.Size;

            // Both inclusive corners, then three interior cells from the fixed seed.
            AddCell((x0, y0, z0));
            AddCell((x0 + Chunk.Size - 1, y0 + Chunk.Size - 1, z0 + Chunk.Size - 1));
            for (var i = 0; i < 3; i++)
            {
                var r = NextRandom(ref state);
                AddCell((
                    x0 + (int)(r & 15),
                    y0 + (int)((r >> 8) & 15),
                    z0 + (int)((r >> 16) & 15)));
            }
        }

        void AddCell((int X, int Y, int Z) cell)
        {
            if (seen.Add(cell)) candidates.Add(cell);
        }

        foreach (var pos in registered) AddChunkCells(pos);
        foreach (var pos in neverRegistered) AddChunkCells(pos);

        foreach (var (x, y, z) in candidates) manager.MarkPlayerPlaced(x, y, z);

        var unmarked = 0;
        for (var i = 0; i < candidates.Count; i += UnmarkEveryNth)
        {
            var (x, y, z) = candidates[i];
            manager.UnmarkPlayerPlaced(x, y, z);
            unmarked++;
        }

        var camera = new Camera(null, null);

        // PASS 1 - camera chunk (0,0,0). Everything with a +-24 coordinate is out of range.
        camera.TestTeleport(new Vector3(8f, 8f, 8f), yaw: 0f, pitch: 0f);
        manager.Update(camera);
        Assert.False(manager.Chunks.ContainsKey(new Vector3(24, 0, 0)),
            "pass 1 did not unload the chunks 24 out, so the gate would be measuring nothing");
        Assert.True(manager.Chunks.ContainsKey(new Vector3(-8, 0, -8)),
            "pass 1 unloaded a chunk that is inside the radius");

        // PASS 2 - camera chunk (9,0,9). The +-8 band on the negative side now falls out; the
        // -1/0/1 band and the +8 band stay. Teleporting (rather than calling Update again) is what
        // makes the scan run at all.
        camera.TestTeleport(new Vector3(9 * Chunk.Size + 8f, 8f, 9 * Chunk.Size + 8f), yaw: 0f, pitch: 0f);
        manager.Update(camera);
        Assert.False(manager.Chunks.ContainsKey(new Vector3(-8, 0, -8)),
            "pass 2 did not unload the far negative band, so the second pass measured nothing");
        Assert.True(manager.Chunks.ContainsKey(new Vector3(8, 0, 8)),
            "pass 2 unloaded a chunk that is inside the radius");
        Assert.True(manager.Chunks.ContainsKey(Vector3.Zero),
            "pass 2 unloaded the origin chunk, which is well inside the radius");

        var surviving = new List<(int X, int Y, int Z)>();
        foreach (var (x, y, z) in candidates)
            if (manager.IsPlayerPlaced(x, y, z))
                surviving.Add((x, y, z));

        // Neither "everything survived" nor "nothing survived" may pass: both are perfectly
        // reproducible constants, and both would mean the gate stopped looking at anything.
        Assert.True(surviving.Count > 0, "no placement survived at all - the gate is vacuous");
        Assert.True(surviving.Count < candidates.Count,
            "every placement survived - nothing was unloaded and the gate is vacuous");

        SortCells(surviving);
        var hash = FnvOffsetBasis;
        foreach (var cell in surviving) hash = FoldCell(hash, cell);

        // One grep-able line, and every field on it is deterministic on any machine and any
        // runtime, so the two trees' lines are compared whole rather than field by field.
        _output.WriteLine(
            $"[c6hash] surviving={surviving.Count} hash=0x{hash:x16} " +
            $"candidates={candidates.Count} unmarked={unmarked} " +
            $"chunksRegistered={registered.Count} chunksLeft={manager.Chunks.Count}");
    }

    // ---------------------------------------------------------------------------------------
    // PART B - the counted measurement.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The unload storm this round is sized against: PERF_TODO's recorded perf-fly capture ends a
    /// 60 s window having gone from 17077 loaded chunks to 4991, i.e. 12086 chunks left the world.
    /// Pinned as a literal, with its derivation named, per <c>LodBuildPoolTests.StressWorkers</c>.
    /// </summary>
    private const int StormUnloads = 17077 - 4991;

    /// <summary>
    /// Placement counts swept. There is no "the" placement count - it is entirely workload
    /// dependent (a player who has built nothing has zero; a long-lived world has thousands), so
    /// the table prints a row per count instead of pretending to one number.
    /// </summary>
    private static readonly int[] PlacementCounts = [250, 1000, 4000];

    /// <summary>
    /// TODAY'S SCAN, reimplemented as a reference model: a flat set of cells, and per unloaded
    /// chunk a <c>RemoveWhere</c> over the WHOLE set with the predicate C6 deleted, transcribed
    /// from <c>ChunkManager.UnloadDistantChunks</c> at HEAD 963535c:
    /// <code>
    /// var x0 = (int)pos.X * Chunk.Size;
    /// var y0 = (int)pos.Y * Chunk.Size;
    /// var z0 = (int)pos.Z * Chunk.Size;
    /// _playerPlaced.RemoveWhere(cell =&gt;
    ///     cell.X &gt;= x0 &amp;&amp; cell.X &lt; x0 + Chunk.Size &amp;&amp;
    ///     cell.Y &gt;= y0 &amp;&amp; cell.Y &lt; y0 + Chunk.Size &amp;&amp;
    ///     cell.Z &gt;= z0 &amp;&amp; cell.Z &lt; z0 + Chunk.Size);
    /// </code>
    /// This transcription is EXPLICITLY NOT the round's output-preservation authority - Part A is,
    /// for the reason given in the class summary. It exists so the cost can be counted.
    /// </summary>
    private sealed class FlatScanModel
    {
        private readonly HashSet<(int X, int Y, int Z)> _cells = new();

        public long Examinations;

        public void Mark((int X, int Y, int Z) cell) => _cells.Add(cell);

        public void Unload(Vector3 pos)
        {
            var x0 = (int)pos.X * Chunk.Size;
            var y0 = (int)pos.Y * Chunk.Size;
            var z0 = (int)pos.Z * Chunk.Size;
            _cells.RemoveWhere(cell =>
            {
                Examinations++;
                return cell.X >= x0 && cell.X < x0 + Chunk.Size &&
                       cell.Y >= y0 && cell.Y < y0 + Chunk.Size &&
                       cell.Z >= z0 && cell.Z < z0 + Chunk.Size;
            });
        }

        public List<(int X, int Y, int Z)> Survivors() => new(_cells);
    }

    /// <summary>
    /// C6's structure, modelled the same way: one bucket per chunk position, so an unload is one
    /// dictionary removal and examines no cell at all. The key is derived by flooring, which is
    /// what <c>VoxelCoords.ChunkPosOf</c> does - the deleted code's <c>(int)pos.X</c> truncation
    /// was only ever safe because it read <c>Chunks</c>' own integral keys.
    /// </summary>
    private sealed class BucketModel
    {
        private readonly Dictionary<Vector3, HashSet<(int X, int Y, int Z)>> _byChunk = new();

        public long BucketRemovalCalls;
        public long BucketsActuallyDropped;

        public void Mark((int X, int Y, int Z) cell)
        {
            var key = ChunkPosOf(cell);
            if (!_byChunk.TryGetValue(key, out var cells))
            {
                cells = new HashSet<(int X, int Y, int Z)>();
                _byChunk[key] = cells;
            }

            cells.Add(cell);
        }

        public void Unload(Vector3 pos)
        {
            BucketRemovalCalls++;
            if (_byChunk.Remove(pos)) BucketsActuallyDropped++;
        }

        public List<(int X, int Y, int Z)> Survivors()
        {
            var survivors = new List<(int X, int Y, int Z)>();
            foreach (var bucket in _byChunk.Values) survivors.AddRange(bucket);
            return survivors;
        }

        private static Vector3 ChunkPosOf((int X, int Y, int Z) cell) => new(
            FloorDiv(cell.X, Chunk.Size), FloorDiv(cell.Y, Chunk.Size), FloorDiv(cell.Z, Chunk.Size));

        private static int FloorDiv(int value, int divisor)
        {
            var q = value / divisor;
            return value % divisor < 0 ? q - 1 : q;
        }
    }

    /// <summary>
    /// THE COUNTED MEASUREMENT, in <c>ColumnCacheReplayProbe</c>'s style: it prints through
    /// <see cref="ITestOutputHelper"/>, asserts no timing, reads no wall clock, and produces the
    /// same numbers on every machine and every run. It replays one recorded unload storm
    /// (<see cref="StormUnloads"/> chunks leaving the world) against both structures and prints
    /// how many CELLS the old predicate examined against how many BUCKET REMOVALS the new one
    /// performed.
    ///
    /// <para><b>What the ratio is and is not.</b> It is arithmetic made visible: the old code did
    /// one full-set scan per unloaded chunk, so the work it did is the unload count multiplied by
    /// the live placement count, while the new code does one dictionary removal per unloaded chunk
    /// regardless of how many placements exist. It is NOT a timing and NOT a frame-time saving.
    /// The real placement count is workload dependent - a player who has built nothing pays
    /// nothing under either structure - so no wall-clock number is claimed anywhere in this round
    /// for C6, and the swept rows are there precisely so nobody has to pick one.</para>
    ///
    /// <para>The two structures' surviving sets are asserted equal at the end of every row. That
    /// is a consistency check on the transcription, not the round's output-preservation gate:
    /// Part A is, because it drives production rather than a model.</para>
    /// </summary>
    [Fact]
    public void UnloadScanCostAgainstPlacementCount()
    {
        var unloads = StormChunkPositions();
        Assert.Equal(StormUnloads, unloads.Count);

        _output.WriteLine(
            $"[c6count] storm={StormUnloads} unloaded chunks (perf-fly capture: 17077 -> 4991 loaded) " +
            "- counts only, no wall clock is read anywhere in this fact");
        _output.WriteLine("[c6count] placements | unloads | old cell-examinations | new bucket-removals | ratio");

        foreach (var placements in PlacementCounts)
        {
            var cells = ScriptedPlacements(placements, unloads);
            Assert.Equal(placements, cells.Count);

            var flat = new FlatScanModel();
            var buckets = new BucketModel();
            foreach (var cell in cells)
            {
                flat.Mark(cell);
                buckets.Mark(cell);
            }

            foreach (var pos in unloads)
            {
                flat.Unload(pos);
                buckets.Unload(pos);
            }

            var flatSurvivors = flat.Survivors();
            var bucketSurvivors = buckets.Survivors();
            SortCells(flatSurvivors);
            SortCells(bucketSurvivors);
            Assert.Equal(flatSurvivors, bucketSurvivors);

            // Non-vacuity: the storm has to have taken some placements and left others, or the row
            // is measuring a scan over a set that was empty or untouched.
            Assert.True(flatSurvivors.Count > 0, $"placements={placements}: the storm took everything");
            Assert.True(flatSurvivors.Count < placements, $"placements={placements}: the storm took nothing");

            _output.WriteLine(
                $"[c6count] {placements,10} | {unloads.Count,7} | {flat.Examinations,21} | " +
                $"{buckets.BucketRemovalCalls,19} | {(double)flat.Examinations / buckets.BucketRemovalCalls,8:F1}x  " +
                $"(bucketsActuallyDropped={buckets.BucketsActuallyDropped} survivors={flatSurvivors.Count})");
        }
    }

    /// <summary>
    /// The storm's chunk positions: a row-major scan of a square of chunk columns at three chunk
    /// heights, truncated to exactly <see cref="StormUnloads"/> distinct positions. Positions only,
    /// no <see cref="Chunk"/> objects, which is what keeps this fact in the millisecond range.
    /// </summary>
    private static List<Vector3> StormChunkPositions()
    {
        var positions = new List<Vector3>(StormUnloads);
        for (var cx = -32; positions.Count < StormUnloads; cx++)
        for (var cz = -32; cz < 32 && positions.Count < StormUnloads; cz++)
        for (var cy = -1; cy <= 1 && positions.Count < StormUnloads; cy++)
            positions.Add(new Vector3(cx, cy, cz));

        return positions;
    }

    /// <summary>
    /// The scripted placements for one row: half of them inside chunks the storm will unload,
    /// spread evenly across the whole storm so the old model's set shrinks over the replay exactly
    /// as it would in the game, and half inside chunks the storm never touches, so neither model
    /// can pass by simply emptying. Cell positions come from the same fixed seed as Part A's.
    /// </summary>
    private static List<(int X, int Y, int Z)> ScriptedPlacements(int count, List<Vector3> unloads)
    {
        var seen = new HashSet<(int X, int Y, int Z)>();
        var cells = new List<(int X, int Y, int Z)>(count);
        var state = PlacementSeed ^ (ulong)count;

        while (cells.Count < count)
        {
            var r = NextRandom(ref state);
            var doomed = cells.Count % 2 == 0;
            Vector3 chunkPos;
            if (doomed)
            {
                // Spread across the storm rather than clustered at its head: a placement in the
                // last chunk to leave is examined by every scan before it.
                chunkPos = unloads[(int)(r % (ulong)unloads.Count)];
            }
            else
            {
                // Far outside the storm's square, so nothing can unload these.
                chunkPos = new Vector3(4096 + (int)(r & 255), (int)((r >> 16) & 3), 4096 + (int)((r >> 32) & 255));
            }

            var cell = (
                (int)chunkPos.X * Chunk.Size + (int)((r >> 40) & 15),
                (int)chunkPos.Y * Chunk.Size + (int)((r >> 44) & 15),
                (int)chunkPos.Z * Chunk.Size + (int)((r >> 48) & 15));
            if (seen.Add(cell)) cells.Add(cell);
        }

        return cells;
    }
}
