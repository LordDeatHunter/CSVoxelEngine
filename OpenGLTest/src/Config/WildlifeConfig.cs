namespace VoxelEngine.Config;

/// <summary>
/// Tuning for the animals of the wild (<c>WildlifeManager</c>, the <c>Animal</c> kinds): their own
/// budget beside the hostile mobs', where and how often they spawn, and each species' body, speeds and
/// temper. Speeds are blocks per second (the player runs at <c>PlayerPhysics.BaseSpeed</c>, 8).
/// </summary>
public static class WildlifeConfig
{
    // --- Budget and spawning (MobCaps.WildlifeCap, WildlifeManager) ---
    // Up to WildlifeCap animals live around each player (MobCaps scales it like the hostile cap). Every
    // SpawnIntervalSeconds (sooner the emptier the wild is, MobCaps.WildlifeSpawnHaste) one dry surface
    // column is searched in the SpawnMin..MaxRadiusBlocks ring around a target, the species rolled for its
    // biome and hour (WildlifeSpawnRules); every animal spawns on its own, never in a group.
    // An animal no target keeps in play (MobTargets.OutOfPlay at DespawnRadiusBlocks) fades out; unlike a
    // hostile mob it never despawns for standing idle.
    public const int WildlifeCap = 60;
    public const float SpawnIntervalSeconds = 5f;
    public const float SpawnMinRadiusBlocks = 36f;
    public const float SpawnMaxRadiusBlocks = 64f;
    public const int SpawnTries = 8;
    public const float DespawnRadiusBlocks = 100f;

    // How likely each species is among the ones a column may spawn (relative weights)
    public const float DeerSpawnWeight = 5f;
    public const float BoarSpawnWeight = 3f;
    // The dire boar is the rare one: a lone miniboss among many herds
    public const float DireBoarSpawnWeight = 0.25f;
    public const float CrowSpawnWeight = 0.8f;
    // Dragonflies keep to the waterside: only a column within NearWaterRadiusBlocks of surface water spawns one
    public const float DragonflySpawnWeight = 2f;
    public const float NearWaterRadiusBlocks = 8f;
    // A flyer appears in the open air AirSpawnLiftMin..MaxBlocks over the ground cell its spawn found
    public const float AirSpawnLiftMinBlocks = 3f;
    public const float AirSpawnLiftMaxBlocks = 6f;

    // --- Flyers that perch (FlyingAnimal) ---
    // With nobody to react to, a flyer stays up for FlyerAirMin..MaxSeconds, then lands when solid ground
    // lies within FlyerLandDropBlocks below it and rests for FlyerPerchMin..MaxSeconds before taking off.
    public const float FlyerAirMinSeconds = 9f;
    public const float FlyerAirMaxSeconds = 22f;
    public const float FlyerPerchMinSeconds = 6f;
    public const float FlyerPerchMaxSeconds = 16f;
    public const float FlyerLandDropBlocks = 8f;
    // A flyer that likes a high perch (FlyingAnimal.ChoosePerchSpot) samples FlyerPerchSamples columns within
    // FlyerPerchSearchRadiusBlocks and heads for the nearest tree top; if it has not landed FlyerPerchSeekSeconds
    // after its air time ran out it gives up and takes any ground.
    public const int FlyerPerchSamples = 20;
    public const float FlyerPerchSearchRadiusBlocks = 14f;
    public const float FlyerPerchSeekSeconds = 10f;

    // --- Gaits (Animal.ChooseGait) ---
    // A walker breaks into its run clip above RunGaitFraction of the way from its walk speed to its run
    // speed, and drops back to the walk below RunGaitFallFraction of it, so it never flickers at the edge.
    public const float RunGaitFraction = 0.45f;
    public const float RunGaitFallFraction = 0.3f;

    // --- Deer (Deer) ---
    // Harmless and shy. It lifts its head and watches a player inside DeerAlertRangeBlocks, bolts when
    // one comes within DeerFleeRangeBlocks or hurts it, and keeps running for DeerFleeSeconds, veering up
    // to DeerFleeVeerDegrees off the straight line away so a herd scatters. Left alone it wanders and
    // grazes every DeerGrazeMin..MaxSeconds.
    public const float DeerHeight = 1.5f;
    public const float DeerHalfExtent = 0.4f;
    public const float DeerWalkSpeed = 2.4f;
    // 1.2x the player's sprint (PlayerPhysics.BaseSpeed 8 * SprintMultiplier 1.5 = 12)
    public const float DeerRunSpeed = 14.4f;
    public const float DeerJumpSpeed = 9f;
    public const float DeerShoveSpeed = 3f;
    public const float DeerAlertRangeBlocks = 14f;
    public const float DeerFleeRangeBlocks = 8f;
    public const float DeerFleeSeconds = 4f;
    public const float DeerFleeVeerDegrees = 35f;
    public const float DeerGrazeMinSeconds = 7f;
    public const float DeerGrazeMaxSeconds = 18f;

    // --- Boar and dire boar (Boar) ---
    // A boar minds its own rooting (every BoarRootMin..MaxSeconds) until it is hurt, or a player stands
    // within BoarCrowdRangeBlocks of it for BoarCrowdSeconds. Then it paws the ground (its threat clip)
    // and charges at BoarChargeSpeed, goring whoever is within BoarGoreReach at the clip's impact for
    // BoarGoreDamage, at most every BoarGoreCooldownSeconds. Its anger lasts BoarAngerSeconds past the
    // last moment it saw its target. The dire boar is the same animal oversized, and angry at anyone it
    // notices within DireBoarNoticeRangeBlocks.
    public const float BoarHeight = 1.05f;
    public const float BoarHalfExtent = 0.45f;
    public const float BoarWalkSpeed = 2.2f;
    public const float BoarChargeSpeed = 8.5f;
    public const float BoarJumpSpeed = 8.5f;
    public const float BoarShoveSpeed = 1.5f;
    public const float BoarCrowdRangeBlocks = 3.5f;
    public const float BoarCrowdSeconds = 2f;
    public const float BoarAngerSeconds = 10f;
    public const float BoarGoreReach = 1.7f;
    public const int BoarGoreDamage = 9;
    public const float BoarGoreCooldownSeconds = 1.6f;
    public const float BoarKnockbackSpeed = 8f;
    public const float BoarKnockbackLift = 4f;
    public const float BoarRootMinSeconds = 8f;
    public const float BoarRootMaxSeconds = 20f;

    public const float DireBoarHeight = 1.75f;
    public const float DireBoarHalfExtent = 0.7f;
    public const float DireBoarWalkSpeed = 2f;
    public const float DireBoarChargeSpeed = 8f;
    public const float DireBoarShoveSpeed = 0.6f;
    public const float DireBoarNoticeRangeBlocks = 20f;
    public const float DireBoarGoreReach = 2.4f;
    public const int DireBoarGoreDamage = 48;
    public const float DireBoarKnockbackSpeed = 13f;
    public const float DireBoarKnockbackLift = 6f;

    // --- Crow (Crow) ---
    // A pest in flocks. A player within CrowNoticeRangeBlocks draws it: it circles CrowHoverMin..MaxBlocks
    // over their head and, within CrowSwoopRangeBlocks horizontally with a clear line, swoops at
    // CrowSwoopSpeedFactor of its flight and rakes for CrowStrikeDamage whoever is within CrowStrikeReach of
    // it (in 3D, at the chest) at the impact, every CrowStrikeMin..MaxSeconds. On the ground it pecks an
    // ankle within CrowPeckReach instead, and left alone it pecks the dirt or caws every
    // CrowHabitMin..MaxSeconds. It is not hostile by nature: it only harasses a player who has hurt it, for
    // CrowAngerSeconds after the blow, and none of the above applies before that. Unprovoked it is a thief:
    // a dropped item within CrowThiefRangeBlocks draws it down, and within CrowThiefReach it takes the item,
    // then leaves the next one alone for CrowThiefCooldownSeconds. It prefers to perch in trees.
    public const float CrowHeight = 0.7f;
    public const float CrowHalfExtent = 0.45f;
    public const float CrowShoveSpeed = 4f;
    public const float CrowCruiseSpeed = 7f;
    public const float CrowVerticalSpeed = 5f;
    public const float CrowAcceleration = 9f;
    public const float CrowHoverMinBlocks = 2.5f;
    public const float CrowHoverMaxBlocks = 5f;
    public const float CrowMinClearanceBlocks = 0.4f;
    public const float CrowBobBlocks = 0.2f;
    public const float CrowBobHz = 0.8f;
    public const float CrowNoticeRangeBlocks = 10f;
    public const float CrowSwoopRangeBlocks = 4f;
    public const float CrowSwoopSpeedFactor = 2f;
    public const float CrowStrikeReach = 1.5f;
    public const float CrowPeckReach = 1.3f;
    public const int CrowStrikeDamage = 2;
    public const float CrowStrikeMinSeconds = 4f;
    public const float CrowStrikeMaxSeconds = 8f;
    public const float CrowKnockbackSpeed = 2f;
    public const float CrowKnockbackLift = 1f;
    public const float CrowHabitMinSeconds = 3f;
    public const float CrowHabitMaxSeconds = 8f;
    // Calm in the air (not harassing anyone, not after a prize) a crow holds no height: it falls under
    // CrowGlideGravity until it sinks at CrowGlideSinkSpeed, and below where it is headed it flaps, each flap
    // one play of its flap clip whose downstroke sets its climb to CrowFlapBoostSpeed (a hop of about
    // boost^2 / 2 gravity blocks). It flaps every CrowFlapMin..MaxSeconds, back to back while more than
    // CrowFlapClimbBlocks below its aim.
    public const float CrowGlideSinkSpeed = 0.8f;
    public const float CrowGlideGravity = 8f;
    public const float CrowFlapMinSeconds = 0.8f;
    public const float CrowFlapMaxSeconds = 1.4f;
    public const float CrowFlapBoostSpeed = 4f;
    public const float CrowFlapClimbBlocks = 1f;
    public const float CrowAngerSeconds = 20f;
    /// <summary>How far from a hurt crow its calm flockmates hear it and turn on the attacker too.</summary>
    public const float CrowFlockCallRangeBlocks = 24f;
    public const float CrowThiefRangeBlocks = 16f;
    public const float CrowThiefReach = 0.9f;
    public const float CrowThiefCooldownSeconds = 15f;

    // --- Dragonfly (Dragonfly) ---
    // A man-sized hunter of the waterside. A player within DragonflyNoticeRangeBlocks is prey: it holds
    // DragonflyHoverMin..MaxBlocks over their head and, within DragonflyDartRangeBlocks horizontally with a
    // clear line, darts at DragonflyDartSpeedFactor of its flight and bites for DragonflyBiteDamage whoever
    // is within DragonflyBiteReach at the impact, every DragonflyBiteCooldownSeconds (the first not before
    // DragonflyFirstBiteDelaySeconds).
    public const float DragonflyHeight = 0.9f;
    public const float DragonflyHalfExtent = 0.5f;
    public const float DragonflyShoveSpeed = 2.5f;
    public const float DragonflyCruiseSpeed = 8f;
    public const float DragonflyVerticalSpeed = 6f;
    public const float DragonflyAcceleration = 12f;
    public const float DragonflyHoverMinBlocks = 1.5f;
    public const float DragonflyHoverMaxBlocks = 3.5f;
    public const float DragonflyMinClearanceBlocks = 0.6f;
    public const float DragonflyBobBlocks = 0.3f;
    public const float DragonflyBobHz = 1.2f;
    public const float DragonflyNoticeRangeBlocks = 16f;
    public const float DragonflyDartRangeBlocks = 5f;
    public const float DragonflyDartSpeedFactor = 2.2f;
    public const float DragonflyBiteReach = 1.9f;
    public const int DragonflyBiteDamage = 8;
    public const float DragonflyFirstBiteDelaySeconds = 1f;
    public const float DragonflyBiteCooldownSeconds = 2.6f;
    public const float DragonflyKnockbackSpeed = 6f;
    public const float DragonflyKnockbackLift = 2.5f;
    // --- Corpse bloom (CorpseBloom) ---
    // The jungle's rooted ambusher. A player within BloomWakeRangeBlocks opens it; within BloomTriggerRadiusBlocks
    // with a clear line it spews, off its BloomSpewCooldownSeconds cooldown. The burst reaches BloomBurstRadiusBlocks
    // horizontally and BloomBurstHeightBlocks vertically for BloomBurstDamage and the poison effect, and leaves a
    // cloud of the same reach that poisons whoever stands in it, player or mob, every BloomCloudPulseSeconds for
    // BloomCloudSeconds, thinning as it goes.
    // It shuts BloomCloseDelaySeconds after the last player left its wake range.
    public const float BloomSpawnWeight = 3f;
    public const float BloomHeight = 1.1f;
    public const float BloomHalfExtent = 0.7f;
    public const float BloomWakeRangeBlocks = 8f;
    public const float BloomTriggerRadiusBlocks = 4f;
    public const float BloomBurstRadiusBlocks = 3.5f;
    public const float BloomBurstHeightBlocks = 2f;
    public const int BloomBurstDamage = 3;
    public const float BloomSpewCooldownSeconds = 6f;
    public const float BloomCloudSeconds = 8f;
    public const float BloomCloudPulseSeconds = 1f;
    public const float BloomCloseDelaySeconds = 4f;

    // --- Giant centipede (Centipede) ---
    // A long hunter: a player within CentipedeNoticeRangeBlocks is prey, run down at CentipedeWalkSpeed and bitten
    // with the chest within CentipedeBiteReach of the head for CentipedeBiteDamage and venom, every
    // CentipedeBiteCooldownSeconds. The simulated body is its head, a cube of CentipedeBodySize that crawls over
    // floors, walls and ceilings like a spider's (CrawlDrive).
    public const float CentipedeSpawnWeight = 2.5f;
    public const float CentipedeBodySize = 0.7f;
    public const float CentipedeShoveSpeed = 2f;
    public const float CentipedeWalkSpeed = 3.6f;
    public const float CentipedeNoticeRangeBlocks = 14f;
    public const float CentipedeBiteReach = 1.8f;
    public const int CentipedeBiteDamage = 9;
    public const float CentipedeBiteCooldownSeconds = 1.8f;
    public const float CentipedeKnockbackSpeed = 4f;
    public const float CentipedeKnockbackLift = 1.5f;
    // The body trails the head along the path it took (CentipedeSpine), remembered every
    // CentipedeTrailSpacingBlocks. At full speed the head turns no tighter than a circle of
    // CentipedeTurnRadiusBlocks, so the body sweeps round instead of doubling back on itself.
    public const float CentipedeTurnRadiusBlocks = 0.8f;
    public const float CentipedeTrailSpacingBlocks = 0.1f;

    // --- Sea life (AquaticAnimal: Fish, Shark, Jellyfish) ---
    // Swimmers fly the shared flight rules through water (Mob.Swims, RiggedMob.Fly): each kind's hover band is
    // the height it keeps over the seabed, capped under the surface. They spawn in water only
    // (MobSpawner.TryFindWaterSpawn): a ring turn near the sea (SeaSpawnChance of the turns, when a player is
    // within SeaSpawnNearBlocks of surface water) picks the kind whose rule fits the column's water depth.
    // Out of water a swimmer falls, flops (fish) and loses BeachedDamage every BeachedDamageIntervalSeconds.
    public const float SeaSpawnChance = 0.5f;
    public const float SeaSpawnNearBlocks = 40f;
    public const int SeaSpawnTries = 24;
    public const float SeaSpawnMinRadiusBlocks = 14f;
    public const float SeaSpawnMaxRadiusBlocks = 40f;
    public const int SchoolMinSize = 3;
    public const int SchoolMaxSize = 5;
    public const float SchoolSpreadBlocks = 2.5f;
    public const int BeachedDamage = 1;
    public const float BeachedDamageIntervalSeconds = 2.5f;
    public const float FlopMinSeconds = 0.6f;
    public const float FlopMaxSeconds = 1.6f;
    public const float FlopHopSpeed = 3.2f;
    public const float FlopSideSpeed = 1.2f;

    // Fish (Cod, Salmon, Tuna): neutral. A threat within FishDartRangeBlocks (FishAlarmFactor times that for
    // FishAlarmSeconds after a blow) sends it fleeing on its dart clip at FishDartSpeedFactor of its cruise.
    // A school follows its first fish at a SchoolSpreadBlocks offset.
    public const float FishDartRangeBlocks = 5f;
    public const float FishAlarmFactor = 2.5f;
    public const float FishAlarmSeconds = 4f;
    public const float FishDartSpeedFactor = 2.4f;
    public const float FishFleeBlocks = 10f;
    public const float FishVerticalSpeed = 1.6f;
    public const float FishAcceleration = 3f;
    public const float FishMinClearanceBlocks = 0.3f;
    public const float FishBobBlocks = 0.15f;
    public const float FishBobHz = 0.4f;
    public const float FishShoveSpeed = 1.5f;

    public const float CodSpawnWeight = 4f;
    public const float CodHeight = 0.44f;
    public const float CodHalfExtent = 0.22f;
    public const float CodCruiseSpeed = 1.6f;
    public const float CodHoverMinBlocks = 0.5f;
    public const float CodHoverMaxBlocks = 6f;
    public const int CodMinWaterDepthBlocks = 3;

    public const float SalmonSpawnWeight = 3f;
    public const float SalmonHeight = 0.5f;
    public const float SalmonHalfExtent = 0.22f;
    public const float SalmonCruiseSpeed = 1.9f;
    public const float SalmonHoverMinBlocks = 0.5f;
    public const float SalmonHoverMaxBlocks = 8f;
    public const int SalmonMinWaterDepthBlocks = 3;

    public const float TunaSpawnWeight = 2f;
    public const float TunaHeight = 0.94f;
    public const float TunaHalfExtent = 0.5f;
    public const float TunaCruiseSpeed = 2.8f;
    public const float TunaHoverMinBlocks = 2f;
    public const float TunaHoverMaxBlocks = 14f;
    public const int TunaMinWaterDepthBlocks = 8;

    // Shark: hostile. A player within SharkNoticeRangeBlocks is prey: it charges at SharkChargeSpeedFactor of its
    // cruise on its charge clip and, within SharkBiteReach of their chest in 3D and off its cooldown, plays bite;
    // the impact event lands SharkBiteDamage. It lunges at SharkLungeSpeedFactor while the bite clip plays.
    public const float SharkSpawnWeight = 0.6f;
    public const float SharkHeight = 1.31f;
    public const float SharkHalfExtent = 0.88f;
    public const float SharkShoveSpeed = 1f;
    public const float SharkCruiseSpeed = 3f;
    public const float SharkVerticalSpeed = 2.2f;
    public const float SharkAcceleration = 2.2f;
    public const float SharkHoverMinBlocks = 2f;
    public const float SharkHoverMaxBlocks = 12f;
    public const float SharkMinClearanceBlocks = 0.5f;
    public const float SharkBobBlocks = 0.2f;
    public const float SharkBobHz = 0.2f;
    public const int SharkMinWaterDepthBlocks = 8;
    public const float SharkNoticeRangeBlocks = 18f;
    public const float SharkChargeSpeedFactor = 2.2f;
    public const float SharkLungeSpeedFactor = 1.6f;
    public const float SharkBiteReach = 2.8f;
    public const int SharkBiteDamage = 12;
    public const float SharkBiteCooldownSeconds = 1.6f;
    public const float SharkKnockbackSpeed = 4f;
    public const float SharkKnockbackLift = 1f;

    // Jellyfish: neutral and never chasing. It drifts at JellyfishDriftSpeed; a player (or mob) within
    // JellyfishStingReach in 3D of its bell and off its cooldown makes it play sting, and the impact event stings
    // everyone still in reach for JellyfishStingDamage and the venom effect.
    public const float JellyfishSpawnWeight = 2f;
    public const float JellyfishHeight = 1.25f;
    public const float JellyfishHalfExtent = 0.31f;
    public const float JellyfishShoveSpeed = 0.8f;
    public const float JellyfishDriftSpeed = 0.7f;
    public const float JellyfishVerticalSpeed = 0.5f;
    public const float JellyfishAcceleration = 1.2f;
    public const float JellyfishHoverMinBlocks = 2f;
    public const float JellyfishHoverMaxBlocks = 16f;
    public const float JellyfishMinClearanceBlocks = 0.5f;
    public const float JellyfishBobBlocks = 0.35f;
    public const float JellyfishBobHz = 0.25f;
    public const int JellyfishMinWaterDepthBlocks = 5;
    public const float JellyfishStingReach = 1.6f;
    public const int JellyfishStingDamage = 4;
    public const float JellyfishStingCooldownSeconds = 2.5f;
    public const float JellyfishKnockbackSpeed = 1.5f;
    public const float JellyfishKnockbackLift = 0.5f;
}
