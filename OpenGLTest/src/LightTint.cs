using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace silkgltriangle;

/// <summary>
/// The 10-bit block-light tint code a vertex corner carries (SH-26), and its C# decode, the
/// twin of the shader's. Bits 0-2 are the dominant palette slot A, bits 3-5 slot B, bits 6-9 q
/// (0..15), B's share of the mix being <c>t = q / WeightDivisor</c> (so t never exceeds 0.5).
/// The form is canonical: q = 0 forces B = 0, and no block light at all is code 0.
/// </summary>
internal static class LightTint
{
    /// <summary>Divisor of q in B's share; the shader mirrors it.</summary>
    public const int WeightDivisor = 30;

    /// <summary>Largest q; B's share stays at or below one half.</summary>
    private const int MaxQ = 15;

    /// <summary>A single-slot code: the slot while there is light, else 0.</summary>
    public static int Flat(int level, int slot) => level > 0 ? slot : 0;

    /// <summary>
    /// The code of a per-slot weight set (length <see cref="BlockLightPalette.MaxSlots"/>): the
    /// two largest positive weights, the lower slot first on a tie. All weights at or below 0
    /// give 0.
    /// </summary>
    public static int FromWeights(ReadOnlySpan<float> w)
    {
        int a = -1, b = -1;
        for (var i = 0; i < w.Length; i++)
        {
            if (w[i] <= 0f) continue;
            if (a < 0 || w[i] > w[a]) { b = a; a = i; }
            else if (b < 0 || w[i] > w[b]) b = i;
        }
        if (a < 0) return 0;
        if (b < 0) return a;
        // Round half up, the same rounding FromSamples' integer form does
        var q = Math.Min((int)MathF.Floor(WeightDivisor * w[b] / (w[a] + w[b]) + 0.5f), MaxQ);
        return q == 0 ? a : a | b << 3 | q << 6;
    }

    /// <summary>
    /// The code of four (level, slot) light samples, weighted by level. Depends only on the
    /// multiset of samples. Allocation-free; the common case (one slot among the lit samples)
    /// returns straight away.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FromSamples(int l0, int s0, int l1, int s1, int l2, int s2, int l3, int s3)
    {
        // Fast path: every lit sample shares one slot
        var slot = -1;
        var mixed = false;
        if (l0 > 0) slot = s0;
        if (l1 > 0) { if (slot < 0) slot = s1; else mixed |= s1 != slot; }
        if (l2 > 0) { if (slot < 0) slot = s2; else mixed |= s2 != slot; }
        if (l3 > 0) { if (slot < 0) slot = s3; else mixed |= s3 != slot; }
        if (!mixed) return slot < 0 ? 0 : slot;
        return Mixed(l0, s0, l1, s1, l2, s2, l3, s3);
    }

    private static int Mixed(int l0, int s0, int l1, int s1, int l2, int s2, int l3, int s3)
    {
        // Up to four distinct slots, accumulated in fixed locals
        int k0 = -1, k1 = -1, k2 = -1, k3 = -1;
        int w0 = 0, w1 = 0, w2 = 0, w3 = 0;
        Add(l0, s0); Add(l1, s1); Add(l2, s2); Add(l3, s3);

        int a = -1, wa = 0, b = -1, wb = 0;
        Pick(k0, w0); Pick(k1, w1); Pick(k2, w2); Pick(k3, w3);
        if (a < 0) return 0;
        if (b < 0) return a;
        var q = Math.Min((WeightDivisor * wb + (wa + wb) / 2) / (wa + wb), MaxQ);
        return q == 0 ? a : a | b << 3 | q << 6;

        void Add(int level, int s)
        {
            if (level <= 0) return;
            if (k0 == s || k0 < 0) { k0 = s; w0 += level; }
            else if (k1 == s || k1 < 0) { k1 = s; w1 += level; }
            else if (k2 == s || k2 < 0) { k2 = s; w2 += level; }
            else { k3 = s; w3 += level; }
        }

        // Keeps the top two; a heavier weight wins, an equal one goes to the lower slot
        void Pick(int k, int w)
        {
            if (k < 0) return;
            if (a < 0 || w > wa || (w == wa && k < a)) { b = a; wb = wa; a = k; wa = w; }
            else if (b < 0 || w > wb || (w == wb && k < b)) { b = k; wb = w; }
        }
    }

    /// <summary>The linear color a code stands for; mirrors the shader's decode.</summary>
    public static Vector3 Decode(int code, ReadOnlySpan<Vector3> palette) =>
        Vector3.Lerp(palette[code & 7], palette[(code >> 3) & 7], (code >> 6) / (float)WeightDivisor);
}
