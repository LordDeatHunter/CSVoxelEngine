using VoxelEngine.Data;

namespace VoxelEngine.Config;

/// <summary>
/// Particle tuning: falling leaves, mining debris, campfire, lava and torch embers and smoke,
/// exhaled bubbles, hot-spring steam and the blade slice trail.
/// </summary>
public static class ParticleConfig
{
    // --- Falling leaves (LeafParticleSystem + LeafParticleRenderer) ---
    // Ambient leaf fall around the camera plus a burst whenever canopy is disturbed
    // (mined, felled, blasted). One batched billboard draw covers all of them.
    public const int LeafParticleMax = 512;
    public const float LeafParticleSpawnPerSecond = 30f;
    public const float LeafParticleSpawnRadius = 24f;
    public const int LeafParticleSpawnTries = 6;
    public const float LeafParticleLifetime = 9f;
    // Leaves flutter down at a near-constant sink instead of accelerating like drops
    public const float LeafParticleFallSpeed = 0.9f;
    public const float LeafParticleFallDrag = 2.5f;
    // A 6x6-texel crop of a 32px tile spans 6/32 of a block, rounded up a touch
    public const float LeafParticleSize = 0.2f;
    // Seconds a landed leaf takes to fade out before it is removed
    public const float LeafParticleFadeSeconds = 1.2f;
    // Texels of the centered square cropped out of the leaves tile for the sprite
    public const int LeafParticleCropPixels = 6;
    public const int LeafParticleBurstMined = 6;
    public const int LeafParticleBurstFell = 24;
    public const int LeafParticleBurstBlast = 16;
    // A leaf dissolving through LeafDecayManager: a light flutter, many fall in a row
    public const int LeafParticleBurstDecay = 2;

    // --- Mining debris (BlockDebris + BlockDebrisSystem) ---
    // Small block-textured cubes chipped off on every mining hit plus a bigger burst when
    // the block breaks. Visual only: they fall, bounce and fade, never collect.
    public const int BlockDebrisMax = 512;
    public const int BlockDebrisPerHit = 4;
    public const int BlockDebrisOnBreak = 20;
    public const float BlockDebrisLifetime = 2.0f;
    public const float BlockDebrisSize = 0.12f;
    // Seconds a cube takes to fade out at the end of its life (or after landing)
    public const float BlockDebrisFadeSeconds = 0.4f;

    public const int LeafParticleBurstThud = 20;

    // --- Campfire particles (FireParticleSystem + FireParticleRenderer) ---
    // Embers and smoke rising off lit firepits, spawned per lit cell near the camera.
    public const float FireEmberLifetime = 1.8f;
    public const float FireSmokeLifetime = 5.0f;
    public const float FireEmberRise = 0.8f;
    public const float FireSmokeRise = 0.5f;
    public const float FireEmberSize = 0.09f;
    public const float FireSmokeSize = 0.22f;
    public const float FireSmokeGrow = 0.12f;
    public const int FireParticleMax = 512;
    public const float FireEmberPerSecond = 36f;
    public const float FireSmokePerSecond = 8f;
    public const float FireParticleRadius = 48f;

    // --- Lava embers and smoke (a second FireParticleSystem instance in World) ---
    // Exposed lava cells ("hot fluid vents", recorded per chunk by the mesher) emit sparse
    // embers and smoke. Their own instance keeps them from competing with campfires.
    /// <summary>Embers per second from each lava vent in range; a pool has many vents, so this is far sparser than a campfire.</summary>
    public const float LavaEmberPerSecond = 0.6f;
    /// <summary>Smoke puffs per second from each lava vent in range.</summary>
    public const float LavaSmokePerSecond = 0.25f;
    /// <summary>Ring capacity of the lava particle instance; the oldest particle is evicted past it.</summary>
    public const int LavaParticleMax = 256;
    /// <summary>Only vents within this distance (blocks) of the eye emit.</summary>
    public const float LavaVentRadius = 32f;
    /// <summary>Cap on emitting vents per frame, filled nearest chunk first so the pool beside the player always wins.</summary>
    public const int LavaVentMaxEmitters = 96;

    // --- Torch flames (a third FireParticleSystem instance in World) ---
    // Every block with a JSON "flame" point (the torches; recorded per chunk by the mesher in
    // Chunk.FlamePoints) gives off a small flicker of embers and a thin wisp of smoke from exactly
    // that point. Cosmetic: never on a headless server.
    /// <summary>Embers per second off each torch in range.</summary>
    public const float TorchEmberPerSecond = 7f;
    /// <summary>Smoke puffs per second off each torch in range.</summary>
    public const float TorchSmokePerSecond = 1.2f;
    /// <summary>Ring capacity of the torch particle instance.</summary>
    public const int TorchParticleMax = 256;
    /// <summary>Only flames within this distance (blocks) of the eye burn.</summary>
    public const float TorchFlameRadius = 24f;
    /// <summary>Cap on burning flames per frame, filled nearest chunk first.</summary>
    public const int TorchFlameMaxEmitters = 48;
    /// <summary>Spawn scatter across a flame point, in blocks: a pixel or so, since the point is exact.</summary>
    public const float TorchFlameJitter = 0.06f;
    /// <summary>Particle scale of a torch flame against a campfire's 1 (size, lifetime, speed, sway).</summary>
    public const float TorchFlameScale = 0.45f;

    // --- Exhaled bubbles (BubbleParticleSystem + BubbleParticleRenderer) ---
    // Small billboards rising off the player's mouth while the eye is submerged, popping
    // at the water surface. One batched billboard draw covers all of them.
    public const int BubbleParticleMax = 128;
    public const float BubblePerSecond = 1f;
    public const float BubbleLifetime = 4f;
    public const float BubbleRise = 0.1f;
    public const float BubbleDrag = 3f;
    public const float BubbleSize = 0.09f;
    public const float BubbleGrow = 0.02f;
    // Seconds a bubble takes to fade in at spawn and out at the end of its life
    public const float BubbleFadeSeconds = 0.4f;
    // Each swim stroke with the eye underwater lets out one burst of this many bubbles
    // (BubbleParticleSystem.TrackSwimStrokes), centered this far in front of the head and scattered
    // up to this far around that point.
    public const int SwimStrokeBubbleCount = 4;
    public const float SwimStrokeBubbleForward = 0.4f;
    public const float SwimStrokeBubbleSpread = 0.15f;

    // --- Water ripple rings (RippleSystem + RippleRenderer, rule WaterRipple) ---
    // A flat ring spreads over the water top wherever a body crosses the surface (the splash
    // crossings of WaterSplash); a harder crossing (strength 0..1) spreads wider, slower and longer.
    public const int RippleMax = 48;
    public const float RippleMinRadius = 0.7f;
    public const float RippleMaxRadius = 2.4f;
    public const float RippleMinSeconds = 0.9f;
    public const float RippleMaxSeconds = 1.7f;
    public const float RippleAlpha = 0.9f;
    /// <summary>Lift over the water top, so the wave train never z-fights the surface it lies on.</summary>
    public const float RippleLift = 0.03f;
    /// <summary>Distance in blocks between two crests of the train behind the leading edge.</summary>
    public const float RippleWavelength = 0.32f;
    /// <summary>How many wavelengths of train trail the leading edge before it dies away.</summary>
    public const float RippleTrainWaves = 3.2f;
    /// <summary>The quad reaches this far past the final radius, so the last crest is never clipped.</summary>
    public const float RippleQuadMargin = 1.12f;

    // Steam: puffs a second over every vent in range, the reach it gathers within and the cap
    public const float SpringSteamPerSecond = 0.6f;
    public const float SpringSteamRadius = 32f;
    public const int SpringSteamMaxVents = 16;
    public const int SpringSteamMaxPuffsPerFrame = 4;
    public const int SpringSteamCount = 2;
    public const float SpringSteamSpread = 0.35f;

    // Slice trail (SliceTrail, SliceTrailRibbon, SliceTrailGlow, SliceTrailRenderer)
    // A glowing ribbon behind a swinging blade's tip: samples in the swinging player's owner
    // frame, a tapered Catmull-Rom ribbon over them, and a procedural glow drawn additively.

    /// <summary>Committed tip samples one trail keeps; the live head rides on top of them.</summary>
    public const int SliceTrailMaxSamples = 48;

    /// <summary>Seconds a sample lives before it is pruned from the trail's old end.</summary>
    public const float SliceTrailLifeSeconds = 0.22f;

    /// <summary>Shortest gap between committed samples, so the ring holds the whole life at any frame rate.</summary>
    public const float SliceTrailMinSampleSeconds = SliceTrailLifeSeconds / (SliceTrailMaxSamples - 2);

    /// <summary>Owner-frame blocks the tip must travel before another sample is committed.</summary>
    public const float SliceTrailMinStepBlocks = 0.01f;

    /// <summary>Owner-frame tip speed (blocks per second) below which an update counts as still.</summary>
    public const float SliceTrailStillBlocksPerSecond = 0.25f;

    /// <summary>Tip speed (blocks per second) at which the trail starts to widen from nothing.</summary>
    public const float SliceTrailMinSpeed = 4f;

    /// <summary>Tip speed (blocks per second) at which the trail reaches its full width.</summary>
    public const float SliceTrailFullSpeed = 14f;

    /// <summary>Catmull-Rom steps the ribbon builder draws between two samples.</summary>
    public const int SliceTrailSubdivisions = 4;

    /// <summary>Share of a segment, from its old end, over which the ribbon tapers in from nothing.</summary>
    public const float SliceTrailTailTaper = 0.35f;

    /// <summary>Share of its width a cross-section loses by the end of its life.</summary>
    public const float SliceTrailAgeShrink = 0.6f;

    /// <summary>Tip point along the sprite diagonal (-1 the hilt corner, 1 the tip corner).</summary>
    public const float SliceTrailTipReach = 0.92f;

    /// <summary>Inner edge point along the sprite diagonal (-1 the hilt corner, 1 the tip corner).</summary>
    public const float SliceTrailInnerReach = 0.15f;

    /// <summary>Across-ribbon position (0 inner edge, 1 tip edge) of the white core band.</summary>
    public const float SliceTrailCoreCenter = 0.85f;

    /// <summary>Gaussian half-width of the white core band, across the ribbon.</summary>
    public const float SliceTrailCoreWidth = 0.08f;

    /// <summary>Extra brightness the core band adds over the colored body.</summary>
    public const float SliceTrailCoreBoost = 1.2f;

    /// <summary>Exponent of the body's brightening from the inner edge toward the tip edge.</summary>
    public const float SliceTrailGlowPower = 0.7f;

    /// <summary>Lanes across the ribbon whose wispy streaks switch on one after another.</summary>
    public const int SliceTrailStreakLanes = 7;

    /// <summary>How deeply a streak lane that is still off dims the body (0 never, 1 to black).</summary>
    public const float SliceTrailStreakDepth = 0.7f;

    /// <summary>Arc length in blocks over which the streak lanes switch on.</summary>
    public const float SliceTrailStreakSpanBlocks = 1.2f;

    /// <summary>Trail color of a blade item whose JSON names no <c>trail</c> color (sRGB).</summary>
    public static readonly Rgba SliceTrailDefaultColor = new(220, 220, 255);
}
