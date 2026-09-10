using System;

namespace silkgltriangle;

/// <summary>
/// Crossed-stick branch piles: up to 4 long thin logs sharing one air cell above the
/// ground, laid alternately across each other campfire-style. Player-only (nothing stamps
/// them during worldgen): the count rides the chunk state byte, written 1..max by the
/// place_stack use, and a bare block with no state reads as a single stick. A pure
/// function of world position like <see cref="PebbleGenerator"/>, so neighbours agree
/// without shared state.
/// </summary>
public static class BranchPileGenerator
{
    private const int VariantSalt = 0xB17E01;
    private const int JitterSalt = 0xB17E02;
    private const int LengthSalt = 0xB17E03;
    private const int CenterSalt = 0xB17E04;

    /// <summary>Edge thickness of one stick, in blocks.</summary>
    public const float Thickness = 0.13f;

    /// <summary>Base height of stick <paramref name="i"/> (0-based) above the cell floor.</summary>
    public static float LayerY(int i) => i * 0.095f;

    /// <summary>Top of a <paramref name="count"/>-stick pile, for hitbox coverage checks.</summary>
    public static float PileTop(int count) => LayerY(count - 1) + Thickness;

    /// <summary>
    /// Sticks rendered (and branches dropped) for a pile cell carrying this state byte: an
    /// explicit player-built pile (1..<paramref name="max"/>) reads back directly, while
    /// anything else — a bare block, which worldgen never stamps — is a single stick.
    /// </summary>
    public static int CountWithState(byte state, int max) =>
        state >= 1 && state <= max ? state : 1;

    /// <summary>Layout rotation (0-3 quarter turns) so neighbouring piles don't look identical.</summary>
    public static int VariantAt(int x, int z) => (int)(UnitHash(x, z, VariantSalt) * 4f);

    /// <summary>Yaw of stick <paramref name="i"/> at (x, z): the pile's cross plus a small
    /// position-hash jitter so identical counts still differ cell to cell.</summary>
    public static float YawAt(int x, int z, int i) =>
        BaseYaw(i) + (UnitHash(x, z, JitterSalt, i) - 0.5f) * (16f * MathF.PI / 180f);

    /// <summary>Length of stick <paramref name="i"/> at (x, z), in blocks: 0.8..0.9.</summary>
    public static float LengthAt(int x, int z, int i) =>
        0.8f + UnitHash(x, z, LengthSalt, i) * 0.1f;

    /// <summary>
    /// Cell-local centre of stick <paramref name="i"/>. Base offsets are defined once, then
    /// rotated by the variant and nudged by the position hash.
    /// </summary>
    public static void GetCenter(int x, int z, int variant, int i, out float cx, out float cz)
    {
        (cx, cz) = i switch
        {
            0 => (0.50f, 0.50f),
            1 => (0.53f, 0.48f),
            2 => (0.47f, 0.53f),
            _ => (0.52f, 0.52f),
        };

        for (var r = 0; r < (variant & 3); r++)
            (cx, cz) = (1f - cz, cx);

        cx += (UnitHash(x, z, CenterSalt, i * 2) - 0.5f) * 0.05f;
        cz += (UnitHash(x, z, CenterSalt, i * 2 + 1) - 0.5f) * 0.05f;
    }

    // Alternating cross, in degrees: each new stick lies roughly across the last.
    private static float BaseYaw(int i) => (i switch
    {
        0 => 15f,
        1 => 105f,
        2 => 60f,
        _ => 150f,
    }) * MathF.PI / 180f;

    private static float UnitHash(int x, int z, int salt, int i = 0)
    {
        var h = SplitMix64.Hash(x, z, WorldGenerationConfig.Seed ^ (salt + i * 0x9E37));
        return (SplitMix64.Mix(h + 0x9E3779B97F4A7C15UL) >> 40) * (1f / (1 << 24));
    }
}
