using System;
using System.Numerics;
using VoxelEngine.Effects.Lighting;
using Xunit;

namespace VoxelEngine.Tests.Effects.Lighting;

/// <summary>
/// The per-corner block-light tint code: how four (level, slot) light samples, or a per-slot
/// weight set, resolve to a two-slot palette blend, and how that code decodes.
/// </summary>
public sealed class LightTintTests
{
    private readonly int _other;

    public LightTintTests()
    {
        TestContent.EnsureLoaded();
        // A registered slot whose color differs from the default, so two-slot mixes are not vacuous
        _other = -1;
        var palette = BlockLightPalette.Linear;
        for (var i = 1; i < BlockLightPalette.Count; i++)
            if (Vector3.Distance(palette[i], palette[0]) > 1e-3f) { _other = i; break; }
        Assert.True(_other > 0, "content registers no light color distinct from the default");
    }

    private static Vector3 Decode(int code) => LightTint.Decode(code, BlockLightPalette.Linear);

    [Fact]
    public void ASingleSlotDecodesExactlyToItsColor()
    {
        for (var slot = 0; slot < BlockLightPalette.Count; slot++)
        {
            var code = LightTint.FromSamples(15, slot, 3, slot, 0, 0, 7, slot);
            Assert.Equal(BlockLightPalette.Linear[slot], Decode(code));
            Assert.Equal(BlockLightPalette.Linear[slot], Decode(LightTint.Flat(9, slot)));
        }
    }

    [Fact]
    public void NoLightIsCodeZero()
    {
        var code = LightTint.FromSamples(0, _other, 0, _other, 0, 0, 0, _other);
        Assert.Equal(0, code);
        Assert.Equal(0, LightTint.Flat(0, _other));
        Assert.Equal(0, LightTint.FromWeights(new float[BlockLightPalette.MaxSlots]));
        Assert.Equal(BlockLightPalette.Linear[0], Decode(code));
    }

    [Fact]
    public void TheCodeDoesNotDependOnSampleOrder()
    {
        var samples = new (int L, int S)[] { (12, 0), (5, _other), (0, _other), (9, 0) };
        var expected = Code(samples);
        Assert.NotEqual(expected & 7, (expected >> 3) & 7);
        foreach (var perm in Permutations(4))
        {
            var p = new[] { samples[perm[0]], samples[perm[1]], samples[perm[2]], samples[perm[3]] };
            Assert.Equal(expected, Code(p));
        }
    }

    [Fact]
    public void ATwoSlotMixLiesBetweenTheColorsNearerTheHeavier()
    {
        var a = BlockLightPalette.Linear[0];
        var b = BlockLightPalette.Linear[_other];
        foreach (var (la, lb) in new[] { (15, 4), (4, 15), (10, 6), (6, 10) })
        {
            var c = Decode(LightTint.FromSamples(la, 0, lb, _other, 0, 0, 0, 0));
            // On the segment: distances to the two ends add up to its length
            Assert.Equal(Vector3.Distance(a, b), Vector3.Distance(c, a) + Vector3.Distance(c, b), 3);
            if (la > lb) Assert.True(Vector3.Distance(c, a) < Vector3.Distance(c, b));
            else Assert.True(Vector3.Distance(c, b) < Vector3.Distance(c, a));
        }
    }

    [Fact]
    public void RaisingASlotsWeightNeverMovesTheDecodeAwayFromIt()
    {
        var b = BlockLightPalette.Linear[_other];
        var previous = float.MaxValue;
        for (var lb = 0; lb <= 45; lb++)
        {
            // Spread lb over up to three samples so the weight can exceed one sample's range
            var s1 = Math.Min(lb, 15);
            var s2 = Math.Clamp(lb - 15, 0, 15);
            var s3 = Math.Clamp(lb - 30, 0, 15);
            var d = Vector3.Distance(Decode(LightTint.FromSamples(15, 0, s1, _other, s2, _other, s3, _other)), b);
            Assert.True(d <= previous + 1e-5f, $"weight {lb}: {d} > {previous}");
            previous = d;
        }
    }

    [Fact]
    public void WeightsAndSamplesAgree()
    {
        var rng = new Random(26);
        for (var n = 0; n < 2000; n++)
        {
            var l = new int[4];
            var s = new int[4];
            var w = new float[BlockLightPalette.MaxSlots];
            for (var i = 0; i < 4; i++)
            {
                l[i] = rng.Next(0, 16);
                s[i] = rng.Next(0, BlockLightPalette.MaxSlots);
                w[s[i]] += l[i];
            }
            Assert.Equal(LightTint.FromWeights(w), LightTint.FromSamples(l[0], s[0], l[1], s[1], l[2], s[2], l[3], s[3]));
        }
    }

    private static int Code((int L, int S)[] p) =>
        LightTint.FromSamples(p[0].L, p[0].S, p[1].L, p[1].S, p[2].L, p[2].S, p[3].L, p[3].S);

    private static System.Collections.Generic.IEnumerable<int[]> Permutations(int n)
    {
        var a = new int[n];
        for (var i = 0; i < n; i++) a[i] = i;
        return Permute(a, 0);

        static System.Collections.Generic.IEnumerable<int[]> Permute(int[] a, int k)
        {
            if (k == a.Length) { yield return (int[])a.Clone(); yield break; }
            for (var i = k; i < a.Length; i++)
            {
                (a[k], a[i]) = (a[i], a[k]);
                foreach (var p in Permute(a, k + 1)) yield return p;
                (a[k], a[i]) = (a[i], a[k]);
            }
        }
    }
}
