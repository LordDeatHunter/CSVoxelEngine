using System;
using System.Collections.Concurrent;
using System.Threading;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The shared eviction policy behind the deterministic worldgen caches (PERF_TODO D2). Replaces the
/// wholesale <c>Clear()</c>-at-the-cap that <see cref="ColumnCache.Get"/> and
/// <c>WormCarver.GetCellWorms</c> both used, which threw away chunk generation's small,
/// permanently-hot working set every time the far-LOD builder's one-shot column sweep tripped the
/// limit. Entries are aged out one generation at a time instead, and anything read while it sits in
/// the oldest generation is carried forward.
///
/// <para>The cache is <c>generations</c> disjoint dictionaries, index 0 newest. A read probes them
/// newest-first; a miss computes the value and inserts it into the newest; when the newest map has
/// reserved <c>capacity / generations</c> insert slots the whole array rotates - a fresh empty
/// map is pushed on the front and the oldest is dropped. Wholesale clearing requires quiescent
/// readers (point 11).</para>
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
/// <c>Volatile.Read</c> of <c>_current</c> for probing, so no reader can observe a half-rotated set.
/// Insertion may refresh that snapshot if its newest map has no free slot (point 5).</para>
///
/// <para><b>4. Every insert reserves a slot before publishing.</b> A map's counter conservatively
/// covers published entries and pending inserts. Admission uses a CAS that cannot exceed
/// <c>_generationEntries</c>; only a confirmed duplicate (a normal false return) returns its slot.
/// <c>TryAdd</c> can publish and then throw, so an exceptional insert keeps its reservation until
/// the map ages out, even if it did not publish. The exception propagates. An insert racing a
/// rotation may finish in a demoted or detached map, but still owns a slot in that map. It can age
/// out early, never overfill the map. Factories run before reserving, so a slow factory holds no
/// slot and retries admission in a newer snapshot if its original map has filled.</para>
///
/// <para><b>5. Any caller can help a full generation rotate.</b> The caller taking the last slot
/// rotates after a successful insert. Another caller finding all slots occupied can also rotate
/// before retrying admission in the current node. Only one rotation CAS for a given node can win;
/// losers discard their candidate. If pending inserts later turn out to be duplicates or throw,
/// the rotated map can be underfull. Without insertion exceptions, sequentially only successful
/// inserts spend the budget; exceptional inserts conservatively consume slots (point 4).
/// Admission retries only after a competing CAS or a full generation; it does not wait for a
/// particular caller. This is not a wait-free bound on an individual call under contention.</para>
///
/// <para><b>6. A promoted key exists in two maps at once.</b> Promotion inserts into the newest map
/// without removing from the oldest. That is benign: the value is one shared instance, so the
/// duplicate costs a dictionary node and nothing else, and it disappears when the oldest generation
/// rotates out. Removing it instead would mean a write to a second map on the same path, which is
/// precisely the contention this type exists to avoid.</para>
///
/// <para><b>7a. Residency ceiling - unconditional in the promotion rate and scheduler.</b>
/// Every dictionary has at most <c>_generationEntries</c> entries, even while rotation allocation
/// or publication is delayed and even after demotion. Promotions reserve the same slots as misses.
/// Therefore any generation snapshot contains at most
/// <c>generations * (capacity / generations) &lt;= capacity</c> dictionary entries, including
/// promotion duplicates, with no per-thread overshoot allowance. This is a bound on resident
/// entries, not total managed bytes: in-flight callers can retain detached snapshots and losing
/// rotation candidates until they return, and garbage collection can reclaim them later. Each
/// caller retains a bounded number of maps/arrays; there is no chain of past generations.</para>
///
/// <para><b>7b. Retention - conditional, and weaker than a fixed floor.</b> An entry read at least
/// once while it sits in the OLDEST generation is carried forward into the insertion snapshot's
/// newest map (or a newer node if full) and survives the rotation that would have dropped it,
/// subject to point 4's concurrent aging; an entry never read there is dropped when that generation
/// rotates out. Promotions consume insert slots and briefly occupy two dictionary nodes for one
/// key, so distinct-key residency can fall below <c>capacity * (generations - 1) / generations</c>.
/// That expression is not a retention floor under workloads with revisits.</para>
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
/// stored here. Anything carrying player edits - <c>LodColumnCaps</c>,
/// <c>TreeCanopy.CanopyDisturbed</c> - must NEVER be moved into this type: for those an eviction
/// is data loss the generator cannot rebuild, not a recompute. Point 2 is the whole safety argument,
/// and it evaporates the moment a value stops being derivable from its key.</para>
///
/// <para><b>11. <see cref="Clear"/> requires quiescent readers.</b> Every value here is a pure
/// function of (key, seed), so <c>WorldSeed.Apply</c> clears every cache on a seed/world swap.
/// The column cache also clears after the last world has safely stopped, releasing derivable
/// values while the process is between worlds. This is not a thread-safe reset: a reader still
/// running could insert into an old node. The caller must serialize the clear with all readers,
/// including direct callers outside a World.</para>
/// </summary>
public sealed class GenerationalCache<TKey, TValue> where TKey : notnull
{
    /// <summary>
    /// One immutable snapshot of the generation array plus that generation's insert budget. Swapped
    /// as a unit (point 3) so no reader ever sees a half-rotated set. <see cref="Slots"/> counts spent
    /// and in-flight reservations in Maps[0], never below its entry count or above its budget.
    /// An uncertain exceptional insert conservatively spends its slot (point 4).
    /// </summary>
    private sealed class Generation(ConcurrentDictionary<TKey, TValue>[] maps)
    {
        internal readonly ConcurrentDictionary<TKey, TValue>[] Maps = maps;
        internal int Slots;
    }

    private readonly int _capacity;
    private readonly int _generations;
    private readonly int _generationEntries;

    /// <summary>Per-instance scheduling seam for bounded rotation-race tests.</summary>
    internal Action? BeforeRotateForTests { get; set; }

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
    /// Pre-sized for the generation budget rather than starting small and relying on
    /// <c>ConcurrentDictionary</c>'s growth, which can take every lock in the table.
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
        // ONE snapshot for probing; insertion refreshes it only if admission finds a full map.
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
    /// Reserves space before publishing an entry. A full snapshot is rotated (or was already
    /// rotated by a peer), then admission retries against the current node. Only a normal false
    /// return refunds its reservation: an exception may follow publication and keeps the slot.
    /// No caller waits for another caller to finish inserting or allocating a rotation.
    /// </summary>
    private void Insert(Generation generation, TKey key, TValue value)
    {
        while (true)
        {
            var slots = Volatile.Read(ref generation.Slots);
            if (slots == _generationEntries)
            {
                Rotate(generation);
                generation = Volatile.Read(ref _current);
                continue;
            }

            // A failed CAS means another insert made progress. Never publish without a slot,
            // even if the thread that filled the generation is paused before its rotation CAS.
            if (Interlocked.CompareExchange(ref generation.Slots, slots + 1, slots) != slots)
                continue;

            // TryAdd can publish a node and then throw during growth. Retain the reservation
            // on an exception; only a confirmed duplicate proves this call added no entry.
            if (!generation.Maps[0].TryAdd(key, value))
            {
                Interlocked.Decrement(ref generation.Slots);
                return;
            }

            if (slots + 1 == _generationEntries)
                Rotate(generation);
            return;
        }
    }

    /// <summary>
    /// Pushes a fresh empty map onto the front and drops the oldest, publishing the new array as one
    /// node. Peers may help a full generation rotate. A lost CAS discards this candidate; the
    /// caller retries admission against the current node if it still needs to insert (point 5).
    /// </summary>
    private void Rotate(Generation observed)
    {
        if (!ReferenceEquals(Volatile.Read(ref _current), observed)) return;

        BeforeRotateForTests?.Invoke();
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
    /// the old node loses its CAS and discards its array (point 5). The
    /// diagnostics counters are not reset: they stay monotone. The caller must ensure no reader
    /// can access this cache while it runs.
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
    /// cost this policy adds over a plain probe. Diagnostics only.</summary>
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
