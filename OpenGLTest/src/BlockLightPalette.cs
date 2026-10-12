using System;
using System.Numerics;
using silkgltriangle.Content;

namespace silkgltriangle;

/// <summary>
/// The small palette of block-light colors (SH-26). A block's JSON may declare
/// <c>light.color</c>; <see cref="ContentRegistry"/> registers each distinct color here and the
/// block carries the resulting slot (<see cref="BlockType.LightSlot"/>). Slot 0 is always the
/// default warm torch color, so every emitter that declares no color looks as it always has.
/// GL-free: the renderer uploads <see cref="Linear"/> as <c>uBlockLightPalette</c>.
/// </summary>
public static class BlockLightPalette
{
    /// <summary>Slots the palette holds; the vertex tint code has 3 bits per slot index.</summary>
    public const int MaxSlots = 8;

    /// <summary>The old single torch color, sRGB (1, 0.62, 0.25): slot 0, the default of every emitter.</summary>
    public static readonly Vector3 DefaultSrgb = new(1f, 0.62f, 0.25f);

    // Registering a color within this of an existing slot, per channel, reuses that slot
    private const float MatchTolerance = 0.5f / 255f;

    private static readonly Vector3[] _srgb = new Vector3[MaxSlots];
    private static readonly Vector3[] _linear = new Vector3[MaxSlots];

    /// <summary>Slots in use, at least 1 (the default).</summary>
    public static int Count { get; private set; }

    static BlockLightPalette() => Reset();

    /// <summary>Linear-light color of every slot; unused slots hold the default.</summary>
    public static ReadOnlySpan<Vector3> Linear => _linear;

    /// <summary>The sRGB color a slot was registered with.</summary>
    public static Vector3 SrgbOf(int slot) => _srgb[slot];

    /// <summary>Back to the default slot alone; every slot is set to the default color.</summary>
    internal static void Reset()
    {
        Count = 1;
        for (var i = 0; i < MaxSlots; i++) Write(i, DefaultSrgb);
    }

    /// <summary>
    /// The slot of <paramref name="srgb"/> (0..1 per channel): an existing slot within
    /// 0.5/255 on every channel, else a new one. Throws naming <paramref name="path"/> once the
    /// palette is full.
    /// </summary>
    internal static byte Register(Vector3 srgb, string path)
    {
        for (var i = 0; i < Count; i++)
        {
            var d = Vector3.Abs(_srgb[i] - srgb);
            if (d.X <= MatchTolerance && d.Y <= MatchTolerance && d.Z <= MatchTolerance)
                return (byte)i;
        }
        if (Count == MaxSlots)
            throw new InvalidOperationException(
                $"Block '{path}' declares a {MaxSlots + 1}th distinct light.color; the block-light palette holds at most {MaxSlots} (the default included).");
        Write(Count, srgb);
        return (byte)Count++;
    }

    private static void Write(int slot, Vector3 srgb)
    {
        _srgb[slot] = srgb;
        _linear[slot] = ColorSpace.SrgbToLinear(srgb);
    }
}
