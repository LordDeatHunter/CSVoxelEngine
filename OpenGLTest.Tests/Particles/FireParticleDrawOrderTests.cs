using System;
using System.Linq;
using Xunit;

namespace silkgltriangle.Tests;

/// <summary>
/// The fire-particle batch is drawn as an additive ember run followed by an alpha-blended
/// smoke run, so the draw order must put every ember first and sort the smoke back-to-front.
/// </summary>
public class FireParticleDrawOrderTests
{
    private static (FireParticleKind[] Kinds, float[] Dist) RandomBatch(int seed, int n)
    {
        var rng = new Random(seed);
        var kinds = new FireParticleKind[n];
        var dist = new float[n];
        for (var i = 0; i < n; i++)
        {
            kinds[i] = rng.Next(2) == 0 ? FireParticleKind.Ember : FireParticleKind.Smoke;
            dist[i] = rng.NextSingle() * 100f;
        }
        return (kinds, dist);
    }

    [Theory]
    [InlineData(1, 37)]
    [InlineData(2, 200)]
    [InlineData(3, 1)]
    public void OrderIsAPermutationWithEmbersFirst(int seed, int n)
    {
        var (kinds, dist) = RandomBatch(seed, n);
        var order = new int[n];

        var embers = FireParticleRenderer.OrderForDraw(kinds, dist, order);

        Assert.Equal(Enumerable.Range(0, n), order.OrderBy(i => i));
        Assert.Equal(kinds.Count(k => k == FireParticleKind.Ember), embers);
        Assert.All(order.Take(embers), i => Assert.Equal(FireParticleKind.Ember, kinds[i]));
        Assert.All(order.Skip(embers), i => Assert.Equal(FireParticleKind.Smoke, kinds[i]));
    }

    [Theory]
    [InlineData(4, 64)]
    [InlineData(5, 300)]
    public void SmokeRunIsBackToFront(int seed, int n)
    {
        var (kinds, dist) = RandomBatch(seed, n);
        var order = new int[n];

        var embers = FireParticleRenderer.OrderForDraw(kinds, dist, order);

        for (var i = embers + 1; i < n; i++)
            Assert.True(dist[order[i - 1]] >= dist[order[i]],
                $"smoke at slot {i} is farther than the one drawn before it");
    }

    [Fact]
    public void SingleKindBatchesReturnTheirBounds()
    {
        var (_, dist) = RandomBatch(6, 50);
        var order = new int[dist.Length];

        var allSmoke = Enumerable.Repeat(FireParticleKind.Smoke, dist.Length).ToArray();
        Assert.Equal(0, FireParticleRenderer.OrderForDraw(allSmoke, dist, order));
        for (var i = 1; i < order.Length; i++) Assert.True(dist[order[i - 1]] >= dist[order[i]]);

        var allEmbers = Enumerable.Repeat(FireParticleKind.Ember, dist.Length).ToArray();
        Assert.Equal(dist.Length, FireParticleRenderer.OrderForDraw(allEmbers, dist, order));
        Assert.Equal(Enumerable.Range(0, dist.Length), order.OrderBy(i => i));
    }
}
