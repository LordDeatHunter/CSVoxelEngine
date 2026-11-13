namespace VoxelEngine.Config;

/// <summary>
/// Sky tuning: the day length, the sun and its glare, the sky dome and sunset glow, stars, the moon
/// and moonlight, the aurora, the cloud layer and the wind.
/// </summary>
public static class SkyConfig
{
    // --- Lighting ---
// Full day/night cycle length in seconds (sun orbit + sky + light modulation): 40 real
// minutes, so the sun crosses the sky at 0.15 degrees per second.
public const float DayLengthSeconds = 2400f;

    // --- Sky ---
    // Angular radius of the visible sun disc in degrees. The real sun is 0.27 degrees across
    // its radius, but at that size it reads as a pinprick, so games exaggerate it.
    public const float SunAngularRadiusDeg = 3.5f;

    // Radius of the glow quad as a multiple of the disc radius: the quad is drawn this much
    // bigger than the disc and the extra ring is pure falloff around it.
    public const float SunGlowScale = 4f;

    // Peak brightness the glow adds at the disc's edge, before the sun color tints it
    public const float SunGlowIntensity = 0.55f;

    // Falloff exponent of the glow from the disc outwards; higher means a tighter halo
    public const float SunGlowExponent = 2.5f;

    // Lens glare (SunGlare): a much wider, fainter glow around the sun that grows as the camera
    // faces it. Linear HDR brightness it adds right at the sun, times the sun color, at its peak.
    // Kept below the point where the sky round the disc washes out to white, so the disc stays crisp.
    public const float SunGlareStrength = 0.28f;

    // Falloff of the glare across the sky: cos(angle to the sun) raised to this. Lower is wider.
    public const float SunGlareExponent = 8f;

    // Angle between the view and the sun beyond which there is no glare at all
    public const float SunGlareFacingDegrees = 65f;

    // Sine of the sun's elevation from which the glare is at full strength; it fades to nothing
    // as the sun reaches the horizon
    public const float SunGlareFullElevation = 0.15f;

    // Rate (per second) at which the glare follows the cloud cover over the sun, so a drifting
    // cloud edge fades it instead of stepping it
    public const float SunGlareCloudEaseRate = 2.5f;

    // --- Sky dome (SkyPalette, SkySample in the sky/world/cloud shaders) ---
    /// <summary>Sine of the view elevation at which the zenith color has fully taken over from the horizon.</summary>
    public const float SkyHorizonBand = 0.35f;

    /// <summary>Sine of the view elevation over which the brighter horizon haze band fades out.</summary>
    public const float SkyHazeWidth = 0.08f;

    /// <summary>
    /// How much brighter the haze band makes the horizon right at elevation 0 (0.2 = 20 %).
    /// 0 while the palette is the flat 28198cc sky color: the haze multiplies the (linearized)
    /// horizon, which would push the low sky and every fogged fragment away from that color.
    /// </summary>
    public const float SkyHazeStrength = 0f;

    /// <summary>Azimuthal falloff exponent of the dusk glow toward the sun; higher is a narrower lobe.</summary>
    public const float SkyGlowExponent = 4f;

    /// <summary>How far the flat sky color moves toward the dusk orange at full dusk (the 28198cc mix).</summary>
    public const float SkyDuskMix = 0.5f;

    /// <summary>Brightness of the sunset glow band toward the sun at its peak (a scale on its sRGB hue in SkyPalette).</summary>
    public const float SkyDuskGlowStrength = 0.5f;

    /// <summary>Sine of the sun's elevation at and above which there is no sunset glow (the flat day sky).</summary>
    public const float SkyGlowAboveSunElevation = 0.2f;

    /// <summary>Sine of the sun's elevation at which the sunset glow is strongest (the sun on the horizon).</summary>
    public const float SkyGlowPeakSunElevation = 0f;

    /// <summary>
    /// Sine of the sun's elevation at and below which the afterglow is gone. Must stay at or above
    /// MoonlightSwitchSunElevation, where the moon takes over the world's light (MoonlightTests).
    /// </summary>
    public const float SkyGlowBelowSunElevation = -0.1f;

    // --- Stars (StarField, Stars in sky-fragment.glsl) ---
    /// <summary>Star cells along each side of one face of the cube of directions (6 faces); a cell holds at most one star.</summary>
    public const int StarCellsPerFace = 72;

    /// <summary>
    /// Chance a cell at a cube face's center holds a star (StarField.Probability scales it per cell
    /// by solid angle), so about this share of 6 * StarCellsPerFace^2 stars cover the whole sphere.
    /// </summary>
    public const float StarDensity = 0.16f;

    /// <summary>
    /// Linear HDR brightness of the brightest star, before its color tint and twinkle (which dims
    /// a star by StarTwinkleDepth / 2 on average). The night dome is near black and the post pass
    /// only clamps and encodes, so 1 already reads as a display-white point.
    /// </summary>
    public const float StarBrightness = 1.35f;

    /// <summary>Brightness fraction of the faintest star; the rest spread up to 1 along the power law below.</summary>
    public const float StarFaintest = 0.04f;

    /// <summary>Exponent of the star brightness power law: higher makes bright stars rarer.</summary>
    public const float StarBrightnessCurve = 4f;

    /// <summary>
    /// Gaussian radius of a faint star in screen pixels (pixel-relative, so stars stay pin-sharp
    /// at any resolution and FOV and never vanish between pixel centers while the view turns).
    /// </summary>
    public const float StarRadiusPixels = 0.85f;

    /// <summary>How much wider than a faint star the brightest star's point is.</summary>
    public const float StarBrightRadiusScale = 1.6f;

    /// <summary>Sine of the sun's elevation above which no star shows; below it they fade in over dusk.</summary>
    public const float StarsGoneAboveSunElevation = -0.02f;

    /// <summary>Sine of the sun's elevation below which the stars are at full brightness.</summary>
    public const float StarsFullBelowSunElevation = -0.22f;

    /// <summary>Sine of the view elevation over which the stars fade in above the horizon haze.</summary>
    public const float StarHorizonFade = 0.2f;

    /// <summary>
    /// Radius of the starless disc around the sun as a multiple of the sun's glow radius
    /// (SunSize * SunGlowScale); the stars feather back in over the next half of that radius.
    /// </summary>
    public const float StarSunMaskScale = 1.2f;

    /// <summary>How deep a star's twinkle dips: its brightness swings between 1 - this and 1.</summary>
    public const float StarTwinkleDepth = 0.45f;

    /// <summary>
    /// Slowest and fastest twinkle, in cycles per second; each star draws its own rate between them
    /// from its cell hash. Slow on purpose: a real scintillation rate would flicker at the frame rate.
    /// </summary>
    public const float StarTwinkleRateMin = 0.25f;
    public const float StarTwinkleRateMax = 1.2f;

    // --- Shooting stars (ShootingStar, ShootingStar in sky-fragment.glsl) ---
    /// <summary>Seconds per slot; a slot holds at most one streak. Must divide the star clock's hourly wrap (3600) so the slots wrap with it.</summary>
    public const float ShootingStarSlotSeconds = 9f;

    /// <summary>Chance a slot holds a streak: about one every ShootingStarSlotSeconds / this.</summary>
    public const float ShootingStarChance = 0.3f;

    /// <summary>How long a streak lives; shorter than a slot, so one never spills into the next.</summary>
    public const float ShootingStarLifeSeconds = 0.9f;

    /// <summary>The angle the head travels along its great circle over its life, and how far the tail trails it.</summary>
    public const float ShootingStarArcDegrees = 30f;
    public const float ShootingStarTrailDegrees = 10f;

    /// <summary>The band of sine-elevations a streak starts in (high enough to clear the horizon haze).</summary>
    public const float ShootingStarMinSinElevation = 0.3f;
    public const float ShootingStarMaxSinElevation = 0.85f;

    /// <summary>Gaussian radius of the streak across its path in screen pixels (pixel-relative like the stars').</summary>
    public const float ShootingStarRadiusPixels = 1.5f;

    /// <summary>Linear HDR brightness of the head, like StarBrightness.</summary>
    public const float ShootingStarBrightness = 2.2f;

    // --- Moon (MoonPhase, SkyPasses.RenderMoon, moon-fragment.glsl) ---
    /// <summary>Days from one full moon to the next; the phase steps once a day, at noon.</summary>
    public const int MoonCycleDays = 8;

    /// <summary>Angular radius of the moon disc in degrees (exaggerated like the sun's).</summary>
    public const float MoonAngularRadiusDeg = 2.6f;

    /// <summary>Radius of the moon quad as a multiple of the disc: the extra ring holds its faint halo.</summary>
    public const float MoonGlowScale = 2.2f;

    /// <summary>Peak brightness of the halo at the disc's edge, scaled by how much of the disc is lit.</summary>
    public const float MoonGlowIntensity = 0.1f;

    /// <summary>Falloff exponent of the halo from the disc outwards.</summary>
    public const float MoonGlowExponent = 3f;

    /// <summary>Linear HDR brightness of the lit disc (times its sprite and its cool tint).</summary>
    public const float MoonIntensity = 1.6f;

    /// <summary>Share of the lit brightness the unlit part keeps (earthshine), so a crescent still shows its whole disc faintly.</summary>
    public const float MoonEarthshine = 0.025f;

    /// <summary>Half-width of the soft terminator between the lit and dark parts, in units of the sphere normal's dot with the light.</summary>
    public const float MoonTerminatorSoftness = 0.06f;

    // --- Aurora (Aurora, AuroraCurtains in sky-fragment.glsl) ---
    /// <summary>Coldness at the eye (EyeClimate.Coldness, the cold ramp) at and below which there is no aurora: the snow line.</summary>
    public const float AuroraColdStart = 0.55f;

    /// <summary>Coldness at the eye from which the aurora is at full strength (past the taiga/frozen line).</summary>
    public const float AuroraColdFull = 0.85f;

    /// <summary>How fast the eased coldness follows the eye's climate (MathUtils.Damp rate, per second): a slow fade across a border.</summary>
    public const float AuroraColdEaseRate = 0.35f;

    /// <summary>Linear HDR brightness of the curtains' bright lower edge at full strength.</summary>
    public const float AuroraBrightness = 0.5f;

    /// <summary>How fast the curtains drift and ripple along the sky (noise cells per second).</summary>
    public const float AuroraDriftSpeed = 0.035f;

    // --- Realm layers (LayerClimate, FrameLighting.SetLighting; the Y bands are in WorldGenerationConfig) ---
    /// <summary>sRGB color the sky palette, and with it the fog and the clear color, turns to with the
    /// eye inside hell: a dim ember red.</summary>
    public static readonly System.Numerics.Vector3 HellFogColor = new(0.32f, 0.06f, 0.03f);

    /// <summary>Blocks below hell's top over which the hell tint ramps from none to full, so crossing
    /// the boundary never pops.</summary>
    public const float HellFogRampBlocks = 24f;

    /// <summary>How fast (MathUtils.Damp rate, per second) the eased hell and altitude amounts follow
    /// the eye, so a teleport or a fast fall still fades.</summary>
    public const float LayerFogEaseRate = 2.5f;

    /// <summary>The world fog's near distance in hell, in blocks: the caverns close in.</summary>
    public const float HellFogNearBlocks = 8f;

    /// <summary>The world fog's far distance in hell, in blocks.</summary>
    public const float HellFogFarBlocks = 110f;

    /// <summary>Per-channel factor the sky palette is multiplied by at full altitude (the eye at the sky
    /// islands, ramped in from the cloud slab's top): a slightly deeper, brighter blue that keeps a
    /// night sky dark.</summary>
    public static readonly System.Numerics.Vector3 SkyAltitudeTint = new(0.82f, 0.93f, 1.1f);

    // --- Cave biome atmosphere (CaveClimate, FrameLighting.SetLighting) ---
    /// <summary>How fast (MathUtils.Damp rate, per second) the eased cave atmosphere follows the eye's cave
    /// biome: slower than the layer fog, so stepping across a biome or cover line fades.</summary>
    public const float CaveFogEaseRate = 1.2f;

    /// <summary>The world fog's near distance in a cave biome at full strength, in blocks.</summary>
    public const float CaveFogNearBlocks = 16f;

    /// <summary>The world fog's far distance in a cave biome at full strength, in blocks.</summary>
    public const float CaveFogFarBlocks = 180f;

    /// <summary>How strongly a cave biome's atmosphere applies at full ease (0..1), every biome but the
    /// magma deep.</summary>
    public const float CaveFogStrength = 0.8f;

    /// <summary>How strongly the magma deep's atmosphere applies at full ease (0..1): its haze is the
    /// thickest.</summary>
    public const float CaveFogStrengthMagmaDeep = 0.9f;

    /// <summary>sRGB fog color of a frozen echo cave: a dim, cold pale blue.</summary>
    public static readonly System.Numerics.Vector3 CaveFogFrozenEcho = new(0.07f, 0.10f, 0.14f);

    /// <summary>sRGB fog color of a desert echo cave: a dusty ochre.</summary>
    public static readonly System.Numerics.Vector3 CaveFogDesertEcho = new(0.12f, 0.09f, 0.05f);

    /// <summary>sRGB fog color of a crystal cave: a dim violet.</summary>
    public static readonly System.Numerics.Vector3 CaveFogCrystal = new(0.08f, 0.06f, 0.13f);

    /// <summary>sRGB fog color of a fungal cave: a dim teal green.</summary>
    public static readonly System.Numerics.Vector3 CaveFogFungal = new(0.04f, 0.10f, 0.08f);

    /// <summary>sRGB fog color of the magma deep: a dim ember.</summary>
    public static readonly System.Numerics.Vector3 CaveFogMagmaDeep = new(0.14f, 0.05f, 0.03f);

    /// <summary>Per-channel ambient factor of a frozen echo cave at full strength: a touch cooler.</summary>
    public static readonly System.Numerics.Vector3 CaveAmbientFrozenEcho = new(0.92f, 0.97f, 1.06f);

    /// <summary>Per-channel ambient factor of a desert echo cave at full strength: a touch warmer.</summary>
    public static readonly System.Numerics.Vector3 CaveAmbientDesertEcho = new(1.05f, 1f, 0.92f);

    /// <summary>Per-channel ambient factor of a crystal cave at full strength: a touch cooler and violet.</summary>
    public static readonly System.Numerics.Vector3 CaveAmbientCrystal = new(0.98f, 0.94f, 1.08f);

    /// <summary>Per-channel ambient factor of a fungal cave at full strength: a touch greener.</summary>
    public static readonly System.Numerics.Vector3 CaveAmbientFungal = new(0.94f, 1.06f, 0.98f);

    /// <summary>Per-channel ambient factor of the magma deep at full strength: warmer.</summary>
    public static readonly System.Numerics.Vector3 CaveAmbientMagmaDeep = new(1.1f, 0.96f, 0.88f);

    // --- Moonlight (Moonlight, FrameLighting.SetLighting) ---
    /// <summary>
    /// Sine of the sun's elevation at and below which the moon lights the world instead of the sun.
    /// Must stay at or below SkyGlowBelowSunElevation, so the sunset glow is gone by then
    /// (MoonlightTests asserts it).
    /// </summary>
    public const float MoonlightSwitchSunElevation = -0.1f;

    /// <summary>How much further the sun sinks (the moon climbs), in sine of elevation, while the moonlight eases in.</summary>
    public const float MoonlightRiseBand = 0.25f;

    /// <summary>The full moon's light in the world shader's sun-intensity units (the noon sun is about 2.6).</summary>
    public const float MoonlightIntensity = 0.07f;

    /// <summary>Share of the full moon's light a new moon still gives, so a moonless night keeps faint shadows.</summary>
    public const float MoonlightNewMoonShare = 0.35f;

    // --- Clouds ---
    // One static field of CloudCellSize-block cells (CloudField) drawn 3x3 around the camera by
    // CloudRenderer. Cell levels come from periodic value noise, so the field tiles with no seam.
    // Heights clear the tallest terrain (~112) plus trees.
    public const float CloudHeight = 160f;
    // Drawn as whole copies 3x3 around the camera, so this must be >= FogFarBlocks for the
    // field edge to stay hidden in fog. Must be a power-of-two multiple of CloudCellSize.
    public const float CloudFieldSize = 2048f;
    public const float CloudCellSize = 16f;
    public const float CloudLayerThickness = 4f;

    /// <summary>Base height of the second, thick cloud sea that lies under the sky islands
    /// (visual only, plans/LAYERS.md): above the solid cloud slab
    /// (<see cref="WorldGenerationConfig.CloudSlabTopY"/>) and below
    /// <see cref="WorldGenerationConfig.SkyIslandBottomY"/>.</summary>
    public const float SkyCloudSeaHeight = 290f;

    /// <summary>Thickness of the sky-island cloud sea in blocks.</summary>
    public const float SkyCloudSeaThickness = 12f;
    public const int CloudMaxLevels = 2;
    // Density above which a cell holds a cloud (tuned for 30-45 % coverage; CloudTests asserts it),
    // and how much more density each extra stacked layer needs.
    public const float CloudThreshold = 0.54f;
    public const float CloudLevelStep = 0.09f;
    public const float CloudOpacity = 0.8f;
    // Fillet radius on every convex cloud edge, in blocks. Must stay below half the layer thickness
    // and half the cell size so the flat parts of a one-layer cell keep a positive size.
    public const float CloudCornerRadius = 1.5f;
    // Quads per quarter turn of a fillet; 3 is plenty with smooth per-vertex normals.
    public const int CloudFilletSegments = 3;
    public const int CloudSeed = 9241;
    public const float CloudDriftBlocksPerSecond = 1.5f;
    /// <summary>
    /// Side of one cull tile of the cloud field mesh, in cells (CloudTiles): each copy of the field
    /// is drawn as the index runs of the tiles that may rasterize in the pass's frustum.
    /// </summary>
    public const int CloudTileCells = 16;
    /// <summary>
    /// How far a cloud tile's vertex box is grown on every side before the frustum test
    /// (CloudTiles.MayRasterize), in blocks. Its job is the near plane: Frustum's near plane is the
    /// Direct3D z_clip &gt;= 0 plane of the System.Numerics projection, but GL (no glClipControl)
    /// clips at z_clip &gt;= -w, which with Camera's planes still rasterizes geometry down to about
    /// half of Camera.NearPlane from the eye. A grown box moves every plane distance by at least the
    /// margin, so the margin must be at least Camera.NearPlane, plus float slack for evaluating the
    /// planes on the CPU at field coordinates against the GPU's transform. A smaller margin can cull
    /// visible near-eye cloud geometry and break the cull's exactness.
    /// </summary>
    public const float CloudTileCullMarginBlocks = 1f;
    /// <summary>
    /// Fraction of direct sun a full-thickness cloud removes from the terrain under its shadow
    /// (test-fragment.glsl CloudShadow); a one-layer cloud removes half of it.
    /// </summary>
    public const float CloudShadowStrength = 0.4f;

    // Silver lining (CloudSilverLining, cloud-fragment.glsl): thin cloud edges near the sun light up.
    /// <summary>Peak brightness of the lining, as a share of the cloud's own display-space sun term.</summary>
    public const float CloudSilverLiningStrength = 0.9f;
    /// <summary>Forward-scatter falloff: cos(angle to the sun) raised to this. Higher keeps it closer to the sun.</summary>
    public const float CloudSilverLiningExponent = 10f;
    /// <summary>Sine of the sun's elevation from which the lining is at full strength (zero at the horizon).</summary>
    public const float CloudSilverLiningFullElevation = 0.1f;
    /// <summary>Blocks between the points the thinness test reads along the view ray behind a cloud surface.</summary>
    public const float CloudSilverLiningStepBlocks = 2.5f;

    // --- Wind (Wind.cs, SH-27) ---
    // One slowly turning wind shared by leaf sway, fire smoke and the cloud drift.
    /// <summary>Wind heading at lighting time 0, degrees from +X toward +Z.</summary>
    public const float WindStartDegrees = 30f;
    /// <summary>How fast the wind heading turns: a full circle every 2400 s (two day cycles).</summary>
    public const float WindTurnDegreesPerSecond = 360f / 2400f;
    /// <summary>Blocks per second the leaf gust pattern rolls downwind (Wind.Offset -> uWindOffset).</summary>
    public const float WindGustSpeed = 3f;
    /// <summary>Downwind lean of swaying leaves at full gust, in blocks (uploaded as the length of uWind).</summary>
    public const float WindLean = 0.02f;
    /// <summary>Blocks per second a smoke puff drifts downwind once it has risen clear of the fire.</summary>
    public const float WindSmokeDrift = 0.35f;
    /// <summary>Blocks per second an ember drifts downwind.</summary>
    public const float WindEmberDrift = 0.15f;
    /// <summary>Blocks per second a falling leaf drifts downwind at full gust (scaled by Wind.Gust).</summary>
    public const float WindLeafDrift = 1.5f;
    /// <summary>How fast (1/s) a falling leaf's sideways motion settles into the wind's, so a burst's
    /// toss is carried off downwind within a second or so.</summary>
    public const float WindLeafDrag = 1.2f;
}
