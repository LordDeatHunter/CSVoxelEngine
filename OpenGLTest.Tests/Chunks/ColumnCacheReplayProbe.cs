using System;
using System.Collections.Generic;
using Xunit;
using Xunit.Abstractions;

namespace silkgltriangle.Tests;

/// <summary>
/// A MEASUREMENT HARNESS for item D2, in the explicit style of <see cref="LodBuildCostProbe"/>: it
/// prints through <see cref="ITestOutputHelper"/> and asserts NO timing. Unlike a frame-time
/// capture this probe's numbers are exact and deterministic - it replays fixed key sequences against
/// private cache instances with a factory that does no work (<c>static key =&gt; key</c>), so it
/// generates no terrain, costs milliseconds, and produces the same counts on every machine and
/// every run. That is what makes it the round's quotable number.
///
/// <para><b>What it measures.</b> D2 replaced a wholesale <c>Clear()</c>-at-the-cap with
/// <see cref="GenerationalCache{TKey,TValue}"/>. The claim was that the far-LOD builder's one-shot
/// column sweep was throwing away chunk generation's small, permanently-hot working set every time
/// it tripped the cap. This probe replays the three key sequences that actually occur -
/// chunk generation's radius-16 disc, each LOD ring's prefetch, and the two interleaved - against
/// both policies and prints the miss counts side by side, plus the hit composition the new policy's
/// one added cost depends on.</para>
///
/// <para><b>The one cost the new policy adds</b> is promotion: a hit in the OLDEST generation is
/// re-inserted into the newest, which is a shared <c>TryAdd</c> write on a path that is otherwise a
/// pure read. <c>promotions / accesses</c> printed below is exactly that new write rate - counted,
/// not estimated. It is a proxy for contention and not a measurement of it: it says how often the
/// write happens, not what a bucket lock costs when it does.</para>
///
/// <para><b>The trade-off this probe exists to print honestly, not to be tuned around.</b> A
/// generational cache can be WORSE than a wholesale clear on one specific shape: a working set that
/// is cyclically revisited and sits between the retention band and the capacity. Such a set fits
/// inside a fill-once cache that never trips its cap, but only <c>(G-1)/G</c> of it survives each
/// rotation here. The level-1 ring is close to that shape - roughly 12.9k distinct columns against a
/// 16384 capacity - so if any row in the table shows the new policy missing more, that row is the
/// expected finding and goes into PERF_TODO as it stands. No assertion here compares the two
/// policies' miss counts; the relation is printed for a human to read. In the real mixed workload
/// the band does not exist in isolation, because the level-2 sweep trips the cap regardless, which
/// is why the MIXED sequence is the one that decides anything.</para>
///
/// <para><b>What the first run found, recorded so the next round does not re-derive it.</b> The
/// feared level-1 loss did NOT materialise: on every ring sweep taken alone, misses came out equal
/// to the distinct key count under both policies and every generation count, i.e. nothing was
/// evicted before its last use. The reason is the emission order rather than the size - a ring is
/// swept closest-first and neighbouring regions share their border columns, so a column's reuse is
/// local in the sequence and lands long before it can age out. The band is real arithmetic but the
/// workload never sits in it. Where the policies actually separate is the MIXED sequence, and the
/// separation is the mechanism D2 named: chunk generation's hot columns survive the sweep.</para>
///
/// <para><b>The MIXED conclusion does not rest on <see cref="MixedLodKeysPerChunkGenKey"/>.</b>
/// The ordering of the policies was re-run at ratios 2, 8 and 32 before the round quoted anything.
/// It is the same at all three: G=2 is WORSE than the wholesale clear (+7.6% to +8.3% misses), G=4
/// and G=8 are both better, and chunk generation's own miss count drops sharply against the clear
/// under both. Only the magnitudes move with the ratio, which is what a pinned-but-arbitrary
/// constant is allowed to do. G=2 losing is worth keeping in view: two generations means the
/// promotable window is half the cache, promotions run at 11-15% of accesses, and those writes buy
/// a retention band too coarse to hold anything - it is the configuration this design is worst in,
/// not a lower bound on it.</para>
///
/// <para><b>Why there is a reference model as well as the real type.</b>
/// <see cref="GenerationalCache{TKey,TValue}"/> deliberately does not count newest-map hits versus
/// middle-generation hits: those counters would sit on the hot path, which is the exact read this
/// round exists to protect. So the split is produced by a probe-local model of the policy (the
/// reference-implementation idiom from <c>ChunkScanFrontierTests</c>), and the model is cross-checked
/// against the real type's own exact counters - misses, promotions and rotations must agree to the
/// unit on every sequence and every generation count. A divergence there means the model is wrong or
/// the type changed, and either way the printed split is not to be trusted.</para>
///
/// <para><b>Pinned, not read from production.</b> Every size below is a fixed constant with the
/// production value it was read off named in its comment, following
/// <c>LodBuildPoolTests.StressWorkers</c>: a sequence sized live from <c>LodRegion.Levels</c> or
/// <c>WorldGenerationConfig.ChunkRadius</c> would silently change shape when an unrelated round
/// retunes a ring, and the table in PERF_TODO would stop describing the run it claims to.</para>
/// </summary>
public sealed class ColumnCacheReplayProbe
{
    private readonly ITestOutputHelper _output;

    public ColumnCacheReplayProbe(ITestOutputHelper output) => _output = output;

    // --- Production values as of 2026-09-19, pinned. Named, never referenced live. ---

    /// <summary><c>WorldGenerationConfig.ColumnCacheCapacity</c>, and the old
    /// <c>Chunk.ColumnCacheLimit</c> it replaced - both 16384, which is what makes the two policies
    /// comparable at all.</summary>
    private const int CacheCapacity = 16384;

    /// <summary><c>WorldGenerationConfig.ChunkRadius</c>. The disc of chunk columns full-detail
    /// generation keeps live: 797 columns at radius 16, and 933 distinct once each build's 3x3
    /// heightmap neighbourhood is included (both counted, not estimated).</summary>
    private const int ChunkDiscRadius = 16;

    /// <summary><c>Chunk.Size</c>.</summary>
    private const int ChunkSize = 16;

    /// <summary><c>LodRegion.Levels[*].RegionRadius</c> - the same 8 at every level, giving the 197
    /// integer offsets inside the disc that <c>LodManager.BuildSortedOffsets</c> produces.</summary>
    private const int LodRegionRadius = 8;

    /// <summary><c>LodRegion.Levels[*].RegionBlocks</c>. With <see cref="ChunkSize"/> these give the
    /// <c>(RegionBlocks / 16 + 2)^2</c> = 36 / 100 / 324 prefetch keys per region that
    /// <c>LodRegion.BuildMeshData</c> issues.</summary>
    private static readonly int[] LodRegionBlocks = [64, 128, 256];

    /// <summary>How many times the chunk-gen disc is replayed in sequence (i). The vertical chunk
    /// stack and the light prefetch re-ask a column many times over; three passes is enough to show
    /// whether the hot set survived, which is the only question asked of that sequence.</summary>
    private const int ChunkGenPasses = 3;

    /// <summary>
    /// The interleave ratio in sequence (iii): one chunk-gen key per this many LOD prefetch keys.
    /// It is pinned and arbitrary - the true ratio between four chunk-gen threads and one LOD
    /// builder is not a constant anywhere in the source and varies with camera speed. The finding
    /// the MIXED row carries does not depend on it: what it shows is whether chunk-gen's small hot
    /// set survives a level-2 sweep, and a set that is wiped wholesale is wiped at any ratio.
    /// </summary>
    private const int MixedLodKeysPerChunkGenKey = 8;

    /// <summary>
    /// The generation counts swept. 4 is what <c>WorldGenerationConfig.ColumnCacheGenerations</c>
    /// ships; 2 and 8 bracket it so the table shows which direction the knob moves things.
    ///
    /// <para>The shipped 4 was KEPT after this probe's first run, and the reason is that 8 wins and
    /// loses at the same time rather than simply winning. Against 4 on the MIXED sequence, 8 cuts
    /// avoidable recomputes (misses above the distinct key count) by roughly 4x and cuts promotions
    /// - the shared writes this policy adds - by roughly 25x, but it recomputes MORE of chunk
    /// generation's own hot columns, by 4% at ratio 32 and by 72% at ratio 2. The mechanism is the
    /// promotable window: at 8 generations the oldest band is an eighth of the cache, so a hot
    /// column is less likely to be touched during the narrow window in which a second chance is
    /// available to it. So the two candidates differ in WHICH work is saved, not only in how much,
    /// and this probe measures counts with a free factory - it has no cost weighting with which to
    /// trade a chunk-gen miss on a generator thread against a LodGen miss on the builder. Raising
    /// this knob is a one-token change and a legitimate later round, but it needs a real capture,
    /// not this table.</para>
    /// </summary>
    private static readonly int[] PolicyGenerations = [2, 4, 8];

    /// <summary>A factory that does no work, so the probe measures the POLICY and nothing else. The
    /// value is the key, which is what lets every access assert it got its own value back.</summary>
    private static readonly Func<(int X, int Z), (int X, int Z)> Identity = static key => key;

    /// <summary>One replayed access, tagged with where it came from so the MIXED sequence can
    /// report chunk generation's misses separately from the sweep's.</summary>
    private readonly record struct Access(int X, int Z, bool IsChunkGen);

    /// <summary>
    /// TODAY'S POLICY, reimplemented as a reference oracle: probe, compute on a miss, and drop the
    /// WHOLE table when it reaches the cap. This is <c>ColumnCache.Get</c> exactly as it stood at
    /// HEAD 5124d26 - <c>TryGetValue</c>, then <c>if (Count &gt;= limit) Clear()</c>, then insert -
    /// with the <c>ConcurrentDictionary</c> swapped for a plain one because the probe is
    /// single-threaded and the policy, not the container, is what is being measured.
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

    /// <summary>
    /// A single-threaded model of <see cref="GenerationalCache{TKey,TValue}"/>'s policy, mirroring
    /// it line for line: probe newest, probe the middle generations without writing, promote on a
    /// hit in the oldest, insert on a miss, and rotate when the newest map has taken
    /// <c>capacity / generations</c> successful inserts (<c>==</c>, and promotions count). It exists
    /// only to split hits into newest-map and middle-generation, which the real type does not count
    /// because those counters would sit on its hot path. Its misses, promotions and rotations are
    /// asserted equal to the real type's on every run, so the split is only ever read off a model
    /// that has just been shown to agree.
    /// </summary>
    private sealed class GenerationalOracle
    {
        private readonly int _generationEntries;
        private Dictionary<(int X, int Z), (int X, int Z)>[] _maps;
        private int _writes;

        public long Misses;
        public long Promotions;
        public long Rotations;
        public long NewestHits;
        public long MiddleHits;

        public GenerationalOracle(int capacity, int generations)
        {
            _generationEntries = capacity / generations;
            _maps = new Dictionary<(int X, int Z), (int X, int Z)>[generations];
            for (var i = 0; i < _maps.Length; i++) _maps[i] = new Dictionary<(int X, int Z), (int X, int Z)>();
        }

        public (int X, int Z) GetOrAdd((int X, int Z) key)
        {
            if (_maps[0].TryGetValue(key, out var newest))
            {
                NewestHits++;
                return newest;
            }

            var oldestIndex = _maps.Length - 1;
            for (var i = 1; i < oldestIndex; i++)
                if (_maps[i].TryGetValue(key, out var older))
                {
                    MiddleHits++;
                    return older;
                }

            if (_maps[oldestIndex].TryGetValue(key, out var oldest))
            {
                Promotions++;
                Insert(key, oldest);
                return oldest;
            }

            Misses++;
            var value = Identity(key);
            Insert(key, value);
            return value;
        }

        private void Insert((int X, int Z) key, (int X, int Z) value)
        {
            if (!_maps[0].TryAdd(key, value)) return;
            if (++_writes == _generationEntries) Rotate();
        }

        private void Rotate()
        {
            var maps = new Dictionary<(int X, int Z), (int X, int Z)>[_maps.Length];
            maps[0] = new Dictionary<(int X, int Z), (int X, int Z)>();
            Array.Copy(_maps, 0, maps, 1, _maps.Length - 1);
            _maps = maps;
            _writes = 0;
            Rotations++;
        }
    }

    /// <summary>
    /// The integer offsets inside a disc of <paramref name="radius"/>, sorted closest-first - the
    /// lattice and the order both <c>LodManager.BuildSortedOffsets</c> and
    /// <c>ChunkGenerationWorker</c>'s scan use, so a replayed sequence arrives in the order the
    /// engine would actually issue it. Order matters to a cache: the same key set visited
    /// closest-first and visited row-major evict differently.
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
    /// (i) CHUNK-GEN. Every chunk column of the radius-16 disc, closest-first, each contributing the
    /// 3x3 column neighbourhood a chunk build actually asks for (<c>ChunkLighting.FillColumns</c>,
    /// dx outer / dz inner), replayed <see cref="ChunkGenPasses"/> times. This is the small,
    /// permanently-hot set D2 says the sweep was throwing away.
    /// </summary>
    private static List<Access> ChunkGenSequence()
    {
        var offsets = DiscOffsets(ChunkDiscRadius);
        var seq = new List<Access>(ChunkGenPasses * offsets.Count * 9);
        for (var pass = 0; pass < ChunkGenPasses; pass++)
            foreach (var (dx, dz) in offsets)
            for (var nx = 0; nx < 3; nx++)
            for (var nz = 0; nz < 3; nz++)
                seq.Add(new Access(dx - 1 + nx, dz - 1 + nz, IsChunkGen: true));
        return seq;
    }

    /// <summary>
    /// (ii) LOD RING. Every region of one level's radius-8 disc, closest-first, each emitting its
    /// <c>(RegionBlocks / 16 + 2)^2</c> prefetch keys in the exact <c>baseChunkX</c> / <c>baseChunkZ</c>
    /// order <c>LodRegion.BuildMeshData</c> issues them (cx outer, cz inner, base = <c>r * span - 1</c>).
    /// </summary>
    private static List<Access> LodRingSequence(int level)
    {
        var regionChunkSpan = LodRegionBlocks[level] / ChunkSize;
        var chunkSpan = regionChunkSpan + 2;
        var offsets = DiscOffsets(LodRegionRadius);

        var seq = new List<Access>(offsets.Count * chunkSpan * chunkSpan);
        foreach (var (rx, rz) in offsets)
        {
            var baseChunkX = rx * regionChunkSpan - 1;
            var baseChunkZ = rz * regionChunkSpan - 1;
            for (var cx = 0; cx < chunkSpan; cx++)
            for (var cz = 0; cz < chunkSpan; cz++)
                seq.Add(new Access(baseChunkX + cx, baseChunkZ + cz, IsChunkGen: false));
        }

        return seq;
    }

    /// <summary>
    /// (iii) MIXED - the burst shape D2 is actually about: the chunk-gen disc cycling underneath the
    /// level 0, 1 and 2 ring sweeps, four generator threads and the LOD builder sharing one cache.
    /// The chunk-gen stream cycles rather than ending, because in the game it does not stop while the
    /// LOD builder sweeps.
    /// </summary>
    private static List<Access> MixedSequence()
    {
        var chunkGen = ChunkGenSequence();
        var lod = new List<Access>();
        for (var level = 0; level < LodRegionBlocks.Length; level++) lod.AddRange(LodRingSequence(level));

        var seq = new List<Access>(lod.Count + lod.Count / MixedLodKeysPerChunkGenKey + 1);
        var cursor = 0;
        for (var i = 0; i < lod.Count; i++)
        {
            seq.Add(lod[i]);
            if ((i + 1) % MixedLodKeysPerChunkGenKey != 0) continue;
            seq.Add(chunkGen[cursor]);
            cursor = (cursor + 1) % chunkGen.Count;
        }

        return seq;
    }

    [Fact]
    public void MissCountsAndHitCompositionForTheRealKeySequences()
    {
        _output.WriteLine(
            $"[probe] capacity={CacheCapacity} chunkDiscRadius={ChunkDiscRadius} " +
            $"lodRegionRadius={LodRegionRadius} chunkGenPasses={ChunkGenPasses} " +
            $"mixedRatio=1 chunk-gen key per {MixedLodKeysPerChunkGenKey} lod keys");
        _output.WriteLine("[probe] factory is identity: this measures the eviction POLICY, not terrain.");
        _output.WriteLine("");

        RunSequence("chunk-gen", ChunkGenSequence());
        for (var level = 0; level < LodRegionBlocks.Length; level++)
            RunSequence($"lod-ring-L{level}", LodRingSequence(level));
        RunSequence("MIXED", MixedSequence());
    }

    /// <summary>
    /// Replays one sequence against every policy and prints the row. The only assertions are
    /// correctness ones - every policy hands back the value belonging to the key it was asked for,
    /// the model agrees with the real type to the unit, and the miss counts are non-zero so a
    /// sequence that silently degenerated cannot be read as a perfect hit rate.
    /// </summary>
    private void RunSequence(string name, List<Access> seq)
    {
        var distinct = new HashSet<(int X, int Z)>();
        var chunkGenAccesses = 0;
        foreach (var a in seq)
        {
            distinct.Add((a.X, a.Z));
            if (a.IsChunkGen) chunkGenAccesses++;
        }

        _output.WriteLine(
            $"[probe] sequence={name} accesses={seq.Count} distinctKeys={distinct.Count} " +
            $"chunkGenAccesses={chunkGenAccesses}");

        var clear = new ClearAtCapOracle(CacheCapacity);
        var clearChunkGenMisses = 0L;
        foreach (var a in seq)
        {
            var key = (a.X, a.Z);
            var before = clear.Misses;
            Assert.Equal(key, clear.GetOrAdd(key));
            if (a.IsChunkGen && clear.Misses != before) clearChunkGenMisses++;
        }

        Assert.True(clear.Misses > 0, $"{name}: the clear-at-cap oracle recorded no misses at all");
        _output.WriteLine(
            $"[probe]   clear-at-cap(cap={CacheCapacity}): misses={clear.Misses} " +
            $"({100.0 * clear.Misses / seq.Count:F2}%) wholesaleClears={clear.Clears} " +
            $"chunkGenMisses={clearChunkGenMisses}");

        foreach (var generations in PolicyGenerations)
        {
            var cache = new GenerationalCache<(int X, int Z), (int X, int Z)>(CacheCapacity, generations);
            var model = new GenerationalOracle(CacheCapacity, generations);
            var chunkGenMisses = 0L;

            foreach (var a in seq)
            {
                var key = (a.X, a.Z);
                var before = cache.Misses;
                Assert.Equal(key, cache.GetOrAdd(key, Identity));
                Assert.Equal(key, model.GetOrAdd(key));
                if (a.IsChunkGen && cache.Misses != before) chunkGenMisses++;
            }

            // The model is only allowed to supply the hit split if it agrees with the real type
            // everywhere the real type does count.
            Assert.Equal(cache.Misses, model.Misses);
            Assert.Equal(cache.Promotions, model.Promotions);
            Assert.Equal(cache.Rotations, model.Rotations);
            Assert.True(cache.Misses > 0, $"{name}: G={generations} recorded no misses at all");

            var hits = seq.Count - cache.Misses;
            Assert.Equal(hits, model.NewestHits + model.MiddleHits + model.Promotions);

            // Rotations arrive every capacity/generations SUCCESSFUL inserts, and promotions are
            // inserts too - printing the prediction next to the count makes the arithmetic checkable
            // from the table alone.
            var predictedRotations = (cache.Misses + cache.Promotions) / (CacheCapacity / generations);
            var delta = 100.0 * (cache.Misses - clear.Misses) / clear.Misses;

            _output.WriteLine(
                $"[probe]   generational(cap={CacheCapacity},G={generations}): misses={cache.Misses} " +
                $"({100.0 * cache.Misses / seq.Count:F2}%) vs clear-at-cap {delta:+0.00;-0.00;0.00}%  " +
                $"newestHits={model.NewestHits} olderGenHits={model.MiddleHits} " +
                $"promotions={cache.Promotions} ({100.0 * cache.Promotions / seq.Count:F2}% of accesses) " +
                $"rotations={cache.Rotations} (predicted {predictedRotations}) " +
                $"chunkGenMisses={chunkGenMisses}");
        }

        _output.WriteLine("");
    }
}
