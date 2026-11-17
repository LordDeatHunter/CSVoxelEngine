namespace VoxelEngine.WorldGen.Features;

/// <summary>Dynamic surface writer admission beneath the separate cloud slab. The two air
/// cells at 196 and 197 remain clear; Legacy keeps its historical placement rules.</summary>
internal static class SurfaceClearance
{
    internal const int HighestWrittenY = WorldGenerationConfig.CloudSlabBottomY - 1
        - WorldGenerationConfig.DynamicSurfaceSlabAirMargin;

    internal static bool Allows(int groundY, int extentAboveGround) =>
        WorldSeed.Profile != WorldGenProfile.Dynamic || groundY + extentAboveGround <= HighestWrittenY;
}
