namespace silkgltriangle;

/// <summary>
/// Encoding of the per-block state byte (see <c>Chunk._state</c>) for fluid cells, mirroring
/// Minecraft's water levels. Only three shapes are legal: <see cref="Source"/> (0), a flowing
/// skirt level 1..7 (higher means thinner), and <see cref="Falling"/> (8) for a column of
/// water pouring down, which is always full height.
///
/// Every non-fluid write zeroes the state byte, so an unmanaged cell reads as a source — which
/// is exactly what generated sea water is, and why the sea needs no state array at all.
/// </summary>
public static class FluidState
{
    /// <summary>A full, self-sustaining fluid cell. Also the value of an untouched state byte.</summary>
    public const byte Source = 0;

    /// <summary>A cell fed from above: full height, spreads sideways only where it lands.</summary>
    public const byte Falling = 8;

    /// <summary>Bits holding the flowing level.</summary>
    public const byte LevelMask = 7;

    /// <summary>Bit marking a falling cell.</summary>
    public const byte FallingBit = 8;

    /// <summary>Thinnest flowing level; level 7 dries up on the next recompute.</summary>
    public const int MaxLevel = 7;

    /// <summary>Height of a source's surface: Minecraft's 8/9 of a block, so a still sea
    /// sits slightly below the cell top and the face above it stays visible.</summary>
    public const float SourceHeight = 8f / 9f;

    /// <summary>Whether the cell is a full source (infinite, never recomputed away).</summary>
    public static bool IsSource(byte state) => state == Source;

    /// <summary>Whether the cell is fed from above and therefore fills its whole height.</summary>
    public static bool IsFalling(byte state) => (state & FallingBit) != 0;

    /// <summary>Flowing level, 1..7; 0 for a source and for a falling cell.</summary>
    public static int Level(byte state) => state & LevelMask;

    /// <summary>Minecraft's "amount" of fluid in the cell, 8 being full. What the spread rules
    /// decrement as water travels away from its source.</summary>
    public static int Amount(byte state) => IsFalling(state) ? 8 : 8 - Level(state);

    /// <summary>State byte of a flowing cell at <paramref name="level"/> (1..7).</summary>
    public static byte Flowing(int level) => (byte)level;

    /// <summary>Height of the fluid surface inside the cell, 0..1. A falling cell fills the
    /// cell completely; callers override with 1.0 when another fluid cell sits directly above,
    /// so stacked water has no seam.</summary>
    public static float SurfaceHeight(byte state) => IsFalling(state) ? 1f : (8 - Level(state)) / 9f;
}
