using System;

namespace VoxelEngine.WorldGen.Caves;

/// <summary>
/// How one Y-band ore's richness follows the cave biome (plans/CAVE_BIOMES.md CB-2): a threshold shift
/// per <see cref="CaveBiome"/>, added to the ore's lattice threshold in the cells of that biome. Negative
/// lowers the threshold, so the ore is richer there; positive leaves less of it; every biome not listed
/// keeps 0, the plain field exactly. Immutable after construction, so the generation workers share it.
/// </summary>
internal sealed class CaveRichness
{
    private readonly float[] _shifts = new float[Enum.GetValues<CaveBiome>().Length];

    /// <summary>The richness of the given (biome, shift) pairs; every other biome shifts by 0.</summary>
    public CaveRichness(params (CaveBiome Biome, float Shift)[] entries)
    {
        foreach (var (biome, shift) in entries)
        {
            _shifts[(int)biome] = shift;
            MinShift = Math.Min(MinShift, shift);
        }
    }

    /// <summary>The lowest shift of any biome, never above 0 (plain shifts by 0).</summary>
    public float MinShift { get; }

    /// <summary>The threshold shift in a cell of <paramref name="biome"/>.</summary>
    public float ShiftOf(CaveBiome biome) => _shifts[(int)biome];
}
