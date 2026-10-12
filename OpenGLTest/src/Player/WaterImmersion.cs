namespace VoxelEngine.Player;

/// <summary>
/// GL-free rule of how deep the player's body stands in a fluid: the share of its height
/// (<see cref="PlayerPhysics.PlayerHeight"/>) under the fluid surface, and the
/// <see cref="WaterPhase"/> that share and the eye give. <see cref="PlayerPhysics.UpdateWaterState"/>
/// feeds it the highest fluid surface over the cells the body overlaps, and <see cref="Camera"/>
/// exposes the result beside <see cref="Camera.IsInWater"/>, which keeps its own meaning (deep enough
/// to swim in at the feet).
/// </summary>
public static class WaterImmersion
{
    /// <summary>
    /// The share of the body under a fluid surface at <paramref name="surfaceY"/> for feet at
    /// <paramref name="feetY"/>, 0..1: 0 with the surface at or below the feet (or no fluid at all,
    /// <see cref="float.NegativeInfinity"/>), 1 once it reaches over the head.
    /// </summary>
    public static float Share(float feetY, float surfaceY)
    {
        if (!(surfaceY > feetY))
            return 0f;
        return MathUtils.Clamp01((surfaceY - feetY) / PlayerPhysics.PlayerHeight);
    }

    /// <summary>
    /// The phase of a body with <paramref name="share"/> of it under: <see cref="WaterPhase.Swim"/>
    /// whenever <paramref name="eyeUnder"/> (the eye below the surface), otherwise
    /// <see cref="WaterPhase.Float"/> from <see cref="WorldGenerationConfig.WaterFloatShare"/> up,
    /// <see cref="WaterPhase.Wade"/> below it and <see cref="WaterPhase.Dry"/> with nothing under.
    /// </summary>
    public static WaterPhase PhaseOf(float share, bool eyeUnder)
    {
        if (eyeUnder)
            return WaterPhase.Swim;
        if (share >= WorldGenerationConfig.WaterFloatShare)
            return WaterPhase.Float;
        return share > 0f ? WaterPhase.Wade : WaterPhase.Dry;
    }

    /// <summary>
    /// The vertical speed a floating body eases toward with <paramref name="share"/> of it under, in
    /// blocks/s: up while deeper than <see cref="WorldGenerationConfig.WaterFloatRestShare"/>, down
    /// while shallower, zero at it, in proportion to the height error
    /// (<see cref="WorldGenerationConfig.FloatBuoyancy"/>) and capped by the swim sink and rise speeds.
    /// It never grows weaker as the error grows, so a body always heads back to the rest.
    /// </summary>
    public static float BuoyancySpeed(float share)
    {
        var error = (MathUtils.Clamp01(share) - WorldGenerationConfig.WaterFloatRestShare) * PlayerPhysics.PlayerHeight;
        return MathUtils.Clamp(WorldGenerationConfig.FloatBuoyancy * error, PlayerPhysics.SinkSpeed, PlayerPhysics.SwimUpSpeed);
    }
}
