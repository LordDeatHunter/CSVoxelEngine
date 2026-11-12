namespace VoxelEngine.WorldGen.Features.Underground;

/// <summary>One deterministic group of one or two adjacent source candidates per 3D cell.
/// Candidates stay in the middle half, leaving at least 32 blocks between different groups.
/// A candidate only becomes fluid when its generator also finds a buried, dry cave floor.</summary>
internal static class CaveFluidSources
{
    private const int Shift = WorldGenerationConfig.CaveFluidCellShift;
    private const int Cell = 1 << Shift;
    private const int Margin = WorldGenerationConfig.CaveFluidSiteMargin;

    internal static (int X, int Y, int Z, bool Pair) SiteAt(int cx, int cy, int cz)
    {
        var h = SplitMix64.Hash(cx ^ (cy * 0x9E37), cz, WorldGenerationConfig.Seed ^ 0x5E82);
        const int span = Cell - 2 * Margin - 1;
        return ((cx << Shift) + Margin + (int)(SplitMix64.Mix(h) % span),
            (cy << Shift) + Margin + (int)(SplitMix64.Mix(h + 1) % span),
            (cz << Shift) + Margin + (int)(SplitMix64.Mix(h + 2) % span),
            (SplitMix64.Mix(h + 3) & 1) != 0);
    }

    internal static bool Contains(int x, int y, int z)
    {
        var site = SiteAt(x >> Shift, y >> Shift, z >> Shift);
        return y == site.Y && z == site.Z
            && (x == site.X || (site.Pair && x == site.X + 1));
    }
}
