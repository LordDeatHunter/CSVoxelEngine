using System;
using System.Collections.Generic;
using System.Numerics;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// Order facts for <see cref="FireParticleSystem"/>, added with PERF_TODO item C12(a) (the four
/// particle systems moved off <see cref="List{T}"/> onto <see cref="ParticleRing{T}"/> so that
/// evicting the oldest entry at the cap is O(1) instead of an O(n) shift). No fire-particle test
/// file existed before this one.
///
/// <para><b>The finding that matters for the round's honesty.</b> Fire is the ONLY one of the four
/// systems whose eviction path is realistically reachable in play: a single lit cell in range
/// produces FireEmberPerSecond = 36 embers plus FireSmokePerSecond = 8 smoke puffs EVERY second,
/// against a FireParticleMax of 512, so a few lit cells in view saturate the cap in seconds and it
/// then evicts on every spawn. The other three sit far below their caps in normal play - ambient
/// leaves settle near 270 of 512, exhaled bubbles near 4 of 128 (BubblePerSecond is 1 against a 4 s
/// lifetime) and mining debris near 40 of 512. So C12(a)'s measured value is nil-to-negligible for
/// three systems and small for this one; what the change actually buys is a clean, shared,
/// order-preserving invariant in place of four hand-rolled O(n) evictions. It landed because the
/// argument is clean and the behaviour is pinned, not because it removes milliseconds.</para>
/// </summary>
public sealed class FireParticleTests
{
    public FireParticleTests() => TestContent.EnsureLoaded();

    /// <summary>
    /// C12(a): eviction at the cap drops the OLDEST particle and leaves every survivor in spawn
    /// order. FireParticleRenderer depth-sorts every frame and so does not observe source order,
    /// but the ring is shared with BlockDebrisSystem, whose submission order does reach the GPU
    /// (ParticleRing's doc comment carries the argument), and the invariant is one invariant.
    ///
    /// Shape: fill to the cap with AddForTest (a bare append with no eviction, exactly as it was
    /// over the List this replaces), then drive ONE real Update so the eviction happens through
    /// the production Spawn path. Identity is by REFERENCE, because Update moves every position.
    /// A 0.03 s step at FireEmberPerSecond = 36 is 1.08 embers of debt - exactly one spawn - and
    /// 0.24 smoke puffs, which is none; and no pre-filled ember can die in that step, since the
    /// shortest possible ember lifetime is FireEmberLifetime * 0.6 = 1.08 s. So the survivor set
    /// is exactly the fill minus its head, with the newly spawned ember at the tail.
    /// </summary>
    [Fact]
    public void EvictionDropsTheOldestFireParticleAndKeepsTheRestInSpawnOrder()
    {
        var max = WorldGenerationConfig.FireParticleMax;
        var system = new FireParticleSystem();
        var rng = new Random(14);

        var filled = new List<FireParticle>();
        for (var i = 0; i < max; i++)
        {
            var particle = new FireParticle(new Vector3(0.5f, 0.5f, 0.5f), FireParticleKind.Ember, rng);
            filled.Add(particle);
            system.AddForTest(particle);
        }
        Assert.Equal(max, system.Particles.Count);

        // One lit cell, well inside FireParticleRadius of the camera; Update takes the list
        // directly, so no CampfireManager is needed.
        var litCells = new List<Vector3> { Vector3.Zero };
        system.Update(0.03f, litCells, Vector3.Zero, rng);

        Assert.Equal(max, system.Particles.Count);
        for (var i = 0; i < max - 1; i++)
            Assert.Same(filled[i + 1], system.Particles[i]);
        Assert.DoesNotContain(filled[0], system.Particles);
        // Anti-vacuity: the tail really is a newly spawned ember, not the fill shifted along.
        Assert.NotSame(filled[max - 1], system.Particles[max - 1]);
        Assert.Equal(FireParticleKind.Ember, system.Particles[max - 1].Kind);
    }
}
