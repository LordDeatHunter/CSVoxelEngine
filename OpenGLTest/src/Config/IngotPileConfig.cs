namespace VoxelEngine.Config;

/// <summary>
/// The look and capacity of an ingot pile (<see cref="VoxelEngine.Simulation.Ingots.IngotPileManager"/>): layers of
/// <see cref="PerLayer"/> ingots side by side with a small gap, each layer turned 90 degrees from the one below.
/// </summary>
public static class IngotPileConfig
{
    /// <summary>Ingots lying side by side in one layer.</summary>
    public const int PerLayer = 3;

    /// <summary>Layers one cell holds.</summary>
    public const int Layers = 3;

    /// <summary>Most ingots one cell holds.</summary>
    public const int Capacity = PerLayer * Layers;

    /// <summary>Length of one ingot, in blocks.</summary>
    public const float IngotLength = 0.8f;

    /// <summary>Width of one ingot, in blocks.</summary>
    public const float IngotWidth = 0.26f;

    /// <summary>Thickness of one ingot, in blocks: a third, so a full pile of <see cref="Layers"/> fills the cell.</summary>
    public const float IngotHeight = 1f / Layers;

    /// <summary>Gap between two ingots of one layer, in blocks.</summary>
    public const float Gap = 0.04f;

    /// <summary>Height of a full pile, which the block's hitbox top matches.</summary>
    public const float FullHeight = Layers * IngotHeight;
}
