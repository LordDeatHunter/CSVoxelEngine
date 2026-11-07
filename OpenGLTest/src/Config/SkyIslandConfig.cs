namespace VoxelEngine.Config;

/// <summary>
/// The sky layer's worldgen knobs (plans/LAYERS.md <c>L-12</c>): the island zones, the floating
/// islands inside them and the thick cloud sea under them. The island band itself is
/// <see cref="WorldGenerationConfig.SkyIslandBottomY"/>..<see cref="WorldGenerationConfig.SkyIslandTopY"/>
/// and the cloud sea's height and thickness are <see cref="SkyConfig.SkyCloudSeaHeight"/> and
/// <see cref="SkyConfig.SkyCloudSeaThickness"/>.
/// </summary>
public static class SkyIslandConfig
{
    /// <summary>Master switch for the sky islands (<c>SkyIslandGenerator</c>).</summary>
    public const bool SkyIslandsEnabled = true;

    /// <summary>Master switch for the cloud sea (<c>CloudSeaGenerator</c>).</summary>
    public const bool CloudSeaEnabled = true;

    // --- Zones: one hashed candidate per zone cell, wide empty sky between them ---

    /// <summary>Side of the square cell that rolls at most one island zone.</summary>
    public const int ZoneCellBlocks = 640;

    /// <summary>Chance a zone cell rolls a zone candidate.</summary>
    public const float ZoneChance = 0.85f;

    /// <summary>Skew of the zone size roll toward small (the size is u^skew), so big zones are rare.</summary>
    public const float ZoneSizeSkew = 2.2f;

    /// <summary>Radius of the smallest zone in blocks.</summary>
    public const float ZoneMinRadius = 150f;

    /// <summary>Radius of the biggest zone in blocks.</summary>
    public const float ZoneMaxRadius = 420f;

    /// <summary>The least open sky between two zones' nominal outlines; the lower hash wins a crowd.</summary>
    public const float ZoneGapBlocks = 140f;

    /// <summary>Share of its radius a zone's outline is pulled inward by the outline noise.</summary>
    public const float ZoneWobble = 0.3f;

    /// <summary>Frequency of the zone outline noise.</summary>
    public const float ZoneWobbleFrequency = 1f / 300f;

    /// <summary>Share of the outline inside which a zone's weight is full; it eases to 0 at the rim.</summary>
    public const float ZoneCoreShare = 0.55f;

    /// <summary>The zone size (0..1) from which a zone is tier 1 (medium).</summary>
    public const float ZoneMediumSize = 0.4f;

    /// <summary>The zone size (0..1) from which a zone is tier 2 (big, the hardest mobs, <c>L-15</c>).</summary>
    public const float ZoneLargeSize = 0.75f;

    // --- Islands: one hashed candidate per island cell, accepted inside a zone ---

    /// <summary>Side of the square cell that rolls at most one island.</summary>
    public const int IslandCellBlocks = 48;

    /// <summary>Chance an island cell rolls a candidate (a zone's weight thins it further toward its rim).</summary>
    public const float IslandChance = 0.85f;

    /// <summary>Skew of the island size roll toward small.</summary>
    public const float IslandSizeSkew = 1.6f;

    /// <summary>Radius of the smallest island.</summary>
    public const float IslandMinRadius = 7f;

    /// <summary>Radius of the biggest island.</summary>
    public const float IslandMaxRadius = 30f;

    /// <summary>The least open sky between two islands' nominal outlines.</summary>
    public const float IslandGapBlocks = 4f;

    /// <summary>Share of its radius an island's outline is pulled inward by the outline noise.</summary>
    public const float IslandWobble = 0.35f;

    /// <summary>Frequency of the island outline noise.</summary>
    public const float IslandWobbleFrequency = 1f / 24f;

    /// <summary>How high the top domes over the island's base at its center, per block of radius.</summary>
    public const float IslandDomePerRadius = 0.15f;

    /// <summary>Most the top detail noise lifts the top.</summary>
    public const float IslandTopDetail = 1.5f;

    /// <summary>Frequency of the top detail noise.</summary>
    public const float IslandTopDetailFrequency = 1f / 10f;

    /// <summary>Least depth of the underside under the center, per block of radius.</summary>
    public const float IslandMinDepthPerRadius = 0.7f;

    /// <summary>Most depth of the underside under the center, per block of radius.</summary>
    public const float IslandMaxDepthPerRadius = 1.1f;

    /// <summary>Exponent of the underside's taper from the center (1 - t)^p: above 1 a pointed cone.</summary>
    public const float IslandTaperExponent = 1.3f;

    /// <summary>Longest stalactite hanging under the underside.</summary>
    public const float IslandStalactiteMax = 6f;

    /// <summary>The stalactite noise value above which a column hangs a stalactite.</summary>
    public const float IslandStalactiteThreshold = 0.4f;

    /// <summary>Frequency of the stalactite noise: a few blocks across.</summary>
    public const float IslandStalactiteFrequency = 1f / 4f;

    /// <summary>Cells of soil under an island's top cell.</summary>
    public const int IslandSoilDepth = 3;

    // --- The cloud sea under the islands ---

    /// <summary>Lowest cell of the cloud sea: <see cref="SkyConfig.SkyCloudSeaHeight"/> as a cell.</summary>
    public const int CloudSeaBottomY = (int)SkyConfig.SkyCloudSeaHeight;

    /// <summary>Highest cell of the cloud sea, <see cref="SkyConfig.SkyCloudSeaThickness"/> cells up.</summary>
    public const int CloudSeaTopY = CloudSeaBottomY + (int)SkyConfig.SkyCloudSeaThickness - 1;

    /// <summary>Frequency of the noise whose high values outline the cloud sea: broad, with a few wide holes.</summary>
    public const float CloudSeaFrequency = 1f / 240f;

    /// <summary>The cover noise value above which a column holds cloud sea; lower is denser.</summary>
    public const float CloudSeaThreshold = -0.3f;

    /// <summary>The cover noise value from which a column has the sea's full thickness.</summary>
    public const float CloudSeaCoreValue = 0.05f;

    /// <summary>Frequency of the noise that roughens the sea's top.</summary>
    public const float CloudSeaDetailFrequency = 1f / 18f;
}
