using System.Numerics;

namespace VoxelEngine.Config;

/// <summary>
/// Per-tile surface shading tuning: the ore highlight and twinkle, snow sparkle, leaf backlight,
/// grass shimmer, ice glint, glass sheen, wet albedo and lava terms, the biome tint palette, the
/// far LOD's sky exposure and the leaf sway.
/// </summary>
public static class SurfaceShadingConfig
{
    // Lava swell normal steepness: LavaWaveHeight's slopes are gentle (broad, low-frequency
    // blobs), so the gain is higher than water's to keep the catch-light visibly riding the heave
    public const float LavaNormalScale = 6f;
    // Strength of the point-light (fire, kiln, TNT) catch-light and sky sheen on the lava crust;
    // backed off over the molten texels, which already glow past it
    public const float LavaCatchLightStrength = 0.6f;
    // Blinn exponent of that catch-light: high, so a nearby fire reads as a small glint
    public const float LavaCatchLightPower = 48f;
    // Lava glow pulse (test-fragment.glsl, LavaPulse): how far the lava's brightness breathes above
    // and below itself on a slow noise field, as a fraction (0.2 = 80..120 %); 0 switches it off
    public const float LavaPulseStrength = 0.2f;

    // --- Ore highlight (test-fragment.glsl) ---
    /// <summary>
    /// Scale on the ore sun highlight (uOreSpecScale). The highlight is added in linear light,
    /// where the sRGB encode lifts a small add on the near-black ore body far more than the same
    /// add did in display space before linear lighting; at full strength the broad lobe washed
    /// coal's specks into gray. 0.1 matches the pre-linear look side by side.
    /// </summary>
    public const float OreSpecLinearScale = 0.1f;

    // --- Ore twinkle (test-fragment.glsl, OreTwinkle) ---
    /// <summary>
    /// Brightness of an ore twinkle at its peak, as a multiple of the direct sun (uOreTwinkle): a
    /// single texel, so like a snow glint it is meant to reach white. 0 switches the term off.
    /// </summary>
    public const float OreTwinkleStrength = 1.5f;
    /// <summary>Share of an ore tile's texels that twinkle at all (uOreTwinkleDensity).</summary>
    public const float OreTwinkleDensity = 0.04f;
    /// <summary>Slowest and fastest twinkle, in flashes per second; each texel hashes its own rate between.</summary>
    public const float OreTwinkleRateMin = 0.2f;
    public const float OreTwinkleRateMax = 0.5f;
    /// <summary>Half-width of a flash as a fraction of its texel's cycle: the flash lights
    /// 2 * this of the cycle, so twinkles stay occasional.</summary>
    public const float OreTwinkleWidth = 0.04f;
    /// <summary>Distances from the eye, in blocks, over which the twinkle fades out, before a 32 px
    /// texel turns subpixel and a flash would only shimmer.</summary>
    public const float OreTwinkleFadeNearBlocks = 12f;
    public const float OreTwinkleFadeFarBlocks = 28f;

    // --- Snow sparkle (test-fragment.glsl) ---
    /// <summary>
    /// Brightness of a snow glint (uSnowSparkle), as a multiple of the direct sun: the sun
    /// mirrored by one ice-crystal facet, added in linear light. A glint is a single texel, so
    /// unlike the ore highlight's broad lobe it cannot wash a face gray, and it is meant to reach
    /// white. Sunlit snow already sits past the tonemap clip for most of the day (the sun's linear
    /// gain is ~2.7 at noon), so the glints read only where it does not: a low sun, whose n.l on
    /// the flat tops is small. 0 switches the term off.
    /// </summary>
    public const float SnowSparkleStrength = 1.5f;
    /// <summary>Share of the snow tile's texels that carry a glinting facet at all (uSnowSparkleDensity).</summary>
    public const float SnowSparkleDensity = 0.15f;

    // --- Leaf backlight (test-fragment.glsl, LeafBacklight) ---
    /// <summary>
    /// Brightness of the light a leaf lets through when it stands between the eye and the sun, as a
    /// multiple of the direct sun on its tinted albedo (uLeafBacklight). 0 switches the term off.
    /// </summary>
    public const float LeafBacklightStrength = 0.9f;
    /// <summary>Exponent of the backlight's lobe around the sun: higher keeps the glow closer to the
    /// sun's direction (6 is about half strength 25 degrees off it).</summary>
    public const float LeafBacklightPower = 6f;
    /// <summary>How far toward the sun, in blocks, the shadow map is read to decide whether sunlight
    /// reaches a leaf from behind: a crown thinner than this along the sun glows, a thicker one stays dark.</summary>
    public const float LeafBacklightReachBlocks = 1.5f;
    /// <summary>Tint of the transmitted light over the leaf's own albedo, authored in sRGB (warm).</summary>
    public static readonly Vector3 LeafBacklightTint = new(1f, 0.9f, 0.6f);

    // --- Grass wind shimmer (test-fragment.glsl, GrassShimmer) ---
    /// <summary>
    /// How much a shimmer band brightens the grass tops, as a share of their own light at a band's
    /// crest and a full gust (uGrassShimmer). 0 switches the term off.
    /// </summary>
    public const float GrassShimmerStrength = 0.5f;
    /// <summary>Distance between two shimmer bands along the wind, in blocks.</summary>
    public const float GrassShimmerWavelengthBlocks = 7f;
    /// <summary>Distance from the eye, in blocks, at which the shimmer starts to fade out.</summary>
    public const float GrassShimmerFadeNearBlocks = 48f;
    /// <summary>Distance from the eye, in blocks, past which there is no shimmer: a band a few blocks
    /// wide turns into shimmering pixels well inside the full-detail ring.</summary>
    public const float GrassShimmerFadeFarBlocks = 112f;

    // --- Ice glint (test-fragment.glsl, IceGlint) ---
    /// <summary>
    /// Brightness of the sun highlight on ice, as a multiple of the direct sun (uIceGlint), added in
    /// linear light. 0 switches the whole ice term off, the sky sheen included.
    /// </summary>
    public const float IceGlintStrength = 1f;
    /// <summary>Blinn exponent of the ice sun highlight: high, since ice is a smooth, flat face.</summary>
    public const float IceGlintShine = 96f;
    /// <summary>Share of the mirrored sky an ice face shows at a full graze (scaled by the Schlick fresnel).</summary>
    public const float IceSheenStrength = 0.6f;

    // --- Glass sheen (test-fragment.glsl, GlassSheen) ---
    /// <summary>
    /// Share of the mirrored sky a glass (or pane) face shows at a full graze, scaled by the Schlick
    /// fresnel and folded into the blend (uGlassSheen). 0 switches the term off.
    /// </summary>
    public const float GlassSheenStrength = 0.6f;

    // --- Wet albedo (test-fragment.glsl, SH-22) ---
    // Albedo multiplier on a submerged face below its own baked water surface. Dry faces beside or
    // above the water are never darkened: the old wet band tinted dry beach tops and banks.
    public const float WetDarken = 0.62f;

    // --- Biome tint (BiomeTint: grass and foliage color from climate) ---
    // sRGB target colors of a flat, fully lit tinted face, one palette per tint kind: three rows
    // along TerrainGenerator.Climate's wetness (0 = Dry, 0.5 = Mid, 1 = Wet), each with a cold
    // (warmth 0) and a warm (warmth 1) corner, interpolated piecewise (BiomeTint.PaletteSrgb).
    // Wetness 0.5 is the band between the dry and the semi-dry humidity ramps, so the Dry row sits
    // on frozen tundra and desert, the Mid row on steppe, savanna and the dry taiga edge, and the
    // Wet row on taiga and forest; the temperate band is the midpoint of each row's two corners.
    // Each Mid corner lies between its Dry and Wet corners on the red/green ratio but yellower
    // than their plain average, so the grass dries monotonically and the steppe and savanna read
    // as dry grassland rather than as a muted forest. At full wetness the grass midpoint
    // (70, 132, 77) lands near grass_top's measured mean, so near-spawn grass matches the
    // inventory icon. Every channel must stay under the ceiling BiomeTint.Gain reaches against
    // the gray tiles (about sRGB 183), which BiomeTintTests enforces.
    public static readonly Vector3 GrassColorColdDry = new Vector3(112f, 138f, 104f) / 255f;
    public static readonly Vector3 GrassColorColdMid = new Vector3(100f, 134f, 98f) / 255f;
    public static readonly Vector3 GrassColorColdWet = new Vector3(74f, 124f, 88f) / 255f;
    public static readonly Vector3 GrassColorWarmDry = new Vector3(142f, 146f, 70f) / 255f;
    public static readonly Vector3 GrassColorWarmMid = new Vector3(128f, 145f, 68f) / 255f;
    public static readonly Vector3 GrassColorWarmWet = new Vector3(66f, 140f, 66f) / 255f;
    // Foliage (leaves, spruce leaves): the same climate axes and rows, a little darker than the grass.
    public static readonly Vector3 FoliageColorColdDry = new Vector3(92f, 118f, 86f) / 255f;
    public static readonly Vector3 FoliageColorColdMid = new Vector3(82f, 116f, 80f) / 255f;
    public static readonly Vector3 FoliageColorColdWet = new Vector3(58f, 106f, 70f) / 255f;
    public static readonly Vector3 FoliageColorWarmDry = new Vector3(120f, 126f, 58f) / 255f;
    public static readonly Vector3 FoliageColorWarmMid = new Vector3(110f, 127f, 56f) / 255f;
    public static readonly Vector3 FoliageColorWarmWet = new Vector3(52f, 120f, 52f) / 255f;
    // The sRGB gray every textureless tile averages to (the two supplied grass tiles measure 98.4
    // and 97.4, and every leaf tile is normalized to it), so the
    // one BiomeTint.Gain fits them all.
    public const float BiomeTintTextureGray = 98f / 255f;

    // --- LOD sky exposure ---
    // LOD vertices bake a coarse skylight term from the column heights (LodSkyExposure), so a
    // far notch or the underside of a canopy bump is not lit like open ground. Open ground,
    // open walls and water keep 1, matching the full skylight near chunks give open sky.
    // How many cells a column must rise above a vertex to fully block that direction. The rise
    // is measured against a one-cell horizontal step, so it is a slope and holds at every level.
    public const float LodSkyRiseCells = 4f;
    // How much a fully blocked horizon (all sampled columns saturated) darkens a vertex
    public const float LodValleyShade = 0.45f;
    // How much the base of a canopy bump is darkened relative to its top face
    public const float LodCanopyShade = 0.55f;
    // Lowest sky exposure any LOD vertex can bake, so no far face goes pitch black
    public const float LodSkyExposureFloor = 0.35f;

    // --- Canopy wind (leaf-wave vertex shader) ---
    // Master strength of the leaf sway, 0 disables it; wavelengths and gusts live in
    // test-vertex.glsl next to the wave itself
    public const float LeafSwayStrength = 1f;
}
