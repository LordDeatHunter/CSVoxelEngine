namespace VoxelEngine.Networking;

/// <summary>
/// What <see cref="MovementRules"/> remembers about one connected player between its states (held
/// on <see cref="ServerPlayer.Movement"/>). GL-free; main thread only, like the rest of the
/// server's player state.
/// </summary>
public sealed class MovementState
{
    /// <summary>
    /// Server seconds the player has spent airborne, out of water and not sinking, over its last
    /// consecutive judged states; anything that lands, swims, falls, flies or lags resets it. Past
    /// <see cref="WorldGenerationConfig.NetHoverGraceSeconds"/> the player is hovering.
    /// </summary>
    public double AirborneStillSeconds { get; set; }
}
