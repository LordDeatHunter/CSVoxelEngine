using System;
using System.Collections.Generic;
using System.Linq;
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
    private static List<Vector3> LitRow(int count)
    {
        var cells = new List<Vector3>(count);
        for (var i = 0; i < count; i++) cells.Add(new Vector3(i, 0f, 0f));
        return cells;
    }

    /// <summary>A system tuned to zero rates never spawns, however many lit cells feed it.</summary>
    [Fact]
    public void ZeroRateSystemSpawnsNothing()
    {
        var system = new FireParticleSystem(0f, 0f, WorldGenerationConfig.FireParticleMax, FireParticleSystem.SpawnOffset);
        var cells = LitRow(8);
        var rng = new Random(3);
        for (var i = 0; i < 60; i++)
            system.Update(0.1f, cells, Vector3.Zero, rng);
        Assert.Empty(system.Particles);
    }

    /// <summary>
    /// Over the same cells and steps, a lower-rate instance never holds more particles than the
    /// default campfire instance (both caps equal, so only the rates differ).
    /// </summary>
    [Fact]
    public void LowerRateSpawnsNoMore()
    {
        var campfire = new FireParticleSystem();
        var sparse = new FireParticleSystem(WorldGenerationConfig.FireEmberPerSecond * 0.25f,
            WorldGenerationConfig.FireSmokePerSecond * 0.25f, WorldGenerationConfig.FireParticleMax,
            FireParticleSystem.SpawnOffset);
        var cells = LitRow(2);
        var rngA = new Random(9);
        var rngB = new Random(9);
        for (var i = 0; i < 20; i++)
        {
            campfire.Update(0.05f, cells, Vector3.Zero, rngA);
            sparse.Update(0.05f, cells, Vector3.Zero, rngB);
            Assert.True(sparse.Particles.Count <= campfire.Particles.Count,
                $"step {i}: sparse {sparse.Particles.Count} > campfire {campfire.Particles.Count}");
        }
        // Anti-vacuity: the sparse instance really spawned something, so the bound is not 0 <= n.
        Assert.NotEmpty(sparse.Particles);
    }

    /// <summary>An instance's own cap bounds it, not the campfire's, however long it is fed.</summary>
    [Fact]
    public void InstanceCapIsRespected()
    {
        var cap = WorldGenerationConfig.FireParticleMax / 8;
        var system = new FireParticleSystem(WorldGenerationConfig.FireEmberPerSecond,
            WorldGenerationConfig.FireSmokePerSecond, cap, FireParticleSystem.SpawnOffset);
        var cells = LitRow(32);
        var rng = new Random(21);
        var peak = 0;
        for (var i = 0; i < 200; i++)
        {
            system.Update(0.05f, cells, Vector3.Zero, rng);
            Assert.True(system.Particles.Count <= cap, $"step {i}: {system.Particles.Count} > cap {cap}");
            peak = Math.Max(peak, system.Particles.Count);
        }
        // Anti-vacuity: the feed really drove the ring to its cap.
        Assert.Equal(cap, peak);
    }

    private static Vector3 Run(FireParticleKind kind, Vector2 wind, int seed, float seconds, int steps)
    {
        var particle = new FireParticle(Vector3.Zero, kind, new Random(seed));
        for (var i = 0; i < steps; i++)
            particle.Update(seconds / steps, wind);
        return particle.Position;
    }

    /// <summary>
    /// SH-27: smoke and embers are carried downwind. The same particle (same seed) ends further
    /// along +X under a +X wind than under a -X one, and further along +Z under +Z than under -Z;
    /// with no wind the sway stays on X only, so the Z path is the straight velocity line and does
    /// not depend on how the time is stepped.
    /// </summary>
    [Fact]
    public void SmokeDriftsDownwind()
    {
        foreach (var kind in new[] { FireParticleKind.Smoke, FireParticleKind.Ember })
        {
            // Shorter than the shortest lifetime of either kind, so the particle is alive throughout
            var seconds = kind == FireParticleKind.Smoke ? 3f : 1f;
            for (var seed = 0; seed < 8; seed++)
            {
                Assert.True(Run(kind, new Vector2(1f, 0f), seed, seconds, 60).X
                            > Run(kind, new Vector2(-1f, 0f), seed, seconds, 60).X);
                Assert.True(Run(kind, new Vector2(0f, 1f), seed, seconds, 60).Z
                            > Run(kind, new Vector2(0f, -1f), seed, seconds, 60).Z);

                var fine = Run(kind, default, seed, seconds, 60);
                var coarse = Run(kind, default, seed, seconds, 1);
                Assert.Equal(coarse.Z, fine.Z, 4);
            }
        }
    }

    [Fact]
    public void PickupSparksAreBrightBriefAndRise()
    {
        foreach (var seed in Enumerable.Range(0, 8))
        {
            var particle = new FireParticle(new Vector3(0f, 1f, 0f), FireParticleKind.Spark, new Random(seed));
            var startY = particle.Position.Y;
            var color = particle.Color;
            Assert.True(color.R > 200 && color.G > 200 && color.B > 120, "a spark is not bright");
            Assert.True(particle.Alpha > 0.5f, "a fresh spark is not visible");

            var died = false;
            for (var i = 0; i < 240 && !died; i++)
                died = !particle.Update(1f / 60f);

            Assert.True(died, "a spark never died");
            Assert.True(particle.Position.Y > startY, "a spark did not rise");
        }

        // A one-off burst adds that many motes to the system
        var system = new FireParticleSystem();
        system.Puff(new Vector3(1f, 1f, 1f), 5, 0.5f, FireParticleKind.Spark, new Random(1));
        Assert.Equal(5, system.Particles.Count);
        Assert.All(system.Particles, p => Assert.Equal(FireParticleKind.Spark, p.Kind));
    }
}
