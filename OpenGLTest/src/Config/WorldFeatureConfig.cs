namespace VoxelEngine.Config;

/// <summary>
/// Worldgen feature tuning: pebbles, flowers, foliage, cotton and the scenery generators (fallen
/// logs, boulders, reeds, lily pads, glow mushrooms, cave formations, gravel and sand pockets,
/// cairns, underground pools, ruined pillars, desert wells, geodes, meteors, hot springs, sea
/// stacks and oases).
/// </summary>
public static class WorldFeatureConfig
{
    // --- Pebbles (PebbleGenerator) ---
    // Ankle-high stones in rare low-frequency patches; a density roll thins inside them
    public const bool PebblesEnabled = true;
    public const float PebblePatchFrequency = 1f / 96f;
    public const float PebblePatchThreshold = 0.25f;
    public const float PebbleDensity = 0.08f;
    // Edge length of one stone, in whole texels (footprint and height alike)
    public const int PebbleMinSizeTexels = 4;
    public const int PebbleMaxSizeTexels = 8;
    // Fraction of natural pebble stones that are obsidian instead of flint (~1% per rock)
    public const float PebbleObsidianChance = 0.01f;

    // --- Flowers (FlowerGenerator over PlantClumps) ---
    // Meadow cross-blocks in clumps on dry grass, only where the forest is open enough
    public const bool FlowersEnabled = true;
    public const int MeadowCellShift = 5;
    public const int MeadowRadius = 4;
    public const int MeadowJitter = 3;
    public const int MeadowMinMembers = 4;
    public const float MeadowClumpChance = 0.45f;
    public const float MeadowEdgeDensity = 0.25f;
    // ForestOpenness a clump's center needs: the open stretches between the woodland
    public const float MeadowMinOpenness = 0.55f;

    // --- Foliage (FoliageGenerator) ---
    // Ground cover on the biome's own surface: forest grass tufts are common, desert dead shrubs
    // sparse. Each is gated by a low-frequency patch and then a position-hash density roll.
    public const bool FoliageEnabled = true;
    public const float GrassPatchFrequency = 1f / 48f;
    public const float GrassPatchThreshold = 0.05f;
    // The tuft density runs from the dry end to the wet end of the climate's wetness
    public const float GrassDensityDry = 0.15f;
    public const float GrassDensityWet = 0.5f;
    public const float ShrubPatchFrequency = 1f / 64f;
    public const float ShrubPatchThreshold = 0.2f;
    public const float ShrubDensity = 0.04f;

    // --- Cotton (CottonGenerator over PlantClumps) ---
    // Clumped, not scattered: at most one clump per 2^CottonCellShift-block cell, accepted with
    // CottonClumpChance and only on the warm, dry forest fringe before the desert line: a forest
    // center whose desert weight (0.5 on the desert line itself) lies in
    // [CottonMinDesertWeight, CottonMaxDesertWeight), the top end being where the grass/sand border
    // dither starts, so a clump never lands on dithered sand. The center sits up to CottonJitter
    // blocks off the cell's middle, so centers are at least 2^shift - 2 * jitter apart, and the clump
    // fills a CottonRadius disc: the CottonMinMembers cells nearest the center always, the rest with
    // a density falling from 1 at the center to CottonEdgeDensity at the rim. Clumps never overlap
    // while that spacing exceeds 2 * radius.
    public const bool CottonEnabled = true;
    public const int CottonCellShift = 6;
    public const int CottonJitter = 16;
    public const int CottonRadius = 3;
    public const int CottonMinMembers = 5;
    public const float CottonClumpChance = 0.6f;
    public const float CottonEdgeDensity = 0.1f;
    public const float CottonMinDesertWeight = 0.2f;
    public const float CottonMaxDesertWeight = 0.5f - WorldGenerationConfig.BiomeBorderDither;

    // --- Fallen logs (FallenLogGenerator): a rare detached oak log on the forest floor ---
    public const bool FallenLogsEnabled = true;
    // One candidate log per 2^FallenLogCellShift-block cell, accepted with FallenLogChance
    public const int FallenLogCellShift = 5;
    public const float FallenLogChance = 0.3f;
    public const int FallenLogMinLength = 3;
    public const int FallenLogMaxLength = 5;

    // --- Boulders (BoulderGenerator): a rare blob of stone or cobblestone on the forest and
    // taiga floor ---
    public const bool BouldersEnabled = true;
    // One candidate boulder per 2^BoulderCellShift-block cell, accepted with BoulderChance
    public const int BoulderCellShift = 5;
    public const float BoulderChance = 0.2f;
    public const int BoulderMinBlocks = 2;
    public const int BoulderMaxBlocks = 4;

    // --- Reeds (ReedGenerator): clumps of reeds on the columns at a water's edge ---
    public const bool ReedsEnabled = true;
    public const int ReedCellShift = 5;
    public const float ReedClumpChance = 0.6f;
    public const int ReedRadius = 2;
    public const int ReedJitter = 2;
    public const int ReedMinMembers = 2;
    public const float ReedEdgeDensity = 0.35f;
    // The edge band: ground this far under the water level (or level with it) takes reeds
    public const int ReedShallowDepth = 1;

    // --- Lily pads (LilyPadGenerator): a pad on a still lake's surface ---
    public const bool LilyPadsEnabled = true;
    public const float LilyPadChance = 0.35f;

    // --- Glow mushrooms (GlowMushroomGenerator): a dim cyan light on deep cave floors ---
    public const bool GlowMushroomsEnabled = true;
    public const float GlowMushroomChance = 0.04f;
    // Blocks under the column's height a mushroom needs (deep caves, never the surface)
    public const int GlowMushroomMinDepth = 12;

    // --- Cave formations (CaveFormationGenerator): stalactites on ceilings, stalagmites on
    // floors, and a stalagmite under every stalactite's drip point ---
    public const bool CaveFormationsEnabled = true;
    public const float StalactiteChance = 0.03f;
    public const float StalagmiteChance = 0.03f;
    public const int CaveFormationMinDepth = 8;

    // --- Gravel and sand pockets (GravelPocketGenerator): loose blobs buried inside stone that
    // cave in when mined ---
    public const bool GravelPocketsEnabled = true;
    public const float GravelPocketFrequency = 1f / 24f;
    public const float GravelPocketThreshold = 0.3f;
    // Blocks under the column's surface a pocket needs (soil and rock caps stay solid)
    public const int GravelPocketMinBurial = 3;
    // Share of a pocket's cells that are sand rather than gravel
    public const float GravelPocketSandShare = 0.35f;

    // --- Cairns (CairnGenerator): a cobblestone stack landmark on a mountain summit ---
    public const bool CairnsEnabled = true;
    public const int CairnCellShift = 6;
    public const float CairnChance = 0.25f;

    // --- Underground pools (UndergroundPoolGenerator): water on cave floors above the lava band
    // and below the sea ---
    public const bool UndergroundPoolsEnabled = true;
    public const float PoolPatchFrequency = 1f / 96f;
    public const float PoolPatchThreshold = 0.35f;
    // Blocks under the column's surface a pool needs (entrances and ravine floors stay dry)
    public const int PoolBurial = 8;
    public const float PoolChance = 0.5f;

    // --- Ruined pillars (RuinedPillarGenerator): broken stone-brick columns on the dry land ---
    public const bool RuinedPillarsEnabled = true;
    public const int PillarCellShift = 6;
    public const float PillarChance = 0.08f;
    public const int PillarMinHeight = 2;
    public const int PillarMaxHeight = 4;

    // --- Desert wells (DesertWellGenerator): a sandstone ring around a sunken pool ---
    public const bool DesertWellsEnabled = true;
    public const int WellCellShift = 7;
    public const float WellChance = 0.06f;

    // --- Geodes (GeodeGenerator): a crystal-lined hollow sphere deep in rock ---
    public const bool GeodesEnabled = true;
    public const int GeodeCellShift = 7;
    public const float GeodeChance = 0.25f;
    // The depth band the centers sit in, and the radius range
    public const int GeodeMinY = -48;
    public const int GeodeMaxY = -16;
    public const int GeodeMinRadius = 2;
    public const int GeodeMaxRadius = 3;

    // --- Meteor craters (MeteorGenerator): a rare obsidian-lined bowl in the land ---
    public const bool MeteorsEnabled = true;
    public const int MeteorCellShift = 8;
    public const float MeteorChance = 0.06f;
    public const int MeteorMinRadius = 4;
    public const int MeteorMaxRadius = 7;
    // Bowl depth per block inside the rim: the center is radius * this deep
    public const float MeteorBowlSlope = 0.6f;

    // --- Hot springs (HotSpringGenerator): a small pond in cold biomes that never freezes ---
    public const bool HotSpringsEnabled = true;
    public const int SpringCellShift = 7;
    public const float SpringChance = 0.3f;
    public const float SpringMinCold = 0.5f;
    public const int SpringRadius = 3;
    // Blocks under the spring's surface its flat floor is carved to
    public const int SpringDepth = 2;

    // --- Sea stacks (SeaStackGenerator): rock pillars in the shallow ocean ---
    public const bool SeaStacksEnabled = true;
    public const int SeaStackCellShift = 6;
    public const float SeaStackChance = 0.25f;
    public const int SeaStackRadius = 2;
    // How shallow the ocean must be for a stack, and how high its top rises above the sea
    public const int SeaStackShallowDepth = 6;
    public const int SeaStackMinHeight = 3;
    public const int SeaStackMaxHeight = 6;

    // --- Oases (OasisGenerator): a green pocket deep in the desert, its own pond and palm ---
    public const bool OasesEnabled = true;
    public const int OasisCellShift = 7;
    public const float OasisChance = 0.25f;
    // The whole disc's radius, and the pond's inner one
    public const int OasisRadius = 6;
    public const int OasisPondRadius = 2;
    // Blocks under the pond's surface its flat floor is carved to
    public const int OasisDepth = 2;
    // Height above the pond's surface a palm's base needs (its own ground, so it rings the pond)
    public const int OasisPalmMinGround = 1;
}
