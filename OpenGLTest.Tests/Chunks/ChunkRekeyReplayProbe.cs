using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using silkgltriangle.Content;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// THE CROSS-TREE OUTPUT-PRESERVATION GATE for item D1, which repacks the chunk-side dictionaries
/// (<c>ChunkManager.Chunks</c>, <c>_dirtyChunks</c>, <c>_queuedChunkPositions</c>,
/// <c>_generatingMeshes</c> and <c>_playerPlacedByChunk</c>) from <see cref="Vector3"/> keys onto a
/// packed <c>long</c>, the way <c>FluidManager</c> already does.
///
/// <para><b>Why a replay and not a transcription.</b> D1 is a pure rekeying: the same chunks must
/// load, unload and mesh at the same times, and the only honest way to show that is to drive the
/// REAL <see cref="ChunkManager"/> through a REAL load / unload / neighbor-resolution sequence on
/// both trees and compare the resulting state to the bit. Nothing here re-implements production, so
/// nothing here can re-implement it wrongly: the two trees either print the same hash line or they
/// do not. This file is written and its hashes recorded BEFORE the first production edit, so it is
/// a per-step tripwire for every later step rather than a post-hoc gate.</para>
///
/// <para><b>It must compile and run UNCHANGED on the pristine export</b>, which is the whole point,
/// so it names only API that exists on BOTH trees: <c>ChunkManager.Chunks</c> (indexer,
/// <c>ContainsKey</c>, <c>TryGetValue</c>, <c>Count</c>, <c>Values</c>),
/// <see cref="ChunkManager.Update"/>, <see cref="ChunkManager.NeedsChunk"/>,
/// <see cref="ChunkManager.MarkDirty"/>, the placement accessors, the internal
/// <c>DirtyCount</c>/<c>GeneratingMeshCount</c>/<c>PendingMeshWorkCount</c>/<c>ActiveMeshRunnerCount</c>
/// counters, <c>Chunk.GenerateMeshData</c>, <c>Chunk.IsMeshed</c>, <c>Camera.TestTeleport</c> and the
/// headless <c>new Chunk(null!, pos, generateBlockData: false)</c> harness <c>ChunkManagerTests</c>
/// and <c>MeshTestWorld</c> already use. It deliberately names NOTHING the D1 round introduces, and
/// it never <c>foreach</c>es over <c>Chunks</c> itself - it enumerates <c>Chunks.Values</c> and reads
/// <c>chunk.Position</c>, because the two trees' enumerators are not the same type.</para>
///
/// <para><b>Why every survivor list is sorted before it is folded.</b> Rekeying a hash table changes
/// its bucket layout and therefore its enumeration order. That is inherent to D1 and is not a
/// defect: every consumer of that order in the engine is order-insensitive in its converged result.
/// A gate that folded enumeration order would therefore fail on a correct change, so every list
/// below is sorted into a total order first and the folds are order-independent by construction.
/// </para>
///
/// <para><b>Determinism.</b> Both facts are exact and machine-independent. Registration order and
/// the scripted cell lists come from a probe-local SplitMix64 seeded by fixed literals - not
/// <c>System.Random</c>, whose sequence is a runtime implementation detail, and not the production
/// SplitMix64, because a probe that seeds itself off production stops being a fixed workload the
/// moment production retunes. No wall clock is read anywhere in this file and no assertion anywhere
/// in it is a timing.</para>
/// </summary>
public sealed class ChunkRekeyReplayProbe
{
    private readonly ITestOutputHelper _output;

    public ChunkRekeyReplayProbe(ITestOutputHelper output)
    {
        _output = output;
        TestContent.EnsureLoaded();
    }

    // --- FNV-1a 64, the same fold ColumnCacheEvictionTests / PlayerPlacedUnloadReplayProbe use:
    //     order-free only if the input is sorted first, which is why every list below is sorted
    //     before it is folded.
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
    /// A probe-local SplitMix64, so the scripted lists are a fixed workload rather than whatever a
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

    /// <summary>Sorts a cell list into a total order so a hash over it is order-independent.</summary>
    private static void SortCells(List<(int X, int Y, int Z)> cells) =>
        cells.Sort(static (a, b) =>
        {
            var byX = a.X.CompareTo(b.X);
            if (byX != 0) return byX;
            var byY = a.Y.CompareTo(b.Y);
            return byY != 0 ? byY : a.Z.CompareTo(b.Z);
        });

    /// <summary>
    /// Floors rather than truncates. Every chunk position reaching these dictionaries is integral,
    /// so the two agree here - but flooring is the convention <c>VoxelCoords.ChunkPosOf</c> keeps
    /// and a probe should not be the one place that truncates.
    /// </summary>
    private static (int X, int Y, int Z) ToCell(Vector3 v) =>
        ((int)MathF.Floor(v.X), (int)MathF.Floor(v.Y), (int)MathF.Floor(v.Z));

    /// <summary>Fisher-Yates over the fixed generator, so registration order is scripted.</summary>
    private static void Shuffle<T>(List<T> items, ref ulong state)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = (int)(NextRandom(ref state) % (ulong)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    // ---------------------------------------------------------------------------------------
    // FACT 1 - the authority, entirely on the synchronous path.
    // ---------------------------------------------------------------------------------------

    /// <summary>Seeds. Fixed literals; never derived from anything.</summary>
    private const ulong RegistrationSeed = 0xD1D1D1D15EEDUL;

    private const ulong ProbeSeed = 0xD1D10B5E0BE5UL;

    /// <summary>
    /// The X and Z chunk coordinates of the registered grid. They straddle the unload radius
    /// (<c>WorldGenerationConfig.ChunkRadius</c> = 16, tested as a squared distance) from both sides
    /// and on both signs: 0 and +-1 stay loaded throughout, +-8 survive the first pass and leave on
    /// the second, +-17 sit just outside the radius on the first, and +-24 are far outside it.
    /// Pinned literals - a gate whose geometry is read live from the radius it exercises stops
    /// exercising it the moment the radius moves.
    /// </summary>
    private static readonly int[] GridAxisXZ = [-24, -17, -8, -1, 0, 1, 8, 17, 24];

    /// <summary>The Y chunk coordinates: the unload test is a 3D distance, and a packed key that
    /// drops or mis-shifts Y has to fail here rather than pass by never being asked.</summary>
    private static readonly int[] GridAxisY = [-1, 0, 1];

    /// <summary>Every seventh placement candidate is unmarked again, so the fold covers the unmark
    /// path and not only mark-then-unload.</summary>
    private const int UnmarkEveryNth = 7;

    /// <summary>
    /// THE AUTHORITY FOLD. A real <see cref="ChunkManager"/>, a real chunk grid spanning both signs
    /// and both sides of the unload radius, scripted placements, a real synchronous mesh build that
    /// drives <c>Chunk.TryResolveNeighbors</c>' 26 chunk probes, and two real unload passes driven
    /// by <c>TestTeleport</c> + <see cref="ChunkManager.Update"/> - folded into one FNV-1a 64 hash
    /// after every phase.
    ///
    /// <para><b>Why this fact stays on the SYNCHRONOUS path, and why that is asserted rather than
    /// assumed.</b> Mesh dispatch is asynchronous: <c>StartMeshGeneration</c> enqueues and
    /// <c>ThreadPool.UnsafeQueueUserWorkItem</c>s a runner, and <c>BuildMeshFor</c> removes its
    /// <c>_generatingMeshes</c> entry from a pool thread in a <c>finally</c>, re-adding to
    /// <c>_dirtyChunks</c> when a face neighbor vanished mid-build. So <c>DirtyCount</c> and
    /// <c>GeneratingMeshCount</c> are NOT deterministic at an arbitrary fold point, and this fact
    /// never folds them as data. It registers its chunks DIRECTLY through the <c>Chunks</c> indexer,
    /// so <c>_dirtyChunks</c> stays empty and <c>ProcessDirtyChunks</c> returns at its
    /// <c>IsEmpty</c> guard having dispatched nothing, and it asserts all three work counters are
    /// zero after every <c>Update</c> as a guard that the fact really stayed there. The asynchronous
    /// path is covered separately, at convergence, by
    /// <see cref="TheDirtySetConvergesToTheSameMeshedChunks"/>.</para>
    ///
    /// <para><b>What each fold takes:</b> the SORTED surviving chunk positions (read from
    /// <c>Chunks.Values</c> via <c>chunk.Position</c>, never from an enumerator over the dictionary
    /// itself), <c>Chunks.Count</c>, <c>LoadedChunkCount</c>, <c>ChunkRemovalVersion</c>,
    /// <c>PlayerPlacedChunkCount</c>, the <see cref="ChunkManager.NeedsChunk"/> answer vector over a
    /// fixed probe list, and the <c>IsPlayerPlaced</c> answer vector over the scripted cells.
    /// <c>MeshVersion</c> is deliberately NOT folded: it counts events rather than state, and
    /// nothing in this round's argument rests on it.</para>
    ///
    /// <para><b>The non-vacuity assertions are the point.</b> A gate that compares two hashes passes
    /// just as happily when nothing survived or when everything did - both are order-independent
    /// constants that reproduce perfectly on both trees. So each pass asserts that chunks actually
    /// left and that chunks actually stayed, the survivor count is asserted strictly between zero
    /// and the registered count, and <c>ChunkRemovalVersion</c> is asserted to have moved exactly on
    /// the passes that removed something.</para>
    ///
    /// <para><b>The pass structure.</b> The first <c>Update</c> after construction always scans,
    /// because <c>_lastUnloadFoci</c> starts null; a second <c>Update</c> from the same camera
    /// chunk would not rescan until the camera crosses a chunk border, so pass two teleports. Pass
    /// 2's camera chunk is (9,0,9) rather than (8,0,8) deliberately: from (8,0,8) the chunk at
    /// (-8,0,8) sits at squared distance exactly 256, i.e. exactly on the inclusive boundary, and a
    /// gate should not balance on a tie.</para>
    /// </summary>
    [Fact]
    public void ChunkManagerStateSurvivesALoadUnloadReplay()
    {
        var manager = new ChunkManager(null!);

        // --- the registered grid, in scripted (shuffled) order --------------------------------
        var registered = new List<Vector3>();
        foreach (var cx in GridAxisXZ)
        foreach (var cy in GridAxisY)
        foreach (var cz in GridAxisXZ)
            registered.Add(new Vector3(cx, cy, cz));

        var registeredSet = new HashSet<Vector3>(registered);
        var registrationOrder = new List<Vector3>(registered);
        var regState = RegistrationSeed;
        Shuffle(registrationOrder, ref regState);

        foreach (var pos in registrationOrder)
            manager.Chunks[pos] = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };

        Assert.Equal(registered.Count, manager.Chunks.Count);

        // --- the NeedsChunk probe list: half registered, half never registered ----------------
        var sortedRegistered = new List<(int X, int Y, int Z)>();
        foreach (var pos in registered) sortedRegistered.Add(ToCell(pos));
        SortCells(sortedRegistered);

        var needsProbes = new List<Vector3>();
        for (var i = 0; needsProbes.Count < 32 && i < sortedRegistered.Count; i += 7)
            needsProbes.Add(new Vector3(sortedRegistered[i].X, sortedRegistered[i].Y, sortedRegistered[i].Z));
        Assert.Equal(32, needsProbes.Count);

        var probeState = ProbeSeed;
        var probeSeen = new HashSet<Vector3>(needsProbes);
        while (needsProbes.Count < 64)
        {
            var r = NextRandom(ref probeState);
            var candidate = new Vector3(
                (int)(r % 61) - 30,
                (int)((r >> 8) % 7) - 3,
                (int)((r >> 16) % 61) - 30);
            if (registeredSet.Contains(candidate) || !probeSeen.Add(candidate)) continue;
            needsProbes.Add(candidate);
        }

        // Sorted, so the answer vector is folded in a fixed order on both trees.
        var needsCells = new List<(int X, int Y, int Z)>();
        foreach (var p in needsProbes) needsCells.Add(ToCell(p));
        SortCells(needsCells);
        needsProbes.Clear();
        foreach (var c in needsCells) needsProbes.Add(new Vector3(c.X, c.Y, c.Z));
        Assert.Equal(64, needsProbes.Count);

        // --- the scripted placement cells -----------------------------------------------------
        // Both inclusive corners of every third registered chunk, plus pinned literals whose block
        // coordinates FLOOR to a different chunk than they would truncate to, plus cells in chunks
        // the manager never hears about (nothing can ever unload those, so they are the half of the
        // survivor set that proves the purge is scoped rather than global).
        var placementSeen = new HashSet<(int X, int Y, int Z)>();
        var placements = new List<(int X, int Y, int Z)>();

        void AddCell((int X, int Y, int Z) cell)
        {
            if (placementSeen.Add(cell)) placements.Add(cell);
        }

        for (var i = 0; i < sortedRegistered.Count; i += 3)
        {
            var (cx, cy, cz) = sortedRegistered[i];
            AddCell((cx * Chunk.Size, cy * Chunk.Size, cz * Chunk.Size));
            AddCell((cx * Chunk.Size + Chunk.Size - 1, cy * Chunk.Size + Chunk.Size - 1,
                cz * Chunk.Size + Chunk.Size - 1));
        }

        // Floor-versus-truncate traps: (int)(-1 / 16) is 0, floor(-1 / 16) is -1.
        AddCell((-1, -1, -1));
        AddCell((-16, -16, -16));
        AddCell((-17, -1, -17));
        AddCell((-1, 0, -1));
        AddCell((15, -1, 15));

        // Never-registered chunks, well outside the grid.
        AddCell((100 * Chunk.Size + 3, 0, 100 * Chunk.Size + 3));
        AddCell((-100 * Chunk.Size + 3, 0, -100 * Chunk.Size + 3));

        SortCells(placements);

        foreach (var (x, y, z) in placements) manager.MarkPlayerPlaced(x, y, z);

        var unmarked = 0;
        for (var i = 0; i < placements.Count; i += UnmarkEveryNth)
        {
            var (x, y, z) = placements[i];
            manager.UnmarkPlayerPlaced(x, y, z);
            unmarked++;
        }

        Assert.True(unmarked > 0, "nothing was unmarked, so the unmark path is not covered");

        // --- folding ---------------------------------------------------------------------------
        var hash = FnvOffsetBasis;

        ulong FoldState(ulong running, int phase)
        {
            running = FoldInt(running, phase);

            var survivors = new List<(int X, int Y, int Z)>();
            foreach (var chunk in manager.Chunks.Values) survivors.Add(ToCell(chunk.Position));
            SortCells(survivors);
            running = FoldInt(running, survivors.Count);
            foreach (var cell in survivors) running = FoldCell(running, cell);

            running = FoldInt(running, manager.Chunks.Count);
            running = FoldInt(running, manager.LoadedChunkCount);
            running = FoldInt(running, manager.ChunkRemovalVersion);
            running = FoldInt(running, manager.PlayerPlacedChunkCount);

            foreach (var p in needsProbes) running = FoldInt(running, manager.NeedsChunk(p) ? 1 : 0);
            foreach (var (x, y, z) in placements)
                running = FoldInt(running, manager.IsPlayerPlaced(x, y, z) ? 1 : 0);

            return running;
        }

        void AssertStayedSynchronous(string where) =>
            Assert.True(
                manager.DirtyCount == 0 && manager.GeneratingMeshCount == 0 &&
                manager.PendingMeshWorkCount == 0,
                $"{where}: the fact left the synchronous path - dirty={manager.DirtyCount} " +
                $"generating={manager.GeneratingMeshCount} pending={manager.PendingMeshWorkCount}");

        // PHASE 1 - registration. PHASE 2 - the scripted placements, marked and partly unmarked.
        AssertStayedSynchronous("phase 1");
        hash = FoldState(hash, 1);
        hash = FoldState(hash, 2);

        // PHASE 3 - the NeedsChunk answer vector before anything unloads. It is folded by FoldState,
        // so this phase exists to pin the answers at a point in the replay where every registered
        // position answers false, rather than only at the end where the unloaded ones answer true.
        hash = FoldState(hash, 3);

        // PHASE 4 - a real synchronous mesh build on the test thread, the pattern MeshingTests uses.
        // This is what exercises Chunk.TryResolveNeighbors' 26 chunk probes deterministically: the
        // centre chunk has all six face neighbors registered and must build, while (17,0,0) is
        // missing its (18,0,0) neighbor and must abandon the build and return null.
        var stone = ContentRegistry.RequireBlock("stone").Id;
        var meshedChunk = manager.Chunks[Vector3.Zero];
        meshedChunk.SetBlockRaw(8, 8, 8, stone, 0);
        meshedChunk.SetBlockRaw(9, 8, 8, stone, 0);
        meshedChunk.SetBlockRaw(8, 9, 8, stone, 0);
        var builtMesh = meshedChunk.GenerateMeshData(manager);
        Assert.NotNull(builtMesh);
        Assert.True(builtMesh.Vertices.Count > 0,
            "the meshed chunk produced nothing, so TryResolveNeighbors' probes are unmeasured");

        var rimChunk = manager.Chunks[new Vector3(17, 0, 0)];
        var rimMesh = rimChunk.GenerateMeshData(manager);
        Assert.Null(rimMesh);

        hash = FoldInt(hash, builtMesh.Vertices.Count);
        hash = FoldInt(hash, builtMesh.Indices.Count);
        hash = FoldInt(hash, rimMesh == null ? -1 : rimMesh.Vertices.Count);
        hash = FoldState(hash, 4);

        var camera = new Camera(null, null);

        // PHASE 5 - camera chunk (0,0,0). Everything at +-17 or +-24 on an axis is out of range.
        camera.TestTeleport(new Vector3(8f, 8f, 8f), yaw: 0f, pitch: 0f);
        manager.Update(camera);
        AssertStayedSynchronous("phase 5");

        Assert.False(manager.Chunks.ContainsKey(new Vector3(24, 0, 0)),
            "pass 1 did not unload the chunks 24 out, so the gate would be measuring nothing");
        Assert.False(manager.Chunks.ContainsKey(new Vector3(-17, 0, 0)),
            "pass 1 did not unload the negative 17 band, so a sign bug in the key could hide here");
        Assert.True(manager.Chunks.ContainsKey(new Vector3(-8, 0, -8)),
            "pass 1 unloaded a chunk that is inside the radius");
        Assert.True(manager.Chunks.ContainsKey(Vector3.Zero), "pass 1 unloaded the origin chunk");
        Assert.Equal(1, manager.ChunkRemovalVersion);

        var afterPass1 = manager.Chunks.Count;
        Assert.True(afterPass1 > 0 && afterPass1 < registered.Count,
            $"pass 1 left {afterPass1} of {registered.Count} chunks - the replay is vacuous");

        hash = FoldState(hash, 5);

        // PHASE 6 - camera chunk (9,0,9). The whole negative 8 band now falls out; the -1/0/1 band
        // and the +8 band stay.
        camera.TestTeleport(new Vector3(9 * Chunk.Size + 8f, 8f, 9 * Chunk.Size + 8f), yaw: 0f, pitch: 0f);
        manager.Update(camera);
        AssertStayedSynchronous("phase 6");

        Assert.False(manager.Chunks.ContainsKey(new Vector3(-8, 0, -8)),
            "pass 2 did not unload the far negative band, so the second pass measured nothing");
        Assert.True(manager.Chunks.ContainsKey(new Vector3(8, 0, 8)),
            "pass 2 unloaded a chunk that is inside the radius");
        Assert.True(manager.Chunks.ContainsKey(Vector3.Zero),
            "pass 2 unloaded the origin chunk, which is well inside the radius");
        Assert.Equal(2, manager.ChunkRemovalVersion);

        var afterPass2 = manager.Chunks.Count;
        Assert.True(afterPass2 > 0 && afterPass2 < afterPass1,
            $"pass 2 left {afterPass2} of {afterPass1} chunks - the second pass is vacuous");

        hash = FoldState(hash, 6);

        // Neither "every marked placement survived" nor "none did" may pass: both are perfectly
        // reproducible constants, and both would mean the fold stopped looking at anything.
        var survivingPlacements = 0;
        foreach (var (x, y, z) in placements)
            if (manager.IsPlayerPlaced(x, y, z))
                survivingPlacements++;
        Assert.True(survivingPlacements > 0, "no placement survived at all - the fold is vacuous");
        Assert.True(survivingPlacements < placements.Count - unmarked,
            "every marked placement survived - no chunk's placements were purged by an unload");

        // One grep-able line; every field on it is deterministic on any machine and any runtime, so
        // the two trees' lines are compared whole rather than field by field.
        _output.WriteLine(
            $"[d1hash-a] hash=0x{hash:x16} registered={registered.Count} afterPass1={afterPass1} " +
            $"left={afterPass2} placements={placements.Count} unmarked={unmarked} " +
            $"survivingPlacements={survivingPlacements} probes={needsProbes.Count} " +
            $"verts={builtMesh.Vertices.Count} idx={builtMesh.Indices.Count} " +
            $"removalVersion={manager.ChunkRemovalVersion} loaded={manager.LoadedChunkCount} " +
            $"placedChunks={manager.PlayerPlacedChunkCount}");
    }

    // ---------------------------------------------------------------------------------------
    // FACT 2 - the asynchronous dirty path, folded at convergence.
    // ---------------------------------------------------------------------------------------

    private const ulong DirtySeed = 0xD1D1D1475EEDUL;

    /// <summary>The bound on the quiescence spin, in iterations. Asserted to have been reached,
    /// never silently exited: a spin that gives up quietly turns this fact into a hash of whatever
    /// happened to be finished when it stopped looking. The bound is a COUNT rather than a
    /// <c>Stopwatch</c> deadline (which is how <c>MeshSchedulingTests.WaitUntil</c> spells the same
    /// idea) because this file reads no wall clock anywhere; each iteration parks for a millisecond,
    /// so in the worst case the fact fails after roughly fifteen seconds, matching that helper's
    /// timeout.</summary>
    private const int SpinBound = 15000;

    /// <summary>How many consecutive quiet <c>Update</c>s count as converged. More than one,
    /// because the work counters can all read zero while <c>_completedMeshQueue</c> still holds
    /// finished builds: <c>BuildMeshFor</c> enqueues the mesh BEFORE its <c>finally</c> drops the
    /// <c>_generatingMeshes</c> entry, and <c>ApplyCompletedMeshes</c> only uploads
    /// <c>MaxMeshUploadsPerFrame</c> per call and re-queues the rest.</summary>
    private const int StableRoundsRequired = 4;

    /// <summary>
    /// THE ASYNCHRONOUS COMPANION. Registers a small solid grid directly, marks a scripted interior
    /// subset dirty, then spins <see cref="ChunkManager.Update"/> until the manager is quiescent and
    /// folds the CONVERGED state.
    ///
    /// <para><b>Why convergence and not per frame.</b> Per-frame mesh selection is order-dependent
    /// by construction: <c>ProcessDirtyChunks</c> caps its scan at
    /// <c>WorldGenerationConfig.MaxDirtyScanPerFrame</c> entries of a lock-free dictionary
    /// enumeration, so WHICH dirty chunks a frame even considers depends on bucket order, and the
    /// over-budget branch then sorts the candidates with an unstable <c>List.Sort</c> on squared
    /// distance alone, so ties - which are everywhere on a chunk lattice - break by insertion order.
    /// D1 changes bucket layout and therefore both. What D1 must preserve is the CONVERGED result:
    /// nothing is lost, only deferred, because out-of-range and unloaded entries are dropped on
    /// sight and missing-neighbor ones are re-added by <c>QueueChunk</c> when the neighbor
    /// arrives. This fact therefore folds the set of chunks that ended up meshed, sorted, which is
    /// order-independent.</para>
    ///
    /// <para>The grid is deliberately all air: <c>Chunk.UpdateMesh</c> throws on a non-empty mesh
    /// without a geometry pool, and this fact drives the real upload path with the headless
    /// <c>null</c> pools the rest of the suite uses. The dirtied subset is exactly the chunks with
    /// all six face neighbors present, so every marked chunk must reach <c>IsMeshed</c> and the rim
    /// must not - and that count is asserted, so the fold cannot pass by meshing nothing.</para>
    /// </summary>
    [Fact]
    public void TheDirtySetConvergesToTheSameMeshedChunks()
    {
        var manager = new ChunkManager(null!);

        var grid = new List<Vector3>();
        for (var cx = -2; cx <= 2; cx++)
        for (var cy = -1; cy <= 1; cy++)
        for (var cz = -2; cz <= 2; cz++)
            grid.Add(new Vector3(cx, cy, cz));

        var order = new List<Vector3>(grid);
        var state = DirtySeed;
        Shuffle(order, ref state);

        foreach (var pos in order)
            manager.Chunks[pos] = new Chunk(null!, pos, generateBlockData: false) { IsBlockDataReady = true };

        Assert.Equal(grid.Count, manager.Chunks.Count);

        // The interior: every chunk whose six face neighbors are all inside the grid. Marked in
        // scripted order, so the dirty set is populated the same way on both trees even though the
        // order it is later enumerated in is not the order it was filled in.
        var toDirty = new List<Vector3>();
        for (var cx = -1; cx <= 1; cx++)
        for (var cz = -1; cz <= 1; cz++)
            toDirty.Add(new Vector3(cx, 0, cz));

        var dirtyOrder = new List<Vector3>(toDirty);
        Shuffle(dirtyOrder, ref state);
        foreach (var pos in dirtyOrder) manager.MarkDirty(pos);
        Assert.Equal(toDirty.Count, manager.DirtyCount);

        var camera = new Camera(null, null);
        camera.TestTeleport(new Vector3(8f, 8f, 8f), yaw: 0f, pitch: 0f);

        var iterations = 0;
        var stable = 0;
        var lastMeshed = -1;
        while (iterations < SpinBound)
        {
            iterations++;
            manager.Update(camera);

            var meshedNow = CountMeshed(manager);
            var quiet = manager.DirtyCount == 0 && manager.GeneratingMeshCount == 0 &&
                        manager.PendingMeshWorkCount == 0 && manager.ActiveMeshRunnerCount == 0;

            stable = quiet && meshedNow == lastMeshed ? stable + 1 : 0;
            lastMeshed = meshedNow;
            if (stable >= StableRoundsRequired) break;

            // Sleep(1), not Yield(). The mesh runners are thread-pool work items, and when the rest
            // of the suite is running in parallel the pool is already saturated: a Yield-spin on the
            // test thread starves the very pool this fact is waiting on (measured: runners claimed,
            // nine builds queued, no progress at all across a two-thousand-iteration yield spin).
            // Parking the test thread is what MeshSchedulingTests.WaitUntil does, for this reason.
            Thread.Sleep(1);
        }

        Assert.True(stable >= StableRoundsRequired,
            $"the dirty set never converged within {SpinBound} updates - iterations={iterations} " +
            $"dirty={manager.DirtyCount} generating={manager.GeneratingMeshCount} " +
            $"pending={manager.PendingMeshWorkCount} runners={manager.ActiveMeshRunnerCount} " +
            $"meshed={lastMeshed}");

        var meshedPositions = new List<(int X, int Y, int Z)>();
        foreach (var chunk in manager.Chunks.Values)
            if (chunk.IsMeshed)
                meshedPositions.Add(ToCell(chunk.Position));
        SortCells(meshedPositions);

        // Non-vacuity, both ways: the marked interior meshed, and the rim did not.
        Assert.Equal(toDirty.Count, meshedPositions.Count);
        Assert.True(meshedPositions.Count < manager.Chunks.Count,
            "every chunk meshed, including the rim that has no outer neighbor - the fold is vacuous");

        var hash = FnvOffsetBasis;
        hash = FoldInt(hash, meshedPositions.Count);
        foreach (var cell in meshedPositions) hash = FoldCell(hash, cell);
        hash = FoldInt(hash, manager.Chunks.Count);
        hash = FoldInt(hash, manager.LoadedChunkCount);
        hash = FoldInt(hash, manager.DirtyCount);
        hash = FoldInt(hash, manager.GeneratingMeshCount);

        _output.WriteLine(
            $"[d1hash-b] hash=0x{hash:x16} meshed={meshedPositions.Count} " +
            $"chunks={manager.Chunks.Count} loaded={manager.LoadedChunkCount} " +
            $"dirty={manager.DirtyCount} generating={manager.GeneratingMeshCount}");
        // The spin's iteration count is deliberately NOT on that line: it depends on thread-pool
        // scheduling, and the line has to be byte-identical across runs and across both trees.
    }

    private static int CountMeshed(ChunkManager manager)
    {
        var count = 0;
        foreach (var chunk in manager.Chunks.Values)
            if (chunk.IsMeshed)
                count++;
        return count;
    }
}
