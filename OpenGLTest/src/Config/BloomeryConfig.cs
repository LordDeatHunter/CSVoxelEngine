namespace VoxelEngine.Config;

/// <summary>
/// Iron smelting tuning: the bloomery's heat model (<see cref="VoxelEngine.Simulation.Smithing.BloomeryRules"/>), its
/// bellows (a block a body lands on) and the anvil the blooms are forged on
/// (<see cref="VoxelEngine.Simulation.Smithing.AnvilManager"/>). What a particular bloomery holds comes from its block
/// JSON (<c>bloomery</c> object) and what a forge takes from its recipe; these numbers are shared by all of them.
/// </summary>
public static class BloomeryConfig
{
    /// <summary>Heat (0..1) a bloomery starts at when a strike lights it.</summary>
    public const float IgnitionHeat = 0.5f;

    /// <summary>Heat lost per second while lit: somebody has to keep landing on the bellows.</summary>
    public const float HeatDecayPerSecond = 0.05f;

    /// <summary>Heat a landing on the bellows adds before its fall counts: what a jump on the spot already gives.</summary>
    public const float BellowsBaseHeat = 0.06f;

    /// <summary>Heat each block of the landing's fall adds on top of <see cref="BellowsBaseHeat"/>.</summary>
    public const float BellowsHeatPerBlock = 0.05f;

    /// <summary>Most heat one landing adds, however far the body fell.</summary>
    public const float BellowsMaxHeat = 0.35f;

    /// <summary>Blocks per second a bellows swells back toward a full block once nothing stands in it: fast enough to
    /// be full again by the time a body that jumped off it comes back down, so the landing presses a full bag.</summary>
    public const float BellowsExpandSpeed = 0.8f;

    /// <summary>Shortest fall (blocks) that works the bellows, so stepping on it or settling onto it does not; a jump
    /// on the spot drops more than this.</summary>
    public const float BellowsMinFallBlocks = 0.4f;

    /// <summary>Shortest gap between two landings that both count on one bloomery, so a lag burst or a crowd of mobs
    /// never pumps faster than a body can jump.</summary>
    public const float PumpCooldownSeconds = 0.35f;

    /// <summary>Heat below which the ore does not smelt: the fire is too cold and the unit's progress runs back.</summary>
    public const float BandMin = 0.35f;

    /// <summary>How bright the hearth and the throat of a lit bloomery glow with no heat left, as a share of
    /// their full glow (<see cref="Simulation.Smithing.BloomeryRules.GlowAlpha"/>).</summary>
    public const float GlowColdAlpha = 0.2f;

    /// <summary>Heat at which the glow is full: well inside the band, so a fire that is merely good enough still
    /// looks duller than one worked hard.</summary>
    public const float GlowFullHeat = 0.7f;

    /// <summary>Seconds of progress a cold fire takes back per second when it is out entirely; a fire just under
    /// <see cref="BandMin"/> takes back almost nothing, so the colder, the faster the unit unsmelts.</summary>
    public const float ColdReverseRate = 1.5f;

    /// <summary>Seconds one ore unit smelts for while the heat is at or over the band.</summary>
    public const float SecondsPerOre = 12f;

    /// <summary>Most blooms a saved bloomery may claim to hold: its output never exceeds its ore cap, so this only
    /// bounds a corrupt save.</summary>
    public const int MaxStoredOutput = 64;

    /// <summary>Slack in seconds a server allows on the anvil's hit gap, for network jitter.</summary>
    public const float HammerSlackSeconds = 0.3f;
}
