using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Pins <see cref="GenerationalCache{TKey,TValue}"/>, the eviction policy that replaced the
/// wholesale <c>Clear()</c>-at-the-cap in <c>Chunk.ColumnCache</c> and <c>WormCarver.CellCache</c>
/// (PERF_TODO D2). The type is deliberately GL-free, worldgen-free and static-free, so every policy
/// decision is asserted here against exact counts on private instances rather than inferred from a
/// terrain run. No <c>TestContent</c> is needed and no state is shared between facts.
///
/// <para>All sizes are tiny and chosen so the arithmetic is checkable by hand: at
/// <c>capacity = 8, generations = 4</c> a generation takes exactly 2 inserts before rotating, and at
/// <c>capacity = 8, generations = 2</c> exactly 4. The production sizes are irrelevant to the policy
/// and are deliberately NOT read from <c>WorldGenerationConfig</c> - sizing a fact from a shipped
/// constant is how <c>LodBuildPoolTests.StressWorkers</c> documents a fact silently defusing itself.</para>
///
/// <para><b>The negative control for each fact</b>, i.e. the source mutation it is here to catch:
/// <list type="bullet">
/// <item><see cref="AHitInTheNewestGenerationNeverCallsTheFactoryAndNeverWrites"/> - promotion
/// reinstated on every non-newest hit instead of only on a hit in the oldest generation. That is the
/// variant that puts a shared bucket-lock write on the common path under a LOD sweep, and its only
/// visible signature is the promotion count moving off zero here.</item>
/// <item><see cref="AnEntryTouchedInItsOldestGenerationSurvivesRotation"/> and
/// <see cref="PromotionKeepsAnEntryPastTheRotationThatWouldHaveDroppedIt"/> - promotion removed
/// altogether, or <c>Rotate</c> dropping the NEWEST map rather than the oldest. Either way the
/// touched entry stops surviving, which is the whole point of the round.</item>
/// <item><see cref="AnEntryNeverTouchedIsEvictedAndRecomputedExactlyOnce"/> - <c>Rotate</c> keeping
/// the oldest map (nothing is ever evicted, so the cache grows without bound and this key is never
/// recomputed).</item>
/// <item><see cref="RotationsCountEveryInsertIncludingPromotions"/> - a promotion that does not
/// spend an insert slot, which breaks the memory ceiling argument, and <c>&gt;=</c> instead of
/// <c>==</c> on the write counter, which rotates on every insert past the cap.</item>
/// <item><see cref="ResidencyNeverExceedsCapacityUnderPromotions"/> - the same, seen from the
/// residency side, where the ceiling claim can actually be violated.</item>
/// <item><see cref="EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads"/> - a key inserted under a
/// different key, or a torn read of a half-rotated generation array.</item>
/// <item><see cref="AValueTypeCacheCompilesAndRoundTrips"/> - a <c>where TValue : class</c>
/// constraint reintroduced, or the probe rewritten as a null test. Either locks the type out of
/// <c>TreeGenerator.ShapeCache</c> and <c>CactusGenerator.ShapeCache</c>, whose values are value
/// tuples, and this fact stops compiling.</item>
/// </list></para>
///
/// <para><b>What the concurrency fact proves, and what it does not.</b>
/// <see cref="EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads"/> demonstrates the absence of the
/// races it happens to exercise, on this machine, under this scheduler, for this deadline. It is not
/// a proof that the design is race-free: that argument is the numbered invariant list on
/// <see cref="GenerationalCache{TKey,TValue}"/> itself, and this fact is a check on it rather than a
/// substitute for it. The deterministic facts above are the ones that fail loudly when the policy is
/// weakened.</para>
/// </summary>
public sealed class GenerationalCacheTests
{
    /// <summary>
    /// A factory that counts its own calls, so "the factory ran" and "the cache missed" can be
    /// asserted against each other rather than one standing in for the other.
    /// </summary>
    private sealed class CountingFactory
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        internal Func<int, string> Func => key =>
        {
            Interlocked.Increment(ref _calls);
            return Value(key);
        };

        internal static string Value(int key) => $"column:{key}";
    }

    [Fact]
    public void AHitInTheNewestGenerationNeverCallsTheFactoryAndNeverWrites()
    {
        // 8 / 4 => a generation rotates after exactly 2 inserts.
        var cache = new GenerationalCache<int, string>(8, 4);
        var factory = new CountingFactory();

        Assert.Equal(CountingFactory.Value(7), cache.GetOrAdd(7, factory.Func));

        // Five hits in the newest map: one probe each, no factory, no write of any kind.
        for (var i = 0; i < 5; i++)
            Assert.Equal(CountingFactory.Value(7), cache.GetOrAdd(7, factory.Func));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Promotions);
        Assert.Equal(0, cache.Rotations);
        Assert.Equal(1, cache.EntryCount);

        // One rotation puts key 7 in generation 1 of 4 - a MIDDLE generation, not the oldest.
        cache.GetOrAdd(200, factory.Func);
        Assert.Equal(1, cache.Rotations);

        Assert.Equal(CountingFactory.Value(7), cache.GetOrAdd(7, factory.Func));
        Assert.Equal(2, factory.Calls);
        Assert.Equal(0, cache.Promotions);

        // A second rotation: key 7 is now in generation 2 of 4, still not the oldest.
        cache.GetOrAdd(201, factory.Func);
        cache.GetOrAdd(202, factory.Func);
        Assert.Equal(2, cache.Rotations);

        Assert.Equal(CountingFactory.Value(7), cache.GetOrAdd(7, factory.Func));

        // The fact that fails the moment promotion is widened past the oldest generation: two hits
        // outside the newest map, and still not a single shared write.
        Assert.Equal(0, cache.Promotions);
        Assert.Equal(4, factory.Calls);
        Assert.Equal(4, cache.Misses);
    }

    [Fact]
    public void AnEntryTouchedInItsOldestGenerationSurvivesRotation()
    {
        var cache = new GenerationalCache<int, string>(8, 4);
        var factory = new CountingFactory();

        // Three rotations (2 inserts each) walk key 0 from the newest map to the oldest.
        cache.GetOrAdd(0, factory.Func);
        foreach (var filler in new[] { 100, 101, 102, 103, 104 })
            cache.GetOrAdd(filler, factory.Func);

        Assert.Equal(3, cache.Rotations);
        Assert.Equal(6, factory.Calls);
        Assert.Equal(0, cache.Promotions);

        // Key 0 is now in the map the next rotation drops. Reading it takes its second chance.
        Assert.Equal(CountingFactory.Value(0), cache.GetOrAdd(0, factory.Func));
        Assert.Equal(1, cache.Promotions);
        Assert.Equal(6, factory.Calls);

        // One more insert completes that generation and rotates the map key 0 came from out of the
        // cache entirely. Under the old wholesale Clear() the whole cache would be empty here.
        cache.GetOrAdd(105, factory.Func);
        Assert.Equal(4, cache.Rotations);
        Assert.Equal(7, factory.Calls);

        Assert.True(cache.TryPeek(0, out var survived), "the promoted entry did not survive its rotation");
        Assert.Equal(CountingFactory.Value(0), survived);

        // And it is served without recomputing: this is the "reuse survives a move" property.
        Assert.Equal(CountingFactory.Value(0), cache.GetOrAdd(0, factory.Func));
        Assert.Equal(7, factory.Calls);
        Assert.Equal(7, cache.Misses);
    }

    [Fact]
    public void AnEntryNeverTouchedIsEvictedAndRecomputedExactlyOnce()
    {
        var cache = new GenerationalCache<int, string>(8, 4);
        var factory = new CountingFactory();

        cache.GetOrAdd(0, factory.Func);

        // Seven more distinct keys: four rotations, the last of which drops the map holding key 0.
        foreach (var filler in new[] { 100, 101, 102, 103, 104, 105, 106 })
            cache.GetOrAdd(filler, factory.Func);

        Assert.Equal(4, cache.Rotations);
        Assert.Equal(8, factory.Calls);
        Assert.Equal(8, cache.Misses);
        Assert.Equal(0, cache.Promotions);

        Assert.False(cache.TryPeek(0, out _), "an untouched entry outlived the generation it was in");

        // Recomputed exactly once, and the recomputed value is the same one (the invariant that
        // makes eviction a cost rather than a correctness hazard).
        Assert.Equal(CountingFactory.Value(0), cache.GetOrAdd(0, factory.Func));
        Assert.Equal(9, factory.Calls);
        Assert.Equal(9, cache.Misses);

        Assert.Equal(CountingFactory.Value(0), cache.GetOrAdd(0, factory.Func));
        Assert.Equal(9, factory.Calls);
    }

    [Fact]
    public void PromotionKeepsAnEntryPastTheRotationThatWouldHaveDroppedIt()
    {
        // 8 / 2 => 4 inserts per generation, and with two generations the second one IS the oldest,
        // so every non-newest hit is a promotion. Keys 0 and 1 enter the same map on the same tick:
        // the only difference between them is that one is read while it sits in that map.
        var cache = new GenerationalCache<int, string>(8, 2);
        var factory = new CountingFactory();

        foreach (var key in new[] { 0, 1, 2, 3 })
            cache.GetOrAdd(key, factory.Func);
        Assert.Equal(1, cache.Rotations);

        Assert.Equal(CountingFactory.Value(0), cache.GetOrAdd(0, factory.Func));
        Assert.Equal(1, cache.Promotions);

        foreach (var key in new[] { 4, 5, 6 })
            cache.GetOrAdd(key, factory.Func);
        Assert.Equal(2, cache.Rotations);

        // Same age, same map, same rotation - only the touched one is still here.
        Assert.True(cache.TryPeek(0, out var kept), "the touched entry was dropped by its rotation");
        Assert.Equal(CountingFactory.Value(0), kept);
        Assert.False(cache.TryPeek(1, out _), "the untouched control entry was retained");

        Assert.Equal(7, factory.Calls);
    }

    [Fact]
    public void RotationsCountEveryInsertIncludingPromotions()
    {
        // 8 / 2 => 4 inserts per generation. 10 misses + 2 promotions = 12 inserts = 3 rotations.
        // If a promotion did not spend an insert slot there would be 10 inserts and 2 rotations, so
        // this count is exactly the claim under test: promotions are charged to the budget, which is
        // what keeps a generation's tenancy - and therefore the memory ceiling - exact.
        var cache = new GenerationalCache<int, string>(8, 2);
        var factory = new CountingFactory();

        foreach (var key in new[] { 0, 1, 2, 3 })
            cache.GetOrAdd(key, factory.Func);

        cache.GetOrAdd(0, factory.Func);

        foreach (var key in new[] { 4, 5, 6 })
            cache.GetOrAdd(key, factory.Func);

        cache.GetOrAdd(0, factory.Func);

        foreach (var key in new[] { 7, 8, 9 })
            cache.GetOrAdd(key, factory.Func);

        Assert.Equal(10, cache.Misses);
        Assert.Equal(10, factory.Calls);
        Assert.Equal(2, cache.Promotions);
        Assert.Equal(3, cache.Rotations);
    }

    [Fact]
    public void ResidencyNeverExceedsCapacityUnderPromotions()
    {
        // The same interleaved workload as RotationsCountEveryInsertIncludingPromotions, watched
        // from the residency side - the place the memory-ceiling claim can actually be violated. A
        // promotion duplicates one key across two maps, so this is where a promotion that did not
        // spend an insert slot would show up as overshoot.
        var cache = new GenerationalCache<int, string>(8, 2);
        var factory = new CountingFactory();
        var peak = 0;

        void Ask(int key)
        {
            cache.GetOrAdd(key, factory.Func);
            var resident = cache.EntryCount;
            Assert.True(resident <= cache.Capacity,
                $"residency {resident} exceeded capacity {cache.Capacity} after key {key}");
            if (resident > peak) peak = resident;
        }

        foreach (var key in new[] { 0, 1, 2, 3, 0, 4, 5, 6, 0, 7, 8, 9 })
            Ask(key);

        // Anti-vacuity: the bound is only meaningful if the workload actually presses against it.
        // It does, to within one entry - and the missing entry is the mechanism, not slack. The
        // insert that would take residency to the ceiling is by construction the same call that
        // fills the generation's budget and therefore rotates, dropping a full oldest map before
        // the caller returns. So the peak observable BETWEEN calls is capacity - 1 whenever every
        // generation is full: (generations - 1) full maps plus a newest map holding one short of
        // its budget. Asserting the exact peak rather than a loose bound is what makes this fact
        // fail if a promotion ever stops spending an insert slot.
        Assert.Equal(cache.Capacity - 1, peak);
        Assert.Equal(2, cache.Promotions);
    }

    [Fact]
    public void MissesCountFactoryCallsExactly()
    {
        var cache = new GenerationalCache<int, string>(16, 4);
        var factory = new CountingFactory();

        for (var key = 0; key < 100; key++)
            Assert.Equal(CountingFactory.Value(key), cache.GetOrAdd(key, factory.Func));

        Assert.Equal(100, factory.Calls);
        Assert.Equal(100, cache.Misses);

        // The four most recent keys are still resident, so re-asking them moves nothing at all.
        for (var key = 96; key < 100; key++)
            Assert.Equal(CountingFactory.Value(key), cache.GetOrAdd(key, factory.Func));

        Assert.Equal(100, factory.Calls);
        Assert.Equal(100, cache.Misses);
        Assert.Equal(0, cache.Promotions);
    }

    [Fact]
    public void EveryKeyAlwaysReturnsItsOwnValueUnderFourThreads()
    {
        // The production shape: four chunk-generation threads plus the LOD builder on one cache,
        // with a key range well past capacity so rotations run continuously under the load.
        const int capacity = 512;
        const int keyRange = capacity * 3;
        const int deadlineMs = 1500;

        var cache = new GenerationalCache<int, string>(capacity, 4);
        var failures = new ConcurrentBag<string>();
        var clock = Stopwatch.StartNew();
        var operations = 0L;

        var threads = new Thread[4];
        for (var t = 0; t < threads.Length; t++)
        {
            var seed = t;
            threads[t] = new Thread(() =>
            {
                var rng = new Random(9000 + seed);
                var local = 0L;

                while (clock.ElapsedMilliseconds < deadlineMs)
                {
                    for (var i = 0; i < 256; i++)
                    {
                        var key = rng.Next(keyRange);
                        var value = cache.GetOrAdd(key, CountingFactory.Value);

                        // The one thing an eviction, a duplicate or a rotation race must never be
                        // able to cause: a value belonging to some other key.
                        if (!string.Equals(value, CountingFactory.Value(key), StringComparison.Ordinal))
                            failures.Add($"key {key} returned '{value}'");

                        local++;
                    }
                }

                Interlocked.Add(ref operations, local);
            })
            {
                IsBackground = true,
                Name = $"GenerationalCacheStress-{seed}"
            };
            threads[t].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        Assert.Empty(failures);

        // Anti-vacuity: a fact that ran three lookups and rotated nothing would pass the assertion
        // above without exercising anything the type does.
        Assert.True(operations > 100_000, $"only {operations} lookups ran; the stress did not engage");
        Assert.True(cache.Rotations > 0, "no generation rotated; eviction was never exercised");
        Assert.True(cache.EntryCount <= cache.Capacity + threads.Length,
            $"residency {cache.EntryCount} exceeded capacity {cache.Capacity} by more than the " +
            "one-entry-per-in-flight-thread rotation race allows");
    }

    [Fact]
    public void AValueTypeCacheCompilesAndRoundTrips()
    {
        // This fact exists to be a compile-time guard: GenerationalCache carries no
        // `where TValue : class` constraint, because TreeGenerator.ShapeCache and
        // CactusGenerator.ShapeCache hold (int, record struct) tuples and a class constraint would
        // lock the type out of those conversions. Reintroducing the constraint, or rewriting the
        // probe as a null test, breaks this file.
        var cache = new GenerationalCache<(int X, int Z), (int Sum, int Product)>(8, 4);
        var calls = 0;

        (int Sum, int Product) Factory((int X, int Z) key)
        {
            calls++;
            return (key.X + key.Z, key.X * key.Z);
        }

        Assert.Equal((7, 12), cache.GetOrAdd((3, 4), Factory));
        Assert.Equal((7, 12), cache.GetOrAdd((3, 4), Factory));
        Assert.Equal(1, calls);

        Assert.True(cache.TryPeek((3, 4), out var peeked));
        Assert.Equal((7, 12), peeked);

        // default(TValue) for a value type is a legitimate cached value, not an "absent" marker.
        Assert.Equal((0, 0), cache.GetOrAdd((0, 0), Factory));
        Assert.Equal(2, calls);
        Assert.Equal((0, 0), cache.GetOrAdd((0, 0), Factory));
        Assert.Equal(2, calls);
        Assert.Equal(2, cache.Misses);
    }
}
