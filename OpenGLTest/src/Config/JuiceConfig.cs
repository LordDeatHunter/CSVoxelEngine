using System.Numerics;

namespace VoxelEngine.Config;

/// <summary>
/// Game-feel effect tuning: landing and footstep dust, slime splats, hit sparks, the death poof,
/// blast shake and push, falling-block dust, lava steam, the tree thud, water splashes, tool
/// shatter, the pickup sparkle, the low-health vignette, the damage arc and the surfacing droplets.
/// </summary>
public static class JuiceConfig
{
    // --- Landing dust (LandingDust rules, GroundDust spawner, drawn as BlockDebris chips) ---
    // A body landing from a real fall kicks up chips of the block it landed on: none below the
    // minimum fall, then more and faster ones the longer the drop (MathUtils.ImpactStrength),
    // reaching the caps at the full fall. Cosmetic: never on a headless server.
    public const float LandingDustMinFallBlocks = 3f;
    public const float LandingDustFullFallBlocks = 14f;
    public const int LandingDustMinCount = 4;
    public const int LandingDustMaxCount = 18;
    // Blocks per second the chips fly out at, mostly sideways so the burst spreads along the ground
    public const float LandingDustMinSpeed = 1.6f;
    public const float LandingDustMaxSpeed = 4.5f;
    // Edge length of a dust chip, smaller than a mining chip so it reads as grit
    public const float LandingDustSize = 0.08f;

    // --- Slime splat (LandingDust.Splat*, GroundDust): a landing slime throws chips of its own
    // green instead of the ground's dust. Every hop's landing splats, growing with the impact like
    // its squash kick: nothing under the minimum (a bob in water, the hair a resting body settles),
    // full at the kick's own clamp (Slime's 20 blocks/s). ---
    public const float SlimeSplatMinImpact = 3f;
    public const float SlimeSplatFullImpact = 20f;
    public const int SlimeSplatMinCount = 3;
    public const int SlimeSplatMaxCount = 10;
    // Blocks per second the goo flies out at
    public const float SlimeSplatMinLaunch = 1.2f;
    public const float SlimeSplatMaxLaunch = 3.2f;
    public const float SlimeSplatSize = 0.1f;
    // sRGB color of the goo when the slime's model has no skin to read its green from
    public static readonly Vector3 SlimeSplatFallbackColor = new(0.45f, 0.78f, 0.35f);

    // --- Mob hit sparks (BlockDebrisSystem.Spark): a punch, slice or thrown hit that connects
    // throws a few pale chips off the mob's body where the striker faces it. Cosmetic:
    // singleplayer and a client (its predicted connect), never a server. ---
    public const int HitSparkCount = 6;
    // Blocks per second the sparks fly out at
    public const float HitSparkSpeed = 2.8f;
    public const float HitSparkSize = 0.07f;
    public static readonly Vector3 HitSparkColor = new(1f, 0.96f, 0.8f);

    // --- Mob death poof (GroundDust.DeathPoof): a mob whose death animation just ended bursts
    // into chips of its own skin color (LandingDust.SkinColor of its model), so a goblin throws
    // goblin-colored chips and a slime its green. Cosmetic: singleplayer and a client, never a server. ---
    public const int MobDeathPoofCount = 12;
    // Blocks per second the chips fly out at, between a landing splat and a mining burst
    public const float MobDeathPoofSpeed = 2.4f;
    public const float MobDeathPoofSize = 0.1f;
    // sRGB color of the chips when the mob's model has no skin to read one from
    public static readonly Vector3 MobDeathPoofFallbackColor = new(0.62f, 0.5f, 0.42f);
    // A gray textureless block tile (its mean gray is BiomeTintTextureGray) that a plain-color chip
    // wears on every face, so BiomeTint.VertexTint lands the chip on its target color
    public const string ColorChipTile = "grass_top_textureless";

    // --- Placement dust (BlockPlacer): a placed solid block puffs a few of its own chips off the
    // face it was pressed against ---
    public const int PlaceDustCount = 6;
    public const float PlaceDustSpeed = 1.2f;
    public const float PlaceDustSize = 0.07f;

    // --- Sprint footstep dust (FootstepDust rules, GroundDust): while sprinting on foot over loose
    // ground (a block with "footstepDust"), every footfall of the gait kicks up a tiny puff ---
    public const int FootstepDustCount = 5;
    // Blocks per second the grit flies out at, well under a landing's
    public const float FootstepDustSpeed = 1.3f;
    // On top of that a footfall kicks its grit up and back, against the run (blocks per second)
    public const float FootstepDustLift = 2.2f;
    public const float FootstepDustKickBack = 1.5f;
    public const float FootstepDustSize = 0.085f;
    // Horizontal blocks per second the body must really be covering: a sprint held against a wall
    // never puffs
    public const float FootstepDustMinSpeed = 4f;

    // --- TNT blast shake (ShakeFalloff): a blast shakes the local camera with this much trauma
    // inside TntManager.BlastRadius, falling off as radius / distance and gone at the range.
    // Cosmetic: singleplayer and a client, never a headless server. ---
    public const float BlastShakeStrength = 1f;
    public const float BlastShakeRangeBlocks = 48f;
    // A client sees a blast as a primed-TNT mirror leaving the server's snapshot with at most this
    // much of its locally burnt-down fuse left (the server only drops a cube when it detonates)
    public const float BlastMirrorFuseSlackSeconds = 0.5f;

    // --- TNT blast push (BlastImpulse): a blast shoves every loose thing within TntManager.BlastRadius
    // away from its center, this many blocks per second at the center, falling linearly to nothing at
    // the radius. The direction is tipped up by the bias so things are tossed, not skated. Gameplay:
    // singleplayer and the server (a player's share goes through its queued knockback). ---
    public const float BlastPushMaxSpeed = 14f;
    public const float BlastPushUpBias = 0.5f;

    // --- Falling block landing dust (LandingDust.FallingBlock*, GroundDust.LandBlock): a sand or gravel
    // cube that settles throws chips of itself out from under its rim, more and faster the longer it
    // fell, none under the minimum (a lip slide) and none under water. A client, which never sees the
    // fall, puffs as if the cube fell the client fall. Cosmetic: never on a headless server. ---
    public const float FallingBlockDustMinFallBlocks = 0.75f;
    public const float FallingBlockDustFullFallBlocks = 10f;
    public const int FallingBlockDustMinCount = 6;
    public const int FallingBlockDustMaxCount = 20;
    public const float FallingBlockDustMinSpeed = 1.6f;
    public const float FallingBlockDustMaxSpeed = 4.4f;
    public const float FallingBlockDustSize = 0.1f;
    public const float FallingBlockDustClientFallBlocks = 3f;

    // --- Steam on lava hardening (FluidManager.HardenedCells -> FireParticleSystem.Puff of
    // FireParticleKind.Steam on the campfire instance): a cell contact hardening turns solid puffs
    // this many pale steam particles off its top, scattered across the spread; at most the cap of
    // cells (within FireParticleRadius of the camera) puff in one frame, so a lake meeting the sea
    // never floods the particle ring. ---
    public const int SteamPuffSmoke = 8;
    public const float SteamPuffSpread = 0.9f;
    public const int SteamPuffMaxCellsPerFrame = 12;
    public const float SteamLifetime = 2.2f;
    public const float SteamRise = 1.1f;
    public const float SteamSize = 0.3f;
    public const float SteamGrow = 0.35f;
    public const float SteamAlpha = 0.6f;

    // --- Tree fall thud (TopplingTree.TryTakeLanding -> BlockEditor.ThudTree): the moment a felled tree
    // swings flat it shakes the local camera (ShakeFalloff: this strength, scaled by how much of
    // TreeThudFullWoodCells of wood fell, full within the near radius, gone at the range), bursts
    // leaves off its landed canopy and kicks the ground's dust up under the trunk at a few points.
    // Cosmetic: singleplayer only (a client sees no toppling tree), never a server. ---
    public const float TreeThudShakeStrength = 0.6f;
    public const int TreeThudFullWoodCells = 12;
    public const float TreeThudShakeNearBlocks = 6f;
    public const float TreeThudShakeRangeBlocks = 36f;
    public const int TreeThudDustPoints = 8;
    public const int TreeThudDustCount = 5;
    public const float TreeThudDustSpeed = 2.6f;

    // --- Water splash (WaterSplash, WaterSplashTracker, BlockDebrisSystem.Splash): anything that
    // crosses a water surface throws pale droplets up from the water top, none under the minimum
    // speed, more and higher the faster it crossed (MathUtils.ImpactStrength), capped at the full
    // speed; the count scales with the body (1 for the player). A step farther than the teleport
    // distance splashes nothing, and a body still for the stale time counts as still. Cosmetic:
    // singleplayer and a client, never a headless server. ---
    public const float WaterSplashMinSpeed = 2.5f;
    public const float WaterSplashFullSpeed = 16f;
    public const int WaterSplashMinCount = 4;
    public const int WaterSplashMaxCount = 18;
    public const float WaterSplashMinLaunch = 1.8f;
    public const float WaterSplashMaxLaunch = 4.5f;
    public const float WaterSplashSize = 0.07f;
    public const float WaterSplashTeleportBlocks = 4f;
    public const float WaterSplashStaleSeconds = 0.3f;
    // How big a splash each kind of body throws, against the player's 1 (a mob's comes from its width)
    public const float WaterSplashSmallScale = 0.35f;
    public const float WaterSplashCubeScale = 1.4f;
    // sRGB color of a droplet: a pale water-blue
    public static readonly Vector3 WaterSplashColor = new(0.72f, 0.86f, 1f);

    // --- Tool break shatter (ToolShatter): a tool whose durability runs out bursts into chips of
    // its sprite's mean color at the hand, and its hotbar slot flashes. Cosmetic: never a server. ---
    public const int ToolShatterCount = 10;
    // Blocks per second the chips fly out at
    public const float ToolShatterSpeed = 3f;
    public const float ToolShatterSize = 0.08f;
    // sRGB color of the chips when the item's sprite cannot be read
    public static readonly Vector3 ToolShatterFallbackColor = new(0.75f, 0.7f, 0.6f);

    // --- Pickup sparkle (FireParticleKind.Spark): a few bright motes pop where a drop is collected,
    // at the collector's chest. Cosmetic: singleplayer only (a client's pickups are server-side). ---
    public const int PickupSparkleCount = 5;
    public const float PickupSparkleSpread = 0.5f;
    // Blocks below the eye the motes start at (the chest, where a collected drop arrives)
    public const float PickupSparkleDrop = 0.35f;
    public const float SparkLifetime = 0.45f;
    // Blocks per second a spark rises at, and its size
    public const float SparkRise = 1.6f;
    public const float SparkSize = 0.12f;

    // --- Low-health vignette (LowHealthVignette, vignette-fragment.glsl) ---
    // Below the threshold (a fraction of the pool) the screen edge pulses red like a slow
    // heartbeat, its strength rising from the min to the max as health falls to nothing. Between
    // beats it eases down to the floor share of that strength rather than vanishing. Off while
    // dead and in creative.
    public const float LowHealthThreshold = 0.3f;
    public const float LowHealthPulseSeconds = 1.25f;
    public const float LowHealthMinStrength = 0.35f;
    public const float LowHealthMaxStrength = 0.85f;
    public const float LowHealthPulseFloor = 0.35f;
    // Damage direction arc (DamageDirection, vignette-fragment.glsl): a red arc on the screen edge
    // toward where the last mob blow came from, full for the hold and then faded out smoothly.
    public const float DamageArcHoldSeconds = 0.4f;
    public const float DamageArcFadeSeconds = 1.1f;
    // Surfacing droplets (SurfacingDroplets, vignette-fragment.glsl): when the eye comes up out of
    // water, drops streak down the screen, full for the hold and gone this long after surfacing.
    public const float SurfacingDropletHoldSeconds = 0.25f;
    public const float SurfacingDropletSeconds = 1.5f;
}
