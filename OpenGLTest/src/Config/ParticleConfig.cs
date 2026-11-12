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

    // An axe hit on tree wood shakes the canopy (TreeShudder): at most this many leaf cells of the box
    // above the hit, each shedding a leaf or two
    public const int TreeShudderCells = 5;
    public const int TreeShudderLeavesPerCell = 1;
    public const int TreeShudderReachBlocks = 4;
    public const int TreeShudderHeightBlocks = 14;

    // --- Campfire particles (FireParticleSystem + FireParticleRenderer) ---
    // Embers and smoke rising off lit firepits, spawned per lit cell near the camera.
    public const float FireEmberLifetime = 1.8f;
    public const float FireSmokeLifetime = 5.0f;
    public const float FireEmberRise = 0.8f;
    public const float FireSmokeRise = 0.5f;
    public const float FireEmberSize = 0.09f;
    public const float FireSmokeSize = 0.22f;
    public const float FireSmokeGrow = 0.12f;
    public const int FireParticleMax = 1536;
    public const float FireEmberPerSecond = 36f;
    public const float FireSmokePerSecond = 8f;
    public const float FireParticleRadius = 48f;
    /// <summary>Flame tongues per second off each lit cell: soft additive teardrops that rise, shrink and cool from white-yellow to deep red.</summary>
    public const float FireFlamePerSecond = 34f;
    public const float FireFlameLifetime = 0.75f;
    public const float FireFlameRise = 1.05f;
    public const float FireFlameSize = 0.36f;
    /// <summary>How much narrower than tall a flame tongue is drawn (its quad is this share as wide as it is high).</summary>
    public const float FireFlameWidthShare = 0.7f;
    /// <summary>Share of a cell's spawn scatter a flame's base spreads over; the flame hugs the middle of the fire.</summary>
    public const float FireFlameSpreadShare = 0.8f;

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

    // --- Water ripples (RippleSystem, rule WaterRipple, wave RippleWave, upload RippleField) ---
    // A wave packet spreads over the water top wherever a body crosses the surface (the splash
    // crossings of WaterSplash); a harder crossing (strength 0..1) spreads wider, longer and steeper.
    // Nothing is drawn for it: the water shader tilts its own normal by the packet's slope, so the
    // reflection, the refraction and the sun glints bend with the waves.
    public const int RippleMax = 48;
    /// <summary>Ripples the water shader takes a frame (its uniform arrays); past it the nearest win.</summary>
    public const int RippleShaded = 16;
    public const float RippleMinRadius = 1.6f;
    public const float RippleMaxRadius = 5.5f;
    public const float RippleMinSeconds = 1.8f;
    public const float RippleMaxSeconds = 3.4f;
    /// <summary>Share of the spread that runs at a constant speed; the rest eases out, so a ring keeps
    /// moving for its whole life and only settles a little at the end.</summary>
    public const float RippleSteadyShare = 0.7f;
    /// <summary>Peak surface slope (rise over run) of the packet for the gentlest and the hardest crossing,
    /// before the spread and the life thin it.</summary>
    public const float RippleMinSlope = 0.4f;
    public const float RippleMaxSlope = 1.1f;
    /// <summary>The packet keeps its full slope up to this radius in blocks, then thins with the square root
    /// of the spread (the same energy over a longer circle).</summary>
    public const float RippleFullSlopeRadius = 1f;
    /// <summary>Distance in blocks between two crests at the leading edge, for a player-sized body.</summary>
    public const float RippleWavelength = 0.5f;
    /// <summary>How many wavelengths of train trail the leading edge before the water is calm again.</summary>
    public const float RippleTrainWaves = 3.5f;
    /// <summary>Wavelengths ahead of the leading edge where the first swell starts to rise, and behind it
    /// where the packet reaches full height.</summary>
    public const float RippleLeadWaves = 0.3f;
    public const float RippleRiseWaves = 0.5f;
    /// <summary>How much shorter the waves get toward the back of the train (long waves outrun short ones):
    /// the wavenumber at the tail is 1 + twice this times the one at the edge.</summary>
    public const float RippleChirp = 0.35f;
    /// <summary>Crests a second that run forward through the packet and die at its leading edge, so the
    /// train is moving water rather than a fixed pattern growing.</summary>
    public const float RippleCrestRate = 1.5f;
    /// <summary>Water whose surface stands farther than this many blocks from the crossing's takes none of
    /// its ripple, so a ring never climbs a waterfall or a ledge.</summary>
    public const float RippleLevelTolerance = 0.5f;

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

    // --- Cave biome motes (AmbientEffects.UpdateCaveMotes, CaveClimate.Motes) ---
    /// <summary>Radius around the eye, in blocks, inside which cave motes appear.</summary>
    public const float CaveMoteRadius = 10f;

    /// <summary>Most cave motes spawned in one frame, however long the frame was.</summary>
    public const int CaveMoteMaxPerFrame = 4;

    /// <summary>Random points tried per mote before giving it up (a point inside rock is refused).</summary>
    public const int CaveMoteSpawnTries = 3;

    /// <summary>Scatter of one cave mote around its spawn point, in blocks.</summary>
    public const float CaveMoteSpread = 0.6f;

    /// <summary>Cave motes per second in a frozen echo cave (cold steam glints).</summary>
    public const float CaveMoteFrozenEchoPerSecond = 3f;

    /// <summary>Cave motes per second in a desert echo cave (drifting dust).</summary>
    public const float CaveMoteDesertEchoPerSecond = 2f;

    /// <summary>Cave motes per second in a crystal cave (glinting sparks).</summary>
    public const float CaveMoteCrystalPerSecond = 4f;

    /// <summary>Cave motes per second in a fungal cave (spores).</summary>
    public const float CaveMoteFungalPerSecond = 5f;

    /// <summary>Cave motes per second in the magma deep (embers).</summary>
    public const float CaveMoteMagmaDeepPerSecond = 6f;
}
