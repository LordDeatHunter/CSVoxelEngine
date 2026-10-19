namespace VoxelEngine.Config;

/// <summary>
/// Particle tuning: falling leaves, mining debris, campfire, lava and torch embers and smoke,
/// exhaled bubbles and hot-spring steam.
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

    // Steam: puffs a second over every vent in range, the reach it gathers within and the cap
    public const float SpringSteamPerSecond = 0.6f;
    public const float SpringSteamRadius = 32f;
    public const int SpringSteamMaxVents = 16;
    public const int SpringSteamMaxPuffsPerFrame = 4;
    public const int SpringSteamCount = 2;
    public const float SpringSteamSpread = 0.35f;
}
