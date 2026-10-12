namespace VoxelEngine.Player;

/// <summary>
/// How deep the player's body stands in a fluid, by the share of its height under the surface
/// (<see cref="WaterImmersion"/>). Ordered shallowest first, so a deeper phase compares greater.
/// </summary>
public enum WaterPhase
{
    /// <summary>No fluid surface above the feet.</summary>
    Dry,

    /// <summary>Under half the body is under (a one-block-deep pool): only the walk slows.</summary>
    Wade,

    /// <summary>Half or more under with the eye still above the surface: floating at the top of deep water.</summary>
    Float,

    /// <summary>The eye is under the surface: fully swimming.</summary>
    Swim,
}
