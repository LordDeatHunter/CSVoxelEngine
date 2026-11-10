namespace VoxelEngine.Config;

/// <summary>
/// Mob tuning: the shared caps and rules (fall damage, lava, loot toss, notice, interest, wander,
/// torch-safe ground, ledges), the per-layer difficulty, slimes, goblins, the hell and sky mobs, healthbars, damage numbers and the goblin notice
/// pip.
/// </summary>
public static class MobConfig
{
    // --- Mob caps (MobCaps) ---
    // Two caps shared by every mob kind, judged against the living mobs of every manager together
    // (dying ones excluded), so a new kind needs no cap of its own. The ring spawners stop at the
    // natural cap, which is MobNaturalCap for each player in play up to MobNaturalCapMaxPlayers of
    // them; a forced spawn (a slime splitting, a scenario, a save restore, a client's mirror) may
    // pass it, but nothing ever spawns past the hard cap, which also sizes the healthbar buffers
    // (MobsMaxAlive).
    public const int MobNaturalCap = 30;
    public const int MobNaturalCapMaxPlayers = 3;
    public const int MobHardCapFactor = 15;
    public const int MobHardCap = MobNaturalCap * MobHardCapFactor;

    // --- Spawn pacing (MobCaps.SpawnHaste, SpawnOrigin, MobTargets.OutOfPlay) ---
    // An emptier world refills faster: every ring spawner's timer runs 1 + MobSpawnRefillHaste times as
    // fast with nothing alive, easing to its plain interval at the natural cap. A target moving at
    // MobSpawnAheadMinSpeed or faster gets MobSpawnAheadShare of its ring columns inside the arc
    // MobSpawnAheadHalfAngleDegrees to either side of its heading, so mobs appear where it is going; and
    // a mob farther than MobLeftBehindBlocks behind such a target (in the rear third of the circle) no
    // longer counts as in play, which frees its place for one ahead.
    public const float MobSpawnRefillHaste = 3f;
    public const float MobSpawnAheadMinSpeed = 2.5f;
    public const float MobSpawnAheadShare = 0.8f;
    public const float MobSpawnAheadHalfAngleDegrees = 55f;
    public const float MobLeftBehindBlocks = 56f;

    // A target that only reports positions (a server's connected player) has its velocity estimated
    // (MotionEstimate): averaged over MobMotionSmoothingSeconds, and a jump faster than MobMotionMaxSpeed
    // is a teleport that restarts it at rest.
    public const float MobMotionSmoothingSeconds = 0.5f;
    public const float MobMotionMaxSpeed = 24f;

    // --- Layer difficulty (plans/LAYERS.md) ---
    /// <summary>How much harder a realm mob gets per hell layer (or sky-island zone step) past the
    /// first: its health and damage are scaled by <c>1 + index * this</c>, so the deeper layers
    /// keep getting harder with no bottom.</summary>
    public const float LayerDifficultyPerLayer = 0.25f;

    /// <summary>The most a realm mob's difficulty multiplier ever reaches (<see cref="LayerDifficultyPerLayer"/>
    /// stops adding past it), so a very deep layer stays hard but never one-shots a player.</summary>
    public const float LayerDifficultyMaxMultiplier = 4f;

    // --- Mob fall damage (MobFallDamage, Mob.NoteLanding) ---
    // A mob landing from more than its safe height takes this share of its own full health (at least
    // 1) per whole block past it, so the rule suits a small and a large pool alike. The safe height is
    // MobSafeFallBlocks; a slime adds its own hop's apex, so its hops never hurt it. Landing in water
    // is free, and only the authoritative simulation bills it.
    public const float MobSafeFallBlocks = 3f;
    public const float MobFallDamagePerBlockShare = 0.1f;

    // --- Mob lava (MobLava, Mob.UpdateHazards) ---
    // A mob whose body box touches lava burns: every tick spent in it costs this share of its own full
    // health (at least 1), with the hit flash. Leaving the lava clears the partial tick, as for the
    // player, so a brush never eats an instant hit.
    public const float MobLavaTickSeconds = 0.5f;
    public const float MobLavaDamageShare = 0.15f;

    // --- Mob loot toss (MobLoot) ---
    // A killed mob's every dropped item leaves its body with this toss on top of a drop's own small
    // pop: a random horizontal heading at MinSpeed..MaxSpeed blocks per second plus Lift upward, so
    // the loot arcs out instead of appearing at its feet.
    public const float MobLootTossMinSpeed = 1.5f;
    public const float MobLootTossMaxSpeed = 2.5f;
    public const float MobLootTossLift = 2.5f;

    // --- Mob notice range (MobTargets.NoticeRadius) ---
    // A mob only chases a target within this horizontal distance, scaled by MobSneakNoticeFactor
    // while the target sneaks, so a crouching player can creep past. Inside the spawn ring's outer
    // edge on purpose: a mob spawned at the far edge waits until the player comes closer.
    public const float MobNoticeRadiusBlocks = 32f;
    public const float MobSneakNoticeFactor = 0.5f;

    // --- Mob interest (MobInterest, Mob.PickTarget / WanderGoal) ---
    // A mob whose line of sight to its target (checked every MobSightCheckSeconds) stays blocked for
    // MobLoseInterestSeconds stops chasing until a check finds the line clear again. A mob chasing
    // nothing wanders: it walks or hops toward a goal MobWanderMin..MaxRadiusBlocks away, picked anew
    // every MobWanderRepickSeconds or on arrival, at MobWanderSpeedFactor of its walk speed (the
    // goblin; a slime keeps its hop). One idle for MobIdleDespawnSeconds and farther than
    // MobIdleDespawnMinDistanceBlocks from every target despawns (fading out, no loot).
    public const float MobLoseInterestSeconds = 8f;
    public const float MobSightCheckSeconds = 0.5f;
    public const float MobWanderMinRadiusBlocks = 3f;
    public const float MobWanderMaxRadiusBlocks = 8f;
    public const float MobWanderRepickSeconds = 6f;
    public const float MobWanderArriveBlocks = 1f;
    public const float MobWanderSpeedFactor = 0.5f;
    public const float MobIdleDespawnSeconds = 30f;
    public const float MobIdleDespawnMinDistanceBlocks = 24f;

    // --- Torch-safe ground (MobSpawner.IsLitByBlocks) ---
    // A ring spawn is refused where the cell a mob would stand in holds this much block light
    // (0..15) or more, so torches and lamps keep a base clear of new mobs.
    public const int MobSpawnBlockLightLimit = 8;

    // --- Ledge caution (LedgeProbe, Mob.IsLedgeAhead) ---
    // A mob will not walk (a goblin's next step, MobLedgeProbeBlocks past its body) or hop (a slime's
    // landing spot) onto ground more than MobLedgeMaxDropBlocks below its feet unless its target
    // stands lower than that; with MobSafeFallBlocks it pairs so a cautious mob never takes fall damage.
    public const float MobLedgeMaxDropBlocks = 3f;
    public const float MobLedgeProbeBlocks = 0.5f;

    // --- Vantage seeking (VantageSeeker; the skeleton archer) ---
    // A ranged mob with no line to its target samples spots on arcs around it, VantageArcStepBlocks of arc apart
    // and up to VantageArcSteps of them to either side, at its own distance and VantageInwardBlocks nearer, and
    // walks to the nearest one with a clear line (re-picked every RealmMobLosRetrySeconds), the arcs kept
    // VantageBandMarginBlocks inside the range band so the walk across never leaves it; within
    // VantageArriveBlocks of it it stands, and one that moved less than VantageMinProgressBlocks between two picks
    // tries the other side first (first guesses).
    public const float VantageArcStepBlocks = 2.5f;
    public const int VantageArcSteps = 4;
    public const float VantageInwardBlocks = 3f;
    public const float VantageBandMarginBlocks = 1f;
    public const float VantageArriveBlocks = 0.4f;
    public const float VantageMinProgressBlocks = 0.3f;

    // --- Flight (FlightSteering, RiggedMob.Fly) ---
    // A flying mob's kind tunes its speeds and hover band (FlightTuning); these are the shared rules.
    // The wall probe looks MobFlightProbeBlocks ahead of the body on each axis it moves along; the
    // ground probe looks up to MobFlightGroundProbeBlocks down for the floor the hover band and the
    // minimum clearance are measured from (none found: no floor rule, e.g. over open sky). The
    // horizontal speed eases off within MobFlightArriveBlocks of the goal, and the vertical speed is
    // MobFlightAltitudeGain per block of height error (capped by the kind's vertical speed). An idle
    // flyer wanders to a point MobWanderMin..MaxRadiusBlocks away and up to MobFlightWanderRiseBlocks
    // above or below it, rerolled up to MobFlightWanderTries times until the point is in air.
    public const float MobFlightProbeBlocks = 0.6f;
    public const float MobFlightGroundProbeBlocks = 24f;
    public const float MobFlightArriveBlocks = 1.5f;
    public const float MobFlightAltitudeGain = 2f;
    public const float MobFlightWanderRiseBlocks = 3f;
    public const int MobFlightWanderTries = 6;

    // --- Slimes (SlimeManager) ---
    // Forest mobs that hop at the player. Natural spawns appear on dry forest surface in a ring
    // around the player and are culled when they stray too far. Damage only lands while a slime
    // is airborne; a grounded touch shoves it instead. Combat values live here so a future
    // player-attack pass reads one place.
    public const float SlimeSpawnIntervalSeconds = 8f;
    public const float SlimeSpawnMinRadiusBlocks = 40f;
    public const float SlimeSpawnMaxRadiusBlocks = 64f;
    public const float SlimeDespawnRadiusBlocks = 100f;
    public const int SlimeSpawnTries = 8;
    public const int SlimeContactDamage = 12;
    public const float SlimeShoveSpeed = 3.5f;
    // Horizontal shove a landing player punch gives a slime; ground friction then scrubs it off
    // so the hit reads as a knockback rather than a launch.
    public const float SlimePunchShoveSpeed = 5f;
    // Knockback a slime hit gives the player: a horizontal shove (decayed by Camera) plus a small
    // upward pop so the hit reads even on flat ground.
    public const float SlimeKnockbackSpeed = 6f;
    public const float SlimeKnockbackLift = 4.5f;
    // Slime sizes (SlimeSize), 1..SlimeMaxSize: size 1 is the plain slime, and every size above it
    // adds one step to each stat's multiplier, so a size s slime has (1 + (s - 1) * step) times the
    // size-1 value: the body scale (drawn model, collision box, healthbar height), the health pool
    // (over the entity JSON's hp), the contact damage and the loot table rolls. The largest size is
    // the rare giant, which splits into the size below it like any other.
    public const int SlimeMaxSize = 4;
    public const float SlimeSizeScaleStep = 0.5f;
    public const float SlimeSizeHealthStep = 1f;
    public const float SlimeSizeDamageStep = 0.5f;
    public const float SlimeSizeLootStep = 1f;
    // Size-scaled hops (SlimeSize.HopLift/HopReach/WindupScale): a larger slime's hop launch speeds
    // (the vertical lift and the horizontal reach) and the crouch before it are the size-1 ones times
    // the body scale to these powers, so a big slime hops higher and farther after a longer crouch.
    // The apex height grows with the square of the lift factor (v^2 / 2g).
    public const float SlimeHopLiftExponent = 0.4f;
    public const float SlimeHopReachExponent = 0.4f;
    public const float SlimeHopWindupExponent = 0.6f;
    // The size a ring spawn rolls (SlimeSize.Roll): each size past 1 is reached with the large-size
    // chance again (so size s+1 has chance^s), and that chance eases from the day value to the night
    // value with DayCycle.NightFactor. A spawn whose ground lies at least SlimeUndergroundDepthBlocks
    // below its column's surface counts as full night, whatever the hour.
    public const float SlimeLargeChanceDay = 0.1f;
    public const float SlimeLargeChanceNight = 0.4f;
    // The last step, into the largest size (the giant), is taken with this share of the usual chance,
    // so a giant is rarer than the geometric falloff alone would make it.
    public const float SlimeMaxSizeChanceFactor = 0.25f;
    public const int SlimeUndergroundDepthBlocks = 4;
    // A slime above the smallest size splits when a hit takes it from above half its health to at
    // or below half while it survives (SlimeSize.SplitsOnHit): this many children of the next
    // smaller size pop out of its body, shoved outward with a small hop. They are forced spawns, so a
    // split near the hard mob cap makes only as many as still fit, and none at the cap.
    public const int SlimeSplitChildren = 2;
    public const float SlimeSplitShoveSpeed = 4f;
    public const float SlimeSplitLift = 5f;
    // Cave slimes (MobSpawner.TryFindCaveSpawn): slimes also spawn on dark cave floors in the same
    // ring, searched SlimeCaveSpawnTries times from a random height within SlimeCaveSearchHalfHeight of
    // the player's feet, down at most SlimeCaveScanDepth cells, on a floor at least
    // SlimeUndergroundDepthBlocks under its column's surface whose standing cell has at most
    // SlimeCaveMaxSkyLight sky light. A player underground is searched for cave spawns first, one on
    // the surface for surface spawns first, each falling back to the other.
    public const int SlimeCaveSpawnTries = 16;
    public const int SlimeCaveSearchHalfHeight = 12;
    public const int SlimeCaveScanDepth = 24;
    public const int SlimeCaveMaxSkyLight = 4;
    // Slime merge (SlimeMerge): two living slimes of the same size below the largest whose bodies
    // touch merge into one of the next size (the split's inverse), its health the pair's share of
    // their pools scaled to the larger one, once both have lived SlimeMergeCooldownSeconds, so a
    // split's children do not merge straight back.
    public const float SlimeMergeCooldownSeconds = 10f;

    // Cave spawn rules (CB-4, CaveSpawnRules): the chance a cave goblin spawn found in a cave biome is kept.
    // Goblins favor the crystal and fungal caves, take plain and echo caves now and then and never the
    // magma deep (weight 0). First guesses.
    public const float GoblinCaveFavoredWeight = 1.0f;
    public const float GoblinCaveOtherWeight = 0.3f;

    // --- Goblins (GoblinManager) ---
    // Short forest raiders that throw the flint they carry at the player from a standoff band,
    // then close in for a club swing once their stones are gone. They spawn like slimes (dry
    // forest surface, a ring around the player) and are culled when they stray too far. The
    // throw band must stay inside the stone's level-ground range, v^2/g at GoblinStoneSpeed
    // under ThrownProjectile.Gravity, with margin for a chest-height target and the
    // semi-implicit Euler drop, or the far edge of the band has no solvable arc.
    public const float GoblinSpawnIntervalSeconds = 7f;
    /// <summary>The night factor (0 day, 1 night) below which no goblin spawns on the surface.</summary>
    public const float GoblinMinNightToSpawn = 0.35f;

    public const float GoblinSpawnMinRadiusBlocks = 40f;
    public const float GoblinSpawnMaxRadiusBlocks = 64f;
    public const int GoblinSpawnTries = 8;
    public const float GoblinDespawnRadiusBlocks = 100f;

    // --- Goblin camps (GoblinCamps): met by chance at night, far from the player ---
    /// <summary>How dark the night must be (DayCycle.NightFactor) before a camp may be pitched.</summary>
    public const float GoblinCampNightFactor = 0.6f;
    /// <summary>Seconds between two rolls for tonight's camp.</summary>
    public const float GoblinCampCheckSeconds = 15f;
    /// <summary>The chance one roll pitches tonight's camp (about half the nights get one).</summary>
    public const float GoblinCampChancePerCheck = 0.03f;
    /// <summary>The nearest a camp is pitched to the player, in blocks: out of sight, to be stumbled on.</summary>
    public const float GoblinCampMinDistanceBlocks = 56f;
    /// <summary>The farthest a camp is pitched to the player; inside the goblins' despawn radius.</summary>
    public const float GoblinCampMaxDistanceBlocks = 88f;
    /// <summary>Columns tried for a camp's site per roll.</summary>
    public const int GoblinCampSiteTries = 16;
    /// <summary>The fewest and most goblins round a camp's fire.</summary>
    public const int GoblinCampMinGoblins = 4;
    public const int GoblinCampMaxGoblins = 8;
    /// <summary>The ring round the fire a camp's goblins rise on, in blocks.</summary>
    public const float GoblinCampRingMinBlocks = 2f;
    public const float GoblinCampRingMaxBlocks = 4.5f;
    /// <summary>Columns tried per goblin for its standing spot.</summary>
    public const int GoblinCampSpotTries = 6;
    /// <summary>Seconds the camp's first branch burns before the fire burns down like any campfire: the rest of the night.</summary>
    public const float GoblinCampFireSeconds = 480f;
    public const float GoblinWalkSpeed = 3.2f;
    public const float GoblinJumpSpeed = 8.5f;
    // Stones (flint) a goblin carries, rolled inclusive at spawn; the rest drop on death.
    public const int GoblinStoneMin = 3;
    public const int GoblinStoneMax = 6;
    // Horizontal distance band a goblin throws from: it closes in beyond the far edge and
    // backpedals inside the near one.
    public const float GoblinThrowMinRange = 4f;
    public const float GoblinThrowMaxRange = 12f;
    // A fresh goblin notices the player before its first throw; unjittered so scripted shots
    // repeat.
    public const float GoblinFirstThrowDelaySeconds = 1f;
    public const float GoblinThrowCooldownSeconds = 2.2f;
    public const float GoblinThrowCooldownJitter = 0.6f;
    // How long a goblin whose throw line was blocked waits before testing it again.
    public const float GoblinLosRetrySeconds = 0.3f;
    // 20^2 / 28 = 14.3 blocks of level range, so the 12-block band edge solves with ~2 blocks
    // to spare.
    public const float GoblinStoneSpeed = 20f;
    public const int GoblinStoneDamage = 8;
    public const float GoblinStoneKnockbackSpeed = 3f;
    public const float GoblinMeleeReach = 1.9f;
    // How close an empty-handed goblin walks before it stops and swings.
    public const float GoblinStandoffBlocks = 1.1f;
    public const int GoblinMeleeDamage = 10;
    public const float GoblinMeleeCooldownSeconds = 1.1f;
    // Knockback a club swing gives the player: a horizontal shove plus a small upward pop.
    public const float GoblinKnockbackSpeed = 5f;
    public const float GoblinKnockbackLift = 3f;
    // Horizontal shove a grounded goblin gets when the player walks into it.
    public const float GoblinShoveSpeed = 3.5f;

    // --- Realm mobs (RealmMob: the hell and sky mobs) ---
    // A realm mob that needs a clear line to its target (a charge, a cast, a dive) raycasts it at most
    // this often; between checks the last answer stands.
    public const float RealmMobLosRetrySeconds = 0.3f;

    // --- Hell mobs (HellMobManager, Hellbrute, Cinderwitch) ---
    // Both live only in hell's cavern pockets (MobSpawner.TryFindHellSpawn): a ring like the goblin's
    // around a player standing in hell, each try scanning a column down from near the player's feet for
    // an open floor (solid ground, two clear dry cells, block light at most HellSpawnMaxBlockLight, so a
    // lava-lit pool edge or a torched base stays clear). The layer index scales each spawn's health,
    // damage and the caster's fire rate (LayerDifficulty) and the pack size: one more mob every
    // HellPackLayersPerExtra layers, up to HellPackMaxSize. HellCasterShare of the spawns are casters.
    public const float HellSpawnIntervalSeconds = 5f;
    public const float HellSpawnMinRadiusBlocks = 30f;
    public const float HellSpawnMaxRadiusBlocks = 52f;
    public const int HellSpawnTries = 10;
    public const int HellSpawnSearchHalfHeight = 16;
    public const int HellSpawnScanDepth = 40;
    public const int HellSpawnMaxBlockLight = 10;
    public const int HellPackLayersPerExtra = 2;
    public const int HellPackMaxSize = 3;
    public const float HellPackSpawnRadiusBlocks = 4f;
    public const float HellCasterShare = 0.4f;
    public const float HellDespawnRadiusBlocks = 100f;

    // The hellbrute: a slow, tanky melee bruiser. It closes at its walk and charges at
    // HellbruteChargeSpeedFactor of it once it sees its target within HellbruteChargeRangeBlocks
    // (a clear line from its eye to the target's chest), then swings a heavy fist whose impact lands
    // at the melee clip's "impact" event only if the target is still in reach, with a big shove.
    // Barely moved by a player walking into it.
    public const float HellbruteHeight = 1.95f;
    public const float HellbruteHalfExtent = 0.45f;
    public const float HellbruteWalkSpeed = 2.6f;
    public const float HellbruteChargeSpeedFactor = 1.8f;
    public const float HellbruteChargeRangeBlocks = 12f;
    public const float HellbruteJumpSpeed = 8.5f;
    public const float HellbruteMeleeReach = 2.3f;
    public const float HellbruteStandoffBlocks = 1.5f;
    public const int HellbruteMeleeDamage = 16;
    public const float HellbruteMeleeCooldownSeconds = 1.6f;
    public const float HellbruteKnockbackSpeed = 9f;
    public const float HellbruteKnockbackLift = 4.5f;
    public const float HellbruteShoveSpeed = 1.2f;

    // The cinderwitch: a ranged fire caster. It keeps HellCasterMinRange..HellCasterMaxRange from its
    // target and, standing on the ground with a clear line to the chest, plays its cast clip and lobs a
    // fire bolt at the clip's "release" event (GoblinAim's low arc under ThrownProjectile.Gravity, so
    // the band must stay inside the bolt's level range v^2/g). A bolt that touches a target deals
    // HellBoltDamage and sets it burning for HellBoltBurnScale of the effect's duration; one that
    // misses burns out. A fresh caster waits HellCasterFirstCastDelaySeconds before its first cast.
    public const float HellCasterHeight = 1.7f;
    public const float HellCasterHalfExtent = 0.3f;
    public const float HellCasterWalkSpeed = 2.4f;
    public const float HellCasterJumpSpeed = 8.5f;
    public const float HellCasterShoveSpeed = 3.5f;
    public const float HellCasterMinRange = 6f;
    public const float HellCasterMaxRange = 15f;
    public const float HellCasterFirstCastDelaySeconds = 1.2f;
    public const float HellCasterCooldownSeconds = 2.8f;
    public const float HellCasterCooldownJitter = 0.5f;
    public const float HellBoltSpeed = 22f;
    public const int HellBoltDamage = 7;
    public const float HellBoltKnockbackSpeed = 2f;
    public const float HellBoltBurnScale = 0.6f;
    // Embers a bolt in flight sheds per second (cosmetic, never on a server)
    public const float HellBoltEmbersPerSecond = 30f;

    // --- Sky mobs (SkyMobManager, Galeraptor, Cloudgolem) ---
    // Both live only on the sky islands (MobSpawner.TryFindSkySpawn): a ring around a player standing in
    // the sky band, each try scanning a column down from near the player's feet for an island top (solid
    // ground in the band under two clear dry cells, inside an island zone, no torch light). The zone's
    // tier (SkyIslands.ZoneAt: 0 small, 1 medium, 2 big) scales each spawn's health and damage by
    // 1 + tier * SkyDifficultyPerTier (SkyDifficulty) and its pack: SkyPackExtraPerTier more mobs per tier,
    // up to SkyPackMaxSize. Only a big zone spawns the golem, as SkyBruiserShare of its spawns; a galeraptor
    // appears SkyFlyerSpawnLiftMin..Max blocks over the island top it was found on.
    public const float SkySpawnIntervalSeconds = 5f;
    public const float SkySpawnMinRadiusBlocks = 24f;
    public const float SkySpawnMaxRadiusBlocks = 48f;
    public const int SkySpawnTries = 16;
    public const int SkySpawnSearchHalfHeight = 16;
    public const int SkySpawnScanDepth = 40;
    public const float SkyDifficultyPerTier = 0.25f;
    public const int SkyPackExtraPerTier = 1;
    public const int SkyPackMaxSize = 3;
    public const float SkyPackSpawnRadiusBlocks = 5f;
    public const float SkyBruiserShare = 0.35f;
    public const float SkyFlyerSpawnLiftMin = 6f;
    public const float SkyFlyerSpawnLiftMax = 14f;
    public const float SkyDespawnRadiusBlocks = 100f;

    // The galeraptor: a diving flyer (FlightTuning below). It hovers GaleraptorHoverMin..MaxBlocks above
    // its target's eye and, off its cooldown with a clear line within GaleraptorDiveRangeBlocks
    // horizontally, plays its dive clip, stooping at the chest at GaleraptorDiveSpeedFactor of its flight;
    // the strike lands at the clip's "impact" event only on a target whose chest is then within
    // GaleraptorDiveReach of its body, then it climbs back to its hover. A fresh one waits
    // GaleraptorFirstDiveDelaySeconds before its first dive.
    public const float GaleraptorHeight = 0.9f;
    public const float GaleraptorHalfExtent = 0.45f;
    public const float GaleraptorCruiseSpeed = 6f;
    public const float GaleraptorVerticalSpeed = 5f;
    public const float GaleraptorAcceleration = 5f;
    public const float GaleraptorHoverMinBlocks = 3f;
    public const float GaleraptorHoverMaxBlocks = 5f;
    public const float GaleraptorMinClearanceBlocks = 0.5f;
    public const float GaleraptorBobBlocks = 0.25f;
    public const float GaleraptorBobHz = 0.6f;
    public const float GaleraptorDiveRangeBlocks = 3f;
    public const float GaleraptorDiveSpeedFactor = 2.2f;
    public const float GaleraptorDiveReach = 1.8f;
    public const float GaleraptorFirstDiveDelaySeconds = 1.5f;
    public const float GaleraptorDiveCooldownSeconds = 2.8f;
    public const int GaleraptorDiveDamage = 6;
    public const float GaleraptorKnockbackSpeed = 5f;
    public const float GaleraptorKnockbackLift = 2f;

    /// <summary>How high above where it was grabbed a carried target is lifted before the raptor drops it.</summary>
    public const float GaleraptorCarryHeightBlocks = 24f;
    /// <summary>The upward speed the talons give a carried target each frame.</summary>
    public const float GaleraptorCarryLiftSpeed = 9f;
    /// <summary>The longest a raptor holds on before letting go regardless of height.</summary>
    public const float GaleraptorCarryMaxSeconds = 6f;
    // How hard a player shoves a galeraptor touching the ground (a dive that bottoms out); in the air never
    public const float GaleraptorShoveSpeed = 3.5f;

    // The cloud golem: a slow, very tough bruiser, harder than a first-layer hellbrute. In
    // CloudgolemSlamTriggerReach and off its cooldown it plays its slam clip; at the clip's "impact" event
    // the shockwave hits every target within CloudgolemShockwaveRadius horizontally whose feet stand within
    // CloudgolemShockwaveHeight of its own, with a big shove and lift. Barely moved by a player walking
    // into it, and it keeps the shared ledge caution, so it never walks off its island after a target that
    // is not below it.
    public const float CloudgolemHeight = 2.6f;
    public const float CloudgolemHalfExtent = 0.7f;
    public const float CloudgolemWalkSpeed = 1.8f;
    public const float CloudgolemJumpSpeed = 8.5f;
    public const float CloudgolemSlamTriggerReach = 2.6f;
    public const float CloudgolemStandoffBlocks = 2f;
    public const int CloudgolemSlamDamage = 22;
    public const float CloudgolemSlamCooldownSeconds = 2.6f;
    public const float CloudgolemShockwaveRadius = 4.5f;
    public const float CloudgolemShockwaveHeight = 2f;
    public const float CloudgolemKnockbackSpeed = 11f;
    public const float CloudgolemKnockbackLift = 7f;
    public const float CloudgolemShoveSpeed = 0.6f;

    // --- Cave mobs (CB-4: CaveMobManager, Shardling, Sporeling, Magmaborn) ---
    // One kind per overworld cave sub-biome (CaveSpawnRules.CaveMobKindFor): the shardling in crystal caves, the
    // sporeling in fungal caves, the magmaborn in the magma-touched deep. They spawn only on dark cave floors of
    // their own biome (MobSpawner.TryFindCaveSpawn masked to it) in a ring around a target standing underground
    // outside hell, CaveMobSpawnTries tries a turn every CaveMobSpawnIntervalSeconds, from a random height within
    // CaveMobSearchHalfHeight of the target's feet, down at most CaveMobScanDepth cells, with at most
    // CaveMobMaxSkyLight sky light and under CaveMobSpawnBlockLightLimit block light. A pack of up to
    // CaveMobPackMaxSize stands within CaveMobPackSpawnRadiusBlocks of the leader, on its own biome.
    public const float CaveMobSpawnIntervalSeconds = 6f;
    public const float CaveMobSpawnMinRadius = 20f;
    public const float CaveMobSpawnMaxRadius = 40f;
    public const int CaveMobSpawnTries = 16;
    public const int CaveMobSearchHalfHeight = 12;
    public const int CaveMobScanDepth = 24;
    public const int CaveMobMaxSkyLight = 4;
    // The shared torch-safe limit: measured under the default seed, fungal_glow (light 14) still leaves a fungal
    // floor on about 0.17 of the spawn turns near the fungal spot (plans/CAVE_BIOMES.md, CB-4 as built)
    public const int CaveMobSpawnBlockLightLimit = MobSpawnBlockLightLimit;
    public const float CaveMobDespawnRadiusBlocks = 80f;
    public const int CaveMobPackMaxSize = 2;
    public const float CaveMobPackSpawnRadiusBlocks = 4f;
    public const int CaveMobPackSearchHalfHeight = 3;
    public const int CaveMobPackScanDepth = 8;

    // Cave difficulty (CaveDifficulty): a cave mob's tier is 0 at or above WorldGenerationConfig.CaveEchoBottomY
    // and rises one tier every CaveDifficultyBandBlocks below it, up to CaveMaxTier, read once from where it
    // spawned and fixed for life (saved and sent with it). Its health and damage scale by
    // 1 + tier * CaveDifficultyPerTier, capped at CaveDifficultyMaxMultiplier.
    public const float CaveDifficultyBandBlocks = 32f;
    public const int CaveMaxTier = 4;
    public const float CaveDifficultyPerTier = 0.2f;
    public const float CaveDifficultyMaxMultiplier = 1.8f;

    // The shardling: a crystal caster. It keeps CaveShardlingMinRange..CaveShardlingMaxRange from its target
    // (still casting at one inside the band) and, grounded off its cooldown with a clear line, plays its cast
    // clip and lobs a crystal shard at the clip's "release" event on GoblinAim's low arc under
    // ThrownProjectile.Gravity, so the band must stay inside the shard's level range v^2/g. A shard that
    // touches a target deals CaveShardDamage and chills it with frostburn for CaveShardChillScale of the
    // effect's duration. A fresh shardling waits CaveShardlingFirstCastDelaySeconds before its first cast.
    public const float CaveShardlingHeight = 1.8f;
    public const float CaveShardlingHalfExtent = 0.3f;
    public const float CaveShardlingWalkSpeed = 2.5f;
    public const float CaveShardlingJumpSpeed = 8.5f;
    public const float CaveShardlingShoveSpeed = 3.5f;
    public const float CaveShardlingMinRange = 5f;
    public const float CaveShardlingMaxRange = 12f;
    public const float CaveShardlingFirstCastDelaySeconds = 1.2f;
    public const float CaveShardlingCooldownSeconds = 2.6f;
    public const float CaveShardlingCooldownJitter = 0.5f;
    public const float CaveShardSpeed = 20f;
    public const int CaveShardDamage = 5;
    public const float CaveShardKnockbackSpeed = 2f;
    public const float CaveShardChillScale = 0.5f;

    // The sporeling: a squat fungal walker. In CaveSporelingTriggerReach and off its cooldown it plays its melee
    // clip; at the clip's "impact" event a spore burst hits every target within CaveSporeBurstRadius
    // horizontally whose feet stand within CaveSporeBurstHeight of its own, dealing CaveSporeBurstDamage and
    // applying the spore effect for CaveSporeScale of its duration.
    public const float CaveSporelingHeight = 1.5f;
    public const float CaveSporelingHalfExtent = 0.35f;
    public const float CaveSporelingWalkSpeed = 2.8f;
    public const float CaveSporelingJumpSpeed = 8.5f;
    public const float CaveSporelingShoveSpeed = 3f;
    public const float CaveSporelingTriggerReach = 1.8f;
    public const float CaveSporelingStandoffBlocks = 1f;
    public const float CaveSporelingCooldownSeconds = 2.2f;
    public const float CaveSporeBurstRadius = 2.8f;
    public const float CaveSporeBurstHeight = 1.5f;
    public const int CaveSporeBurstDamage = 6;
    public const float CaveSporeKnockbackSpeed = 3f;
    public const float CaveSporeKnockbackLift = 1.5f;
    public const float CaveSporeScale = 1f;

    // The magmaborn: a hulking brute of cracked rock in the magma deep. In CaveMagmabornMeleeReach and off its
    // cooldown it plays its melee clip; the blow lands at the clip's "impact" event only on a target still in
    // reach, with a heavy shove, and sets it burning for CaveMagmabornBurnScale of the effect's duration.
    public const float CaveMagmabornHeight = 1.95f;
    public const float CaveMagmabornHalfExtent = 0.5f;
    public const float CaveMagmabornWalkSpeed = 2.2f;
    public const float CaveMagmabornJumpSpeed = 8.5f;
    public const float CaveMagmabornShoveSpeed = 1f;
    public const float CaveMagmabornMeleeReach = 2.4f;
    public const float CaveMagmabornStandoffBlocks = 1.5f;
    public const int CaveMagmabornMeleeDamage = 14;
    public const float CaveMagmabornMeleeCooldownSeconds = 1.8f;
    public const float CaveMagmabornKnockbackSpeed = 8f;
    public const float CaveMagmabornKnockbackLift = 4f;
    public const float CaveMagmabornBurnScale = 0.5f;

    // --- Skeletons (SkeletonManager, Skeleton; the battle tower) ---
    // Raised only by the tower's bone piles (a forced spawn with the floor's tier), never by a ring, half of them
    // warriors and half archers. A warrior stabs with its copper dagger in melee reach and otherwise closes in; an
    // archer, inside SkeletonBowMinRange..SkeletonBowMaxRange with a clear line, draws its bow, without one walks to a
    // spot that has one (VantageSeeker), backs away when the target is nearer and only punches one in reach; the
    // arrow leaves at the shoot clip's "release" event on GoblinAim's low arc under ThrownProjectile.Gravity, so the
    // band must stay inside the arrow's level range v^2/g.
    /// <summary>Height of a skeleton's body box, in blocks.</summary>
    public const float SkeletonHeight = 1.9f;
    /// <summary>Half the width of a skeleton's body box, in blocks.</summary>
    public const float SkeletonHalfExtent = 0.3f;
    /// <summary>A skeleton's walk speed, in blocks per second.</summary>
    public const float SkeletonWalkSpeed = 5.625f;
    /// <summary>A skeleton's auto-jump launch speed.</summary>
    public const float SkeletonJumpSpeed = 8.5f;
    /// <summary>How hard a player walking into a grounded skeleton shoves it.</summary>
    public const float SkeletonShoveSpeed = 3f;
    /// <summary>How far a skeleton's melee blow reaches (MobTargets.InMeleeReach).</summary>
    public const float SkeletonMeleeReach = 2f;
    /// <summary>The damage one warrior's dagger stab deals on the first floor (TowerDifficulty scales it).</summary>
    public const int SkeletonMeleeDamage = 14;
    /// <summary>Seconds between two dagger stabs.</summary>
    public const float SkeletonMeleeCooldownSeconds = 0.85f;
    /// <summary>The knockback speed of a landed stab.</summary>
    public const float SkeletonMeleeKnockbackSpeed = 5f;
    /// <summary>The upward lift of a landed melee blow (a stab or a punch).</summary>
    public const float SkeletonMeleeKnockbackLift = 2.5f;
    /// <summary>The damage one archer's punch deals on the first floor (TowerDifficulty scales it).</summary>
    public const int SkeletonArcherPunchDamage = 5;
    /// <summary>Seconds between two punches.</summary>
    public const float SkeletonArcherPunchCooldownSeconds = 1.2f;
    /// <summary>The knockback speed of a landed punch.</summary>
    public const float SkeletonArcherPunchKnockbackSpeed = 3f;
    /// <summary>Inside this horizontal distance an archer stops shooting and backs away.</summary>
    public const float SkeletonBowMinRange = 5f;
    /// <summary>Beyond this horizontal distance an archer walks in before it shoots.</summary>
    public const float SkeletonBowMaxRange = 14f;
    /// <summary>Seconds a fresh skeleton waits before its first shot.</summary>
    public const float SkeletonFirstShotDelaySeconds = 0.8f;
    /// <summary>Seconds between two shots, before the jitter.</summary>
    public const float SkeletonShotCooldownSeconds = 1.6f;
    /// <summary>The most a shot cooldown is lengthened or shortened at random.</summary>
    public const float SkeletonShotCooldownJitter = 0.27f;
    /// <summary>An arrow's launch speed (its level range v^2/g must clear SkeletonBowMaxRange).</summary>
    public const float SkeletonArrowSpeed = 24f;
    /// <summary>The damage one arrow deals on the first floor (TowerDifficulty scales it).</summary>
    public const int SkeletonArrowDamage = 10;
    /// <summary>The knockback speed of an arrow hit.</summary>
    public const float SkeletonArrowKnockbackSpeed = 2f;
    /// <summary>A skeleton farther than this from every target despawns.</summary>
    public const float SkeletonDespawnRadiusBlocks = 96f;
    /// <summary>The chance a fresh warrior wears an outfit with a copper piece (SkeletonOutfit.Roll).</summary>
    public const float SkeletonWarriorCopperChance = 0.12f;
    /// <summary>The chance a fresh warrior wears leather alone, one to three pieces.</summary>
    public const float SkeletonWarriorLeatherChance = 0.5f;
    /// <summary>The chance a fresh archer wears a single leather piece.</summary>
    public const float SkeletonArcherLeatherChance = 0.15f;
    /// <summary>The share of every blow a leather piece on a skeleton takes off.</summary>
    public const float SkeletonLeatherArmorShare = 0.06f;
    /// <summary>The share of every blow a copper piece on a skeleton takes off.</summary>
    public const float SkeletonCopperArmorShare = 0.12f;
    /// <summary>Seconds a skeleton bound to a tower floor may chase nobody (no target noticed, or its sight of one lost,
    /// see MobInterest) before it crumbles back into its floor's bone piles, owed and raised again at full health
    /// (a first guess).</summary>
    public const float SkeletonPileReturnIdleSeconds = 20f;

    // --- Tower difficulty (TowerDifficulty) ---
    /// <summary>The highest tower floor tier (floors 0..TowerMaxTier).</summary>
    public const int TowerMaxTier = 7;
    /// <summary>How much harder a tower mob gets per floor tier.</summary>
    public const float TowerDifficultyPerTier = 0.25f;
    /// <summary>The most a tower mob's difficulty multiplier ever reaches.</summary>
    public const float TowerDifficultyMaxMultiplier = 2.5f;
    /// <summary>One more mob in a pile's wave every this many tiers.</summary>
    public const int TowerPackTiersPerExtra = 2;
    /// <summary>The most mobs one pile wave raises.</summary>
    public const int TowerPackMaxSize = 4;

    // --- Battle tower locks (TowerBlockWriter, SkeletonPileManager, TowerManager) ---
    /// <summary>The most cells the tower block writer clears in one frame (opened doors, spent piles).</summary>
    public const int TowerMaxBlockWritesPerFrame = 32;
    /// <summary>How close, horizontally, a target on a pile's floor must come before the pile raises skeletons.</summary>
    public const float SkeletonPileTriggerBlocks = 8f;
    /// <summary>How many waves of skeletons one pile raises before it is spent.</summary>
    public const int SkeletonPileWaves = 2;
    /// <summary>Seconds a pile waits after a wave before it raises the next one.</summary>
    public const float SkeletonPileWaveSeconds = 6f;
    /// <summary>How close, horizontally, a pile on a chest's floor must be for opening that chest to force it awake.</summary>
    public const float SkeletonChestAlarmBlocks = 16f;
    /// <summary>How many living skeletons may stand near a pile on the ground floor before it raises another.</summary>
    public const int SkeletonPileNearLimitBase = 5;
    /// <summary>How many more living skeletons each floor above the ground floor allows near a pile.</summary>
    public const int SkeletonPileNearLimitPerTier = 2;
    /// <summary>How close, in blocks, a living skeleton must stand to a pile to count against its limit.</summary>
    public const float SkeletonPileNearRadiusBlocks = 16f;
    /// <summary>Seconds between the tower manager's looks for a tower under each target.</summary>
    public const float TowerScanSeconds = 0.5f;
    /// <summary>A bound skeleton farther than this from its tower's center is released and owed back to its floor.</summary>
    public const float TowerSkeletonLeashBlocks = 48f;
    /// <summary>Seconds a defeated tower waits before it starts to collapse (BT-8), so the Skeleton King's death and loot
    /// play first (a first guess).</summary>
    public const float TowerCollapseDelaySeconds = 4f;
    /// <summary>The most tower cells the collapse checks against its ruin rule in one frame, across every collapsing tower;
    /// the writes themselves share <see cref="TowerMaxBlockWritesPerFrame"/> with the door and pile sweep.</summary>
    public const int TowerCollapseScanCellsPerFrame = 4096;

    // --- Skeleton King (SkeletonKingManager, SkeletonKing, TowerManager's altar) ---
    /// <summary>Seconds the altar's ritual runs before the Skeleton King rises (a first guess).</summary>
    public const float SkeletonKingRitualSeconds = 3.5f;
    // Every number below is a first guess, to be tuned from visual-examples/skeleton-king.json.
    /// <summary>Height of the king's collision body in blocks (the model stands 48 px).</summary>
    public const float SkeletonKingHeight = 3.0f;
    /// <summary>Half the king's horizontal body edge in blocks (its trunk; pauldrons and club stick out).</summary>
    public const float SkeletonKingHalfExtent = 0.6f;
    /// <summary>The king's walk speed in blocks per second (before the enrage speeds it up).</summary>
    public const float SkeletonKingWalkSpeed = 2.2f;
    /// <summary>The king's auto-jump launch speed.</summary>
    public const float SkeletonKingJumpSpeed = 9f;
    /// <summary>How far a player walking into the king pushes it (it barely moves).</summary>
    public const float SkeletonKingShoveSpeed = 0.4f;
    /// <summary>At or below this share of its health the king enters phase 2 (summon waves join its attacks).</summary>
    public const float SkeletonKingPhase2Share = 0.66f;
    /// <summary>At or below this share of its health the king enters phase 3 (the enrage).</summary>
    public const float SkeletonKingPhase3Share = 0.33f;
    /// <summary>A target within this melee reach of the king makes it slam (MobTargets.InMeleeReach).</summary>
    public const float SkeletonKingSlamTriggerReach = 4.5f;
    /// <summary>The slam's shockwave reaches every target within this horizontal radius...</summary>
    public const float SkeletonKingSlamRadius = 9f;
    /// <summary>...whose feet stand within this many blocks of the king's (MobTargets.InShockwave).</summary>
    public const float SkeletonKingSlamHeight = 2.5f;
    /// <summary>The damage the slam's shockwave deals.</summary>
    public const int SkeletonKingSlamDamage = 100;
    /// <summary>How far in front of the king's feet its club strikes the ground (where the ground heaves).</summary>
    public const float SkeletonKingSlamReachBlocks = 1.6f;
    /// <summary>The knockback speed of the slam's shockwave.</summary>
    public const float SkeletonKingSlamKnockbackSpeed = 12f;
    /// <summary>The upward lift of the slam's shockwave.</summary>
    public const float SkeletonKingSlamKnockbackLift = 7f;
    /// <summary>Seconds between two slams (shortened by the enrage).</summary>
    public const float SkeletonKingSlamCooldownSeconds = 3.5f;
    /// <summary>How many bone shards the slam throws out all round at its impact (flying like the bone blade's
    /// shrapnel, ShrapnelConfig).</summary>
    public const int SkeletonKingSlamShrapnelCount = 10;
    /// <summary>The damage one slam shard deals to the target it touches (never to the king or a skeleton).</summary>
    public const int SkeletonKingSlamShrapnelDamage = 12;
    /// <summary>How far above the king's feet the slam's shards burst out, so they fly at a standing player's body.</summary>
    public const float SkeletonKingSlamShrapnelLiftBlocks = 0.8f;
    /// <summary>Inside this horizontal distance the king does not volley.</summary>
    public const float SkeletonKingVolleyMinRange = 4f;
    /// <summary>Beyond this horizontal distance the king does not volley (must stay inside v^2/g at the volley speed).</summary>
    public const float SkeletonKingVolleyMaxRange = 16f;
    /// <summary>A volley bone's launch speed, before the burst's jitter.</summary>
    public const float SkeletonKingVolleySpeed = 24f;
    /// <summary>How many bones one volley's burst throws, one after another.</summary>
    public const int SkeletonKingBurstBones = 9;
    /// <summary>How many more bones an enraged (phase 3) burst throws.</summary>
    public const int SkeletonKingBurstEnrageExtraBones = 4;
    /// <summary>Seconds from a burst's first bone to its last.</summary>
    public const float SkeletonKingBurstSeconds = 0.5f;
    /// <summary>The most a burst bone strays left or right of the aimed throw, in degrees.</summary>
    public const float SkeletonKingBurstSpreadDegrees = 13f;
    /// <summary>The most a burst bone strays up or down from the aimed throw, in degrees.</summary>
    public const float SkeletonKingBurstPitchDegrees = 5f;
    /// <summary>The most a burst bone's speed differs from the aimed throw's, as a share.</summary>
    public const float SkeletonKingBurstSpeedJitter = 0.12f;
    /// <summary>The damage one burst bone deals.</summary>
    public const int SkeletonKingVolleyDamage = 18;
    /// <summary>The knockback speed of a king's bone hit (a volley bone or a slam shard).</summary>
    public const float SkeletonKingBoneKnockbackSpeed = 4f;
    /// <summary>Seconds between two volleys (shortened by the enrage).</summary>
    public const float SkeletonKingVolleyCooldownSeconds = 4f;
    /// <summary>Seconds a freshly risen king waits before its first volley.</summary>
    public const float SkeletonKingFirstVolleyDelaySeconds = 2f;
    /// <summary>Seconds between two summon waves (shortened by the enrage).</summary>
    public const float SkeletonKingSummonCooldownSeconds = 14f;
    /// <summary>The chance, from phase 2 on, that a summon wave is chosen when its cooldown is over.</summary>
    public const float SkeletonKingSummonChance = 0.35f;
    /// <summary>How many skeletons one summon wave raises at most.</summary>
    public const int SkeletonKingSummonCount = 3;
    /// <summary>The most living skeletons the king's summons keep at once.</summary>
    public const int SkeletonKingMaxMinions = 6;
    /// <summary>How far from the king's feet a summoned skeleton rises.</summary>
    public const float SkeletonKingSummonRadius = 3.5f;
    /// <summary>The enraged (phase 3) king's cooldowns are multiplied by this.</summary>
    public const float SkeletonKingEnrageCooldownFactor = 0.6f;
    /// <summary>The enraged (phase 3) king walks this much faster.</summary>
    public const float SkeletonKingEnrageSpeedFactor = 1.35f;
    /// <summary>Seconds with no target engaged after which a hurt king heals to full and starts over at phase 1.</summary>
    public const float SkeletonKingResetSeconds = 20f;
    /// <summary>A king this far below its home's feet (it fell off the roof) is put back on its home.</summary>
    public const float SkeletonKingLeashDropBlocks = 6f;
    /// <summary>A king knocked this far past its leash radius is put back on its home.</summary>
    public const float SkeletonKingLeashMarginBlocks = 4f;
    /// <summary>A king farther than this from every target fades out (the tower lets it go); also an unbound king's leash.</summary>
    public const float SkeletonKingLeaveRadiusBlocks = 64f;
    /// <summary>The boss bar shows while the viewer is within this many blocks of the king.</summary>
    public const float SkeletonKingBossBarRangeBlocks = 48f;
    /// <summary>The ritual's sparks and shake show within this many blocks of the altar.</summary>
    public const float SkeletonKingRitualEffectRangeBlocks = 64f;
    /// <summary>Sparks per second off an altar while its ritual runs.</summary>
    public const float SkeletonKingRitualSparksPerSecond = 30f;
    /// <summary>Seconds between the ritual's small camera rumbles (a first guess).</summary>
    public const float SkeletonKingRitualShakePulseSeconds = 0.6f;
    /// <summary>Trauma of one ritual rumble within <see cref="SkeletonKingRitualShakeNearBlocks"/> of the altar, falling
    /// off with distance (<c>ShakeFalloff</c>) to nothing at <see cref="SkeletonKingRitualEffectRangeBlocks"/>.</summary>
    public const float SkeletonKingRitualShakeStrength = 0.25f;
    /// <summary>Within this many blocks of the altar a ritual rumble shakes at full strength.</summary>
    public const float SkeletonKingRitualShakeNearBlocks = 8f;

    // --- Entity healthbars (EntityHealthbarRenderer) ---
    // Five hearts float above every live mob, billboarded to face the camera, using the same
    // heart sprites as the player HUD. The pool is drawn proportionally across the row (each
    // heart is MaxHealth / Hearts HP), so any entity kind reads correctly whatever its pool is.
    // The buffers are sized for the most mobs that can ever be alive together: the hard cap.
    public const int MobsMaxAlive = MobHardCap;
    public const int EntityHealthbarHearts = 5;
    // Width (and height) of one heart in blocks; neighbors sit edge to edge like the HUD row.
    public const float EntityHeartSizeBlocks = 0.2f;
    // How far above the mob's head (Position + Mob.HeadAboveCenter) the row's center floats.
    public const float EntityHealthbarLiftBlocks = 0.22f;

    // --- Floating damage numbers (DamageNumberPool, DamageNumberRenderer) ---
    // A hit shows its damage as a small number above the mob's healthbar that rises, drifts a
    // little sideways and fades out: fully opaque for the hold, faded over the rest of its life. It
    // pops in at PopScale and springs back. The pool is a fixed array; a full one replaces its
    // oldest number.
    public const int DamageNumberCapacity = 32;
    public const float DamageNumberLifetimeSeconds = 0.9f;
    public const float DamageNumberHoldSeconds = 0.45f;
    public const float DamageNumberRiseBlocks = 0.8f;
    public const float DamageNumberDriftBlocks = 0.25f;
    public const float DamageNumberPopSeconds = 0.18f;
    public const float DamageNumberPopScale = 1.5f;
    // Start height above the healthbar row's center, and the digit cap height, in blocks.
    public const float DamageNumberLiftBlocks = 0.18f;
    public const float DamageNumberCapHeightBlocks = 0.2f;
    // A client sees a killing blow only as the mob leaving the server's snapshot. Within this range
    // of the camera (well inside every despawn radius, so it is never a despawn) that is a death,
    // shown as the health the mob had left.
    public const float DamageNumberDeathRangeBlocks = 32f;

    // --- Goblin flee (Goblin.IsFleeing): below this share of its pool a goblin gives up and runs
    // from the player at this much of its walk speed ---
    public const float GoblinFleeHealthShare = 0.25f;
    public const float GoblinFleeSpeedFactor = 1.4f;

    // --- Goblin dodge (GoblinManager.IsAimedAt): a charged throw aimed at a goblin (at least this
    // charge, within the cone and range) makes it sidestep out of the line while the aim holds ---
    public const float GoblinDodgeChargeThreshold = 0.5f;
    public const float GoblinDodgeConeDegrees = 12f;
    public const float GoblinDodgeRangeBlocks = 20f;

    // --- Goblin pack alert (GoblinManager.AlertPackAround): a goblin that notices a player pulls
    // every other goblin within this radius forward, skipping their first-throw delay ---
    public const float GoblinPackAlertRadiusBlocks = 12f;

    // --- Goblin pack spawn (GoblinManager.UpdateSpawning): a ring spawn rolls up to this many
    // goblins, the rest of the pack landing within this radius of the found cell ---
    public const int GoblinPackMaxSize = 3;
    public const float GoblinPackSpawnRadiusBlocks = 3f;
    public const int GoblinPackSpawnTries = 8;

    // --- Cave goblins (GoblinManager.TryFindCaveSpawn, CB-4): a target standing underground is searched
    // for a dark cave floor first, GoblinCaveSpawnTries times in the goblin ring (no night gate: caves
    // are always dark), the floor kept by its cave biome's GoblinCaveFavoredWeight/OtherWeight. A cave
    // pack mate is searched on the leader's own floor, from within GoblinCavePackSearchHalfHeight of it
    // and down at most GoblinCavePackScanDepth cells, and only in the leader's cave biome. ---
    public const int GoblinCaveSpawnTries = 12;
    public const int GoblinCavePackSearchHalfHeight = 3;
    public const int GoblinCavePackScanDepth = 8;

    // --- Goblin torches (GoblinTorches): a spawn may carry torches, held in the off hand while it
    // is dark (night or underground), lit like a small campfire and dropped on death ---
    public const float GoblinTorchChanceDay = 0.03f;
    public const float GoblinTorchChanceNight = 0.4f;
    public const float GoblinTorchChanceUnderground = 0.7f;
    public const int GoblinTorchMaxCount = 2;
    // Night factor from which a carried torch is out
    public const float GoblinTorchNightFactor = 0.5f;

    // --- Goblin notice pip (NoticePipRenderer): a "!" pops over a goblin's head the moment it
    // first notices a player (the same moment its first-throw delay starts) ---
    public const float GoblinNoticePipSeconds = 0.9f;
    // Blocks the pip floats above the head, and its cap height
    public const float GoblinNoticePipRiseBlocks = 0.55f;
    public const float GoblinNoticePipCapHeightBlocks = 0.45f;
    // Pips drawn per frame; past it the rest wait for the next frame
    public const int GoblinNoticePipMax = 16;
}
