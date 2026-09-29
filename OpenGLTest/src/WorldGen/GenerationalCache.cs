using System;
using System.Collections.Concurrent;
using System.Threading;

namespace silkgltriangle;

/// <summary>
/// The shared eviction policy behind the deterministic worldgen caches (PERF_TODO D2). Replaces the
/// wholesale <c>Clear()</c>-at-the-cap that <see cref="Chunk.GetColumnData"/> and
/// <c>WormCarver.GetCellWorms</c> both used, which threw away chunk generation's small,
/// permanently-hot working set every time the far-LOD builder's one-shot column sweep tripped the
/// limit. Entries are aged out one generation at a time instead, and anything read while it sits in
/// the oldest generation is carried forward.
///
/// <para>The cache is <c>generations</c> disjoint dictionaries, index 0 newest. A read probes them
/// newest-first; a miss computes the value and inserts it into the newest; when the newest map has
/// taken <c>capacity / generations</c> successful inserts the whole array rotates - a fresh empty
/// map is pushed on the front and the oldest is dropped. Nothing is ever cleared wholesale except by
/// the seed swap (point 11).</para>
///
/// <para><b>1. Values are pure deterministic functions of the key.</b> Every value stored here is
/// produced by <c>factory(key)</c> with no other input than the key and the CURRENT world seed,
/// which only <c>WorldSeed.Apply</c> changes, clearing every cache as it does (point 11). Each value
/// is immutable after publication. So two entries computed for one key are interchangeable: it
/// does not matter which of them a reader gets, or whether it gets a recomputed one.</para>
///
/// <para><b>2. An eviction is a cost, never a wrong value.</b> A key is only ever inserted under its
/// own key, the generations are disjoint dictionaries, and no entry is ever rewritten in place. So
/// the only thing losing an entry can produce is one extra <c>factory</c> call, which by point 1
/// yields a value equal to the one that was dropped. There is no path by which this type can return
/// a value belonging to a different key, or a stale value that differs from a fresh one.</para>
///
/// <para><b>3. The generation array is published as one immutable node.</b> <c>Generation</c>
/// holds a readonly array, fully built before it is swapped in by
/// <c>Interlocked.CompareExchange</c> (a full fence). <see cref="GetOrAdd"/> takes exactly one
/// <c>Volatile.Read</c> of <c>_current</c> and uses that snapshot for the whole call, so no reader
/// can observe a half-rotated set - the same protocol as <c>ChunkScanFrontier</c>'s node (C3) and
/// <c>LodManager</c>'s TryAdd-gated claim (C5).</para>
///
/// <para><b>4. An insert racing a rotation is dropped one generation early.</b> A caller whose
/// snapshot was taken before a rotation inserts into what is now <c>Maps[1]</c>. That entry is
/// perfectly usable - it is found by every subsequent probe - it just ages out one rotation sooner
/// than it otherwise would. Cost, never correctness. The same race can push a demoted map one entry
/// past its nominal tenancy, which is the bounded slack named in point 7a.</para>
///
/// <para><b>5. Exactly one thread per generation triggers a rotation.</b> <c>Insert</c> tests
/// <c>Interlocked.Increment(ref gen.Writes) == _generationEntries</c> - <c>==</c>, not <c>&gt;=</c>,
/// so of the many threads that push a generation's counter past the cap exactly one sees the
/// equality and calls <c>Rotate</c>. A losing CAS inside <c>Rotate</c> means somebody else
/// already rotated this node; the fresh array is simply discarded and nothing is retried, so this
/// type cannot spin. A thread still holding a stale node whose counter crosses the cap again costs
/// at most one wasted allocation.</para>
///
/// <para><b>6. A promoted key exists in two maps at once.</b> Promotion inserts into the newest map
/// without removing from the oldest. That is benign: the value is one shared instance, so the
/// duplicate costs a dictionary node and nothing else, and it disappears when the oldest generation
/// rotates out. Removing it instead would mean a write to a second map on the same path, which is
/// precisely the contention this type exists to avoid.</para>
///
/// <para><b>7a. Memory ceiling - unconditional in the promotion rate.</b> Each map accepts exactly
/// <c>_generationEntries</c> successful inserts during its tenure as <c>Maps[0]</c> and never
/// another one afterwards, because every insert goes through <c>Maps[0]</c> of the caller's
/// snapshot. Promotions are counted in that budget deliberately. So the entries across all
/// <c>generations</c> maps are bounded by <c>generations * (capacity / generations) &lt;= capacity</c>
/// at all times, however hot or cold the workload is. The only slack is point 4's race: at most one
/// extra entry per thread in flight through a rotation, which is bounded by the thread count and not
/// by the workload. This is the claim the cache's memory sizing rests on.</para>
///
/// <para><b>7b. Retention - conditional, and weaker than a fixed floor.</b> An entry read at least
/// once while it sits in the OLDEST generation is carried forward into the newest and survives the
/// rotation that would have dropped it; an entry never read there is dropped when that generation
/// rotates out. Distinct-key residency is therefore strictly BELOW
/// <c>capacity * (generations - 1) / generations</c> whenever promotions occur, because a promotion
/// consumes an insert slot and briefly occupies two dictionary nodes for one key. Do not restate
/// this as a flat <c>capacity * (G-1)/G</c> retention floor: that is false under any workload with
/// revisits, which is the real workload.</para>
///
/// <para><b>8. No locks, and no <c>Count</c> on any live path.</b> Every path here is
/// <c>ConcurrentDictionary</c> probes plus <c>Interlocked</c>, per AGENTS.md's lock-free-shared-state
/// rule. In particular the insert budget is an <c>Interlocked</c> counter, NOT
/// <c>ConcurrentDictionary.Count</c> - which acquires every lock in the table and which the replaced
/// code evaluated on every single miss, on the exact path the chunk-generation threads and the LOD
/// builder share. <see cref="EntryCount"/> is the one place <c>Count</c> is called and it is
/// diagnostics only.</para>
///
/// <para><b>9. Callable from any thread, including the main thread.</b> There is no GL here and no
/// worldgen dependency - this is a plain data structure. The hot path (a hit in the newest
/// generation) is a single dictionary probe and no write, identical in cost to the
/// <c>TryGetValue</c> fast path it replaced, which matters because these caches are read from the
/// frame path as well as from the background generators. Promotion is restricted to hits in the
/// OLDEST generation for the same reason: a write on every non-newest hit would put a bucket lock on
/// the common path under a LOD sweep.</para>
///
/// <para><b>10. Standing obligation.</b> Only values that are pure functions of the key may be
/// stored here. Anything carrying player edits - <c>LodRegion.ColumnCaps</c>,
/// <c>TreeGenerator.CanopyDisturbed</c> - must NEVER be moved into this type: for those an eviction
/// is data loss the generator cannot rebuild, not a recompute. Point 2 is the whole safety argument,
/// and it evaporates the moment a value stops being derivable from its key.</para>
///
/// <para><b>11. <see cref="Clear"/> exists ONLY for the world-seed swap.</b> Every value here is a
/// pure function of (key, seed), so once <c>WorldSeed.Apply</c> changes the seed every entry is
/// wrong and a wholesale drop is the correct response - the one place this type is ever cleared
/// wholesale. It is not a thread-safe reset: a worker still reading while it runs could insert an
/// old-seed value into the fresh node. Excluding that race is the caller's job (Apply runs only
/// once every worldgen thread is joined).</para>
/// </summary>
public sealed class GenerationalCache<TKey, TValue> where TKey : notnull
{
    /// <summary>
    /// One immutable snapshot of the generation array plus that generation's insert budget. Swapped
    /// as a unit (point 3) so no reader ever sees a half-rotated set. <see cref="Writes"/> is a field
    /// and not a property because it is only ever touched through <c>Interlocked</c>.
    /// </summary>
    private sealed class Generation(ConcurrentDictionary<TKey, TValue>[] maps)
    {
        internal readonly ConcurrentDictionary<TKey, TValue>[] Maps = maps;
        internal int Writes;
    }

    private readonly int _capacity;
    private readonly int _generations;
    private readonly int _generationEntries;

    private Generation _current;
    private long _misses;
    private long _promotions;
    private long _rotations;

    /// <param name="capacity">The residency ceiling in entries (point 7a).</param>
    /// <param name="generations">How many age bands the capacity is split into. Higher retains a
    /// smaller hot set for longer; 2 is the coarsest useful value.</param>
    public GenerationalCache(int capacity, int generations)
    {
        if (generations < 2)
            throw new ArgumentOutOfRangeException(nameof(generations), generations,
                "a generational cache needs at least two generations to age anything out");
        if (capacity < generations)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity,
                $"capacity must leave at least one entry per generation (generations = {generations})");

        _capacity = capacity;
        _generations = generations;
        _generationEntries = capacity / generations;

        var maps = new ConcurrentDictionary<TKey, TValue>[generations];
        for (var i = 0; i < maps.Length; i++)
            maps[i] = NewMap();
        _current = new Generation(maps);
    }

    /// <summary>
    /// Pre-sized so that filling a generation to its budget never trips
    /// <c>ConcurrentDictionary</c>'s own growth, which takes every lock in the table.
    /// </summary>
    private ConcurrentDictionary<TKey, TValue> NewMap() =>
        new(Environment.ProcessorCount, _generationEntries);

    /// <summary>
    /// The cached value for <paramref name="key"/>, computing it through <paramref name="factory"/>
    /// on a miss. Safe from any thread; see the type doc for what an eviction, a duplicate or a race
    /// can and cannot cause. <paramref name="factory"/> may run concurrently for the same key on two
    /// threads - by point 1 both results are interchangeable and the loser is simply discarded.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        // ONE read of the node, used for the whole call (point 3).
        var generation = Volatile.Read(ref _current);
        var maps = generation.Maps;

        // Hot path: newest generation, one probe, no write.
        if (maps[0].TryGetValue(key, out var newest))
            return newest;

        var oldestIndex = maps.Length - 1;

        // Middle generations: a hit returns without writing. These entries have at least one more
        // rotation of life left, so there is nothing to rescue yet.
        for (var i = 1; i < oldestIndex; i++)
            if (maps[i].TryGetValue(key, out var older))
                return older;

        // Oldest generation: the one the next rotation drops, so a hit here takes its second chance
        // and is promoted into the newest map (point 9 explains why only here).
        if (maps[oldestIndex].TryGetValue(key, out var oldest))
        {
            Interlocked.Increment(ref _promotions);
            Insert(generation, key, oldest);
            return oldest;
        }

        Interlocked.Increment(ref _misses);
        var value = factory(key);
        Insert(generation, key, value);
        return value;
    }

    /// <summary>
    /// Adds to the newest map of the caller's snapshot and spends one of that generation's insert
    /// slots. <c>==</c> rather than <c>&gt;=</c> is load-bearing: it makes exactly one thread per
    /// generation the one that rotates (point 5).
    /// </summary>
    private void Insert(Generation generation, TKey key, TValue value)
    {
        if (!generation.Maps[0].TryAdd(key, value)) return;

        if (Interlocked.Increment(ref generation.Writes) == _generationEntries)
            Rotate(generation);
    }

    /// <summary>
    /// Pushes a fresh empty map onto the front and drops the oldest, publishing the new array as one
    /// node. A lost CAS means somebody else already rotated this node: the fresh array is discarded
    /// and nothing is retried (point 5).
    /// </summary>
    private void Rotate(Generation observed)
    {
        var previous = observed.Maps;
        var maps = new ConcurrentDictionary<TKey, TValue>[previous.Length];
        maps[0] = NewMap();
        // The last element of previous is deliberately not copied: that is the eviction.
        Array.Copy(previous, 0, maps, 1, previous.Length - 1);

        if (Interlocked.CompareExchange(ref _current, new Generation(maps), observed) == observed)
            Interlocked.Increment(ref _rotations);
    }

    /// <summary>
    /// Drops every generation by publishing a fresh, empty node (point 11). A <c>Rotate</c> racing on
    /// the old node loses its CAS and discards its array (point 5), so nothing is retried. The
    /// diagnostics counters are not reset: they stay monotone. Only for <c>WorldSeed.Apply</c>, whose
    /// precondition is that no worker thread can read this cache while it runs.
    /// </summary>
    public void Clear()
    {
        var maps = new ConcurrentDictionary<TKey, TValue>[_generations];
        for (var i = 0; i < maps.Length; i++)
            maps[i] = NewMap();
        Interlocked.Exchange(ref _current, new Generation(maps));
    }

    /// <summary>Factory calls made, i.e. lookups that found nothing in any generation. Diagnostics
    /// and the facts only.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Hits in the oldest generation, each of which cost one shared write. This is the one
    /// cost this policy adds over a plain probe; measured, not assumed. Diagnostics only.</summary>
    public long Promotions => Interlocked.Read(ref _promotions);

    /// <summary>Completed rotations, i.e. generations aged out. Diagnostics only.</summary>
    public long Rotations => Interlocked.Read(ref _rotations);

    /// <summary>The residency ceiling this instance was built with (point 7a).</summary>
    public int Capacity => _capacity;

    /// <summary>How many age bands the capacity is split into.</summary>
    public int Generations => _generations;

    /// <summary>
    /// Probes every generation without promoting and without inserting, so a fact can ask "is this
    /// key still resident" without changing the answer. Diagnostics and the facts only - the engine
    /// goes through <see cref="GetOrAdd"/>.
    /// </summary>
    public bool TryPeek(TKey key, out TValue? value)
    {
        foreach (var map in Volatile.Read(ref _current).Maps)
            if (map.TryGetValue(key, out value))
                return true;

        value = default;
        return false;
    }

    /// <summary>
    /// Entries resident across all generations, counting a promoted key once per map it sits in.
    /// EXPENSIVE and TEST-ONLY: <c>ConcurrentDictionary.Count</c> acquires every lock in the table,
    /// and this calls it once per generation. Never call it from engine code - a debug-overlay
    /// occupancy readout wants a cheap approximation, not this.
    /// </summary>
    public int EntryCount
    {
        get
        {
            var total = 0;
            foreach (var map in Volatile.Read(ref _current).Maps)
                total += map.Count;
            return total;
        }
    }
}
