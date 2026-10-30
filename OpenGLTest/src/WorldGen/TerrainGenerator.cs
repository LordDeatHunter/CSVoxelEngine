using System;
using VoxelEngine.Chunks;
using VoxelEngine.Content;
using VoxelEngine.Effects.Climate;
using VoxelEngine.WorldGen.Features;
using VoxelEngine.WorldGen.Features.Underground;

namespace VoxelEngine.WorldGen;

/// <summary>
/// The 2D column build of world generation: the terrain palette, each column's land height (<see cref="LandHeight"/>, with
/// its trenches and the highlands and mountain ranges of <see cref="TerrainRelief"/>) and the full <see cref="Compute"/> pass into <see cref="ColumnData"/> (heights with ravines
/// cut in, dirt depth, the cave inputs, water levels, biomes and the corner climate), plus the column queries the rest of
/// the engine calls it by. The biome climate split lives in <see cref="BiomeClassifier"/>, the lakes, rivers and water
/// levels in <see cref="WaterBodies"/> and every noise channel in <see cref="TerrainNoise"/>; the queries here forward to
/// them. Everything is a pure function of (x, z) and the seed, so results are cached per chunk column by
/// <see cref="ColumnCache.Get"/> and shared by the vertical chunk stack and the far LOD, and it is safe to call from any
/// worker thread.
/// </summary>
public static class TerrainGenerator
{
    // The terrain palette: the generator owns the choice of which blocks natural ground is made
    // of, and Chunk only fills its columns with them. Resolved once here so the per-voxel fill
    // and the far LOD both read a byte instead of hitting the registry.

    /// <summary>Block the top of a dry column is made of.</summary>
    public static readonly byte SurfaceBlock;

    /// <summary>Block filling the soil band under the surface (and the top of a sea floor).</summary>
    public static readonly byte SoilBlock;

    /// <summary>Block everything below the soil band is made of.</summary>
    public static readonly byte RockBlock;

    /// <summary>The frozen tundra's top and shallow soil under its snow, over stone.</summary>
    public static readonly byte PermafrostBlock;

    /// <summary>Fluid the sea is filled with, up to and including sea level.</summary>
    public static readonly byte SeaBlock;

    /// <summary>Block dirt touching fresh sea water rots into on the spot.</summary>
    public static readonly byte MudBlock;

    /// <summary>Block beach columns (see <see cref="IsBeachHeight"/>) are built from.</summary>
    public static readonly byte SandBlock;

    /// <summary>Block capping beach columns where <see cref="IsGravelPatch"/> holds.</summary>
    public static readonly byte GravelBlock;

    /// <summary>Block a desert column's soil band is made of, under its surface sand.</summary>
    public static readonly byte SandstoneBlock;

    /// <summary>Frozen water surface and the frozen biome's ice; the ice-sheet block.</summary>
    public static readonly byte IceBlock;

    /// <summary>Layered snow block; the snow accumulation pass stamps it.</summary>
    public static readonly byte SnowBlock;

    static TerrainGenerator()
    {
        SurfaceBlock = ContentRegistry.RequireBlock("grass").Id;
        SoilBlock = ContentRegistry.RequireBlock("dirt").Id;
        RockBlock = ContentRegistry.RequireBlock("stone").Id;
        PermafrostBlock = ContentRegistry.RequireBlock("permafrost").Id;
        SeaBlock = ContentRegistry.RequireBlock("water").Id;
        MudBlock = ContentRegistry.RequireBlock("mud").Id;
        SandBlock = ContentRegistry.RequireBlock("sand").Id;
        GravelBlock = ContentRegistry.RequireBlock("gravel").Id;
        SandstoneBlock = ContentRegistry.RequireBlock("sandstone").Id;
        IceBlock = ContentRegistry.RequireBlock("ice").Id;
        SnowBlock = ContentRegistry.RequireBlock("snow").Id;
    }

    /// <summary>
    /// Whether a column's terrain sits below the waterline. This is only the height test: since
    /// land floods only in a lake or river bed, a below-waterline column is not necessarily wet —
    /// ask <see cref="IsWaterColumn"/> for that. A column whose height equals
    /// <see cref="WorldGenerationConfig.SeaLevel"/> is at the line itself and is not below it.
    /// </summary>
    public static bool IsSubmerged(int height) =>
        WorldGenerationConfig.WaterEnabled && height < WorldGenerationConfig.SeaLevel;

    /// <summary>Whether a column height sits in the shoreline band that builds from sand.</summary>
    public static bool IsBeachHeight(int height) =>
        height >= WorldGenerationConfig.SeaLevel - WorldGenerationConfig.BeachBelowSeaLevel
        && height <= WorldGenerationConfig.SeaLevel + WorldGenerationConfig.BeachAboveSeaLevel;

    /// <summary>Whether the beach column at (x, z) is capped with gravel. Pure function of
    /// world position like everything else here, so neighbors agree without shared state.</summary>
    public static bool IsGravelPatch(float x, float z) =>
        TerrainNoise.GravelNoise.GetNoise(x, z) > WorldGenerationConfig.GravelThreshold;

    /// <summary>Biome at a world column; a pure function of (x, z) like the rest of the 2D half.</summary>
    public static Biome BiomeAt(float x, float z) => BiomeWeights(x, z, out _, out _);

    /// <summary>The ocean and desert weights and the biome they classify to (<see cref="BiomeClassifier.BiomeWeights"/>).</summary>
    public static Biome BiomeWeights(float x, float z, out float oceanWeight, out float desertWeight) =>
        BiomeClassifier.BiomeWeights(x, z, out oceanWeight, out desertWeight);

    /// <summary>0..1 how cold (x, z) is (<see cref="BiomeClassifier.ColdWeight"/>).</summary>
    public static float ColdWeight(float x, float z) => BiomeClassifier.ColdWeight(x, z);

    /// <summary>0..1 how warm (x, z) is (<see cref="BiomeClassifier.WarmWeight"/>).</summary>
    public static float WarmWeight(float x, float z) => BiomeClassifier.WarmWeight(x, z);

    /// <summary>
    /// The climate axes behind the biome ramps, as 0..1, so a palette keyed to them has its
    /// corners on the biome lines (<see cref="BiomeTint"/>). Warmth is 0 fully cold, exactly 0.5
    /// across the temperate band (the cold and warm ramps are exclusive) and 1 fully warm;
    /// wetness reads the two humidity ramps the same way (<see cref="BiomeClassifier.WetnessOf"/>):
    /// 0 fully dry, exactly 0.5 between the dry and the semi-dry ramp (steppe, savanna) and 1 wet.
    /// Raw humidity is two-octave FBm and would never reach the palette corners, hence the ramps.
    /// It reads the very climate sample the biome does (<see cref="BiomeClassifier.SampleClimate"/>),
    /// so colors and biomes agree. A pure function of position like everything else here.
    /// </summary>
    public static void Climate(float x, float z, out float warmth01, out float wetness01)
    {
        WorldgenReads.Note();
        BiomeClassifier.SampleClimate(x, z, out var t, out var h);
        warmth01 = 0.5f * (1f - BiomeClassifier.ColdOf(t)) + 0.5f * BiomeClassifier.WarmOf(t);
        wetness01 = BiomeClassifier.WetnessOf(h);
    }

    /// <summary>Water level of a column that holds no water at all (<see cref="WaterBodies.NoWater"/>).</summary>
    public const int NoWater = WaterBodies.NoWater;

    /// <summary>The y water in column (x, z) rises to (<see cref="WaterBodies.WaterLevelAt"/>).</summary>
    public static int WaterLevelAt(float x, float z) => WaterBodies.WaterLevelAt(x, z);

    /// <summary>Whether the column (x, z) at this height is wet (<see cref="WaterBodies.IsWaterColumn"/>).</summary>
    public static bool IsWaterColumn(int x, int z, int height) => WaterBodies.IsWaterColumn(x, z, height);

    /// <summary>0..1 gate on a surface water body as the column nears a desert (<see cref="WaterBodies.SurfaceWaterGate"/>).</summary>
    internal static float SurfaceWaterGate(float desertWeight) => WaterBodies.SurfaceWaterGate(desertWeight);

    /// <summary>0..1 how strongly a lake or river covers (x, z) (<see cref="WaterBodies.SurfaceWaterWeight"/>).</summary>
    public static float SurfaceWaterWeight(float x, float z) => WaterBodies.SurfaceWaterWeight(x, z);

    /// <summary>
    /// 0..1 how open the forest at (x, z) is: a clearing or grassy plain rather than woodland.
    /// Trees thin out to none as it rises and the hills flatten a little, so the forest alternates
    /// between rolling woodland and open regions instead of one uniform canopy. Pure function of
    /// (x, z) like the rest of the 2D half, so the tree stamper and any cache agree.
    /// </summary>
    public static float ForestOpenness(float x, float z) =>
        MathUtils.SmoothStep(WorldGenerationConfig.OpennessStart, WorldGenerationConfig.OpennessEnd,
            TerrainNoise.OpennessNoise.GetNoise(x, z));

    /// <summary>
    /// How many blocks a trench lowers the ocean floor at (x, z): a warped ridged band (|n| near
    /// zero) gives the gash line, a rarity mask makes trenches finite, and the parabolic profile
    /// gives steep walls like <see cref="RavineDepthAt"/>. The caller weights it by the ocean
    /// share so a trench only ever cuts underwater.
    /// </summary>
    private static float TrenchDepthAt(float x, float z)
    {
        var mask = MathUtils.SmoothStep(WorldGenerationConfig.TrenchMaskStart,
            WorldGenerationConfig.TrenchMaskEnd, TerrainNoise.TrenchMask.GetNoise(x, z));
        if (mask <= 0f) return 0f;

        var warpedX = x;
        var warpedZ = z;
        TerrainNoise.TrenchWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(TerrainNoise.TrenchRidge.GetNoise(warpedX, warpedZ));
        if (ridge >= WorldGenerationConfig.TrenchWidth) return 0f;

        var t = ridge / WorldGenerationConfig.TrenchWidth;
        return WorldGenerationConfig.TrenchDepth * mask * (1f - t * t);
    }

    /// <summary>OpenSimplex2 noise seeded from the world seed (<see cref="TerrainNoise.MakeNoise"/>).</summary>
    internal static FastNoiseLite MakeNoise(int seedOffset, float frequency, int octaves = 1) =>
        TerrainNoise.MakeNoise(seedOffset, frequency, octaves);

    /// <summary>
    /// One column's land height before any water or ravine is cut into it: the forest, desert-dune
    /// and ocean-ramp profiles blended by the biome weights and clamped above the waterline. A lake
    /// measures this at its own rim to choose where its water sits, so it must stay the exact value
    /// <see cref="Compute"/> carves into. <paramref name="forestHeight"/> is the pre-blend forest
    /// profile, which the river carve reads to dry out as the land climbs; it includes the massifs
    /// and the lone peaks (<see cref="TerrainPeaks.WithMassif"/>, <see cref="TerrainPeaks.WithLonePeak"/>),
    /// so every later carve sees them.
    /// </summary>
    internal static float LandHeight(float worldX, float worldZ, out Biome biome,
        out float oceanWeight, out float desertWeight, out float forestHeight) =>
        LandHeightCore(worldX, worldZ, massifs: true, lonePeaks: true,
            out biome, out oceanWeight, out desertWeight, out forestHeight);

    /// <summary>
    /// The land height without any hashed peak: the relief land a massif's acceptance and rise are
    /// measured from. The massif site factory reads only this, never <see cref="LandHeight"/>, so it
    /// never recurses into itself.
    /// </summary>
    internal static float ReliefLandHeight(float x, float z) =>
        LandHeightCore(x, z, massifs: false, lonePeaks: false, out _, out _, out _, out _);

    /// <summary><see cref="ReliefLandHeight(float, float)"/> with the outs of <see cref="LandHeight"/>,
    /// so a caller can compare the two profiles out for out.</summary>
    internal static float ReliefLandHeight(float x, float z, out Biome biome, out float oceanWeight,
        out float desertWeight, out float forestHeight) =>
        LandHeightCore(x, z, massifs: false, lonePeaks: false,
            out biome, out oceanWeight, out desertWeight, out forestHeight);

    /// <summary>
    /// <see cref="ReliefLandHeight(float, float)"/> as a (x, z) height function, for the massif rim samples
    /// (<see cref="TerrainSites.RimRange"/>). A static, non-capturing field, so reading it allocates nothing.
    /// </summary>
    internal static readonly Func<float, float, float> ReliefHeightAt =
        static (x, z) => ReliefLandHeight(x, z);

    /// <summary>
    /// The land height with the massifs and without the lone peaks: the profile the lone-peak
    /// flatness test reads. It includes massifs and never lone peaks, so the lone-peak site test
    /// cannot recurse into itself.
    /// </summary>
    internal static float PreFeatureLandHeight(float x, float z) =>
        LandHeightCore(x, z, massifs: true, lonePeaks: false, out _, out _, out _, out _);

    /// <summary><see cref="PreFeatureLandHeight(float, float)"/> with the outs of <see cref="LandHeight"/>,
    /// so a caller can compare the two profiles out for out.</summary>
    internal static float PreFeatureLandHeight(float x, float z, out Biome biome, out float oceanWeight,
        out float desertWeight, out float forestHeight) =>
        LandHeightCore(x, z, massifs: true, lonePeaks: false,
            out biome, out oceanWeight, out desertWeight, out forestHeight);

    /// <summary>
    /// <see cref="PreFeatureLandHeight(float, float)"/> as a (x, z) height function, for the lone-peak
    /// rim samples (<see cref="TerrainSites.RimRange"/>). A static, non-capturing field, so reading it
    /// allocates nothing.
    /// </summary>
    internal static readonly Func<float, float, float> PreFeatureHeightAt =
        static (x, z) => PreFeatureLandHeight(x, z);

    /// <summary>The body of <see cref="LandHeight"/>, with the massif and lone-peak terms switchable for
    /// <see cref="ReliefLandHeight(float, float)"/> and <see cref="PreFeatureLandHeight(float, float)"/>.</summary>
    private static float LandHeightCore(float worldX, float worldZ, bool massifs, bool lonePeaks, out Biome biome,
        out float oceanWeight, out float desertWeight, out float forestHeight)
    {
        // Forest floor: a broad swell plus a hill mass (a positive-only field gated by the
        // hill mask) so hills rise in patches and open flat valleys run between them. Open
        // regions (see ForestOpenness) flatten the hills and drop their trees, so the
        // forest alternates between rolling woodland and open grass instead of one mass.
        // The relief layer (TerrainRelief) sits on top: a highland lifts the base and steepens
        // the hills, and mountain ranges run only along highlands, lifting the floor well above
        // the hills; the spawn fades both out. Every relief term is exactly neutral where the
        // highland weight is 0. The floor stays above sea level, which is what makes a carved
        // lake or river enclosed.
        var highland = TerrainRelief.HighlandWeight(worldX, worldZ);
        var swell = TerrainNoise.ForestSwellNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.ForestSwellAmplitude;
        var hillMask = MathUtils.SmoothStep(WorldGenerationConfig.HillMaskStart,
            WorldGenerationConfig.HillMaskEnd, TerrainNoise.HillMaskNoise.GetNoise(worldX, worldZ));
        var warpedX = worldX;
        var warpedZ = worldZ;
        TerrainNoise.HillWarp.DomainWarp(ref warpedX, ref warpedZ);
        var hill = 0.5f + 0.5f * TerrainNoise.HillNoise.GetNoise(warpedX, warpedZ);
        var rough = TerrainNoise.ValleyRoughNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.ValleyRoughAmplitude;
        var openness = ForestOpenness(worldX, worldZ);

        // Biome blend: the smooth arid weight flattens the hills of dry and semi-dry land into
        // plains (steppe, savanna, tundra), dunes lift the desert share of the land, then the
        // ocean ramp empties the basins. All are weighted 0..1, so a border lerps between the
        // profiles and comes out as a slope, never a cliff.
        biome = BiomeClassifier.BiomeWeights(worldX, worldZ, out oceanWeight, out desertWeight, out var aridWeight);
        var hillScale = 1f - WorldGenerationConfig.OpennessHillReduction * openness;
        hillScale *= 1f - WorldGenerationConfig.DryHillReduction * aridWeight;
        // A highland steepens the hills; the arid reduction above still applies, so an arid
        // highland reads as a lifted plateau
        hillScale *= TerrainRelief.HillGain(highland);
        // The lift goes right after the swell: at a highland weight of 0 it adds an exact 0f,
        // so the float sums keep their association and unmasked land is bit-identical
        var lift = TerrainRelief.HighlandLift(highland);
        var reliefForest = WorldGenerationConfig.ForestBaseHeight + swell + lift
            + hill * WorldGenerationConfig.HillAmplitude * hillMask * hillScale
            + rough
            + TerrainRelief.MountainHeight(worldX, worldZ, highland);
        // Massifs (TerrainPeaks) rise from the forest profile alone, after the ranges. WithMassif is
        // the exact identity outside every site's disc, so land outside them is bit-identical. The
        // desert and ocean lerps below would dilute a massif, which is why a site is accepted only
        // where its center and rim carry zero desert and ocean weight.
        forestHeight = massifs ? TerrainPeaks.WithMassif(worldX, worldZ, reliefForest) : reliefForest;
        // Lone peaks rise last, on top of the massifs, with the same exact identity outside their discs
        if (lonePeaks) forestHeight = TerrainPeaks.WithLonePeak(worldX, worldZ, forestHeight);
        var heightF = forestHeight;

        if (desertWeight > 0f)
        {
            // Smooth dunes on the swell, not the forest hills: a desert is its own profile. It
            // takes the highland lift too, so a highland desert does not sit in a basin.
            var dune = WorldGenerationConfig.ForestBaseHeight + swell + lift
                + TerrainNoise.DuneNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.DuneAmplitude;
            heightF = MathUtils.Lerp(heightF, dune, desertWeight);
        }

        if (oceanWeight > 0f)
        {
            var depth = MathF.Pow(oceanWeight, WorldGenerationConfig.OceanDepthExponent);
            var oceanHeight = MathUtils.Lerp(WorldGenerationConfig.OceanShoreHeight,
                WorldGenerationConfig.OceanFloorHeight, depth);
            oceanHeight -= TrenchDepthAt(worldX, worldZ) * oceanWeight;
            heightF = MathUtils.Lerp(heightF, oceanHeight, oceanWeight);
        }

        // Keep the land above the waterline, but ease the clamp out as the ocean blend
        // takes over. Clamping only while the biome was land put a hard step at the ocean
        // line, because the ocean side never clamped; fading it makes the coast a slope.
        // Lakes and rivers are cut after this, so they still reach below the waterline.
        var shoreClamp = 1f - MathUtils.SmoothStep(WorldGenerationConfig.OceanShoreWeight,
            0.5f, oceanWeight);
        if (shoreClamp > 0f)
            heightF = MathUtils.Lerp(heightF,
                MathF.Max(heightF, WorldGenerationConfig.SeaLevel + 1f), shoreClamp);

        return heightF;
    }

    /// <summary>
    /// <see cref="LandHeight"/> as a (x, z) height function, for the hashed-site rim samples
    /// (<see cref="TerrainSites.RimRange"/>). A static, non-capturing field, so reading it allocates nothing.
    /// </summary>
    internal static readonly Func<float, float, float> LandHeightAt =
        static (x, z) => LandHeight(x, z, out _, out _, out _, out _);

    /// <summary>Computes every 2D input for the chunk column at (chunkX, chunkZ).</summary>
    public static ColumnData Compute(int chunkX, int chunkZ)
    {
        const int count = Chunk.Size * Chunk.Size;
        var heights = new int[count];
        var bed = new int[count];
        var dirtDepth = new byte[count];
        var entrance = new float[count];
        var intensity = new float[count];
        var biomes = new Biome[count];
        var waterLevel = new int[count];
        var oasis = new bool[count];

        var baseX = (float)chunkX * Chunk.Size;
        var baseZ = (float)chunkZ * Chunk.Size;

        for (var x = 0; x < Chunk.Size; x++)
        {
            var worldX = x + baseX;
            for (var z = 0; z < Chunk.Size; z++)
            {
                var worldZ = z + baseZ;
                var i = (x << Chunk.SizeShift) | z;

                var heightF = LandHeight(worldX, worldZ, out var biome, out var oceanWeight,
                    out var desertWeight, out var forestHeight);

                // Surface water: a lake is a bowl around its own water level and a river a channel
                // down to the waterline, each returning to un-carved ground at its edge, so the
                // water is enclosed by its own banks. A river also fades out as the local land
                // climbs, so it dries into a wash uphill instead of gorging a hillside. Every land
                // biome but the desert, gated toward a desert so a body closes before the biome
                // line instead of ending in a wall.
                var gate = SurfaceWaterGate(desertWeight);
                var lakeWeight = 0f;
                var lakeSurface = NoWater;
                var lakeFloor = 0;
                var riverWeight = 0f;
                var lakeGuard = 0f;
                if (WaterBodies.CanHoldSurfaceWater(biome))
                    WaterBodies.SampleSurfaceWater(worldX, worldZ, out lakeWeight, out lakeSurface, out lakeFloor,
                        out riverWeight, out lakeGuard);

                if (lakeWeight > 0f)
                {
                    // Never fills ground the lake already sits below: on a slope the bowl only
                    // ever digs, and where the land is lower the water simply covers it, the
                    // same way an ocean would.
                    var target = MathF.Min(lakeFloor, heightF);
                    heightF = MathUtils.Lerp(heightF, target, lakeWeight * gate);
                }

                if (riverWeight > 0f)
                {
                    var lowland = WaterBodies.RiverLowland(forestHeight);
                    var river = riverWeight * gate * lowland;
                    if (river > 0f)
                        heightF = MathUtils.Lerp(heightF,
                            WorldGenerationConfig.SeaLevel - WorldGenerationConfig.RiverBedDepth, river);
                }

                waterLevel[i] = WaterBodies.WaterLevelFor(biome, gate, riverWeight, lakeWeight, lakeSurface);

                // An oasis: a hashed deep-desert site marks its disc (the fill and the far LOD
                // wear grass there) and carves its flat pond into the heightmap
                if (WorldFeatureConfig.OasesEnabled
                    && OasisGenerator.TryGetDisc((int)worldX, (int)worldZ, out _, out _,
                        out var oasisSurface))
                {
                    oasis[i] = true;
                    if (OasisGenerator.IsPond((int)worldX, (int)worldZ) && oasisSurface < heightF)
                    {
                        heightF = MathF.Min(heightF, oasisSurface - WorldFeatureConfig.OasisDepth);
                        waterLevel[i] = oasisSurface;
                    }
                }

                // A hot spring: a cold, hashed site carves its small pond and holds its own warm
                // surface, which SnowGenerator never freezes and whose vents steam
                if (WorldFeatureConfig.HotSpringsEnabled
                    && HotSpringGenerator.TryGetSite((int)worldX, (int)worldZ, out var springLevel)
                    && springLevel < heightF)
                {
                    heightF = MathF.Min(heightF, springLevel - WorldFeatureConfig.SpringDepth);
                    waterLevel[i] = springLevel;
                }

                var height = (int)heightF;

                // A sea stack: a hashed site in the shallow ocean raises the column into a tapered
                // rock pillar, part of the heightmap so the far LOD reads it too
                if (WorldFeatureConfig.SeaStacksEnabled)
                {
                    var stackTop = SeaStackGenerator.TopAt((int)worldX, (int)worldZ, height, biome);
                    if (stackTop > height) height = stackTop;
                }

                // A great lake's guard 1 maps to the threshold, so a ravine has no cut at the lake's
                // outline and tapers in across the guard band instead of cutting a bank or an island
                // below the surface.
                var surfaceWater = MathF.Max(lakeWeight, riverWeight) * gate;
                var guarded = lakeGuard * WorldGenerationConfig.WaterBodyThreshold;
                if (guarded > surfaceWater) surfaceWater = guarded;

                // Sampled once and reused: the same value cuts the ravine into the heightmap and
                // decides how much soil is left on the column.
                var ravineDepth = WorldGenerationConfig.RavinesEnabled
                    ? (int)(RavineDepthAt(worldX, worldZ) * RavineCutFade(oceanWeight, surfaceWater))
                    : 0;

                bed[i] = height;
                heights[i] = height - ravineDepth;

                var dirt = WorldGenerationConfig.DirtDepthBase
                           + WorldGenerationConfig.DirtDepthVariation * TerrainNoise.DirtNoise.GetNoise(worldX, worldZ);
                var soil = Math.Clamp((int)MathF.Round(dirt), 1, 8);

                // Ravine walls are exposed rock, not stacked grass/dirt terraces: strip the soil
                // entirely once the column is cut deep enough, and thin it toward the rim so the
                // transition fades in. Above the cutoff this stays >= 1, so 0 only ever means
                // "bare stone column" to Chunk.GenerateColumn.
                if (ravineDepth >= WorldGenerationConfig.RavineBareStoneDepth)
                    soil = 0;
                else if (ravineDepth > 0)
                    soil = Math.Min(soil, WorldGenerationConfig.RavineBareStoneDepth - ravineDepth);

                // A grass-biome peak above the rock line is bare stone too, so a high crest reads as
                // a rocky mountain instead of another tree-covered hill. Read off the final heightmap
                // (heights[i], ravines included) so the far LOD's rock cap agrees.
                if (BiomeTraits.ShowsBareRock(biome) && heights[i] >= WorldGenerationConfig.MountainRockHeight)
                    soil = 0;

                dirtDepth[i] = (byte)soil;

                // Surface cave entrances fade out before the shore band too: an entrance on a beach
                // let a cave breach the sand into a water-filled hole (see the ravine fade above).
                var entranceShore = 1f - MathUtils.SmoothStep(
                    WorldGenerationConfig.OceanShoreWeight - WorldGenerationConfig.EntranceShoreFade,
                    WorldGenerationConfig.OceanShoreWeight, oceanWeight);
                entrance[i] = MathUtils.SmoothStep(
                    WorldGenerationConfig.EntranceStart,
                    WorldGenerationConfig.EntranceEnd,
                    TerrainNoise.EntranceNoise.GetNoise(worldX, worldZ)) * entranceShore;

                intensity[i] = TerrainNoise.IntensityNoise.GetNoise(worldX, worldZ) * WorldGenerationConfig.CaveIntensityRange;
                biomes[i] = BiomeClassifier.DitherBiome(worldX, worldZ, biome, desertWeight);
            }
        }

        // The biome-tint lattice climate of this column's minimum corner, evaluated once here so
        // the chunk mesher reads it instead of noise. The int arguments convert to float exactly
        // as BiomeTint.TintsAt(chunkX << LatticeShift, ...) receives them on the direct path (the
        // far LOD, the felled tree), so both paths see the same bits; do not reuse baseX/baseZ.
        Climate(chunkX << Chunk.SizeShift, chunkZ << Chunk.SizeShift, out var cornerWarmth, out var cornerWetness);

        return new ColumnData(heights, dirtDepth, entrance, intensity, biomes, bed, waterLevel,
            cornerWarmth, cornerWetness, oasis);
    }

    /// <summary>
    /// How many blocks a ravine lowers the terrain at (x, z): a ridged noise band (|n| close to
    /// zero) gives the canyon line, a rarity mask makes ravines finite segments and fades their
    /// ends out, and the parabolic profile gives steep walls with a rounded floor.
    /// </summary>
    private static int RavineDepthAt(float x, float z)
    {
        var mask = MathUtils.SmoothStep(
            WorldGenerationConfig.RavineMaskStart,
            WorldGenerationConfig.RavineMaskEnd,
            TerrainNoise.RavineMask.GetNoise(x, z));
        if (mask <= 0f) return 0;

        var warpedX = x;
        var warpedZ = z;
        TerrainNoise.RavineWarp.DomainWarp(ref warpedX, ref warpedZ);

        var ridge = MathF.Abs(TerrainNoise.RavineRidge.GetNoise(warpedX, warpedZ));
        if (ridge >= WorldGenerationConfig.RavineWidth) return 0;

        var t = ridge / WorldGenerationConfig.RavineWidth;
        return (int)(WorldGenerationConfig.RavineDepth * mask * (1f - t * t));
    }

    /// <summary>
    /// 0..1 share of a ravine's cut that survives at a column. A ravine is a land feature, and one
    /// running into water used to leave a dry trench slicing the water's edge, because the flood
    /// only reaches columns whose own bed already sits below the level: full cut on dry land, gone
    /// under the ocean (<see cref="WorldGenerationConfig.OceanShoreWeight"/>) and gone across any
    /// lake's or river's footprint (<see cref="WorldGenerationConfig.WaterBodyThreshold"/>), so a
    /// body's bank is never carved out from under its own surface. A column only holds a surface
    /// water level above that threshold, so the second fade leaves every lake and river uncut by
    /// construction while the ocean keeps its own gashes as trenches. Compute raises the
    /// surface-water weight to a great lake's ravine guard times that threshold, so the cut is
    /// already gone at a great lake's outline and tapers back in across its guard band, leaving
    /// its banks and island whole.
    /// </summary>
    private static float RavineCutFade(float oceanWeight, float surfaceWaterWeight) =>
        (1f - MathUtils.SmoothStep(WorldGenerationConfig.OceanShoreWeight, 0.5f, oceanWeight))
        * (1f - MathUtils.SmoothStep(0f, WorldGenerationConfig.WaterBodyThreshold,
            surfaceWaterWeight));
}
