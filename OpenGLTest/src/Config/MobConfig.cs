namespace VoxelEngine.Config;

/// <summary>
/// Mob tuning: the shared caps and rules (fall damage, lava, loot toss, notice, interest, wander,
/// torch-safe ground, ledges), the per-layer difficulty, slimes, goblins, the hell mobs, healthbars, damage numbers and the goblin notice
/// pip.
/// </summary>
public static class MobConfig
{
    // --- Mob caps (MobCaps) ---
    // Two caps shared by every mob kind, judged against the living mobs of every manager together
    // (dying ones excluded), so a new kind needs no cap of its own. The ring spawners stop at the
    // natural cap; a forced spawn (a slime splitting, a scenario, a save restore, a client's
    // mirror) may pass it, but nothing ever spawns past the hard cap, which also sizes the
    // healthbar buffers (MobsMaxAlive).
    public const int MobNaturalCap = 12;
    public const int MobHardCapFactor = 15;
    public const int MobHardCap = MobNaturalCap * MobHardCapFactor;

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
    public const float SlimeSpawnMinRadiusBlocks = 20f;
    public const float SlimeSpawnMaxRadiusBlocks = 40f;
    public const float SlimeDespawnRadiusBlocks = 64f;
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

    // --- Goblins (GoblinManager) ---
    // Short forest raiders that throw the flint they carry at the player from a standoff band,
    // then close in for a club swing once their stones are gone. They spawn like slimes (dry
    // forest surface, a ring around the player) and are culled when they stray too far. The
    // throw band must stay inside the stone's level-ground range, v^2/g at GoblinStoneSpeed
    // under ThrownProjectile.Gravity, with margin for a chest-height target and the
    // semi-implicit Euler drop, or the far edge of the band has no solvable arc.
    public const float GoblinSpawnIntervalSeconds = 14f;
    public const float GoblinSpawnMinRadiusBlocks = 20f;
    public const float GoblinSpawnMaxRadiusBlocks = 40f;
    public const int GoblinSpawnTries = 8;
    public const float GoblinDespawnRadiusBlocks = 64f;
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

    // --- Hell mobs (HellMobManager, Hellbrute, Cinderwitch) ---
    // Both live only in hell's cavern pockets (MobSpawner.TryFindHellSpawn): a ring like the goblin's
    // around a player standing in hell, each try scanning a column down from near the player's feet for
    // an open floor (solid ground, two clear dry cells, block light at most HellSpawnMaxBlockLight, so a
    // lava-lit pool edge or a torched base stays clear). The layer index scales each spawn's health,
    // damage and the caster's fire rate (LayerDifficulty) and the pack size: one more mob every
    // HellPackLayersPerExtra layers, up to HellPackMaxSize. HellCasterShare of the spawns are casters.
    public const float HellSpawnIntervalSeconds = 10f;
    public const float HellSpawnMinRadiusBlocks = 20f;
    public const float HellSpawnMaxRadiusBlocks = 40f;
    public const int HellSpawnTries = 10;
    public const int HellSpawnSearchHalfHeight = 16;
    public const int HellSpawnScanDepth = 40;
    public const int HellSpawnMaxBlockLight = 10;
    public const int HellPackLayersPerExtra = 2;
    public const int HellPackMaxSize = 3;
    public const float HellPackSpawnRadiusBlocks = 4f;
    public const float HellCasterShare = 0.4f;
    public const float HellDespawnRadiusBlocks = 64f;
    public const float HellMobLosRetrySeconds = 0.3f;

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
