using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// A MEASUREMENT HARNESS for item D6, the direct analogue of <see cref="ColumnCacheReplayProbe"/>
/// and written to the same rules: it replays fixed key sequences against private cache instances
/// with a factory that does no work (<c>static key =&gt; key</c>), so it generates no terrain,
/// produces the same counts on every machine and every run, and costs about 2 s (measured, Release).
/// It prints through <see cref="ITestOutputHelper"/>, asserts NO timing and compares no wall clock.
///
/// <para><b>Why it runs BEFORE the conversion it informs.</b> D6 proposes moving three worldgen
/// caches - <c>TreeGenerator.ShapeCache</c> (cap 16384), <c>TreeGenerator.CanopyCache</c> (cap
/// 4096) and <c>CactusGenerator.ShapeCache</c> (cap 16384) - off <c>if (Count &gt;= limit)
/// Clear()</c> and onto <see cref="GenerationalCache{TKey,TValue}"/>. PERF_TODO's lesson 4 and
/// <see cref="ColumnCacheReplayProbe"/>'s own doc comment both record that the generational policy
/// CAN lose to a wholesale clear: at G = 2 it missed 7.6-8.3% MORE than the clear on the column
/// cache's mixed sequence. That losing regime is measured, not hypothetical, so this round takes
/// the capture first and lets it decide, rather than converting three caches and asserting in prose
/// that the policy is better. This file compiles and passes against pristine HEAD: it touches only
/// <see cref="GenerationalCache{TKey,TValue}"/> and probe-local models.</para>
///
/// <para><b>PRE-REGISTERED DECISION RULE.</b> Written here in final wording before the first run,
/// and obeyed literally afterwards. For each of the three caches the row that decides is the
/// INTERLEAVED one - a bulk stream alone trips the cap under both policies and a hot stream alone
/// trips it under neither, so only the mixture separates them.
/// <list type="number">
/// <item>CANOPY has one interleaved sequence: read it at G = 4. If generational misses &lt;=
/// wholesale-Clear misses, convert that cache at G = 4 and decide nothing else.</item>
/// <item>The two SHAPE caches have no pinned working-set size (see "Why the shape sweep is
/// synthetic" below), so their rule is read across the WHOLE swept range: convert at G = 4 only if
/// generational misses &lt;= wholesale-Clear misses at EVERY swept distinct-key cardinality at
/// G = 4. One losing cardinality is enough to fire the rule, because nothing in this round rules
/// that cardinality out. A swept cardinality below the 16384 capacity never trips the cap under
/// either policy and therefore ties rather than deciding; those rows stay in the printed table (they
/// are real points of the range) and at least one row per cache must trip the cap or the whole
/// verdict is refused as vacuous.</item>
/// <item>If the rule fires for a cache, its conversion is NOT landed blind. Either (a) price THAT
/// ONE cache's <c>Generations</c> against THIS capture - which is exactly the bar lesson 4 sets, a
/// real capture rather than a blind re-price - taking the G that beats the clear by the largest
/// margin, and beats it at every swept cardinality for a shape cache, and recording the swept table
/// in PERF_TODO as the derivation; or (b) if NO swept G beats the clear, leave that cache on its
/// existing <c>ConcurrentDictionary</c> + <c>Clear()</c> and rewrite its share of the D6 bullet with
/// the finding, on the R3/C5 no-go precedent.</item>
/// <item>No G is moved for any cache whose row does not fire the rule, and nothing below is tuned to
/// make the new policy look better. The <c>[decision]</c> lines this fact prints are the input to
/// the conversion steps and are transcribed into PERF_TODO as they stand.</item>
/// </list></para>
///
/// <para><b>What the first run found, recorded after the rule above was fixed, so the next round
/// does not re-derive it.</b> The rule did not fire for any of the three caches: at G = 4 the
/// generational policy missed the same or fewer times than the wholesale clear on EVERY interleaved
/// row, so all three convert at G = 4 and no <c>Generations</c> value was re-priced. The margins are
/// small and honestly so - canopy -0.08%, tree shape 0.00% / 0.00% / -0.13% / -5.32% / -8.90% across
/// the swept cardinalities, cactus shape 0.00% / 0.00% / -0.65% / -1.44% / -3.35% - and the shape
/// rows show exactly why the margin varies: where the working set fits under the cap nothing is
/// evicted by either policy and the two tie to the unit, and the gap only opens once the sweep runs
/// past the capacity, widening with how far past it goes. D2's own finding repeats here too: on every sequence taken ALONE, misses came out equal
/// to the distinct key count under both policies at every G, i.e. nothing was evicted before its last
/// use, because a ring is swept closest-first and neighbouring regions share their border keys. So
/// the honest summary of D6 is that it buys uniformity and removes the all-locks
/// <c>ConcurrentDictionary.Count</c> from the miss path, plus a real but modest miss reduction that
/// only appears once a workload outruns the cache - not a hit-rate win at the sizes the engine
/// usually sits at. The feared losing regime (G = 2 beating nothing, as on the column cache) did not
/// appear on any of these three: the worst G = 2 row here is a tie.</para>
///
/// <para><b>Only the wholesale-clear side is modelled here.</b> <see cref="ColumnCacheReplayProbe"/>
/// carries a second, generational reference model because that round needed the HIT COMPOSITION -
/// newest-map hits versus middle-generation hits - and the real type deliberately does not count
/// those, since the counters would sit on the exact hot read the round existed to protect. D6 needs
/// miss counts alone, and <see cref="GenerationalCache{TKey,TValue}"/> exposes
/// <see cref="GenerationalCache{TKey,TValue}.Misses"/>,
/// <see cref="GenerationalCache{TKey,TValue}.Promotions"/> and
/// <see cref="GenerationalCache{TKey,TValue}.Rotations"/> exactly. So the generational side is read
/// straight off the real type and there is no second model to cross-check. The model was not
/// dropped by accident; there is nothing here for it to do.</para>
///
/// <para><b>What actually touches each cache</b> - read out of the tree at HEAD 963535c, because
/// two of the three turned out not to have the caller set the D6 bullet assumes:
/// <list type="bullet">
/// <item><c>TreeGenerator.CanopyCache</c> has exactly ONE engine caller:
/// <c>LodRegion.BuildMeshData</c> (LodRegion.cs:524) asks <c>TryGetCanopy</c> once per FINE column
/// of a region, which is what makes the 4096 cap bite. Chunk generation never touches it - the
/// public <c>CanopyTop</c> / <c>CanopyLeafBlock</c> entry points have no caller in the engine at
/// all. So this cache has no chunk-generation hot set, and the near ring (level 0) plays that role
/// instead: it is the smallest key set, and it is the one re-asked as regions are rebuilt on edits
/// and ring shifts while the far sweep runs.</item>
/// <item><c>TreeGenerator.ShapeCache</c> is shared by chunk generation (<c>TreeGenerator.Stamp</c>,
/// which walks a (16 + 2*8)^2 = 1024-column footprint per chunk) and by the canopy builder
/// (<c>BuildCanopyTops</c>, the same 1024-column footprint per chunk COLUMN), and the canopy builder
/// is driven by the LOD sweep above. That is the shared-cache shape D2 was about, one level
/// down.</item>
/// <item><c>CactusGenerator.ShapeCache</c> has exactly ONE caller, <c>CactusGenerator.Stamp</c> on
/// the generation workers. Nothing sweeps it. The only thing that can trip its 16384 cap is a
/// traverse - a player flying across desert generating fresh columns - so that is the bulk stream
/// modelled for it, with the resting radius-16 disc as the hot set underneath. The question the
/// interleaved row asks is the same one in all three cases: does the small live set survive the
/// stream that trips the cap.</item>
/// </list></para>
///
/// <para><b>Why the shape sweep is synthetic.</b> A shape-cache key is an ACCEPTED trunk or stem
/// column, not every column <c>TryGetTrunk</c> is asked about, so the sequence's distinct
/// cardinality is set by the acceptance rate. The plan carried two samples of that rate which
/// disagree by roughly 34% (0.389% and 0.523%) and nothing in this round reconciles them, so
/// pinning either would make the table depend on an unreconciled number. Instead acceptance is a
/// probe-local hash thresholded to hit a swept list of PINNED distinct-key cardinalities -
/// 4k / 8k / 16k / 32k / 64k against the 16384 cap - and a row is printed per cardinality. The table
/// is then auditable without believing either sample, and the decision rule reads the whole range.
/// The geometry around it is real: footprints of the true width, in the true scan order, over the
/// true chunk disc and the true LOD ring, so neighbouring columns share trunks exactly as they do in
/// the engine.</para>
///
/// <para><b>Runs of identical consecutive keys are collapsed.</b> The canopy sequence asks one key
/// per FINE column, so a level-2 region asks each of its 256 chunk columns 256 times over; replaying
/// all 12.9M raw asks per ring would cost memory and time to no purpose. Collapsing a run of
/// identical consecutive keys to one access is EXACTLY state-preserving for every number this probe
/// reads: under the wholesale clear a repeat of the immediately preceding key is a hit and changes
/// nothing, and under the generational policy a hit performs no write unless it lands in the oldest
/// generation, in which case the first access of the run promotes and the rest are newest-map hits.
/// Misses, promotions, rotations and clears are therefore identical with and without the collapse;
/// only hit counts change, and no assertion or decision reads those. The collapse is applied per
/// stream BEFORE interleaving, which leaves one measure-zero approximation: an interleaved access
/// falling between two repeats of a bulk key could in principle trigger the rotation that drops it.
/// Both the raw and the collapsed access counts are printed so the reduction stays visible.</para>
///
/// <para><b>Pinned, not read from production.</b> Every size below is a fixed constant naming the
/// production value it was read off, following <c>LodBuildPoolTests.StressWorkers</c> and
/// <see cref="ColumnCacheReplayProbe"/>: a sequence sized live from <c>LodRegion.Levels</c> or
/// <c>WorldGenerationConfig</c> would silently change shape when an unrelated round retunes a ring,
/// and the table quoted in PERF_TODO would stop describing the run it claims to.</para>
/// </summary>
public sealed class WorldgenShapeCacheReplayProbe
{
    private readonly ITestOutputHelper _output;

    public WorldgenShapeCacheReplayProbe(ITestOutputHelper output) => _output = output;

    // --- Production values as of 2026-09-19 (HEAD 963535c), pinned. Named, never referenced live. ---

    /// <summary><c>TreeGenerator.CanopyCacheLimit</c>.</summary>
    private const int CanopyCapacity = 4096;

    /// <summary><c>TreeGenerator.ShapeCacheLimit</c> and <c>CactusGenerator.ShapeCacheLimit</c> -
    /// both 16384, which is why one constant serves both shape caches.</summary>
    private const int ShapeCapacity = 16384;

    /// <summary><c>Chunk.Size</c> and <c>Chunk.SizeShift</c>.</summary>
    private const int ChunkSize = 16;

    private const int ChunkSizeShift = 4;

    /// <summary><c>WorldGenerationConfig.ChunkRadius</c>: the radius-16 disc of chunk columns
    /// full-detail generation keeps live, 797 columns.</summary>
    private const int ChunkDiscRadius = 16;

    /// <summary><c>WorldGenerationConfig.TreeReachBlocks</c>. <c>TreeGenerator.Stamp</c> and
    /// <c>BuildCanopyTops</c> both walk <c>(16 + 2*8)^2</c> = 1024 columns per chunk column.</summary>
    private const int TreeReach = 8;

    /// <summary><c>WorldGenerationConfig.CactusReachBlocks</c>, giving a <c>(16 + 2*6)^2</c> = 784
    /// column footprint per chunk.</summary>
    private const int CactusReach = 6;

    /// <summary><c>LodRegion.Levels[*].RegionRadius</c> - the same 8 at every level, giving the 197
    /// integer offsets inside the disc that <c>LodManager.BuildSortedOffsets</c> produces.</summary>
    private const int LodRegionRadius = 8;

    /// <summary><c>LodRegion.Levels[*].RegionBlocks</c>.</summary>
    private static readonly int[] LodRegionBlocks = [64, 128, 256];

    /// <summary><c>LodRegion.Levels[*].CellSize</c>. With <see cref="LodRegionBlocks"/> these give
    /// the <c>(RegionBlocks / CellSize)^2</c> cells and the <c>CellSize^2</c> fine columns per cell
    /// that <c>LodRegion.BuildMeshData</c>'s canopy loop walks (i outer, j, then dx, dz).</summary>
    private static readonly int[] LodCellSizes = [2, 4, 8];

    /// <summary>The near ring, standing in for the canopy cache's hot set (see the type doc): 197
    /// regions of 4x4 chunk columns against the 4096 cap.</summary>
    private const int CanopyHotLevel = 0;

    /// <summary>The far ring, the canopy cache's cap-tripper: 197 regions of 16x16 chunk columns
    /// against the same 4096 cap.</summary>
    private const int CanopyBulkLevel = 2;

    /// <summary>The ring whose canopy builds drive the tree shape cache's bulk stream: 197 regions
    /// of 8x8 chunk columns, each chunk column asking <c>BuildCanopyTops</c> for its 1024-column
    /// trunk footprint.</summary>
    private const int TreeSweepLevel = 1;

    /// <summary>How many times the chunk-gen disc is replayed in the hot streams, matching
    /// <c>ColumnCacheReplayProbe.ChunkGenPasses</c>: the vertical chunk stack re-stamps the same
    /// footprint once per chunk of column height, so a column is re-asked many times over.</summary>
    private const int ChunkGenPasses = 3;

    /// <summary>Chunks of travel in the cactus traverse. Pinned and arbitrary: 256 chunks is a
    /// 4096-block flight, long enough to trip a 16384-entry cap several times over at every swept
    /// cardinality, which is all the bulk stream has to do.</summary>
    private const int TraverseChunks = 256;

    /// <summary>
    /// One hot access per this many bulk accesses in the interleaved sequences, pinned at
    /// <see cref="ColumnCacheReplayProbe"/>'s value and arbitrary for the same reason: the true ratio
    /// between four generation threads and one LOD builder is not a constant anywhere in the source
    /// and varies with camera speed. The finding the interleaved row carries does not depend on it -
    /// a set that is wiped wholesale is wiped at any ratio.
    /// </summary>
    private const int InterleaveRatio = 8;

    /// <summary>The generation counts swept, as D2's round swept them: 4 is what
    /// <c>ColumnCacheGenerations</c> and <c>CellCacheGenerations</c> ship and what D6 proposes, and 2
    /// and 8 bracket it. 2 is kept in the sweep precisely because it is the configuration this design
    /// measured WORST in on the column cache.</summary>
    private static readonly int[] PolicyGenerations = [2, 4, 8];

    /// <summary>Distinct accepted-trunk cardinalities swept for the two shape caches, against the
    /// 16384 cap: two below it, one at it, two above. See "Why the shape sweep is synthetic".</summary>
    private static readonly int[] ShapeDistinctTargets = [4096, 8192, 16384, 32768, 65536];

    /// <summary>A factory that does no work, so the probe measures the POLICY and nothing else. The
    /// value is the key, which is what lets every access assert it got its own value back.</summary>
    private static readonly Func<(int X, int Z), (int X, int Z)> Identity = static key => key;

    /// <summary>One replayed access, tagged with which stream it came from so an interleaved
    /// sequence can report the hot set's misses separately from the bulk's.</summary>
    private readonly record struct Access(int X, int Z, bool IsHot);

    /// <summary>
    /// TODAY'S POLICY, reimplemented as a reference oracle: probe, compute on a miss, and drop the
    /// WHOLE table when it reaches the cap. This is <c>TreeGenerator.TryGetTrunk</c>,
    /// <c>TreeGenerator.GetCanopyColumns</c> and <c>CactusGenerator.TryGetCactus</c> exactly as they
    /// stand at HEAD 963535c - <c>TryGetValue</c>, then <c>if (Count &gt;= limit) Clear()</c>, then
    /// insert - with the <c>ConcurrentDictionary</c> swapped for a plain one because the probe is
    /// single-threaded and the policy, not the container, is what is being measured. (The all-locks
    /// <c>ConcurrentDictionary.Count</c> that the real code evaluates on every miss is the OTHER
    /// thing D6 removes; it costs nothing here and is not what this table is about.)
    /// </summary>
    private sealed class ClearAtCapOracle(int limit)
    {
        private readonly Dictionary<(int X, int Z), (int X, int Z)> _map = new();

        public long Misses;
        public long Clears;

        public (int X, int Z) GetOrAdd((int X, int Z) key)
        {
            if (_map.TryGetValue(key, out var hit)) return hit;

            Misses++;
            var value = Identity(key);
            if (_map.Count >= limit)
            {
                _map.Clear();
                Clears++;
            }

            _map[key] = value;
            return value;
        }
    }

    // --- Sequence construction ------------------------------------------------------------------

    /// <summary>
    /// The integer offsets inside a disc of <paramref name="radius"/>, sorted closest-first - the
    /// lattice and the order both <c>LodManager.BuildSortedOffsets</c> and the chunk scan use, so a
    /// replayed sequence arrives in the order the engine would issue it. Order matters to a cache:
    /// the same key set visited closest-first and visited row-major evict differently. The x/z tie
    /// break is the probe's own (production sorts on distance alone, and <c>List.Sort</c> is
    /// unstable) so that this file's counts are reproducible to the unit.
    /// </summary>
    private static List<(int dx, int dz)> DiscOffsets(int radius)
    {
        var scratch = new List<(int dx, int dz, int distSq)>();
        for (var dx = -radius; dx <= radius; dx++)
        for (var dz = -radius; dz <= radius; dz++)
        {
            var distSq = dx * dx + dz * dz;
            if (distSq <= radius * radius) scratch.Add((dx, dz, distSq));
        }

        scratch.Sort((a, b) =>
        {
            var byDistance = a.distSq.CompareTo(b.distSq);
            if (byDistance != 0) return byDistance;
            var byX = a.dx.CompareTo(b.dx);
            return byX != 0 ? byX : a.dz.CompareTo(b.dz);
        });

        var offsets = new List<(int dx, int dz)>(scratch.Count);
        foreach (var o in scratch) offsets.Add((o.dx, o.dz));
        return offsets;
    }

    /// <summary>
    /// The probe's own SplitMix64-shaped mix, deliberately NOT the engine's <c>SplitMix64</c>:
    /// acceptance here is a synthetic stand-in for terrain rules, and a probe whose counts could move
    /// because an unrelated round retuned a production hash would stop describing the run PERF_TODO
    /// quotes.
    /// </summary>
    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Whether the block column (x, z) grows a trunk in this synthetic world. Uniform in
    /// (x, z) and independent of everything else, so a footprint's accepted columns are irregularly
    /// spaced the way real trunks are, and the expected distinct count over an area is exactly
    /// <c>area * threshold / 2^32</c>.</summary>
    private static bool Accepted(int x, int z, uint threshold) =>
        (uint)(Mix(((ulong)(uint)x << 32) | (uint)z) >> 32) < threshold;

    /// <summary>The acceptance threshold that puts <paramref name="distinctTarget"/> accepted columns
    /// in an area of <paramref name="areaColumns"/> block columns.</summary>
    private static uint ThresholdFor(int distinctTarget, long areaColumns)
    {
        var rate = distinctTarget / (double)areaColumns;
        return (uint)Math.Round(rate * 4294967296.0);
    }

    /// <summary>
    /// The trunk/stem footprint one chunk column's <c>Stamp</c> or <c>BuildCanopyTops</c> walks:
    /// <c>[x0 - reach, x0 + 16 + reach)</c> squared, tx outer and tz inner, which is the scan order
    /// both production loops use. Only ACCEPTED columns reach the cache, so only those are emitted.
    /// </summary>
    private static IEnumerable<Access> FootprintRaw(int chunkX, int chunkZ, int reach, uint threshold, bool isHot)
    {
        var x0 = chunkX * ChunkSize;
        var z0 = chunkZ * ChunkSize;
        for (var tx = x0 - reach; tx < x0 + ChunkSize + reach; tx++)
        for (var tz = z0 - reach; tz < z0 + ChunkSize + reach; tz++)
            if (Accepted(tx, tz, threshold))
                yield return new Access(tx, tz, isHot);
    }

    /// <summary>(HOT, both shape caches) Chunk generation's own stream: every chunk column of the
    /// radius-16 disc, closest-first, each stamping its footprint, replayed
    /// <see cref="ChunkGenPasses"/> times.</summary>
    private static IEnumerable<Access> ChunkGenDiscRaw(int reach, uint threshold)
    {
        var offsets = DiscOffsets(ChunkDiscRadius);
        for (var pass = 0; pass < ChunkGenPasses; pass++)
        foreach (var (dx, dz) in offsets)
        foreach (var a in FootprintRaw(dx, dz, reach, threshold, isHot: true))
            yield return a;
    }

    /// <summary>(BULK, tree shape cache) The canopy builder's stream: every chunk column of one LOD
    /// ring, region disc closest-first and row-major inside a region, each asking
    /// <c>BuildCanopyTops</c> for its 1024-column trunk footprint.</summary>
    private static IEnumerable<Access> CanopySweepShapeRaw(int level, uint threshold)
    {
        var regionChunkSpan = LodRegionBlocks[level] / ChunkSize;
        foreach (var (rx, rz) in DiscOffsets(LodRegionRadius))
        {
            var baseChunkX = rx * regionChunkSpan;
            var baseChunkZ = rz * regionChunkSpan;
            for (var cx = 0; cx < regionChunkSpan; cx++)
            for (var cz = 0; cz < regionChunkSpan; cz++)
            foreach (var a in FootprintRaw(baseChunkX + cx, baseChunkZ + cz, TreeReach, threshold, isHot: false))
                yield return a;
        }
    }

    /// <summary>(BULK, cactus shape cache) A traverse: the radius-16 disc flying +X one chunk at a
    /// time, modelled as its 33-column leading edge entering fresh ground each step. Nothing else in
    /// the tree can trip this cache's cap.</summary>
    private static IEnumerable<Access> CactusTraverseRaw(uint threshold)
    {
        for (var step = 0; step < TraverseChunks; step++)
        for (var cz = -ChunkDiscRadius; cz <= ChunkDiscRadius; cz++)
        foreach (var a in FootprintRaw(ChunkDiscRadius + 1 + step, cz, CactusReach, threshold, isHot: false))
            yield return a;
    }

    /// <summary>
    /// (CANOPY) One LOD ring's canopy asks, in <c>LodRegion.BuildMeshData</c>'s exact order: region
    /// disc closest-first, then cell i outer, cell j, then dx, dz over the cell's fine columns, each
    /// fine column resolving to its chunk column by arithmetic shift (which is floor division, as in
    /// production's <c>x &gt;&gt; Chunk.SizeShift</c>). The canopy loop reads the region's own fine
    /// columns with no margin, so a region contributes exactly <c>(RegionBlocks / 16)^2</c> keys.
    /// </summary>
    private static IEnumerable<Access> CanopyRingRaw(int level, bool isHot)
    {
        var regionBlocks = LodRegionBlocks[level];
        var cellSize = LodCellSizes[level];
        var cells = regionBlocks / cellSize;

        foreach (var (rx, rz) in DiscOffsets(LodRegionRadius))
        {
            var baseFineX = rx * regionBlocks;
            var baseFineZ = rz * regionBlocks;
            for (var i = 0; i < cells; i++)
            for (var j = 0; j < cells; j++)
            for (var dx = 0; dx < cellSize; dx++)
            for (var dz = 0; dz < cellSize; dz++)
                yield return new Access(
                    (baseFineX + i * cellSize + dx) >> ChunkSizeShift,
                    (baseFineZ + j * cellSize + dz) >> ChunkSizeShift,
                    isHot);
        }
    }

    /// <summary>One materialised sequence plus the raw ask count it was collapsed from.</summary>
    private sealed record Sequence(string Name, Access[] Accesses, long RawAsks);

    /// <summary>Collapses runs of identical consecutive keys and materialises the result, so each
    /// policy replays an array instead of re-running the generator. See the type doc for why the
    /// collapse is exactly state-preserving for every number read here.</summary>
    private static Sequence Collapse(string name, IEnumerable<Access> raw)
    {
        var collapsed = new List<Access>();
        var have = false;
        var lastX = 0;
        var lastZ = 0;
        long rawAsks = 0;

        foreach (var a in raw)
        {
            rawAsks++;
            if (have && a.X == lastX && a.Z == lastZ) continue;
            collapsed.Add(a);
            lastX = a.X;
            lastZ = a.Z;
            have = true;
        }

        return new Sequence(name, collapsed.ToArray(), rawAsks);
    }

    /// <summary>The bulk stream with the hot stream cycling underneath it at
    /// <see cref="InterleaveRatio"/>. The hot stream cycles rather than ending because in the game it
    /// does not stop while the bulk stream runs.</summary>
    private static Sequence Interleaved(string name, Sequence hot, Sequence bulk)
    {
        var merged = new List<Access>(bulk.Accesses.Length + bulk.Accesses.Length / InterleaveRatio + 1);
        var cursor = 0;
        for (var i = 0; i < bulk.Accesses.Length; i++)
        {
            merged.Add(bulk.Accesses[i]);
            if ((i + 1) % InterleaveRatio != 0) continue;
            merged.Add(hot.Accesses[cursor]);
            cursor = (cursor + 1) % hot.Accesses.Length;
        }

        return new Sequence(name, merged.ToArray(), hot.RawAsks + bulk.RawAsks);
    }

    // --- Replay ---------------------------------------------------------------------------------

    /// <summary>The miss counts one sequence produced, keyed by policy: the wholesale clear, then one
    /// entry per swept generation count.</summary>
    private sealed class Row
    {
        public long ClearMisses;
        public long WholesaleClears;
        public readonly Dictionary<int, long> GenerationalMisses = new();
    }

    [Fact]
    public void ShapeAndCanopyMissesUnderBothEvictionPolicies()
    {
        _output.WriteLine(
            $"[probe] D6 capture. canopyCap={CanopyCapacity} shapeCap={ShapeCapacity} " +
            $"chunkDiscRadius={ChunkDiscRadius} lodRegionRadius={LodRegionRadius} " +
            $"chunkGenPasses={ChunkGenPasses} traverseChunks={TraverseChunks} " +
            $"interleave=1 hot key per {InterleaveRatio} bulk keys");
        _output.WriteLine("[probe] factory is identity: this measures the eviction POLICY, not terrain.");
        _output.WriteLine(
            "[probe] runs of identical consecutive keys are collapsed (exactly state-preserving; see doc comment).");
        _output.WriteLine("");

        RunCanopy();
        RunShape("tree-shape", TreeReach, isTraverse: false);
        RunShape("cactus-shape", CactusReach, isTraverse: true);
    }

    /// <summary>
    /// The canopy cache: the near ring as the hot set, the far ring as the cap-tripper, and the two
    /// interleaved. One cardinality only - a canopy key is a chunk column, so its distinct counts are
    /// pure geometry and need no acceptance rate.
    /// </summary>
    private void RunCanopy()
    {
        _output.WriteLine($"=== CACHE canopy (TreeGenerator.CanopyCache, capacity {CanopyCapacity}) ===");
        _output.WriteLine(
            $"[probe] hot = LOD level {CanopyHotLevel} ring, bulk = LOD level {CanopyBulkLevel} ring; " +
            "sole engine caller is LodRegion.BuildMeshData (LodRegion.cs:524), one TryGetCanopy per fine column.");

        var hot = Collapse($"lod-ring-L{CanopyHotLevel} (hot)", CanopyRingRaw(CanopyHotLevel, isHot: true));
        var bulk = Collapse($"lod-ring-L{CanopyBulkLevel} (bulk)", CanopyRingRaw(CanopyBulkLevel, isHot: false));
        var mixed = Interleaved("INTERLEAVED", hot, bulk);

        RunSequence(hot, CanopyCapacity);
        RunSequence(bulk, CanopyCapacity);
        var decision = RunSequence(mixed, CanopyCapacity);

        ReportDecision("canopy", [("single", decision)]);
    }

    /// <summary>
    /// One of the two shape caches, swept across <see cref="ShapeDistinctTargets"/>. The tree cache's
    /// bulk stream is the level-1 canopy sweep; the cactus cache's is a traverse, because nothing
    /// sweeps it (see the type doc).
    /// </summary>
    private void RunShape(string cache, int reach, bool isTraverse)
    {
        var regionChunkSpan = LodRegionBlocks[TreeSweepLevel] / ChunkSize;
        var bulkChunkColumns = isTraverse
            ? (long)TraverseChunks * (2 * ChunkDiscRadius + 1)
            : (long)DiscOffsets(LodRegionRadius).Count * regionChunkSpan * regionChunkSpan;
        var bulkArea = bulkChunkColumns * ChunkSize * ChunkSize;

        _output.WriteLine($"=== CACHE {cache} (capacity {ShapeCapacity}) ===");
        _output.WriteLine(
            $"[probe] hot = radius-{ChunkDiscRadius} chunk-gen disc x{ChunkGenPasses}, bulk = " +
            $"{(isTraverse ? $"{TraverseChunks}-chunk traverse" : $"LOD level {TreeSweepLevel} canopy sweep")} " +
            $"over {bulkChunkColumns} chunk columns ({bulkArea} block columns), footprint reach {reach}.");

        var decisions = new List<(string Label, Row Row)>();

        foreach (var target in ShapeDistinctTargets)
        {
            var threshold = ThresholdFor(target, bulkArea);
            _output.WriteLine(
                $"[probe] -- distinctTarget={target} acceptanceThreshold={threshold} " +
                $"(~{100.0 * threshold / 4294967296.0:F4}% of columns accepted)");

            var hot = Collapse($"chunk-gen disc (hot, T={target})", ChunkGenDiscRaw(reach, threshold));
            var bulk = isTraverse
                ? Collapse($"traverse (bulk, T={target})", CactusTraverseRaw(threshold))
                : Collapse($"canopy sweep (bulk, T={target})", CanopySweepShapeRaw(TreeSweepLevel, threshold));
            var mixed = Interleaved($"INTERLEAVED (T={target})", hot, bulk);

            RunSequence(hot, ShapeCapacity);
            RunSequence(bulk, ShapeCapacity);
            decisions.Add(($"T={target}", RunSequence(mixed, ShapeCapacity)));
        }

        ReportDecision(cache, decisions.ToArray());
    }

    /// <summary>
    /// Replays one sequence against the wholesale clear and against the real
    /// <see cref="GenerationalCache{TKey,TValue}"/> at every swept generation count, and prints the
    /// row. The only assertions are correctness ones - every policy hands back the value belonging to
    /// the key it was asked for, and misses are non-zero so a degenerated sequence cannot read as a
    /// perfect hit rate. The anti-vacuity check that the cap was actually tripped lives in
    /// <see cref="ReportDecision"/>, because it is a property of a cache's whole swept range rather
    /// than of one row: a shape sweep whose distinct cardinality sits BELOW the 16384 capacity
    /// legitimately never trips it and both policies then miss exactly the distinct key count.
    /// NOTHING here asserts one policy beats the other: the relation is printed for the pre-registered
    /// rule to be applied to.
    /// </summary>
    private Row RunSequence(Sequence sequence, int capacity)
    {
        var distinct = new HashSet<(int X, int Z)>();
        var hotAccesses = 0;
        foreach (var a in sequence.Accesses)
        {
            distinct.Add((a.X, a.Z));
            if (a.IsHot) hotAccesses++;
        }

        _output.WriteLine(
            $"[probe] sequence={sequence.Name} accesses={sequence.Accesses.Length} " +
            $"(rawAsks={sequence.RawAsks}) distinctKeys={distinct.Count} hotAccesses={hotAccesses}");

        var row = new Row();

        var clear = new ClearAtCapOracle(capacity);
        var clearHotMisses = 0L;
        foreach (var a in sequence.Accesses)
        {
            var key = (a.X, a.Z);
            var before = clear.Misses;
            Assert.Equal(key, clear.GetOrAdd(key));
            if (a.IsHot && clear.Misses != before) clearHotMisses++;
        }

        Assert.True(clear.Misses > 0, $"{sequence.Name}: the clear-at-cap oracle recorded no misses at all");

        row.ClearMisses = clear.Misses;
        row.WholesaleClears = clear.Clears;
        _output.WriteLine(
            $"[probe]   clear-at-cap(cap={capacity}): misses={clear.Misses} " +
            $"({100.0 * clear.Misses / sequence.Accesses.Length:F2}%) wholesaleClears={clear.Clears} " +
            $"hotMisses={clearHotMisses}");

        foreach (var generations in PolicyGenerations)
        {
            var cache = new GenerationalCache<(int X, int Z), (int X, int Z)>(capacity, generations);
            var hotMisses = 0L;

            foreach (var a in sequence.Accesses)
            {
                var key = (a.X, a.Z);
                var before = cache.Misses;
                Assert.Equal(key, cache.GetOrAdd(key, Identity));
                if (a.IsHot && cache.Misses != before) hotMisses++;
            }

            Assert.True(cache.Misses > 0, $"{sequence.Name}: G={generations} recorded no misses at all");
            row.GenerationalMisses[generations] = cache.Misses;

            // Rotations arrive every capacity/generations SUCCESSFUL inserts, and promotions are
            // inserts too - printing the prediction next to the count makes the arithmetic checkable
            // from the table alone.
            var predictedRotations = (cache.Misses + cache.Promotions) / (capacity / generations);
            var delta = 100.0 * (cache.Misses - clear.Misses) / clear.Misses;

            _output.WriteLine(
                $"[probe]   generational(cap={capacity},G={generations}): misses={cache.Misses} " +
                $"({100.0 * cache.Misses / sequence.Accesses.Length:F2}%) vs clear-at-cap {delta:+0.00;-0.00;0.00}%  " +
                $"promotions={cache.Promotions} ({100.0 * cache.Promotions / sequence.Accesses.Length:F2}% of accesses) " +
                $"rotations={cache.Rotations} (predicted {predictedRotations}) hotMisses={hotMisses}");
        }

        _output.WriteLine("");
        return row;
    }

    /// <summary>
    /// Applies the pre-registered decision rule to the interleaved rows of one cache and prints the
    /// verdict the conversion steps consume. A shape cache brings several rows (one per swept
    /// cardinality) and must win on all of them; the canopy cache brings one.
    ///
    /// <para>ANTI-VACUITY: at least one of a cache's interleaved rows must have tripped the cap under
    /// the wholesale clear, or the two policies were never asked to differ and the verdict would be
    /// worthless. A row whose own distinct cardinality sits below the capacity is kept in the table
    /// and printed with <c>wholesaleClears=0</c> - it is a real point of the swept range, it simply
    /// cannot separate the policies.</para>
    /// </summary>
    private void ReportDecision(string cache, (string Label, Row Row)[] rows)
    {
        var losingAtFour = new List<string>();
        var anyCapTrip = false;
        foreach (var (label, row) in rows)
        {
            var gen = row.GenerationalMisses[4];
            var delta = 100.0 * (gen - row.ClearMisses) / row.ClearMisses;
            _output.WriteLine(
                $"[decision] cache={cache} interleaved[{label}] G=4: generationalMisses={gen} " +
                $"clearMisses={row.ClearMisses} delta={delta:+0.00;-0.00;0.00}% " +
                $"wholesaleClears={row.WholesaleClears}");
            if (gen > row.ClearMisses) losingAtFour.Add(label);
            if (row.WholesaleClears > 0) anyCapTrip = true;
        }

        Assert.True(anyCapTrip,
            $"{cache}: no interleaved row tripped the cap, so nothing here separates the two policies");

        if (losingAtFour.Count == 0)
        {
            _output.WriteLine($"[decision] cache={cache} VERDICT: CONVERT AT G=4 (rule not fired).");
            _output.WriteLine("");
            return;
        }

        _output.WriteLine(
            $"[decision] cache={cache} RULE FIRED at G=4 on: {string.Join(", ", losingAtFour)}. " +
            "Branch (a)/(b) applies - the alternatives, from this same capture:");

        foreach (var generations in PolicyGenerations)
        {
            if (generations == 4) continue;
            var worst = double.NegativeInfinity;
            var wins = true;
            foreach (var (_, row) in rows)
            {
                var gen = row.GenerationalMisses[generations];
                var delta = 100.0 * (gen - row.ClearMisses) / row.ClearMisses;
                if (delta > worst) worst = delta;
                if (gen > row.ClearMisses) wins = false;
            }

            _output.WriteLine(
                $"[decision] cache={cache} candidate G={generations}: beatsClearEverywhere={wins} " +
                $"worstDeltaAcrossRows={worst:+0.00;-0.00;0.00}%");
        }

        _output.WriteLine("");
    }
}
