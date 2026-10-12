using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VoxelEngine.Particles;
using Xunit;

namespace VoxelEngine.Tests.Particles;

/// <summary>
/// Pins <see cref="ParticleRing{T}"/>, the order-preserving circular buffer the four particle
/// systems swap onto in place of <see cref="List{T}"/> (PERF_TODO C12(a)).
///
/// <para><b>Why these facts are written before the systems are swapped.</b> The swap is one line per
/// system and the compiler checks the shapes, but it cannot check the SEQUENCE: a subtly wrong
/// <c>RemoveAt</c> - a swap-remove, an off-by-one on the shorter-side shift, a head that advances on
/// the wrong branch - compiles perfectly and silently corrupts all four systems at once. The one
/// consumer whose submission order reaches the GPU is <c>RenderManager.RenderBlockDebris</c>, which
/// composites overlapping semi-transparent cubes in list order whenever leaves drew that frame (the
/// conditional blend-state argument is on <see cref="ParticleRing{T}"/> itself), so a reordering bug
/// would show up as a subtle, intermittent compositing difference rather than as a crash.</para>
///
/// <para>The oracle is <see cref="List{T}"/> itself: the type being replaced, driven through the
/// identical operation script, with Count, every index and the full enumeration compared after EVERY
/// operation rather than at the end. A differential fact of that shape localizes a divergence to the
/// operation that caused it, and it cannot be satisfied by a container that merely ends up holding
/// the right set.</para>
///
/// <para><b>What these facts do not cover.</b> They are single-threaded, as the particle systems are
/// (all four are driven from World.Update on the main thread and read by the renderers on the same
/// thread), so nothing here says anything about concurrent use - and nothing should, because the
/// type is not intended for it and does not claim to be.</para>
/// </summary>
public sealed class ParticleRingTests
{
    /// <summary>
    /// The centerpiece: a seeded script of several thousand mixed operations driven against both a
    /// <see cref="ParticleRing{T}"/> and a <see cref="List{T}"/>, asserting they agree on Count,
    /// every index and the whole enumeration after every single operation.
    ///
    /// <para>The script deliberately mixes the PRODUCTION shape (evict the head, then append - what
    /// all four systems do at their cap) with plain appends that overrun the initial capacity and
    /// force the growth path, plus head, tail and interior removals. The interior removals are what
    /// exercise both directions of the shorter-side shift, and the four systems' reap loops - which
    /// walk backwards dropping dead particles - take exactly that path.</para>
    ///
    /// <para>Anti-vacuity: every operation kind must have executed a meaningful number of times, and
    /// the ring must actually have been driven past its initial capacity, or a script that happened
    /// to only append would satisfy the equality trivially.</para>
    /// </summary>
    [Fact]
    public void RingMatchesAListUnderTheSameOperations()
    {
        const int initialCapacity = 8;
        var rng = new Random(20250913);
        var ring = new ParticleRing<int>(initialCapacity);
        var oracle = new List<int>();

        var next = 0;
        var evictAndAdd = 0;
        var plainAdd = 0;
        var removeHead = 0;
        var removeTail = 0;
        var removeInterior = 0;
        var maxCount = 0;

        for (var step = 0; step < 6000; step++)
        {
            switch (rng.Next(5))
            {
                case 0 when oracle.Count > 0:
                    // The production shape: at the cap, drop the oldest and append the newest.
                    ring.RemoveAt(0);
                    oracle.RemoveAt(0);
                    ring.Add(next);
                    oracle.Add(next);
                    next++;
                    evictAndAdd++;
                    break;
                case 1:
                case 2:
                    // A bare append, which past the initial capacity takes the growth path.
                    ring.Add(next);
                    oracle.Add(next);
                    next++;
                    plainAdd++;
                    break;
                case 3 when oracle.Count > 0:
                    if (rng.Next(2) == 0)
                    {
                        ring.RemoveAt(0);
                        oracle.RemoveAt(0);
                        removeHead++;
                    }
                    else
                    {
                        ring.RemoveAt(oracle.Count - 1);
                        oracle.RemoveAt(oracle.Count - 1);
                        removeTail++;
                    }
                    break;
                case 4 when oracle.Count > 2:
                    var interior = rng.Next(1, oracle.Count - 1);
                    ring.RemoveAt(interior);
                    oracle.RemoveAt(interior);
                    removeInterior++;
                    break;
                default:
                    continue;
            }

            maxCount = Math.Max(maxCount, oracle.Count);

            Assert.Equal(oracle.Count, ring.Count);
            for (var i = 0; i < oracle.Count; i++)
                Assert.Equal(oracle[i], ring[i]);
            Assert.Equal(oracle, ring.ToList());
        }

        Assert.True(evictAndAdd > 100, $"evict-then-add ran only {evictAndAdd} times");
        Assert.True(plainAdd > 100, $"plain add ran only {plainAdd} times");
        Assert.True(removeHead > 100, $"head removal ran only {removeHead} times");
        Assert.True(removeTail > 100, $"tail removal ran only {removeTail} times");
        Assert.True(removeInterior > 100, $"interior removal ran only {removeInterior} times");
        Assert.True(maxCount > initialCapacity,
            $"the ring never grew past its initial capacity (max count {maxCount})");
    }

    /// <summary>
    /// Growth taken while the buffer is WRAPPED - the one case where a naive <c>Array.Copy</c> of the
    /// backing store would silently rotate the sequence. The ring is filled, evicted from the head so
    /// the head leaves slot 0, refilled so the live run straddles the end of the array, and only then
    /// pushed past capacity. The logical sequence must survive untouched.
    /// </summary>
    [Fact]
    public void GrowthPreservesOrderAcrossTheWrap()
    {
        var ring = new ParticleRing<int>(4);

        foreach (var v in new[] { 1, 2, 3, 4 }) ring.Add(v);   // full, head at slot 0
        ring.RemoveAt(0);
        ring.RemoveAt(0);                                       // head now at slot 2: [3, 4]
        ring.Add(5);
        ring.Add(6);                                            // full again and wrapped: [3, 4, 5, 6]

        Assert.Equal(new[] { 3, 4, 5, 6 }, ring.ToArray());

        ring.Add(7);                                            // grows from a wrapped state

        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, ring.ToArray());
        for (var i = 0; i < ring.Count; i++)
            Assert.Equal(i + 3, ring[i]);

        // And the grown buffer still behaves: more appends than the new capacity, order intact.
        for (var v = 8; v <= 20; v++) ring.Add(v);
        Assert.Equal(Enumerable.Range(3, 18).ToArray(), ring.ToArray());
    }

    /// <summary>
    /// Enumeration is oldest-first and agrees with the indexer, through both the generic and the
    /// non-generic interface - the sequence <c>RenderManager.RenderBlockDebris</c>'s <c>foreach</c>
    /// submits. Checked after evictions, so the head is not at slot 0 and a physical-order enumerator
    /// would produce a rotated sequence.
    /// </summary>
    [Fact]
    public void EnumeratorYieldsOldestFirst()
    {
        var ring = new ParticleRing<int>(4);
        for (var v = 1; v <= 4; v++) ring.Add(v);
        ring.RemoveAt(0);
        ring.RemoveAt(0);
        ring.Add(5);
        ring.Add(6);                                            // wrapped, logical [3, 4, 5, 6]

        var byEnumerator = new List<int>();
        foreach (var v in ring) byEnumerator.Add(v);
        Assert.Equal(new[] { 3, 4, 5, 6 }, byEnumerator);

        var byIndexer = new List<int>();
        for (var i = 0; i < ring.Count; i++) byIndexer.Add(ring[i]);
        Assert.Equal(byEnumerator, byIndexer);

        var byNonGeneric = new List<int>();
        foreach (int v in (IEnumerable)ring) byNonGeneric.Add(v);
        Assert.Equal(byEnumerator, byNonGeneric);

        Assert.Empty(new ParticleRing<int>(4));
    }

    /// <summary>
    /// The bounds guard is against the LIVE count, not the backing capacity - a ring with room to
    /// spare must still reject an index past its last live entry, or a renderer walking a stale count
    /// would read a vacated slot (which now holds <c>default</c>) as if it were a particle.
    /// <c>RemoveAt</c> shares the same guard and is checked here too.
    /// </summary>
    [Fact]
    public void IndexerRejectsOutOfRange()
    {
        var empty = new ParticleRing<int>(8);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => empty.RemoveAt(0));

        var ring = new ParticleRing<int>(8);
        ring.Add(1);
        ring.Add(2);
        ring.Add(3);

        Assert.Throws<ArgumentOutOfRangeException>(() => ring[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[8]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring.RemoveAt(3));

        Assert.Equal(new[] { 1, 2, 3 }, ring.ToArray());
    }
}
