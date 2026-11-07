namespace VoxelEngine.Config;

/// <summary>
/// The sandworm boss's tuning (plans/SANDWORM.md): its chain of segments, the burrow, breach, air and dash
/// phases of its brain, and the contact it deals. Distances are blocks, speeds blocks per second, angles
/// degrees unless the name says radians.
/// </summary>
public static class SandwormConfig
{
    // --- The chain (SandwormChain) ---

    /// <summary>Segments in the chain: the head, the body rings and the tail.</summary>
    public const int SegmentCount = 14;

    /// <summary>Distance from the head to the first body segment.</summary>
    public const float HeadLinkBlocks = 2.75f;

    /// <summary>Distance between two body segments (the model's ring is this long plus an overlap).</summary>
    public const float BodyLinkBlocks = 2.5f;

    /// <summary>Distance from the last body segment to the tail.</summary>
    public const float TailLinkBlocks = 2.25f;

    /// <summary>The sharpest bend between two neighboring links; a tighter turn drags the rest along a wider arc.</summary>
    public const float MaxBendDegrees = 38f;

    /// <summary>Draw scale of the first body segment (the head is 1).</summary>
    public const float BodyScaleStart = 1f;

    /// <summary>Draw scale of the last body segment; the ones between taper linearly.</summary>
    public const float BodyScaleEnd = 0.7f;

    /// <summary>Draw scale of the tail.</summary>
    public const float TailScale = 0.6f;

    // --- Brain: the thump, the dig and its telegraph ---

    /// <summary>How long the summon's thumping lasts before the worm starts digging.</summary>
    public const float ThumpSeconds = 4f;

    /// <summary>How far under the sand surface the head travels while burrowed.</summary>
    public const float BurrowDepthBlocks = 5f;

    /// <summary>Horizontal speed of the burrowed head.</summary>
    public const float DigSpeed = 9f;

    /// <summary>How fast the head's height eases to its burrow (or slide) height.</summary>
    public const float VerticalEaseRate = 4f;

    /// <summary>Horizontal distance to its target at which the worm locks the point it will surface at.</summary>
    public const float LockDistanceBlocks = 11f;

    /// <summary>Horizontal distance from the locked point at which the worm breaches.</summary>
    public const float BreachTriggerBlocks = 1.5f;

    /// <summary>Horizontal range inside which the surfacing telegraph (sand puffs) ramps from 0 to full.</summary>
    public const float TelegraphRangeBlocks = 22f;

    /// <summary>Horizontal range inside which the earthquake ramps from 0 to full.</summary>
    public const float ShakeRangeBlocks = 48f;

    /// <summary>Seconds with no target before the worm gives up and burrows away.</summary>
    public const float RetreatAfterSeconds = 20f;

    // --- Brain: breach and air ---

    /// <summary>Upward speed of the head as it breaches.</summary>
    public const float BreachUpSpeed = 30f;

    /// <summary>Height above the sand at which a breach ends and the head is airborne.</summary>
    public const float BreachClearBlocks = 2f;

    /// <summary>Horizontal speed of the airborne head toward its target.</summary>
    public const float AirSpeed = 11f;

    /// <summary>How fast the airborne head's horizontal velocity eases toward its steering.</summary>
    public const float AirSteerRate = 2.5f;

    /// <summary>Extra downward speed per second added to a dive, on top of gravity.</summary>
    public const float DiveAcceleration = 26f;

    /// <summary>Chance, at the apex, to slide rather than dive.</summary>
    public const float SlideChance = 0.45f;

    /// <summary>The chance of repeating the pick it just made is scaled by this.</summary>
    public const float RepeatPickFactor = 0.4f;

    // --- Brain: the dash ---

    /// <summary>Height of the head's center over the sand while it slides (half its body is under).</summary>
    public const float SlideHeightBlocks = 1f;

    /// <summary>Speed of the surface dash.</summary>
    public const float DashSpeed = 14f;

    /// <summary>How fast the dash may turn toward its target, deliberately slow so it can be dodged.</summary>
    public const float DashTurnDegreesPerSecond = 32f;

    /// <summary>The longest a dash lasts.</summary>
    public const float DashMaxSeconds = 6f;

    // --- Contact ---

    /// <summary>Damage of the head's chomp as it breaches and flies.</summary>
    public const int ChompDamage = 30;

    /// <summary>Upward launch the chomp gives its victim.</summary>
    public const float ChompLift = 11f;

    /// <summary>Sideways shove of the chomp.</summary>
    public const float ChompKnockback = 6f;

    /// <summary>Damage of the dash's contact (any exposed segment).</summary>
    public const int DashDamage = 12;

    /// <summary>Sideways shove of the dash's contact.</summary>
    public const float DashKnockback = 9f;

    /// <summary>Seconds between two contact hits on the same target.</summary>
    public const float ContactCooldownSeconds = 0.8f;

    // --- Look: the jaw, the sand it throws and the quake it makes ---

    /// <summary>How far the lower jaw hangs open at rest (radians).</summary>
    public const float JawClosedRadians = 0.12f;

    /// <summary>How far the lower jaw hangs open while it breaches, flies and slides (radians).</summary>
    public const float JawOpenRadians = 0.85f;

    /// <summary>How fast the jaw eases to its target.</summary>
    public const float JawEaseRate = 7f;

    /// <summary>Sand chips a second thrown around the surfacing point when the worm is far from it...</summary>
    public const float TelegraphChipsMin = 6f;

    /// <summary>...rising to this many a second as it reaches the surface.</summary>
    public const float TelegraphChipsMax = 110f;

    /// <summary>Chip speed far from the surface...</summary>
    public const float TelegraphSpeedMin = 1.2f;

    /// <summary>...and at the breach.</summary>
    public const float TelegraphSpeedMax = 5.5f;

    /// <summary>Radius of the churned sand around the surfacing point when far...</summary>
    public const float TelegraphRadiusMin = 1.2f;

    /// <summary>...and at the breach.</summary>
    public const float TelegraphRadiusMax = 3.6f;

    /// <summary>Size of one sand chip.</summary>
    public const float SandChipSize = 0.24f;

    /// <summary>Chips thrown in the ring where the head breaks through.</summary>
    public const int BreachChips = 70;

    /// <summary>Chips thrown where a diving worm goes back under.</summary>
    public const int DiveChips = 36;

    /// <summary>Chips a second kicked up along the surface by each exposed part of a dash.</summary>
    public const float DashChipsPerSecond = 26f;

    /// <summary>The camera trauma at full strength of the earthquake of a worm tunneling near it.</summary>
    public const float QuakeStrength = 0.6f;

    /// <summary>The earthquake is full inside this distance of the head...</summary>
    public const float QuakeNearBlocks = 6f;

    /// <summary>...and gone beyond this one.</summary>
    public const float QuakeRangeBlocks = 56f;

    /// <summary>Camera trauma of the breach boom at full strength.</summary>
    public const float BreachShake = 1f;

    /// <summary>Camera trauma of one thump pulse at full strength.</summary>
    public const float ThumpShake = 0.35f;

    /// <summary>Seconds between two thump pulses while the summon rumbles.</summary>
    public const float ThumpPulseSeconds = 0.7f;

    // --- Body, damage and reach ---

    /// <summary>Half extent of the head's hit box in blocks; a body segment's is this times its draw scale.</summary>
    public const float HeadRadiusBlocks = 1.5f;

    /// <summary>The share of a blow's damage that lands when it hits the body or tail instead of the head.</summary>
    public const float BodyDamageShare = 0.6f;

    /// <summary>The worm hunts a target only within this horizontal distance.</summary>
    public const float HuntRadiusBlocks = 96f;

    /// <summary>Past this horizontal distance from every target the worm retreats and is gone.</summary>
    public const float DespawnRadiusBlocks = 160f;

    /// <summary>How far from the desert the worm may stray before it gives up and retreats: it burrows through any
    /// ground, but not far from its home biome.</summary>
    public const float DesertLeaveBlocks = 48f;

    /// <summary>Seconds the worm's death plays before it is removed.</summary>
    public const float DeathSeconds = 2.5f;
}
