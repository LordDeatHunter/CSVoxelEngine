using VoxelEngine.Entities.Sandworms;
using VoxelEngine.Juice;

namespace VoxelEngine.Particles;

/// <summary>
/// The numbers behind the sandworm's cosmetic effects, GL-free: how many sand chips a second, how fast and how
/// wide, the closer the worm is to surfacing (<paramref name="telegraph"/> 0..1, <see cref="SandwormBrain.Telegraph"/>),
/// and how hard the earthquake of a worm tunneling shakes a camera some distance from it.
/// </summary>
public static class SandwormEffectRules
{
    /// <summary>Sand chips a second thrown around the surfacing point.</summary>
    public static float ChipsPerSecond(float telegraph) =>
        MathUtils.Lerp(SandwormConfig.TelegraphChipsMin, SandwormConfig.TelegraphChipsMax, telegraph);

    /// <summary>The speed of those chips.</summary>
    public static float ChipSpeed(float telegraph) =>
        MathUtils.Lerp(SandwormConfig.TelegraphSpeedMin, SandwormConfig.TelegraphSpeedMax, telegraph);

    /// <summary>The radius of the churned sand around the surfacing point.</summary>
    public static float Radius(float telegraph) =>
        MathUtils.Lerp(SandwormConfig.TelegraphRadiusMin, SandwormConfig.TelegraphRadiusMax, telegraph);

    /// <summary>The camera trauma the earthquake holds at <paramref name="distance"/> blocks from the head: only
    /// while the worm digs (<see cref="SandwormPhase.Dig"/>), full close by and gone at range.</summary>
    public static float QuakeTrauma(SandwormPhase phase, float distance) =>
        phase == SandwormPhase.Dig
            ? ShakeFalloff.Trauma(SandwormConfig.QuakeStrength, SandwormConfig.QuakeNearBlocks,
                SandwormConfig.QuakeRangeBlocks, distance)
            : 0f;

    /// <summary>The trauma to add in a frame of <paramref name="deltaTime"/> to hold the shake at
    /// <paramref name="trauma"/>: what the camera decays in that time (its trauma runs out in
    /// <paramref name="decaySeconds"/>).</summary>
    public static float HoldTrauma(float trauma, float deltaTime, float decaySeconds = 0.7f) =>
        trauma * deltaTime / decaySeconds;
}
