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
    // biome and hour (WildlifeSpawnRules), and its group placed within GroupSpawnRadiusBlocks of the first.
    // An animal no target keeps in play (MobTargets.OutOfPlay at DespawnRadiusBlocks) fades out; unlike a
    // hostile mob it never despawns for standing idle.
    public const int WildlifeCap = 30;
    public const float SpawnIntervalSeconds = 1.5f;
    public const float SpawnMinRadiusBlocks = 36f;
    public const float SpawnMaxRadiusBlocks = 64f;
    public const int SpawnTries = 8;
    public const float GroupSpawnRadiusBlocks = 5f;
    public const int GroupSpawnTries = 8;
    public const float DespawnRadiusBlocks = 100f;

    // How likely each species is among the ones a column may spawn (relative weights), and its group size
    public const float DeerSpawnWeight = 5f;
    public const int DeerGroupMin = 2;
    public const int DeerGroupMax = 4;
    public const float BoarSpawnWeight = 3f;
    public const int BoarGroupMin = 1;
    public const int BoarGroupMax = 3;
    // The dire boar is the rare one: a lone miniboss among many herds
    public const float DireBoarSpawnWeight = 0.25f;

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
    public const float DeerRunSpeed = 9.5f;
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
}
