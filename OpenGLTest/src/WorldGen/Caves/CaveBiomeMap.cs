using System.Runtime.CompilerServices;
using VoxelEngine.Chunks;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// One chunk's cave biome per cell (plans/CAVE_BIOMES.md CB-2), classified once by
/// <see cref="CaveBiomes.Fill"/> right after the column fill and read by every cave consumer: the
/// noise cave shape, the ore richness, the exclusive ores and the palette pass. A plain buffer the
/// generation worker reuses chunk after chunk, so <see cref="CaveBiomes.Fill"/> writes every cell.
/// </summary>
public sealed class CaveBiomeMap
{
    private const int SubBiomeMask =
        (1 << (int)CaveBiome.Crystal) | (1 << (int)CaveBiome.Fungal) | (1 << (int)CaveBiome.MagmaDeep);

    /// <summary>A <see cref="CaveBiome"/> per cell, in <see cref="Chunk.Idx"/> order.</summary>
    public readonly byte[] Cells = new byte[Chunk.BlockCount];

    /// <summary>Bit <c>1 &lt;&lt; (int)biome</c> for every biome some cell holds.</summary>
    public int Present { get; private set; }

    /// <summary>Whether some cell is <paramref name="biome"/>.</summary>
    public bool Has(CaveBiome biome) => (Present & (1 << (int)biome)) != 0;

    /// <summary>Whether some cell is a sub-band biome (crystal, fungal or the magma-touched deep).</summary>
    public bool AnySubBiome => (Present & SubBiomeMask) != 0;

    /// <summary>Rebuilds <see cref="Present"/> from <see cref="Cells"/> (for a hand-filled map).</summary>
    internal void Recount()
    {
        Present = 0;
        foreach (var cell in Cells) Present |= 1 << cell;
    }

    /// <summary>Forgets every biome before a fill.</summary>
    internal void Clear() => Present = 0;

    /// <summary>Writes cell <paramref name="idx"/>'s biome and marks it present.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Set(int idx, CaveBiome biome)
    {
        Cells[idx] = (byte)biome;
        Present |= 1 << (int)biome;
    }
}
