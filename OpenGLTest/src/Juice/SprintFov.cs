using VoxelEngine.Player;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free rule of the sprint FOV widening: how many degrees the view eases out by while the
/// player sprints. <see cref="Camera"/> eases its widening toward <see cref="TargetDegrees"/>
/// through <see cref="Step"/> and adds the result to the base FOV and the punch pinch, so the two
/// compose instead of one overwriting the other.
/// </summary>
public static class SprintFov
{
    /// <summary>
    /// The widening the view eases toward this frame, 0..<see cref="WorldGenerationConfig.SprintFovExtraDegrees"/>.
    /// It needs the sprint key held with movement input, on foot or flying, and never widens while
    /// sneaking or swimming. On top of that gate it scales with how close the real horizontal speed
    /// is to the on-foot sprint speed and is clamped there, so the 10x flight sprint widens exactly as
    /// far as a run, and holding Shift while backing up (or wading through leaves) widens little or
    /// not at all, since the body is not actually going fast.
    /// </summary>
    public static float TargetDegrees(bool sprintHeld, bool moving, bool sneaking, bool swimming,
        float horizontalSpeed)
    {
        if (!sprintHeld || !moving || sneaking || swimming)
            return 0f;

        const float walk = PlayerPhysics.BaseSpeed;
        const float run = PlayerPhysics.BaseSpeed * PlayerPhysics.SprintMultiplier;
        return WorldGenerationConfig.SprintFovExtraDegrees * MathUtils.InverseLerp(walk, run, horizontalSpeed);
    }

    /// <summary>Eases the current widening toward <paramref name="target"/>, frame-rate independent.</summary>
    public static float Step(float current, float target, float deltaTime) =>
        MathUtils.Damp(current, target, WorldGenerationConfig.SprintFovEaseRate, deltaTime);
}
