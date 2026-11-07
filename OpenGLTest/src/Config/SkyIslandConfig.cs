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
    public const int ZoneCellBlocks = 1200;

    /// <summary>Chance a zone cell rolls a zone candidate.</summary>
    public const float ZoneChance = 1f;

    /// <summary>Skew of the zone size roll toward small (the size is u^skew), so big zones are rare.</summary>
    public const float ZoneSizeSkew = 1.6f;

    /// <summary>Radius of the smallest zone in blocks.</summary>
    public const float ZoneMinRadius = 510f;

    /// <summary>Radius of the biggest zone in blocks.</summary>
    public const float ZoneMaxRadius = 1320f;

    /// <summary>The least open sky between two zones' nominal outlines; the lower hash wins a crowd.</summary>
    public const float ZoneGapBlocks = 6f;

    /// <summary>Share of its radius a zone's outline is pulled inward by the outline noise.</summary>
    public const float ZoneWobble = 0.3f;

    /// <summary>Frequency of the zone outline noise.</summary>
    public const float ZoneWobbleFrequency = 1f / 300f;

    /// <summary>Share of the outline inside which a zone's weight is full; it eases to 0 at the rim.</summary>
    public const float ZoneCoreShare = 0.7f;

    /// <summary>The zone size (0..1) from which a zone is tier 1 (medium).</summary>
    public const float ZoneMediumSize = 0.4f;

    /// <summary>The zone size (0..1) from which a zone is tier 2 (big, the hardest mobs, <c>L-15</c>).</summary>
    public const float ZoneLargeSize = 0.75f;

    // --- Islands: one hashed candidate per island cell, accepted inside a zone ---

    /// <summary>Side of the square cell that rolls at most one island.</summary>
    public const int IslandCellBlocks = 240;

    /// <summary>Chance an island cell rolls a candidate (a zone's weight thins it further toward its rim).</summary>
    public const float IslandChance = 0.8f;

    /// <summary>Skew of the island size roll toward small.</summary>
    public const float IslandSizeSkew = 1.8f;

    /// <summary>Radius of the smallest island.</summary>
    public const float IslandMinRadius = 7f;

    /// <summary>Radius of the biggest island.</summary>
    public const float IslandMaxRadius = 30f;

    /// <summary>The least open sky between two islands' nominal outlines.</summary>
    public const float IslandGapBlocks = 20f;

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

    /// <summary>Room kept free over the highest island top for its plants and trees: an island's base is
    /// rolled so its top stays this far under the band's top, and a sky tree grows only inside the band.</summary>
    public const int IslandFloraHeadroom = 18;

    // --- Landmasses: great floating continents, one hashed candidate per big cell (SkyLandmasses) ---

    /// <summary>Side of the square cell that rolls at most one landmass.</summary>
    public const int LandmassCellBlocks = 4200;

    /// <summary>Chance a landmass cell rolls a candidate.</summary>
    public const float LandmassChance = 0.9f;

    /// <summary>Skew of the landmass size roll toward small.</summary>
    public const float LandmassSizeSkew = 1.0f;

    /// <summary>Bounding radius of the smallest landmass.</summary>
    public const float LandmassMinRadius = 1200f;

    /// <summary>Bounding radius of the biggest landmass.</summary>
    public const float LandmassMaxRadius = 2500f;

    /// <summary>The least open sky between two landmasses' bounding discs.</summary>
    public const float LandmassGapBlocks = 400f;

    /// <summary>The ring around a landmass' bounding disc, as a share of its radius, in which broken-off islands drift.</summary>
    public const float LandmassFringeShare = 0.35f;

    /// <summary>The most blocks the fringe is wide, whatever the landmass' size.</summary>
    public const float LandmassFringeMaxBlocks = 600f;

    /// <summary>Share of the bounding radius that is the ellipse's long semi-axis: the rest is room for the coast's warp.</summary>
    public const float LandmassBoundShare = 1.35f;

    /// <summary>Least short-over-long ratio of a landmass's elliptical body.</summary>
    public const float LandmassMinAspect = 0.45f;

    /// <summary>How far the warp pushes a point, as a share of the long semi-axis (under <see cref="LandmassBoundShare"/> - 1).</summary>
    public const float LandmassWarp = 0.33f;

    /// <summary>Wavelength of the warp noise as a share of the long semi-axis, so a landmass bends about once across its length.</summary>
    public const float LandmassWarpWavelengthShare = 0.9f;

    /// <summary>Blocks the rim is roughened inward by the island outline noise.</summary>
    public const float LandmassRimWobble = 9f;

    /// <summary>Blocks in from the coast over which the top rises from the base to its hills.</summary>
    public const float LandmassTopRamp = 40f;

    /// <summary>Blocks in from the coast over which the underside thickens to its full keel.</summary>
    public const float LandmassKeelRamp = 170f;

    /// <summary>Least and most height of a landmass's rolling hills over its base.</summary>
    public const float LandmassMinHills = 8f;
    public const float LandmassMaxHills = 18f;

    /// <summary>Least and most depth of a landmass's keel under its base.</summary>
    public const float LandmassMinDepth = 22f;
    public const float LandmassMaxDepth = 40f;

    /// <summary>Frequency of the hill noise: a hill a hundred blocks across.</summary>
    public const float LandmassHillFrequency = 1f / 110f;

    /// <summary>Frequency of the keel noise.</summary>
    public const float LandmassKeelFrequency = 1f / 60f;

    /// <summary>Bounding radius from which a landmass counts as medium (sky mob tier 1).</summary>
    public const float LandmassMediumTierRadius = 1700f;

    /// <summary>Bounding radius from which a landmass counts as big (sky mob tier 2).</summary>
    public const float LandmassBigTierRadius = 2100f;

    // --- Sky biomes (L-13): meadow and frost highland, by a coherent 2D noise at the island's center ---

    /// <summary>Frequency of the sky biome noise: regions hundreds of blocks across, about a zone's size.</summary>
    public const float SkyBiomeFrequency = 1f / 700f;

    /// <summary>The biome noise value above which an island is frost highland; below it is meadow.</summary>
    public const float SkyFrostMinValue = 0f;

    /// <summary>The longest vine hanging under an island's underside, in cells.</summary>
    public const int SkyVineMaxLength = 6;

    /// <summary>One meadow underside column in this many hangs a vine.</summary>
    public const int MeadowVineRate = 14;

    /// <summary>One frost highland underside column in this many hangs a vine.</summary>
    public const int FrostVineRate = 9;

    /// <summary>One meadow top cell in this many grows a plant (tall grass or a flower).</summary>
    public const int MeadowPlantRate = 3;

    /// <summary>One frost highland top cell in this many grows a frost fern.</summary>
    public const int FrostPlantRate = 9;

    /// <summary>One frost highland top cell in this many is a crystal outcrop instead of frosted turf.</summary>
    public const int FrostCrystalRate = 23;

    /// <summary>Side of a sky tree cell (<c>SkyTrees</c>): each cell roots at most one tree, grown inside
    /// the cell's columns, so two trees never meet. Divides the chunk size.</summary>
    public const int SkyTreeCellBlocks = 16;

    /// <summary>How far inside its cell's sides a sky tree's root stands.</summary>
    public const int SkyTreeCellMargin = 4;

    /// <summary>The chance a meadow tree cell roots a sky oak.</summary>
    public const float MeadowTreeChance = 0.45f;

    /// <summary>The chance a frost highland tree cell roots a crystal tree.</summary>
    public const float FrostTreeChance = 0.35f;

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
