namespace VoxelEngine.Config;

/// <summary>
/// Iron smelting tuning: the bloomery's heat model (<see cref="VoxelEngine.Simulation.Smithing.BloomeryRules"/>),
/// its bellows (the player's jumps) and the anvil the bloom is hammered on
/// (<see cref="VoxelEngine.Simulation.Smithing.AnvilManager"/>). What a particular bloomery or anvil holds comes
/// from its block JSON (<c>bloomery</c> and <c>anvil</c> objects); these numbers are shared by all of them.
/// </summary>
public static class BloomeryConfig
{
    /// <summary>Heat (0..1) a bloomery starts at when a strike lights it.</summary>
    public const float IgnitionHeat = 0.5f;

    /// <summary>Heat lost per second while lit: the player has to keep the bellows going.</summary>
    public const float HeatDecayPerSecond = 0.05f;

    /// <summary>Heat one bellows pump (one jump beside the bloomery) adds, up to 1.</summary>
    public const float PumpHeat = 0.12f;

    /// <summary>Shortest gap between two pumps that both count, so a held jump key or a lag burst never
    /// pumps faster than a person can jump.</summary>
    public const float PumpCooldownSeconds = 0.35f;

    /// <summary>How far from a bloomery's center (in blocks) a jump still works its bellows.</summary>
    public const float PumpRangeBlocks = 3.5f;

    /// <summary>Heat below which the ore does not smelt: the fire is too cold, progress stalls.</summary>
    public const float BandMin = 0.35f;

    /// <summary>Heat above which the ore overheats: it burns instead of forming iron, and the fuel burns faster.</summary>
    public const float BandMax = 0.8f;

    /// <summary>Seconds one ore unit smelts for while the heat sits inside the band.</summary>
    public const float SecondsPerOre = 12f;

    /// <summary>Seconds of overheating during one unit's smelt after which it burns to slag instead of
    /// becoming a bloom.</summary>
    public const float BurnSeconds = 3f;

    /// <summary>How much faster the fuel burns while the heat is above the band.</summary>
    public const float OverheatFuelFactor = 2f;

    /// <summary>Slag one burnt ore unit leaves.</summary>
    public const int SlagPerBurntUnit = 1;

    /// <summary>Slag one bloom gives up when it is hammered into wrought iron.</summary>
    public const int SlagPerBloom = 1;

    /// <summary>Wrought iron one bloom hammers into.</summary>
    public const int IronPerBloom = 1;

    /// <summary>Most blooms (or slag) a saved bloomery may claim to hold: its output never exceeds its ore cap, so
    /// this only bounds a corrupt save.</summary>
    public const int MaxStoredOutput = 64;

    /// <summary>Slack in seconds a server allows on the anvil's hit gap, for network jitter.</summary>
    public const float HammerSlackSeconds = 0.3f;
}
