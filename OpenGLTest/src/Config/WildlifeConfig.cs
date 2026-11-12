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
    public const int DireBoarGoreDamage = 24;
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
    public const float CrowAngerSeconds = 20f;
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
}
