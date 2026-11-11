using System.Numerics;

namespace VoxelEngine.Config;

/// <summary>
/// The bone-shrapnel effect (a blade's own <c>shrapnel</c> object and the bone charm's
/// <c>bone_shrapnel</c> ability): the charm's weaker default profile, how much a worn charm raises a
/// blade's own, and how the fragments fly. A landed melee hit sprays them and any other enemy they
/// touch takes the profile's damage (<see cref="Simulation.BoneShrapnel"/>).
/// </summary>
public static class ShrapnelConfig
{
    // --- The charm alone: a handful of weak shards on any weapon's hit ---
    public const int CharmCount = 3;
    public const int CharmDamage = 2;

    // --- A hit that has both a blade's own shrapnel and the charm: the blade's count and damage are
    // raised by this factor. The charm's own profile is not what a blade uses; it only sharpens the
    // blade, so the blade's effect always stays the stronger of the two. ---
    public const float TogetherFactor = 1.2f;

    // --- The fragments in flight ---
    /// <summary>How many fragments may fly at once; the oldest burst is dropped past the cap.</summary>
    public const int MaxLiveFragments = 96;

    /// <summary>How long a fragment lives before it is spent, in seconds.</summary>
    public const float LifeSeconds = 0.7f;

    /// <summary>Launch speed band, in blocks per second.</summary>
    public const float SpeedMin = 7f;
    public const float SpeedMax = 12f;

    /// <summary>Share of the loose-body gravity a fragment falls under, so a burst spreads sideways
    /// instead of dropping to the floor in a step.</summary>
    public const float GravityScale = 0.3f;

    /// <summary>How far out of the source's body a fragment starts, in blocks.</summary>
    public const float SpawnOffsetBlocks = 0.35f;

    /// <summary>Launch elevation band above the horizon, in radians (a little below it to a bit above).</summary>
    public const float MinElevationRadians = -0.1f;
    public const float MaxElevationRadians = 0.55f;

    // --- Cosmetic chips at the burst (never on a headless server) ---
    public const int BurstSparkCount = 8;
    public const float BurstSparkSpeed = 3.2f;
    public const float BurstSparkSize = 0.07f;

    /// <summary>sRGB color of the pale chips a burst throws (bone white, matching the hit sparks).</summary>
    public static readonly Vector3 BurstSparkColor = new(0.94f, 0.9f, 0.78f);
}
