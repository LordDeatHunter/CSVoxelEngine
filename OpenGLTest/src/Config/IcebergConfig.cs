namespace VoxelEngine.Config;

/// <summary>
/// Iceberg tuning: the caps and the spawn ring, the open-water cold band at the pack-ice edge,
/// the size roll and mass, the drift and push, the render-only wobble, the life cycle and the
/// client mirror's dead reckoning. Icebergs are not mobs, so none of <see cref="MobConfig"/>'s
/// caps apply to them.
/// </summary>
public static class IcebergConfig
{
    // --- Caps and spawning (IcebergManager, IcebergSpawner) ---

    /// <summary>The ring spawner stops once this many icebergs are alive.</summary>
    public const int NaturalCap = 6;

    /// <summary>
    /// Hard cap on live icebergs: a forced spawn (a scenario, a save restore) may pass the
    /// natural cap but never this one.
    /// </summary>
    public const int MaxAlive = 16;

    /// <summary>Seconds between two attempts of the ring spawner.</summary>
    public const float SpawnIntervalSeconds = 20f;

    /// <summary>Random ring points one spawn attempt tries before giving up.</summary>
    public const int SpawnTries = 8;

    /// <summary>Inner radius in blocks of the spawn ring around a target.</summary>
    public const float SpawnMinRadiusBlocks = 48f;

    /// <summary>Outer radius in blocks of the spawn ring around a target.</summary>
    public const float SpawnMaxRadiusBlocks = 96f;

    /// <summary>
    /// Horizontal distance in blocks from a berg's footprint inside which a target keeps it from
    /// spawning there, or from sinking at the end of its life.
    /// </summary>
    public const float SpawnClearRadiusBlocks = 24f;

    /// <summary>A berg farther than this many blocks from every target is removed outright.</summary>
    public const float DespawnRadiusBlocks = 160f;

    /// <summary>A berg moves only while some target is within this many blocks of it.</summary>
    public const float SimulateRadiusBlocks = 128f;

    // --- The spawn band (IcebergSpawnRule) ---

    /// <summary>
    /// The coldest weight below which no berg spawns: a little below
    /// <see cref="WorldGenerationConfig.PackIceColdStart"/>, so the band covers the open leads of
    /// the pack-ice band plus a fringe of open sea just warmer than it. The band ends at
    /// <see cref="WorldGenerationConfig.FreezeColdThreshold"/>, where the sheet is solid.
    /// </summary>
    public const float SpawnColdMin = 0.55f;

    // --- Size and mass (IcebergShape) ---

    /// <summary>Smallest horizontal scale a berg rolls. Scale is horizontal only.</summary>
    public const float ScaleMin = 0.85f;

    /// <summary>Largest horizontal scale a berg rolls. Scale is horizontal only.</summary>
    public const float ScaleMax = 1.25f;

    /// <summary>Mass per cubic block of a berg's model volume (above and below the waterline).</summary>
    public const float Density = 1f;

    // --- Motion (IcebergMotion, IcebergContact) ---

    /// <summary>Speed in blocks per second a berg drifts at once it has settled on the wind.</summary>
    public const float DriftSpeed = 0.15f;

    /// <summary>
    /// Largest angle in radians a berg's drift heading turns away from the wind, hashed per berg,
    /// so neighboring bergs do not drift in lockstep.
    /// </summary>
    public const float DriftSpreadRadians = 0.6f;

    /// <summary>
    /// Rate per second (the <see cref="VoxelEngine.Core.MathUtils.Damp"/> rate) at which the
    /// water pulls a berg's velocity back toward its drift.
    /// </summary>
    public const float WaterDragRate = 0.5f;

    /// <summary>Speed in blocks per second a berg never exceeds, however hard it is pushed.</summary>
    public const float MaxSpeed = 1.5f;

    /// <summary>
    /// Force one body in side contact applies to a berg. Divided by the berg's mass, so a big berg
    /// barely moves under the same push that nudges a small one.
    /// </summary>
    public const float PlayerPushForce = 6f;

    /// <summary>
    /// Largest horizontal gap in blocks between a body and a berg box that still counts as side
    /// contact. Must be at least <see cref="VoxelEngine.Chunks.BoxSweep.StepSize"/>, the gap a
    /// swept body stops short of a box.
    /// </summary>
    public const float ContactSlackBlocks = 0.1f;

    /// <summary>
    /// Largest height in blocks the feet may stand above a berg box's top and still ride it.
    /// </summary>
    public const float RideSlackBlocks = 0.1f;

    // --- Wobble (IcebergWobble, render only) ---

    /// <summary>Largest bob in blocks of a scale-1 berg.</summary>
    public const float BobBlocks = 0.08f;

    /// <summary>Largest pitch or roll in radians of a scale-1 berg.</summary>
    public const float TiltRadians = 0.02f;

    /// <summary>
    /// Exponent of the scale the wobble amplitudes are divided by, so a bigger berg wobbles less.
    /// </summary>
    public const float WobbleScaleExponent = 0.5f;

    /// <summary>Shortest period in seconds of one wobble sine.</summary>
    public const float WobblePeriodMinSeconds = 4f;

    /// <summary>Longest period in seconds of one wobble sine.</summary>
    public const float WobblePeriodMaxSeconds = 9f;

    // --- Life cycle (IcebergManager) ---

    /// <summary>Shortest life in seconds a spawned berg rolls before it starts sinking.</summary>
    public const float LifeMinSeconds = 240f;

    /// <summary>Longest life in seconds a spawned berg rolls before it starts sinking.</summary>
    public const float LifeMaxSeconds = 600f;

    /// <summary>Seconds a freshly spawned berg takes to rise out of the sea (render only).</summary>
    public const float EmergeSeconds = 6f;

    /// <summary>Seconds a berg at the end of its life takes to sink out of sight.</summary>
    public const float SinkSeconds = 8f;

    // --- Client mirror (Iceberg.AdvanceMirror) ---

    /// <summary>
    /// Longest time in seconds a client's mirror dead-reckons along its last velocity past a
    /// snapshot, so a lost snapshot never carries a berg away.
    /// </summary>
    public const float MirrorMaxLeadSeconds = 0.3f;
}
