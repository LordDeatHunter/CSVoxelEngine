using VoxelEngine.Player;

namespace VoxelEngine.Juice;

/// <summary>
/// GL-free rule of the strafe camera tilt: the view rolls a little toward the side the player
/// strafes to, a lean into the move, and eases back to level when the strafe stops.
/// <see cref="Camera"/> eases its tilt toward <see cref="TargetRadians"/> through <see cref="Step"/>
/// and adds it to the other roll terms (run sway, hit kick, death), never replacing them. The sign
/// follows <c>Camera.GetViewMatrix</c>'s roll: positive tilts the view's up axis toward the camera's
/// right, so a strafe to the right leans the head right.
/// </summary>
public static class StrafeTilt
{
    /// <summary>The largest tilt either way, in radians (<see cref="WorldGenerationConfig.StrafeTiltDegrees"/>).</summary>
    public static float MaxRadians => MathUtils.ToRadians(WorldGenerationConfig.StrafeTiltDegrees);

    /// <summary>
    /// The roll the view eases toward this frame. <paramref name="strafe"/> is the sideways movement
    /// input along the camera's right, -1 (full left) .. 1 (full right), clamped so a stray value can
    /// never roll past <see cref="MaxRadians"/>. Flight and death never tilt.
    /// </summary>
    public static float TargetRadians(float strafe, bool flying, bool dead) =>
        flying || dead ? 0f : MathUtils.Clamp(strafe, -1f, 1f) * MaxRadians;

    /// <summary>Eases the current tilt toward <paramref name="target"/>, frame-rate independent.</summary>
    public static float Step(float current, float target, float deltaTime) =>
        MathUtils.Damp(current, target, WorldGenerationConfig.StrafeTiltEaseRate, deltaTime);
}
