using System;

namespace VoxelEngine.WorldGen.Trees;

/// <summary>
/// Encoding of the per-block state byte (see <c>Chunk._state</c>) for leaf cells of the
/// reworked trees: bits 0-2 hold the hydration (how far the leaf may still be from wood,
/// 0..<see cref="MaxHydration"/>) and bit 3 the persistent flag, set on player-placed leaves
/// so they never decay. The two fields are independent.
/// </summary>
public static class LeafState
{
    /// <summary>Bits holding the hydration.</summary>
    public const byte HydrationMask = 0x07;

    /// <summary>Bit marking a persistent (never-decaying) leaf.</summary>
    public const byte PersistentBit = 0x08;

    /// <summary>Largest hydration the field can hold.</summary>
    public const int MaxHydration = 7;

    /// <summary>Packs both fields; throws when the hydration is out of range.</summary>
    public static byte Encode(int hydration, bool persistent)
    {
        CheckHydration(hydration);
        return (byte)(hydration | (persistent ? PersistentBit : 0));
    }

    /// <summary>Hydration, 0..7.</summary>
    public static int Hydration(byte state) => state & HydrationMask;

    /// <summary>Whether the leaf never decays.</summary>
    public static bool IsPersistent(byte state) => (state & PersistentBit) != 0;

    /// <summary>Replaces the hydration, leaving the persistent flag untouched.</summary>
    public static byte WithHydration(byte state, int hydration)
    {
        CheckHydration(hydration);
        return (byte)(state & ~HydrationMask | hydration);
    }

    /// <summary>Sets or clears the persistent flag, leaving the hydration untouched.</summary>
    public static byte WithPersistent(byte state, bool persistent) =>
        (byte)(persistent ? state | PersistentBit : state & ~PersistentBit);

    private static void CheckHydration(int hydration)
    {
        if (hydration is < 0 or > MaxHydration)
            throw new ArgumentOutOfRangeException(nameof(hydration), hydration, $"Leaf hydration must be 0..{MaxHydration}.");
    }
}
